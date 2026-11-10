using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>
/// Océans, mers et lacs : la surface de l'eau de chaque chunk (construite par <see cref="ChunkBuilder"/>) est rendue
/// avec shaders/ocean.gdshader. Ce nœud fabrique chaque image le jeu de vagues de Gerstner à partir de l'état de la
/// mer réel au-dessus du joueur (<see cref="Waves"/> : mer du vent + deux houles NOAA GFS-Wave) et du vent GFS
/// (clapot), et gère la vue sous l'eau (brouillard bleu).
/// </summary>
public partial class Ocean : Node
{
    /// <summary>Période (m) du repère des vagues : voir le commentaire en tête du shader.</summary>
    public const double Period = 8192;
    public const int MaxWaves = 64;

    public FlyCamera Camera;
    public Waves Waves;
    public Atmosphere Atmosphere;
    public Godot.Environment Env;

    public static ShaderMaterial Material { get; private set; }

    /// <summary>Nombre de vagues selon la qualité graphique (Bas 16, Moyen 32, Élevé 48, Ultra 64).</summary>
    static int WaveBudget => Math.Clamp(GraphicsQuality.Current.WaterWaves, 8, MaxWaves);

    // Copie des vagues pour HeightAt (lue par les véhicules)
    static Wave[] _snapshot = Array.Empty<Wave>();
    public SeaState Sea { get; private set; }
    public bool Underwater { get; private set; }
    public string Status { get; private set; } = "";

    static readonly Vector4[] K = new Vector4[MaxWaves];
    static readonly Vector4[] P = new Vector4[MaxWaves];

    sealed class Wave { public int Sys; public double Kx, Ky, Kz, Omega, Phase0, Amp, Q, Lambda, Lake; }
    readonly List<Wave> _waves = new();
    double _time, _sinceSample = 99;
    Vec3d _builtAt;
    SeaState _built;
    float _builtWind;
    SeaState _smooth;
    float _wind, _windE, _windN;

    public static ShaderMaterial EnsureMaterial()
    {
        if (Material != null) return Material;
        Material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/ocean.gdshader") };
        return Material;
    }

    public override void _Ready()
    {
        EnsureMaterial();
        HookVehicles();
    }

    /// <summary>
    /// Hauteur (m) de la surface au-dessus du niveau de la mer au point <paramref name="p"/> (repère planète) à
    /// l'instant <paramref name="t"/> (s, Time.GetTicksMsec()/1000) : même somme de vagues de Gerstner que le shader
    /// (on remonte le déplacement horizontal par deux itérations), pour que les bateaux suivent les vagues affichées.
    /// </summary>
    public static double HeightAt(Vec3d p, double t)
    {
        var waves = _snapshot;
        if (waves.Length == 0) return 0;
        var up = p.Normalized();
        var x = p;
        double h = 0;
        for (int iter = 0; iter < 3; iter++)
        {
            double dx = 0, dy = 0, dz = 0;
            h = 0;
            foreach (var w in waves)
            {
                if (w.Amp < 0.0005) continue;
                double th = w.Kx * x.X + w.Ky * x.Y + w.Kz * x.Z - (w.Omega * t + w.Phase0);
                h += w.Amp * Math.Cos(th);
                if (iter == 2) continue;
                var K = new Vec3d(w.Kx, w.Ky, w.Kz);
                var D = (K - up * K.Dot(up)).Normalized();
                double sh = -w.Q * w.Amp * Math.Sin(th);
                dx += D.X * sh; dy += D.Y * sh; dz += D.Z * sh;
            }
            if (iter < 2) x = p - new Vec3d(dx, dy, dz); // point de départ dont le déplacement retombe sur p
        }
        return h;
    }

    /// <summary>
    /// Branche <see cref="HeightAt"/> sur les véhicules (WaterSurface.Waves) si cette classe existe dans le jeu,
    /// sans dépendance de compilation entre les deux modules.
    /// </summary>
    static void HookVehicles()
    {
        foreach (var type in typeof(Ocean).Assembly.GetTypes())
        {
            if (type.Name != "WaterSurface") continue;
            Func<Vec3d, double, double> f = HeightAt;
            var field = type.GetField("Waves", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (field != null && field.FieldType.IsAssignableFrom(f.GetType())) { field.SetValue(null, f); GD.Print("[Ocean] vagues branchées sur les bateaux"); }
            var prop = type.GetProperty("Waves", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            if (prop != null && prop.CanWrite && prop.PropertyType.IsAssignableFrom(f.GetType())) { prop.SetValue(null, f); GD.Print("[Ocean] vagues branchées sur les bateaux"); }
        }
    }

    /// <summary>Ajoute (ou remplace) la surface d'eau d'un chunk.</summary>
    public static void Attach(MeshInstance3D chunk, ChunkData data)
    {
        foreach (var child in chunk.GetChildren())
            if (child is MeshInstance3D m && m.Name == "Water") child.QueueFree();
        if (data.WaterIndices == null || data.WaterIndices.Length == 0) return;
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = data.WaterVertices;
        arrays[(int)Mesh.ArrayType.Normal] = data.WaterNormals;
        arrays[(int)Mesh.ArrayType.Color] = data.WaterColors;
        arrays[(int)Mesh.ArrayType.TexUV] = data.WaterUV;
        arrays[(int)Mesh.ArrayType.TexUV2] = data.WaterUV2;
        arrays[(int)Mesh.ArrayType.Index] = data.WaterIndices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        // Les vagues déplacent les sommets : on agrandit la boîte englobante pour ne pas les faire disparaître trop tôt.
        mesh.CustomAabb = mesh.GetAabb().Grow(30);
        var mi = new MeshInstance3D { Name = "Water", Mesh = mesh, MaterialOverride = EnsureMaterial() };
        mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        chunk.AddChild(mi);
    }

    static float Smooth(float cur, float target, float k) => cur + (target - cur) * k;

    static WaveSystem SmoothSys(WaveSystem a, WaveSystem b, float k)
    {
        if (a.Height <= 0.001f) return b;
        if (b.Height <= 0.001f) { a.Height = Smooth(a.Height, 0, k); return a; }
        a.Height = Smooth(a.Height, b.Height, k);
        a.Period = Smooth(a.Period, b.Period, k);
        float e = Smooth(a.ToEast, b.ToEast, k), n = Smooth(a.ToNorth, b.ToNorth, k), l = MathF.Sqrt(e * e + n * n);
        if (l > 1e-4f) { a.ToEast = e / l; a.ToNorth = n / l; }
        return a;
    }

    public override void _Process(double delta)
    {
        if (Camera == null || Material == null) return;
        _time = Time.GetTicksMsec() / 1000.0;
        var cam = Camera.PlayerPos;
        var up = cam.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        var utc = Atmosphere?.GameUtc ?? DateTime.UtcNow;

        // État de la mer : échantillonné toutes les 2 s puis lissé (pas de saut quand on se déplace)
        _sinceSample += delta;
        if (_sinceSample > 2)
        {
            _sinceSample = 0;
            var s = Waves?.Sample(lat, lon, utc) ?? default;
            var w = Atmosphere?.Current ?? default;
            float k = _smooth.Valid ? 0.35f : 1f;
            _wind = Smooth(_wind, w.Wind10Speed, k);
            _windE = Smooth(_windE, w.Wind10East, k); _windN = Smooth(_windN, w.Wind10North, k);
            if (s.Valid)
            {
                var m = _smooth;
                m.Valid = true;
                m.Hs = Smooth(m.Hs, s.Hs, k);
                m.Wind = SmoothSys(m.Wind, s.Wind, k);
                m.Swell1 = SmoothSys(m.Swell1, s.Swell1, k);
                m.Swell2 = SmoothSys(m.Swell2, s.Swell2, k);
                _smooth = m;
            }
            else if (!_smooth.Valid) _smooth = EstimateFromWind();
            Sea = _smooth;
            if (_waves.Count == 0 || NeedsRebuild(cam)) Build(cam);
            Status = $"mer {_smooth.Hs:F1} m" +
                     (_smooth.Swell1.Height > 0.05f ? $", houle {_smooth.Swell1.Height:F1} m {_smooth.Swell1.Period:F0} s du {_smooth.Swell1.FromDeg:F0}°" : "") +
                     (_smooth.Wind.Height > 0.05f ? $", mer du vent {_smooth.Wind.Height:F1} m {_smooth.Wind.Period:F0} s" : "") +
                     (Waves != null && s.Valid ? $" ({Waves.Status})" : " (estimée d'après le vent)");
        }
        Upload();
        UpdateUnderwater(up, lat, lon);
    }

    /// <summary>Hors données (lacs, côtes) : mer du vent estimée d'après le vent GFS (mer pleinement développée, bornée).</summary>
    SeaState EstimateFromWind()
    {
        float u = Math.Clamp(_wind, 0.5f, 30);
        var s = new SeaState { Valid = true };
        float hs = Math.Min(0.21f * u * u / 9.81f, 2.5f);
        float tp = Math.Clamp(0.8f * 2 * MathF.PI * u / 9.81f, 2, 9);
        float l = MathF.Sqrt(_windE * _windE + _windN * _windN);
        s.Wind = new WaveSystem { Height = hs, Period = tp, ToEast = l > 0.1f ? _windE / l : 0, ToNorth = l > 0.1f ? _windN / l : 1 };
        s.Hs = hs;
        return s;
    }

    int _builtQuality = -1;

    bool NeedsRebuild(Vec3d cam)
    {
        if (_builtQuality != WaveBudget) return true;
        if ((cam - _builtAt).Length() > 100_000) return true;
        if (MathF.Abs(_wind - _builtWind) > 1.5f) return true;
        bool Diff(WaveSystem a, WaveSystem b) =>
            (a.Height > 0.02f) != (b.Height > 0.02f) ||
            a.Height > 0.02f && MathF.Abs(a.Period - b.Period) > 0.04f * Math.Max(1, b.Period) || a.ToEast * b.ToEast + a.ToNorth * b.ToNorth < 0.9985f;
        return Diff(_smooth.Wind, _built.Wind) || Diff(_smooth.Swell1, _built.Swell1) || Diff(_smooth.Swell2, _built.Swell2);
    }

    static uint Hash(uint x) { x ^= x >> 16; x *= 0x7feb352d; x ^= x >> 15; x *= 0x846ca68b; x ^= x >> 16; return x; }
    static double Rand(uint seed) => (Hash(seed) & 0xffffff) / 16777216.0;

    /// <summary>
    /// Construit le jeu de vagues (directions, longueurs d'onde) pour la région du joueur. Les amplitudes suivent
    /// ensuite l'état de la mer à chaque image, sans saut.
    /// </summary>
    void Build(Vec3d cam)
    {
        _builtAt = cam; _built = _smooth; _builtWind = _wind;
        _waves.Clear();
        Geo.LocalFrame(cam.Normalized(), out var east, out var north);
        uint seed = 1;
        int sys = 0;
        void Add(double lambda, double e, double n, double lake)
        {
            double k = 2 * Math.PI / lambda;
            var dir = east * e + north * n;
            double unit = 2 * Math.PI / Period;
            // vecteur d'onde arrondi sur la grille 2π/Period (voir le shader)
            double kx = Math.Round(dir.X * k / unit) * unit, ky = Math.Round(dir.Y * k / unit) * unit, kz = Math.Round(dir.Z * k / unit) * unit;
            double kk = Math.Sqrt(kx * kx + ky * ky + kz * kz);
            if (kk < 1e-9) return;
            _waves.Add(new Wave
            {
                Sys = sys, Kx = kx, Ky = ky, Kz = kz, Omega = Math.Sqrt(9.81 * kk), Phase0 = Rand(seed++) * 2 * Math.PI,
                Lambda = 2 * Math.PI / kk, Lake = lake,
            });
        }
        void AddSystem(WaveSystem s, int count, double spreadDeg, double lake)
        {
            if (s.Height <= 0.02f || s.Period < 1) return;
            double baseAng = Math.Atan2(s.ToEast, s.ToNorth);
            for (int i = 0; i < count; i++)
            {
                // périodes réparties autour du pic (0,75 à 1,3 × T), directions étalées autour de la moyenne
                double f = 0.75 + 0.55 * (i + Rand(seed++)) / count;
                double ang = baseAng + (Rand(seed++) - 0.5) * 2 * spreadDeg * Math.PI / 180;
                double T = s.Period * f;
                Add(9.81 * T * T / (2 * Math.PI), Math.Sin(ang), Math.Cos(ang), lake);
            }
        }
        int budget = WaveBudget;
        int sw = budget >= 64 ? 1 : budget >= 32 ? 2 : 3; // moins de composantes par système en basse qualité
        sys = 0; AddSystem(_smooth.Swell1, 7 / sw + 1, 10, 0);
        sys = 1; AddSystem(_smooth.Swell2, 5 / sw + 1, 12, 0);
        sys = 2; AddSystem(_smooth.Wind, 10 / sw + 1, 35, 0);
        sys = 3;
        // Clapot et rides poussés par le vent local : aussi sur les lacs
        double wAng = Math.Atan2(_windE, _windN);
        double peak = _smooth.Wind.Height > 0.05f ? _smooth.Wind.Wavelength : 20;
        int left = budget - _waves.Count;
        for (int i = 0; i < left; i++)
        {
            double t = (i + 0.5) / left;
            double lambda = Math.Exp(Math.Log(Math.Min(peak * 0.5, 12)) * (1 - t) + Math.Log(0.25) * t);
            double ang = wAng + (Rand(seed++) - 0.5) * 2 * 60 * Math.PI / 180;
            Add(lambda, Math.Sin(ang), Math.Cos(ang), 1);
        }
        _builtQuality = WaveBudget;
        GD.Print($"[Ocean] {_waves.Count} vagues ({Status})");
    }

    void Upload()
    {
        // Amplitudes : Σa² = Hs²/8 pour chaque système ; clapot proportionnel à la longueur d'onde et au vent
        float windF = Math.Clamp((_wind - 1) / 12f, 0, 1);
        double[] hs = { _smooth.Swell1.Height, _smooth.Swell2.Height, _smooth.Wind.Height };
        int nw = Math.Min(_waves.Count, MaxWaves);
        var counts = new int[4];
        for (int w = 0; w < nw; w++) counts[_waves[w].Sys]++;
        double choppy = 0.45 + 0.4 * windF;
        for (int w = 0; w < nw; w++)
        {
            var wv = _waves[w];
            wv.Amp = wv.Sys < 3 ? Math.Sqrt(hs[wv.Sys] * hs[wv.Sys] / 8 / counts[wv.Sys]) : wv.Lambda * (0.0008 + 0.003 * windF);
        }
        double sumQ = 0;
        for (int w = 0; w < nw; w++) sumQ += _waves[w].Amp * 2 * Math.PI / _waves[w].Lambda;
        for (int w = 0; w < nw; w++)
        {
            var wv = _waves[w];
            double k = 2 * Math.PI / wv.Lambda;
            // raideur répartie pour que Σ Q·a·k = « choppy » (pas de boucles dans les crêtes)
            wv.Q = sumQ > 1e-6 ? Math.Min(1, choppy / sumQ) : 0;
            double phase = (wv.Omega * _time + wv.Phase0) % (2 * Math.PI);
            K[w] = new Vector4((float)wv.Kx, (float)wv.Ky, (float)wv.Kz, (float)wv.Amp);
            P[w] = new Vector4((float)phase, (float)wv.Q, (float)wv.Lambda, (float)(wv.Lake * (wv.Lambda < 6 ? 0.6 : 0)));
        }
        _snapshot = _waves.GetRange(0, nw).ConvertAll(w => new Wave { Sys = w.Sys, Kx = w.Kx, Ky = w.Ky, Kz = w.Kz, Omega = w.Omega, Phase0 = w.Phase0, Amp = w.Amp, Q = w.Q, Lambda = w.Lambda, Lake = w.Lake }).ToArray();
        Material.SetShaderParameter("wave_count", nw);
        Material.SetShaderParameter("wave_k", K);
        Material.SetShaderParameter("wave_p", P);
        Material.SetShaderParameter("whitecaps", Math.Clamp((_wind - 4.5f) / 8f, 0, 1));
        Material.SetShaderParameter("time_s", (float)(_time % 10000));
        float night = Atmosphere == null ? 0 : (float)Math.Clamp((2 - Atmosphere.SunElevationDeg) / 10, 0, 1);
        Material.SetShaderParameter("night", night);
    }

    /// <summary>Sous la surface de la mer : brouillard bleu-vert dense (une vingtaine de mètres de visibilité).</summary>
    void UpdateUnderwater(Vec3d up, double lat, double lon)
    {
        double alt = Camera.PosD.Length() - Geo.Radius;
        bool under = false;
        if (alt < 0)
        {
            HeightProvider.Sample(lat, lon, 30, out bool water, out _, out _, out bool sea);
            under = water && sea;
        }
        bool was = Underwater;
        Underwater = under;
        if (Env == null) return;
        if (!under) { if (was) Env.FogSkyAffect = 0; return; }
        float day = Atmosphere == null ? 1 : (float)Math.Clamp((Atmosphere.SunElevationDeg + 4) / 14, 0.05, 1);
        float depthLight = (float)Math.Exp(alt / 60.0); // la lumière baisse avec la profondeur
        Env.FogEnabled = true;
        Env.FogLightColor = new Color(0.03f, 0.22f, 0.26f) * day * (0.25f + 0.75f * depthLight);
        Env.FogLightEnergy = 1;
        Env.FogDensity = 0.045f;
        Env.FogSkyAffect = 1;
    }
}
