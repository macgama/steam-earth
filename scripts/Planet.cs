using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using Godot;

namespace SteamEarth;

/// <summary>
/// La Terre : 6 faces de cube-sphère, chacune subdivisée en quadtree selon la distance à la caméra.
/// Rendu « relatif à la caméra » : la caméra reste à l'origine Godot et chaque chunk est placé à
/// (centre − position caméra), calculé en double. On évite ainsi les tremblements dus aux floats
/// à 6 371 km de l'origine (équivalent d'une « origine flottante » recalée à chaque image).
/// </summary>
public partial class Planet : Node3D
{
    [Export] public int MaxLevel = 15;          // niveau 15 ≈ 10 m entre sommets
    [Export] public double SplitFactor = 1.6;   // plus grand = plus de détail, plus de chunks
    [Export] public int Resolution = 32;        // quads par côté de chunk
    [Export] public int MaxConcurrentBuilds = 6;

    public FlyCamera Camera;
    public int VisibleChunks { get; private set; }
    public int TotalChunks { get; private set; }
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
        public Vec3d Center;
        public double Edge; // longueur approximative d'un côté (m)
        public bool Requested, Ready, Dead;
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
    }

    QuadNode MakeNode(int face, int level, double u0, double v0, double size)
    {
        return new QuadNode
        {
            Face = face, Level = level, U0 = u0, V0 = v0, Size = size,
            Center = Geo.CubeToSphere(face, u0 + size / 2, v0 + size / 2) * Geo.Radius,
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
            var mi = new MeshInstance3D { Mesh = mesh, MaterialOverride = _material, Visible = false };
            mi.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            AddChild(mi);
            item.node.Mesh = mi;
            item.node.Ready = true;
        }

        VisibleChunks = 0;
        TotalChunks = 0;
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
        // Toujours utiliser les tuiles 30 m quand elles existent : mélanger 30 m et 10 km
        // entre deux niveaux voisins crée des marches visibles.
        bool detail = true;
        int res = Resolution;
        Task.Run(() =>
        {
            ChunkData data;
            try { data = ChunkBuilder.Build(face, u0, v0, size, res, detail); }
            catch (Exception e) { GD.PrintErr(e.ToString()); data = null; }
            if (data != null) _done.Enqueue((n, data));
            else _done.Enqueue((new QuadNode { Dead = true }, null));
        });
    }

    double _horizon;

    bool WantsSplit(QuadNode n, Vec3d cam)
    {
        if (n.Level >= MaxLevel) return false;
        double dist = (cam - n.Center).Length() - n.Edge * 0.75;
        if (dist > _horizon) return false; // derrière l'horizon : inutile de détailler
        return dist < SplitFactor * n.Edge;
    }

    void Show(QuadNode n, Vec3d cam, bool visible)
    {
        if (n.Mesh == null) return;
        n.Mesh.Visible = visible;
        if (visible)
        {
            n.Mesh.Position = (n.Center - cam).ToVector3();
            VisibleChunks++;
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
                    MakeNode(n.Face, n.Level + 1, n.U0, n.V0, h),
                    MakeNode(n.Face, n.Level + 1, n.U0 + h, n.V0, h),
                    MakeNode(n.Face, n.Level + 1, n.U0, n.V0 + h, h),
                    MakeNode(n.Face, n.Level + 1, n.U0 + h, n.V0 + h, h),
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
