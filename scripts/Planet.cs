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
    public double SplitFactor = 1.6;            // plus grand = plus de détail, plus de chunks
    public int MaxConcurrentBuilds = 6;

    public FlyCamera Camera;
    public int VisibleChunks { get; private set; }
    public int TotalChunks { get; private set; }
    public int DeepestLevel { get; private set; }
    public int PendingBuilds => _inFlight;
    public bool Idle => _inFlight == 0 && _done.IsEmpty && !_wantedMore;

    QuadNode[] _roots;
    StandardMaterial3D _material;
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
        public int BuiltVersion;
    }

    public override void _Ready()
    {
        _material = new StandardMaterial3D
        {
            VertexColorUseAsAlbedo = true,
            VertexColorIsSrgb = true,
            Roughness = 0.95f,
        };
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
        MaxConcurrentBuilds = q.TerrainBuildThreads;
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

    public override void _Process(double delta)
    {
        if (Camera == null) return;
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
            arrays[(int)Mesh.ArrayType.Index] = item.data.Indices;
            var mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            item.node.Requested = false;
            item.node.Incomplete = item.data.Incomplete;
            item.node.BuiltVersion = item.data.DataVersion;
            if (item.node.Mesh != null)
            {
                // Reconstruction (données fines arrivées) : on remplace le maillage sur place.
                item.node.Mesh.Mesh = mesh;
                Vegetation.Attach(item.node.Mesh, item.data);
                continue;
            }
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = _material, Visible = false };
            mi.CastShadow = GraphicsQuality.ShadowFor(ShadowCaster.Terrain);
            AddChild(mi);
            Vegetation.Attach(mi, item.data);
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
        _horizon = Math.Sqrt(h * (2 * Geo.Radius + h)) + 340_000; // + horizon d'un sommet de 9 km
        foreach (var r in _roots) Update(r, cam);
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

    bool WantsSplit(QuadNode n, Vec3d cam)
    {
        if (n.Level >= HdMaxLevel) return false;
        if (n.Level >= MaxLevel && !(n.Hd ??= TouchesHd(n))) return false;
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
            if (n.Incomplete && n.BuiltVersion != StreamedRaster.DataVersion) Request(n);
            if (n.Level > DeepestLevel) DeepestLevel = n.Level;
        }
    }

    void Update(QuadNode n, Vec3d cam)
    {
        TotalChunks++;
        if (!n.Ready) Request(n);

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
            foreach (var c in n.Children) { if (!c.Ready) { Request(c); allReady = false; } }
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
