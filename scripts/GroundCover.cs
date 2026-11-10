using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Détails au ras du sol autour du joueur : touffes d'herbe (balancées par le vent réel), rochers et cailloux.
/// Le sol est découpé en cases de 16 m (grille en latitude/longitude, positions déterministes : on retrouve les
/// mêmes cailloux en revenant). Les cases proches sont construites en arrière-plan, rendues par MultiMesh,
/// et oubliées quand on s'éloigne. Rien n'est construit au-dessus de 150 m du sol.
/// Les gros rochers sont solides (<see cref="Obstacles"/>).
/// </summary>
public partial class GroundCover : Node3D
{
    public FlyCamera Camera;
    public Atmosphere Atmosphere;

    /// <summary>Densité de l'herbe (0 = pas d'herbe, 1 = maximum).</summary>
    public float GrassDensity = 0.7f;
    /// <summary>Rayon (m) autour du joueur.</summary>
    public float Distance = 80f;

    const double Cell = 16;             // m
    const double MetersPerDegLat = 111_320;
    const double MaxHeightAboveGround = 150;

    sealed class Item
    {
        public Vec3d Origin;
        public MultiMeshInstance3D Grass, Rocks;
        public bool Ready, Dead;
        public int Version;
    }

    sealed class Built
    {
        public Vec3d Origin;
        public Transform3D[] Grass, Rocks;
        public Color[] GrassTint, RockTint;
        public List<Obstacles.Shape> Solid;
        public bool Incomplete;
    }

    readonly Dictionary<(int, int), Item> _cells = new();
    readonly ConcurrentQueue<((int, int) key, Item item, Built data)> _done = new();
    int _building;
    Mesh _tuft, _rock;
    ShaderMaterial _grassMat;
    StandardMaterial3D _rockMat;
    float _builtDensity = -1;

    public int Cells => _cells.Count;
    public int GrassInstances { get; private set; }

    public override void _ExitTree() => GraphicsQuality.Changed -= OnQuality;
    void OnQuality(QualityProfile q, QualityProfile old) { GrassDensity = q.GrassDensity; Distance = q.GrassDistance; }

    public override void _Ready()
    {
        OnQuality(GraphicsQuality.Current, null);
        GraphicsQuality.Changed += OnQuality;
        _grassMat = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/grass.gdshader") };
        _rockMat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, Roughness = 0.95f };
        _tuft = MakeTuft();
        var sphere = new SphereMesh { Radius = 0.5f, Height = 0.7f, RadialSegments = 7, Rings = 4 };
        var st = new SurfaceTool();
        st.AppendFrom(sphere, 0, new Transform3D(Basis.Identity, new Vector3(0, 0.2f, 0)));
        var rock = new ArrayMesh();
        st.Commit(rock);
        rock.SurfaceSetMaterial(0, _rockMat);
        _rock = rock;
    }

    /// <summary>Trois quads croisés de 1 m de haut (mis à l'échelle par instance), plus sombres au pied.</summary>
    Mesh MakeTuft()
    {
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        var dark = new Color(0.35f, 0.45f, 0.30f);
        var light = new Color(1.0f, 1.0f, 0.85f);
        for (int k = 0; k < 3; k++)
        {
            float a = k * Mathf.Pi / 3;
            var d = new Vector3(Mathf.Cos(a), 0, Mathf.Sin(a)) * 0.22f;
            var n = new Vector3(-Mathf.Sin(a), 0.6f, Mathf.Cos(a)).Normalized();
            Vector3 p0 = -d, p1 = d, p2 = d * 0.35f + Vector3.Up, p3 = -d * 0.35f + Vector3.Up;
            void V(Vector3 p, Color c) { st.SetNormal(n); st.SetColor(c); st.AddVertex(p); }
            V(p0, dark); V(p1, dark); V(p2, light);
            V(p0, dark); V(p2, light); V(p3, light);
        }
        var mesh = st.Commit();
        mesh.SurfaceSetMaterial(0, _grassMat);
        return mesh;
    }

    public override void _Process(double delta)
    {
        if (Camera == null) return;
        // Cases terminées
        for (int k = 0; k < 6 && _done.TryDequeue(out var r); k++)
        {
            _building--;
            if (r.item.Dead) continue;
            Install(r.item, r.data);
        }

        var player = Camera.PlayerPos;
        var up = player.Normalized();
        Geo.ToLatLon(up, out double lat, out double lon);
        double agl = player.Length() - Geo.Radius - HeightProvider.SurfaceHeight(lat, lon, 30);
        float radius = GrassDensity <= 0 || Distance <= 0 ? 0 : Distance;
        bool active = agl < MaxHeightAboveGround && radius > 0;

        // Vent réel : intensité et direction (approximation : direction dans le repère est/nord)
        if (Atmosphere != null)
        {
            var w = Atmosphere.Current;
            _grassMat.SetShaderParameter("wind_strength", Math.Clamp(w.Wind10Speed / 15f, 0, 1));
            float sp = Math.Max(w.Wind10Speed, 0.01f);
            _grassMat.SetShaderParameter("wind_dir", new Vector2(w.Wind10East / sp, -w.Wind10North / sp));
            _grassMat.SetShaderParameter("fade_distance", radius);
        }

        // Densité changée (qualité) : tout reconstruire
        if (_builtDensity != GrassDensity) { foreach (var c in _cells.Values) Free(c); _cells.Clear(); _builtDensity = GrassDensity; }

        var wanted = new HashSet<(int, int)>();
        if (active)
        {
            double dLat = Cell / MetersPerDegLat;
            int ci = (int)Math.Floor(lat / dLat);
            int n = (int)Math.Ceiling(radius / Cell) + 1;
            for (int i = ci - n; i <= ci + n; i++)
            {
                double rowLat = (i + 0.5) * dLat;
                double dLon = Cell / (MetersPerDegLat * Math.Max(0.05, Math.Cos(rowLat * Math.PI / 180)));
                int cj = (int)Math.Floor(lon / dLon);
                for (int j = cj - n; j <= cj + n; j++)
                {
                    double clat = rowLat, clon = (j + 0.5) * dLon;
                    double dist = (Geo.FromLatLon(clat, clon) * Geo.Radius - up * Geo.Radius).Length();
                    if (dist > radius + Cell * 0.75) continue;
                    wanted.Add((i, j));
                    if (!_cells.ContainsKey((i, j)) && _building < 4)
                    {
                        var item = new Item { Origin = Geo.FromLatLon(clat, clon) * Geo.Radius };
                        _cells[(i, j)] = item;
                        Build((i, j), item, dLat, dLon);
                    }
                }
            }
        }
        // Cases trop loin : oubliées
        var drop = new List<(int, int)>();
        foreach (var kv in _cells) if (!wanted.Contains(kv.Key)) drop.Add(kv.Key);
        foreach (var k in drop) { Free(_cells[k]); _cells.Remove(k); }

        // Placement relatif à la caméra (comme les chunks du terrain)
        var cam = Camera.PosD;
        foreach (var c in _cells.Values)
        {
            if (!c.Ready) continue;
            var pos = (c.Origin - cam).ToVector3();
            if (c.Grass != null) c.Grass.Position = pos;
            if (c.Rocks != null) c.Rocks.Position = pos;
            // relief plus fin arrivé depuis : on reconstruit
        }
    }

    void Build((int, int) key, Item item, double dLat, double dLon)
    {
        _building++;
        float density = GrassDensity;
        Task.Run(() =>
        {
            Built b = null;
            try { b = Scatter(key.Item1, key.Item2, dLat, dLon, item.Origin, density); }
            catch (Exception e) { GD.PrintErr("[GroundCover] " + e.Message); }
            _done.Enqueue((key, item, b ?? new Built { Grass = Array.Empty<Transform3D>(), Rocks = Array.Empty<Transform3D>() }));
        });
    }

    static uint Hash(int x, int y, int z)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)z * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }
    static float Rand(ref uint s) { s = s * 1664525u + 1013904223u; return (s >> 8) / 16777216f; }

    static Built Scatter(int ci, int cj, double dLat, double dLon, Vec3d origin, float density)
    {
        var grass = new List<Transform3D>(); var gt = new List<Color>();
        var rocks = new List<Transform3D>(); var rt = new List<Color>();
        var solid = new List<Obstacles.Shape>();
        bool incomplete = false;
        uint seed = Hash(ci, cj, 77);
        // Candidats : herbe tous les ~0,45 m au maximum (selon la densité), cailloux plus rares
        int grassCount = (int)(density * 1200);
        int stoneCount = 90;
        for (int k = 0; k < grassCount + stoneCount; k++)
        {
            bool isGrass = k < grassCount;
            double lat = (ci + Rand(ref seed)) * dLat, lon = (cj + Rand(ref seed)) * dLon;
            float r1 = Rand(ref seed), r2 = Rand(ref seed), r3 = Rand(ref seed), r4 = Rand(ref seed);
            byte cover = LandCover.Sample(lat, lon, 10, false, out _);
            if (cover is LandCover.Water or LandCover.BuiltUp or LandCover.SnowIce) continue;
            // densité selon le type de sol
            float pGrass = cover switch
            {
                LandCover.Grass or LandCover.Wetland => 1f,
                LandCover.Crop => 0.55f,
                LandCover.Shrub or LandCover.Moss => 0.6f,
                LandCover.Tree or LandCover.Mangrove => 0.35f,
                LandCover.Bare => 0.08f,
                _ => 0.5f,
            };
            float pStone = cover switch
            {
                LandCover.Bare => 1f,
                LandCover.Moss => 0.6f,
                LandCover.Shrub => 0.35f,
                LandCover.Grass => 0.12f,
                LandCover.Tree => 0.15f,
                LandCover.Crop => 0.08f,
                _ => 0.1f,
            };
            if (isGrass ? r1 > pGrass : r1 > pStone) continue;
            float h = HeightProvider.Sample(lat, lon, 1, out bool water, out bool pending);
            if (pending) incomplete = true;
            if (water) continue;
            var dir = Geo.FromLatLon(lat, lon);
            var p = dir * (Geo.Radius + h);
            // pas dans les bâtiments
            var up = dir.ToVector3();
            var any = Math.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right;
            var x = any.Cross(up).Normalized();
            var z = x.Cross(up);
            float a = r2 * Mathf.Tau;
            var xr = x * Mathf.Cos(a) + z * Mathf.Sin(a);
            var zr = xr.Cross(up);
            if (isGrass)
            {
                float height = 0.18f + r3 * 0.45f;
                float width = 0.8f + r4 * 0.6f;
                var basis = new Basis(xr * width, up * height, zr * width);
                grass.Add(new Transform3D(basis, (p - origin).ToVector3()));
                // teinte : vert de prairie, plus jaune en culture et en broussaille
                var tint = cover switch
                {
                    LandCover.Crop => new Color(0.30f, 0.28f, 0.10f),
                    LandCover.Shrub or LandCover.Moss or LandCover.Bare => new Color(0.24f, 0.24f, 0.11f),
                    LandCover.Tree => new Color(0.12f, 0.18f, 0.07f),
                    _ => new Color(0.16f, 0.26f, 0.07f),
                };
                float v = 0.8f + r3 * 0.4f;
                gt.Add(new Color(tint.R * v, tint.G * v, tint.B * v));
            }
            else
            {
                // surtout des cailloux, parfois un rocher (rarement un gros bloc)
                float size = r3 < 0.75f ? 0.06f + r4 * 0.18f : r3 < 0.97f ? 0.3f + r4 * 0.6f : 1.0f + r4 * 1.5f;
                float squash = 0.5f + Rand(ref seed) * 0.5f;
                var basis = new Basis(xr * size * (0.8f + r2 * 0.5f), up * size * squash, zr * size);
                var pos = p - dir * (size * 0.15); // un peu enterré
                rocks.Add(new Transform3D(basis, (pos - origin).ToVector3()));
                float g = 0.32f + r4 * 0.15f;
                rt.Add(new Color(g * 1.02f, g, g * 0.94f));
                if (size * squash * 0.55f > Obstacles.StepUp)
                    solid.Add(Obstacles.Shape.Circle(pos, size * 0.45, h - 0.2, h + size * squash * 0.55));
            }
        }
        return new Built { Origin = origin, Grass = grass.ToArray(), GrassTint = gt.ToArray(), Rocks = rocks.ToArray(), RockTint = rt.ToArray(), Solid = solid, Incomplete = incomplete };
    }

    void Install(Item item, Built b)
    {
        GrassInstances -= item.Grass?.Multimesh?.InstanceCount ?? 0;
        item.Grass?.QueueFree(); item.Rocks?.QueueFree();
        item.Grass = Make(_tuft, b.Grass, b.GrassTint);
        item.Rocks = Make(_rock, b.Rocks, b.RockTint);
        GrassInstances += b.Grass.Length;
        if (item.Rocks != null) Obstacles.Set(item.Rocks, b.Solid);
        item.Ready = true;
    }

    MultiMeshInstance3D Make(Mesh mesh, Transform3D[] xf, Color[] tints)
    {
        if (xf.Length == 0) return null;
        var mm = new MultiMesh { TransformFormat = MultiMesh.TransformFormatEnum.Transform3D, UseColors = true, Mesh = mesh, InstanceCount = xf.Length };
        for (int i = 0; i < xf.Length; i++) { mm.SetInstanceTransform(i, xf[i]); mm.SetInstanceColor(i, tints[i]); }
        var mi = new MultiMeshInstance3D { Multimesh = mm, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(mi);
        return mi;
    }

    void Free(Item item)
    {
        item.Dead = true;
        GrassInstances -= item.Grass?.Multimesh?.InstanceCount ?? 0;
        item.Grass?.QueueFree(); item.Rocks?.QueueFree();
        item.Grass = item.Rocks = null;
    }
}
