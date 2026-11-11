using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// La Terre : 6 faces de cube-sphère, chacune subdivisée en quadtree selon la distance à la caméra.
/// Rendu « relatif à la caméra » : la caméra reste à l'origine Godot et chaque chunk est placé à
/// (centre − position caméra), calculé en double. On évite ainsi les tremblements dus aux floats
/// à 6 371 km de l'origine (équivalent d'une « origine flottante » recalée à chaque image).
/// </summary>
public partial class Planet : Node3D
{
    [Export] public int MaxLevel = 15;          // niveau 15 ≈ 10 m entre sommets (données 30 m)
    [Export] public int Resolution = 32;        // quads par côté de chunk
    // Réglés par le niveau de qualité graphique (GraphicsQuality) :
    public int HdMaxLevel = 19;                 // zones haute définition (Suisse 0,5 m) : niveau 19 ≈ 0,6 m
    public int SeaMaxLevel = 17;                // en mer, près de la surface : niveau 17 ≈ 2,5 m (vagues)
    public double SplitFactor = 1.6;            // plus grand = plus de détail, plus de chunks
    public int MaxConcurrentBuilds = 6;

    public FlyCamera Camera;
    public int VisibleChunks { get; private set; }
    public int TotalChunks { get; private set; }
    public int DeepestLevel { get; private set; }
    public int PendingBuilds => _inFlight;
    public bool Idle => _inFlight == 0 && _done.IsEmpty && !_wantedMore;

    QuadNode[] _roots;
    ShaderMaterial _material;


    ImageTexture _snowTex;
    Image _snowImg;

    /// <summary>Part du sol couverte de neige (0 à 1) à cet endroit et cette altitude : même calcul que le shader
    /// du terrain (weather_snow), pour que l'herbe disparaisse sous la même neige.</summary>
    public float SnowCover(double lat, double lon, double h)
    {
        var img = _snowImg;
        if (img == null) return 0;
        int x = Math.Clamp((int)Math.Floor(((lon % 360) + 360) % 360 + 0.5) % 360, 0, img.GetWidth() - 1);
        int y = Math.Clamp((int)Math.Floor(90 - lat + 0.5), 0, img.GetHeight() - 1);
        var sm = img.GetPixel(x, y);
        double dh = h - sm.G;
        double t = sm.B - 0.0065 * dh;
        double depth = sm.R * Math.Exp(Math.Clamp(dh / 600, -6, 3));
        static double Smooth(double a, double b, double v) { double k = Math.Clamp((v - a) / (b - a), 0, 1); return k * k * (3 - 2 * k); }
        return (float)(Smooth(0.005, 0.06, depth) * (1 - Smooth(274, 277.5, t)));
    }
    DateTime _snowUpdated = DateTime.MinValue;
    DateTime _snowRun;

    /// <summary>Neige réelle (GFS) : met à jour la carte de neige du shader, au plus toutes les 2 minutes.</summary>
    public void UpdateSnow(Weather weather, DateTime utc)
    {
        if (_material == null || weather == null) return;
        if ((DateTime.UtcNow - _snowUpdated).TotalSeconds < 120 && weather.RunTime == _snowRun) return;
        _snowUpdated = DateTime.UtcNow;
        _snowRun = weather.RunTime;
        var img = weather.SnowMap(utc);
        _snowImg = img;
        _material.SetShaderParameter("snow_enabled", img != null ? 1.0f : 0.0f);
        if (img == null) return;
        if (_snowTex == null) { _snowTex = ImageTexture.CreateFromImage(img); _material.SetShaderParameter("snow_map", _snowTex); }
        else _snowTex.Update(img);
    }

    static readonly string[] GroundLayers = { "grass", "forest", "field", "urban", "dirt", "rock", "snow" };

    static Texture2DArray LoadGroundLayers()
    {
        var images = new Godot.Collections.Array<Image>();
        foreach (var n in GroundLayers)
        {
            var img = GD.Load<Texture2D>($"res://data/textures/{n}.png").GetImage();
            if (img.IsCompressed()) img.Decompress();
            img.Convert(Image.Format.Rgb8);
            img.GenerateMipmaps();
            images.Add(img);
        }
        var arr = new Texture2DArray();
        arr.CreateFromImages(images);
        return arr;
    }
    /// <summary>Direction du Soleil (repère monde), fournie par l'atmosphère, pour allumer les villes côté nuit.</summary>
    public Vector3 SunDirWorld = Vector3.Up;
    int _inFlight;
    bool _wantedMore;
    readonly ConcurrentQueue<(QuadNode node, ChunkData data)> _done = new();

    sealed class QuadNode
    {
        public int Face, Level;
        public double U0, V0, Size;
        public QuadNode[] Children;
        public MeshInstance3D Mesh;
        public Vec3d Center;    // origine du maillage (niveau de la mer)
        public Vec3d CenterDir;
        public double MeanHeight; // altitude moyenne, héritée du parent tant que le chunk n'est pas construit
        public Vec3d LodCenter => CenterDir * (Geo.Radius + MeanHeight);
        public double Edge; // longueur approximative d'un côté (m)
        public bool Requested, Ready, Dead;
        public bool Incomplete; // construit avec des données provisoires (téléchargement en cours)
        public bool? Hd;        // le chunk touche une zone haute définition (calculé au besoin)
        public bool Sea;        // le chunk touche la mer
        public int BuiltVersion;
        public int Fallbacks = int.MaxValue; // points du maillage affiché pris sur une donnée de secours (voir ChunkData)
        public int Retries;      // reconstructions de suite restées provisoires
        public double NotBefore; // pas de nouvelle reconstruction avant (s, horloge du jeu)
    }

    public override void _Ready()
    {
        _material = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/terrain.gdshader") };
        _material.SetShaderParameter("layers", LoadGroundLayers());
        var lights = NightLights.CreateTexture();
        if (lights != null) _material.SetShaderParameter("city_lights", lights);
        _roots = new QuadNode[6];
        for (int f = 0; f < 6; f++) _roots[f] = MakeNode(f, 0, -1, -1, 2);
        ReadQuality(GraphicsQuality.Current);
        GraphicsQuality.Changed += OnQualityChanged;
    }

    public override void _ExitTree() => GraphicsQuality.Changed -= OnQualityChanged;

    void ReadQuality(QualityProfile q)
    {
        SplitFactor = q.TerrainSplitFactor;
        HdMaxLevel = Math.Max(MaxLevel, q.TerrainHdMaxLevel);
        SeaMaxLevel = Math.Max(MaxLevel, q.WaterMaxLevel);
        MaxConcurrentBuilds = q.TerrainBuildThreads;
        HeightProvider.ApplyQuality(q);
        // Textures du sol : 0 = aucune ; 1 = une échelle, proches ; 2 = deux échelles, 800 m ; 3 = deux échelles, 1,5 km ;
        // 4 = deux échelles, 2,5 km + nuances à grande échelle sur tout le sol
        int gd = Math.Clamp(q.GroundDetail, 0, 4);
        _material.SetShaderParameter("detail_strength", gd > 0 ? 1.0f : 0.0f);
        _material.SetShaderParameter("detail_distance", gd switch { 1 => 300f, 2 => 800f, 3 => 1500f, _ => 2500f });
        _material.SetShaderParameter("two_scales", gd >= 2);
        _material.SetShaderParameter("macro_variation", gd >= 4);
    }

    void OnQualityChanged(QualityProfile q, QualityProfile old)
    {
        ReadQuality(q);
        // Végétation changée : les chunks visibles sont reconstruits peu à peu (même mécanisme que l'arrivée de données fines).
        bool vegetation = q.VegetationDensity != old.VegetationDensity || q.VegetationMaxSpacing != old.VegetationMaxSpacing;
        var terrainShadow = GraphicsQuality.ShadowFor(ShadowCaster.Terrain);
        var treeShadow = GraphicsQuality.ShadowFor(ShadowCaster.Trees);
        void Walk(QuadNode n)
        {
            if (n.Mesh != null)
            {
                n.Mesh.CastShadow = terrainShadow;
                foreach (var c in n.Mesh.GetChildren()) if (c is MultiMeshInstance3D mm) mm.CastShadow = treeShadow;
                if (vegetation && n.Ready) { n.Incomplete = true; n.BuiltVersion = -1; }
            }
            if (n.Children != null) foreach (var c in n.Children) Walk(c);
        }
        foreach (var r in _roots) Walk(r);
    }

    QuadNode MakeNode(int face, int level, double u0, double v0, double size, double meanHeight = 0)
    {
        var dir = Geo.CubeToSphere(face, u0 + size / 2, v0 + size / 2);
        return new QuadNode
        {
            Face = face, Level = level, U0 = u0, V0 = v0, Size = size,
            Center = dir * Geo.Radius, CenterDir = dir, MeanHeight = meanHeight,
            Edge = size / 2 * Math.PI / 2 * Geo.Radius,
        };
    }

    double _time;
    readonly System.Collections.Generic.List<(QuadNode node, double priority)> _wanted = new();

    public override void _Process(double delta)
    {
        if (Camera == null) return;
        _time += delta;
        // Intègre les chunks terminés (au plus N par image pour éviter les à-coups)
        for (int k = 0; k < 24 && _done.TryDequeue(out var item); k++)
        {
            _inFlight--;
            if (item.node.Dead) continue;
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = item.data.Vertices;
            arrays[(int)Mesh.ArrayType.Normal] = item.data.Normals;
            arrays[(int)Mesh.ArrayType.Color] = item.data.Colors;
            arrays[(int)Mesh.ArrayType.TexUV] = item.data.RockSnow;
            arrays[(int)Mesh.ArrayType.Custom0] = item.data.Layers;
            arrays[(int)Mesh.ArrayType.Custom1] = item.data.TexOffset;
            arrays[(int)Mesh.ArrayType.Custom2] = item.data.Geo3;
            arrays[(int)Mesh.ArrayType.Index] = item.data.Indices;
            var mesh = new ArrayMesh();
            var fmt = (Mesh.ArrayFormat)(((long)Mesh.ArrayCustomFormat.RgbaFloat << (int)Mesh.ArrayFormat.FormatCustom0Shift)
                | ((long)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom1Shift)
                | ((long)Mesh.ArrayCustomFormat.RgbFloat << (int)Mesh.ArrayFormat.FormatCustom2Shift));
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays, null, null, fmt);
            item.node.Requested = false;
            item.node.BuiltVersion = item.data.DataVersion;
            if (item.data.Incomplete)
            {
                // Toujours provisoire : on espace les essais (sinon les mêmes chunks lointains occupent tous les fils).
                item.node.Retries++;
                item.node.NotBefore = _time + Math.Min(0.15 * (1 << Math.Min(item.node.Retries, 6)), 6);
            }
            else item.node.Retries = 0;
            if (item.node.Mesh != null && item.data.Fallbacks > item.node.Fallbacks)
            {
                // Reconstruction moins bonne que le maillage affiché (des données ont été libérées entre-temps) :
                // on garde l'ancien plutôt que de faire réapparaître le relief de secours.
                item.node.Incomplete = true;
                continue;
            }
            item.node.Incomplete = item.data.Incomplete;
            item.node.Fallbacks = item.data.Fallbacks;
            if (item.node.Mesh != null)
            {
                // Reconstruction (données fines arrivées) : on remplace le maillage sur place.
                item.node.Mesh.Mesh = mesh;
                item.node.Sea = item.data.HasSea;
                Vegetation.Attach(item.node.Mesh, item.data);
                Ocean.Attach(item.node.Mesh, item.data);
                continue;
            }
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = _material, Visible = false };
            mi.CastShadow = GraphicsQuality.ShadowFor(ShadowCaster.Terrain);
            AddChild(mi);
            Vegetation.Attach(mi, item.data);
            Ocean.Attach(mi, item.data);
            item.node.Sea = item.data.HasSea;
            item.node.Mesh = mi;
            item.node.MeanHeight = item.data.MeanHeight;
            item.node.Ready = true;
        }

        VisibleChunks = 0;
        TotalChunks = 0;
        DeepestLevel = 0;
        _wantedMore = false;
        var cam = Camera.PosD;
        // Le repère planète est tourné pour que la verticale du joueur soit l'axe Y de Godot
        // (ciel, brouillard et physique de Godot supposent Y = haut).
        Basis = Camera.WorldRotation;
        double h = Math.Max(0, cam.Length() - Geo.Radius);
        // Lumières des villes : une case Black Marble fait ~5 km. Près du sol elle ferait un halo
        // uniforme sur tout le quartier ; on l'atténue (les fenêtres des bâtiments prennent le relais).
        _material.SetShaderParameter("sun_dir", SunDirWorld);
        _material.SetShaderParameter("planet_center", Basis * (-cam).ToVector3());
        _material.SetShaderParameter("planet_basis_inv", Basis.Inverse());
        _material.SetShaderParameter("lights_strength", (float)Math.Clamp((h - 200) / 4000, 0.12, 1.0));
        _camAlt = cam.Length() - Geo.Radius;
        _horizon = Math.Sqrt(h * (2 * Geo.Radius + h)) + 340_000; // + horizon d'un sommet de 9 km
        _wanted.Clear();
        foreach (var r in _roots) Update(r, cam);
        IssueRequests(cam);
    }

    /// <summary>
    /// Lance les constructions voulues, les plus proches du joueur d'abord. Avant, l'ordre de parcours de l'arbre
    /// décidait : des chunks lointains reconstruits en boucle pouvaient retarder longtemps le sol sous les pieds.
    /// Une reconstruction (le chunk a déjà un maillage provisoire) passe après une première construction.
    /// </summary>
    void IssueRequests(Vec3d cam)
    {
        if (_wanted.Count == 0) return;
        _wanted.Sort((a, b) => a.priority.CompareTo(b.priority));
        foreach (var (node, _) in _wanted)
        {
            if (_inFlight >= MaxConcurrentBuilds) { _wantedMore = true; break; }
            Request(node);
        }
    }

    void Want(QuadNode n, Vec3d cam)
    {
        if (n.Requested || n.Dead) return;
        double d = Math.Max(0, (cam - n.LodCenter).Length() - n.Edge * 0.7);
        _wanted.Add((n, n.Ready ? d * 3 + 50 : d));
    }

    void Request(QuadNode n)
    {
        if (n.Requested) return;
        if (_inFlight >= MaxConcurrentBuilds) { _wantedMore = true; return; }
        n.Requested = true;
        _inFlight++;
        int face = n.Face; double u0 = n.U0, v0 = n.V0, size = n.Size;
        int res = Resolution;
        Task.Run(() =>
        {
            ChunkData data;
            try { data = ChunkBuilder.Build(face, u0, v0, size, res); }
            catch (Exception e) { GD.PrintErr(e.ToString()); data = null; }
            if (data != null) _done.Enqueue((n, data));
            else _done.Enqueue((new QuadNode { Dead = true }, null));
        });
    }

    double _horizon;
    double _camAlt;

    bool WantsSplit(QuadNode n, Vec3d cam)
    {
        if (n.Level >= HdMaxLevel) return false;
        if (n.Level >= MaxLevel && !(n.Hd ??= TouchesHd(n)) && !(n.Sea && n.Level < SeaMaxLevel && _camAlt < 300)) return false;
        // Distance au relief réel et non au niveau de la mer : sinon, à 3 000 m d'altitude,
        // le joueur serait « loin » de tous les chunks et le relief resterait grossier sous ses pieds.
        double dist = (cam - n.LodCenter).Length() - n.Edge * 0.75;
        if (dist > _horizon) return false; // derrière l'horizon : inutile de détailler
        return dist < SplitFactor * n.Edge;
    }

    /// <summary>Vrai si un des coins ou le centre du chunk est couvert par des données haute définition.</summary>
    static bool TouchesHd(QuadNode n)
    {
        for (int j = 0; j <= 2; j++)
        for (int i = 0; i <= 2; i++)
        {
            Geo.ToLatLon(Geo.CubeToSphere(n.Face, n.U0 + n.Size * i / 2, n.V0 + n.Size * j / 2), out double lat, out double lon);
            if (SwissAlti3D.Covers(lat, lon)) return true;
        }
        return false;
    }

    void Show(QuadNode n, Vec3d cam, bool visible)
    {
        if (n.Mesh == null) return;
        n.Mesh.Visible = visible;
        if (visible)
        {
            n.Mesh.Position = (n.Center - cam).ToVector3();
            VisibleChunks++;
            // Des données plus fines sont arrivées depuis la construction : on reconstruit.
            if (n.Incomplete && n.BuiltVersion != StreamedRaster.DataVersion && _time >= n.NotBefore) Want(n, cam);
            if (n.Level > DeepestLevel) DeepestLevel = n.Level;
        }
    }

    void Update(QuadNode n, Vec3d cam)
    {
        TotalChunks++;
        if (!n.Ready) Want(n, cam);

        if (WantsSplit(n, cam))
        {
            if (n.Children == null)
            {
                double h = n.Size / 2;
                n.Children = new[]
                {
                    MakeNode(n.Face, n.Level + 1, n.U0, n.V0, h, n.MeanHeight),
                    MakeNode(n.Face, n.Level + 1, n.U0 + h, n.V0, h, n.MeanHeight),
                    MakeNode(n.Face, n.Level + 1, n.U0, n.V0 + h, h, n.MeanHeight),
                    MakeNode(n.Face, n.Level + 1, n.U0 + h, n.V0 + h, h, n.MeanHeight),
                };
            }
            bool allReady = true;
            foreach (var c in n.Children) { if (!c.Ready) { Want(c, cam); allReady = false; } }
            if (allReady)
            {
                Show(n, cam, false);
                foreach (var c in n.Children) Update(c, cam);
            }
            else
            {
                Show(n, cam, true);
                foreach (var c in n.Children) HideRecursive(c);
            }
        }
        else
        {
            if (n.Children != null && n.Ready)
            {
                foreach (var c in n.Children) Destroy(c);
                n.Children = null;
            }
            if (n.Ready || n.Children == null) Show(n, cam, true);
            else foreach (var c in n.Children) Update(c, cam); // garder les enfants tant que le parent n'est pas prêt
        }
    }

    void HideRecursive(QuadNode n)
    {
        if (n.Mesh != null) n.Mesh.Visible = false;
        if (n.Children != null) foreach (var c in n.Children) HideRecursive(c);
    }

    void Destroy(QuadNode n)
    {
        n.Dead = true;
        if (n.Mesh != null) { n.Mesh.QueueFree(); n.Mesh = null; }
        if (n.Children != null) foreach (var c in n.Children) Destroy(c);
        n.Children = null;
    }
}
