using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Rendu de l'atmosphère et de la météo réelle autour de la caméra :
/// soleil à sa vraie position (heure UTC réelle), ciel jour/nuit/couvert, brouillard selon la visibilité,
/// trois couches de nuages locales (basse, moyenne, haute) poussées par le vent,
/// une couche nuageuse mondiale vue de l'espace, pluie et neige.
/// </summary>
public partial class Atmosphere : Node3D
{
    public FlyCamera Camera;
    public Planet Planet;
    public Weather Weather;
    public DirectionalLight3D Sun;
    public Godot.Environment Env;

    /// <summary>Décalage par rapport à l'heure réelle (touches + / − / R), pour tester.</summary>
    public double TimeOffsetHours;
    public DateTime GameUtc => DateTime.UtcNow.AddHours(TimeOffsetHours);

    /// <summary>
    /// « Jour permanent » (touche J) : le soleil reste comme en début d'après-midi là où se trouve le joueur.
    /// L'heure affichée et la météo restent réelles ; seul l'éclairage change. Réglage mémorisé.
    /// </summary>
    public bool AlwaysDay
    {
        get => _alwaysDay;
        set { _alwaysDay = value; SaveSetting("always_day", value); }
    }
    bool _alwaysDay = LoadSetting("always_day", false);
    const double AlwaysDaySolarHour = 13.0;
    const string SettingsPath = "user://settings.cfg";

    static bool LoadSetting(string key, bool fallback)
    {
        var cfg = new ConfigFile();
        return cfg.Load(SettingsPath) == Error.Ok ? (bool)cfg.GetValue("affichage", key, fallback) : fallback;
    }

    static void SaveSetting(string key, bool value)
    {
        var cfg = new ConfigFile();
        cfg.Load(SettingsPath); // garde les autres réglages s'il y en a
        cfg.SetValue("affichage", key, value);
        cfg.Save(SettingsPath);
    }
    public WeatherSample Current { get; private set; }
    public double SunElevationDeg { get; private set; }

    ProceduralSkyMaterial _sky;
    readonly CloudLayer[] _layers = new CloudLayer[3];
    MeshInstance3D _globalClouds;
    ShaderMaterial _globalMat;
    ImageTexture _coverTex;
    double _coverAge = 1e9;
    GpuParticles3D _rain, _snow;
    NoiseTexture2D _noise;

    // Bases des couches (m au-dessus du niveau de la mer)
    static readonly float[] LayerAltitude = { 2000, 4500, 9000 };
    const double Period = 40_000; // période de la texture de bruit (m)

    sealed class CloudLayer
    {
        public MeshInstance3D Mesh;
        public ShaderMaterial Mat;
        public double DriftE, DriftN; // déplacement cumulé par le vent (m)
    }

    public override void _Ready()
    {
        _sky = (ProceduralSkyMaterial)Env.Sky.SkyMaterial;
        _noise = new NoiseTexture2D
        {
            Width = 512, Height = 512, Seamless = true, GenerateMipmaps = true,
            Noise = new FastNoiseLite { NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth, Frequency = 0.008f, FractalOctaves = 5 },
        };

        var layerShader = new Shader { Code = LayerShader };
        for (int i = 0; i < 3; i++)
        {
            var mat = new ShaderMaterial { Shader = layerShader };
            mat.SetShaderParameter("noise_tex", _noise);
            mat.SetShaderParameter("period", (float)Period / (i == 2 ? 0.5f : 1f));
            var plane = new PlaneMesh { Size = new Vector2(160_000, 160_000), SubdivideWidth = 48, SubdivideDepth = 48 };
            var mi = new MeshInstance3D { Mesh = plane, MaterialOverride = mat, ExtraCullMargin = 2000, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            AddChild(mi);
            _layers[i] = new CloudLayer { Mesh = mi, Mat = mat };
        }

        // Couche mondiale (vue de l'espace) : sphère enfant de la planète, texturée avec la couverture GFS
        _globalMat = new ShaderMaterial { Shader = new Shader { Code = GlobalShader } };
        _globalMat.SetShaderParameter("noise_tex", _noise);
        var sphere = new SphereMesh { Radius = (float)(Geo.Radius + 6000), Height = (float)(2 * (Geo.Radius + 6000)), RadialSegments = 192, Rings = 96 };
        _globalClouds = new MeshInstance3D { Mesh = sphere, MaterialOverride = _globalMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        Planet.AddChild(_globalClouds);

        _rain = MakePrecip(false);
        _snow = MakePrecip(true);
    }

    GpuParticles3D MakePrecip(bool snow)
    {
        var pm = new ParticleProcessMaterial
        {
            EmissionShape = ParticleProcessMaterial.EmissionShapeEnum.Box,
            EmissionBoxExtents = snow ? new Vector3(40, 20, 40) : new Vector3(40, 15, 40),
            Direction = Vector3.Down,
            Spread = snow ? 15 : 2,
            InitialVelocityMin = snow ? 0.8f : 8f,
            InitialVelocityMax = snow ? 1.5f : 10f,
            Gravity = Vector3.Zero,
            TurbulenceEnabled = snow,
            TurbulenceNoiseStrength = 0.5f,
        };
        var mat = new StandardMaterial3D
        {
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            AlbedoColor = snow ? new Color(1, 1, 1, 0.9f) : new Color(0.75f, 0.8f, 0.9f, 0.35f),
            BillboardMode = snow ? BaseMaterial3D.BillboardModeEnum.Enabled : BaseMaterial3D.BillboardModeEnum.FixedY,
            BillboardKeepScale = true,
        };
        var quad = new QuadMesh { Size = snow ? new Vector2(0.05f, 0.05f) : new Vector2(0.015f, 0.7f), Material = mat };
        var p = new GpuParticles3D
        {
            Amount = snow ? 6000 : 8000,
            Lifetime = snow ? 25 : 3.5,
            ProcessMaterial = pm,
            DrawPass1 = quad,
            Position = new Vector3(0, 15, 0),
            VisibilityAabb = new Aabb(new Vector3(-60, -80, -60), new Vector3(120, 120, 120)),
            Emitting = false,
            Preprocess = snow ? 10 : 2,
        };
        AddChild(p);
        return p;
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed)
        {
            if (k.PhysicalKeycode is Key.Equal or Key.KpAdd) TimeOffsetHours += k.ShiftPressed ? 0.25 : 1;
            if (k.PhysicalKeycode is Key.Minus or Key.KpSubtract) TimeOffsetHours -= k.ShiftPressed ? 0.25 : 1;
            if (k.PhysicalKeycode == Key.R && !k.Echo) TimeOffsetHours = 0;
            if (k.PhysicalKeycode == Key.J && !k.Echo) AlwaysDay = !AlwaysDay;
        }
    }

    public override void _Process(double delta)
    {
        var utc = GameUtc;
        var up = Camera.PosD.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        double alt = Camera.Altitude;
        double agl = alt - Camera.GroundHeight;
        var w = Weather.Sample(lat, lon, utc);
        Current = w;

        // --- Soleil (repère planète : la lumière est enfant de la planète)
        var sunUtc = utc;
        if (AlwaysDay)
        {
            // Instant UTC où il est 13 h solaires à la longitude du joueur (même jour, donc même saison).
            double h = AlwaysDaySolarHour - lon / 15.0;
            sunUtc = utc.Date.AddHours(h);
        }
        var sunDir = SunPosition.Direction(sunUtc);
        Sun.Transform = new Transform3D(Basis.LookingAt(-sunDir.ToVector3(), Math.Abs(sunDir.Y) > 0.99 ? Vector3.Right : Vector3.Up), Vector3.Zero);
        double sinElev = up.Dot(sunDir);
        SunElevationDeg = Math.Asin(Math.Clamp(sinElev, -1, 1)) * 180 / Math.PI;
        float day = (float)Math.Clamp((SunElevationDeg + 6) / 12.0, 0, 1);        // 0 nuit → 1 jour
        float golden = (float)Math.Clamp(1 - Math.Abs(SunElevationDeg - 2) / 10.0, 0, 1); // lever / coucher
        bool space = alt > 60_000;
        float overcast = space ? 0 : Math.Clamp(w.CloudLow * 0.8f + w.CloudMid * 0.5f + w.CloudHigh * 0.2f, 0, 1);
        if (alt > LayerAltitude[0] + 500) overcast *= 0.3f; // au-dessus de la couche basse, le soleil revient

        Sun.LightEnergy = (float)Math.Clamp(sinElev * 6 + 0.1, 0, 1) * 1.6f * (1 - 0.75f * overcast);
        Sun.LightColor = new Color(1, 0.85f, 0.7f).Lerp(Colors.White, (float)Math.Clamp(SunElevationDeg / 15, 0, 1));

        // --- Ciel
        var dayTop = new Color(0.22f, 0.42f, 0.78f).Lerp(new Color(0.55f, 0.58f, 0.62f), overcast);
        var dayHorizon = new Color(0.65f, 0.75f, 0.88f).Lerp(new Color(0.68f, 0.70f, 0.72f), overcast).Lerp(new Color(0.95f, 0.55f, 0.30f), golden * (1 - overcast));
        var night = new Color(0.025f, 0.035f, 0.07f);
        _sky.SkyTopColor = night.Lerp(dayTop, day);
        _sky.SkyHorizonColor = night.Lerp(dayHorizon, day);
        _sky.GroundHorizonColor = _sky.SkyHorizonColor;
        _sky.GroundBottomColor = night.Lerp(new Color(0.2f, 0.2f, 0.22f), day);
        _sky.SunAngleMax = 30;

        Env.BackgroundMode = space ? Godot.Environment.BGMode.Color : Godot.Environment.BGMode.Sky;
        Env.BackgroundColor = Colors.Black;
        Env.AmbientLightEnergy = space ? 0.05f : 0.18f + 0.4f * day; // un peu de lumière la nuit pour rester jouable

        // --- Brouillard : visibilité météo (3/visibilité ≈ 5 % de contraste à cette distance)
        double airDecay = Math.Exp(-Math.Max(0, alt) / 8000);
        double vis = Math.Max(200, w.VisibilityM);
        double fogDensity = 0.00002 + (3.0 / vis) * Math.Exp(-Math.Max(0, agl) / 1500);
        // Dans un nuage : visibilité quasi nulle
        for (int i = 0; i < 3; i++)
        {
            float cov = i == 0 ? w.CloudLow : i == 1 ? w.CloudMid : w.CloudHigh;
            if (cov > 0.6f && Math.Abs(alt - LayerAltitude[i]) < 150) fogDensity = Math.Max(fogDensity, 0.01 * cov);
        }
        Env.FogEnabled = !space;
        Env.FogDensity = (float)(fogDensity * airDecay);
        Env.FogLightColor = night.Lerp(new Color(0.62f, 0.68f, 0.76f).Lerp(new Color(0.7f, 0.7f, 0.72f), overcast), day);

        // --- Couches de nuages locales
        float localFade = (float)Math.Clamp(1 - (alt - 20_000) / 20_000, 0, 1);
        double eastM = lon * Math.PI / 180 * Geo.Radius * Math.Cos(lat * Math.PI / 180);
        double northM = lat * Math.PI / 180 * Geo.Radius;
        var lightCol = new Color(0.08f, 0.09f, 0.12f).Lerp(Colors.White, day).Lerp(new Color(1f, 0.75f, 0.6f), golden * 0.6f);
        for (int i = 0; i < 3; i++)
        {
            var L = _layers[i];
            float cov = i == 0 ? w.CloudLow : i == 1 ? w.CloudMid : w.CloudHigh;
            var wind = i == 0 ? (w.Wind850East, w.Wind850North) : (w.Wind850East * (1.5f + i), w.Wind850North * (1.5f + i));
            L.DriftE = (L.DriftE + wind.Item1 * delta) % Period;
            L.DriftN = (L.DriftN + wind.Item2 * delta) % Period;
            double period = Period / (i == 2 ? 0.5 : 1);
            float offE = (float)(((eastM - L.DriftE) % period + period) % period);
            float offN = (float)(((northM - L.DriftN) % period + period) % period);
            L.Mat.SetShaderParameter("offset", new Vector2(offE, -offN));
            L.Mat.SetShaderParameter("coverage", cov);
            L.Mat.SetShaderParameter("light_color", lightCol * (i == 0 ? 1f - 0.35f * cov : 1f));
            L.Mat.SetShaderParameter("opacity", (i == 2 ? 0.6f : 0.95f) * localFade);
            L.Mesh.Position = new Vector3(0, (float)(LayerAltitude[i] - alt), 0);
            L.Mesh.Visible = localFade > 0 && cov > 0.02f;
        }

        // --- Couche mondiale
        _coverAge += delta;
        if (_coverAge > 60 && Weather.HasData)
        {
            _coverAge = 0;
            var map = Weather.CloudCoverMap(utc);
            var img = Image.CreateFromData(Weather.MapWidth, Weather.MapHeight, false, Image.Format.L8, map);
            if (_coverTex == null) { _coverTex = ImageTexture.CreateFromImage(img); _globalMat.SetShaderParameter("cover_tex", _coverTex); }
            else _coverTex.Update(img);
        }
        float globalFade = (float)Math.Clamp((alt - 15_000) / 25_000, 0, 1);
        _globalClouds.Visible = globalFade > 0;
        _globalClouds.Position = (-Camera.PosD).ToVector3();
        _globalMat.SetShaderParameter("fade", globalFade);
        _globalMat.SetShaderParameter("sun_dir", sunDir.ToVector3());

        // --- Pluie / neige (sous la couche basse uniquement)
        float intensity = Math.Clamp(w.PrecipMmH / 4f, 0, 1);
        bool under = alt < LayerAltitude[0] && agl < 1500;
        bool snow = w.FrozenFraction > 0.5f || w.TemperatureC < 0.5f;
        var gravityWind = new Vector3(w.Wind10East, 0, -w.Wind10North);
        _rain.Emitting = under && !snow && w.PrecipMmH > 0.1f;
        _snow.Emitting = under && snow && w.PrecipMmH > 0.05f;
        _rain.AmountRatio = intensity;
        _snow.AmountRatio = intensity;
        ((ParticleProcessMaterial)_rain.ProcessMaterial).Gravity = gravityWind * 0.5f;
        ((ParticleProcessMaterial)_snow.ProcessMaterial).Gravity = gravityWind * 0.15f;
    }

    const string LayerShader = @"
shader_type spatial;
render_mode unshaded, cull_disabled, depth_draw_never, blend_mix;
uniform sampler2D noise_tex : repeat_enable, filter_linear_mipmap;
uniform vec2 offset;          // position de la caméra (m, modulo la période)
uniform float period = 40000.0;
uniform float coverage = 0.5;
uniform float opacity = 0.95;
uniform vec3 light_color : source_color = vec3(1.0);
const float PLANET_RADIUS = 6371000.0;
const float HALF_SIZE = 80000.0;
varying vec2 wpos;
void vertex() {
    wpos = VERTEX.xz;
    VERTEX.y -= dot(VERTEX.xz, VERTEX.xz) / (2.0 * PLANET_RADIUS); // courbure de la Terre
}
void fragment() {
    vec2 uv = (wpos + offset) / period;
    float n = texture(noise_tex, uv).r * 0.7 + texture(noise_tex, uv * 5.3 + vec2(0.37, 0.71)).r * 0.3;
    float thr = mix(0.78, 0.22, coverage);
    float a = smoothstep(thr - 0.03, thr + 0.10, n);
    a *= 1.0 - smoothstep(HALF_SIZE * 0.5, HALF_SIZE, length(wpos));
    ALBEDO = light_color * mix(0.7, 1.0, smoothstep(thr, thr + 0.3, n));
    ALPHA = a * opacity;
}";

    const string GlobalShader = @"
shader_type spatial;
render_mode unshaded, cull_back, depth_draw_never, blend_mix;
uniform sampler2D cover_tex : filter_linear, repeat_enable;
uniform sampler2D noise_tex : repeat_enable, filter_linear_mipmap;
uniform vec3 sun_dir;
uniform float fade = 1.0;
varying vec3 dir;
void vertex() { dir = VERTEX; }
void fragment() {
    vec3 n = normalize(dir);
    float lat = asin(clamp(n.y, -1.0, 1.0));
    float lon = atan(-n.z, n.x);
    vec2 uv = vec2(fract(lon / (2.0 * PI)), 0.5 - lat / PI);
    float c = texture(cover_tex, uv).r;
    float det = texture(noise_tex, uv * vec2(24.0, 12.0)).r * 0.65 + texture(noise_tex, uv * vec2(97.0, 48.5) + vec2(0.31, 0.17)).r * 0.35;
    float a = smoothstep(0.45, 0.9, c * (0.55 + det * 0.75)) * 0.9 + smoothstep(0.2, 0.8, c) * 0.1;
    float light = clamp(dot(n, normalize(sun_dir)) * 4.0 + 0.2, 0.02, 1.0);
    ALBEDO = vec3(light);
    ALPHA = a * fade * 0.95;
}";
}
