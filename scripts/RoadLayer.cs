using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Routes, chemins et voies ferrées du monde entier (OpenStreetMap via Overture Maps), chargés par tuiles
/// autour du joueur et posés sur le relief :
/// - tuiles de zoom 14 (~2,4 km à l'équateur) jusqu'à ~3 km : tous les tronçons, en rubans de leur largeur ;
/// - tuiles de zoom 12 jusqu'à ~25 km (plus en altitude) : lignes fines, masquées là où le détail est chargé.
/// Les tuiles sont téléchargées à la demande (cache disque), puis reconstruites quand le relief 30 m arrive.
/// Touche T : afficher / masquer.
/// </summary>
public partial class RoadLayer : Node3D
{
    public FlyCamera Camera;
    public Planet Planet;
    public bool ShowLayer = true;

    const int DetailZoom = 14, OverviewZoom = 12;
    const int MaxBuilds = 3;

    sealed class Tile
    {
        public int Z, X, Y;
        public Vec3d Center;          // direction du centre (unitaire)
        public double HalfSize;       // demi-diagonale approximative (m)
        public bool Loading, Ready, Dead, Incomplete;
        public int BuiltVersion;
        public byte[] Mvt;            // tuile brute gardée pour les reconstructions
        public Vec3d Origin;
        public MeshInstance3D Mesh;           // zoom 14
        public MeshInstance3D[] Sub;          // zoom 12 : 16 sous-tuiles
        public double LastBuild;
        public double Spacing = 10;   // précision du relief utilisée (m)
        public int Segments;
    }

    readonly Dictionary<(int, int, int), Tile> _tiles = new();
    readonly ConcurrentQueue<(Tile tile, RoadMeshData data)> _done = new();
    Task<PmTilesArchive> _archive;
    ShaderMaterial _ribbonMat, _lineMat;
    int _building;
    double _clock, _nextScan;

    public int LoadedTiles { get; private set; }
    public int Segments { get; private set; }
    /// <summary>Plus rien à charger ni à construire (pour les captures automatiques).</summary>
    public bool Idle => _building == 0 && _done.IsEmpty && !_wanting;
    public string Status => _archive == null ? "" : !_archive.IsCompleted ? "connexion…" : _archive.Result == null ? "hors ligne" : $"{LoadedTiles} tuiles, {Segments:N0} tronçons";
    bool _wanting;

    public override void _Ready()
    {
        _ribbonMat = new ShaderMaterial { Shader = new Shader { Code = RibbonShader } };
        _lineMat = new ShaderMaterial { Shader = new Shader { Code = LineShader } };
        _archive = Overture.OpenAsync("transportation");
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode == Key.T) ShowLayer = !ShowLayer;
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        while (_done.TryDequeue(out var item)) Install(item.tile, item.data);

        var cam = Camera.PosD;
        var player = Camera.PlayerPos;
        double agl = Math.Max(0, Camera.Altitude - Camera.GroundHeight);

        if (_clock >= _nextScan && _archive.IsCompletedSuccessfully && _archive.Result != null)
        {
            _nextScan = _clock + 0.25;
            Scan(player, agl);
        }

        // Placement relatif à la caméra et masquage des sous-tuiles lointaines couvertes par du détail
        LoadedTiles = 0; Segments = 0;
        foreach (var t in _tiles.Values)
        {
            if (!t.Ready) continue;
            LoadedTiles++;
            Segments += t.Segments;
            var pos = (t.Origin - cam).ToVector3();
            if (t.Mesh != null) { t.Mesh.Visible = ShowLayer; t.Mesh.Position = pos; }
            if (t.Sub != null)
                for (int i = 0; i < 16; i++)
                {
                    var m = t.Sub[i];
                    if (m == null) continue;
                    bool covered = _tiles.TryGetValue((DetailZoom, 4 * t.X + i % 4, 4 * t.Y + i / 4), out var d) && d.Ready;
                    m.Visible = ShowLayer && !covered;
                    m.Position = pos;
                }
            // Relief plus fin arrivé depuis la construction : on reconstruit (au plus une fois toutes les 2 s)
            if (t.Loading || _building >= MaxBuilds) continue;
            if (t.Incomplete && t.BuiltVersion != StreamedRaster.DataVersion && _clock - t.LastBuild > 2)
                Build(t);
            // Zone haute définition : le relief sous les routes s'affine (ou redevient grossier) avec la distance
            else if (t.Z == DetailZoom && SpacingFor(t, player, agl) != t.Spacing && _clock - t.LastBuild > 1)
                Build(t);
        }
        _lineMat.SetShaderParameter("opacity", (float)Math.Clamp(0.75 - agl / 80_000, 0.2, 0.6));
    }

    /// <summary>Choisit les tuiles voulues autour du joueur, lance les chargements, libère les tuiles lointaines.</summary>
    void Scan(Vec3d player, double agl)
    {
        Geo.ToLatLon(player.Normalized(), out double lat, out double lon);
        var dir = player.Normalized();
        double scale = GraphicsQuality.Current.RoadsRadiusScale; // niveau de qualité graphique
        double rDetail = agl < 6_000 ? Math.Clamp(2_500 + agl * 0.8, 2_500, 5_000) * scale : 0;
        double rOverview = agl < 80_000 ? Math.Clamp(20_000 + agl * 1.5, 20_000, 60_000) * scale : 0;

        var wanted = new HashSet<(int, int, int)>();
        Want(DetailZoom, rDetail);
        Want(OverviewZoom, rOverview);

        void Want(int z, double radius)
        {
            if (radius <= 0) return;
            double dLat = radius / 111_320, dLon = radius / (111_320 * Math.Max(Math.Cos(lat * Math.PI / 180), 0.05));
            PmTilesArchive.TileXY(lat + dLat, lon - dLon, z, out int x0, out int y0);
            PmTilesArchive.TileXY(lat - dLat, lon + dLon, z, out int x1, out int y1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    var key = (z, x, y);
                    if (!_tiles.TryGetValue(key, out var t))
                    {
                        PmTilesArchive.TileToLatLon(z, x, y, 0.5, 0.5, out double cla, out double clo);
                        PmTilesArchive.TileToLatLon(z, x, y, 0, 0, out double ala, out double alo);
                        var c = Geo.FromLatLon(cla, clo);
                        t = new Tile { Z = z, X = x, Y = y, Center = c, HalfSize = (Geo.FromLatLon(ala, alo) - c).Length() * Geo.Radius };
                    }
                    double d = (t.Center - dir).Length() * Geo.Radius - t.HalfSize;
                    if (d > radius) continue;
                    wanted.Add(key);
                    _tiles.TryAdd(key, t);
                }
        }

        // Libère ce qui n'est plus voulu (avec une marge : les tuiles ne clignotent pas en bordure)
        var remove = new List<(int, int, int)>();
        foreach (var (key, t) in _tiles)
        {
            if (wanted.Contains(key)) continue;
            double r = t.Z == DetailZoom ? rDetail : rOverview;
            double d = (t.Center - dir).Length() * Geo.Radius - t.HalfSize;
            if (d > r * 1.3 + 500 || !(t.Ready || t.Loading)) remove.Add(key);
        }
        foreach (var key in remove) { Free(_tiles[key]); _tiles.Remove(key); }

        // Lance les chargements, les plus proches d'abord
        _wanting = false;
        var todo = new List<Tile>();
        foreach (var key in wanted) { var t = _tiles[key]; if (!t.Ready && !t.Loading) todo.Add(t); }
        todo.Sort((a, b) => ((a.Center - dir).Length() - a.HalfSize / Geo.Radius * (a.Z == DetailZoom ? 4 : 1))
            .CompareTo((b.Center - dir).Length() - b.HalfSize / Geo.Radius * (b.Z == DetailZoom ? 4 : 1)));
        foreach (var t in todo)
        {
            if (_building >= MaxBuilds) { _wanting = true; break; }
            Build(t);
        }
    }

    /// <summary>Précision du relief voulue pour une tuile détaillée : la même que le terrain affiché à cette distance
    /// (le terrain se subdivise jusqu'à ~0,6 m près du joueur en zone 0,5 m, ~d/50 plus loin).</summary>
    static double SpacingFor(Tile t, Vec3d player, double agl)
    {
        PmTilesArchive.TileToLatLon(t.Z, t.X, t.Y, 0.5, 0.5, out double lat, out double lon);
        if (!SwissAlti3D.Covers(lat, lon)) return 10;
        double flat = Math.Max(0, (t.Center - player.Normalized()).Length() * Geo.Radius - t.HalfSize);
        double d = Math.Sqrt(flat * flat + agl * agl);
        double Want(double dist) => dist < 600 ? 1 : dist < 2_000 ? 4 : 10;
        double w = Want(d);
        // hystérésis : on ne repasse au grossier qu'avec 30 % de marge (pas de reconstructions en boucle en bordure)
        if (w > t.Spacing && Want(d / 1.3) <= t.Spacing) return t.Spacing;
        return w;
    }

    void Build(Tile t)
    {
        t.Loading = true;
        _building++;
        t.LastBuild = _clock;
        var archive = _archive.Result;
        if (t.Z == DetailZoom) t.Spacing = SpacingFor(t, Camera.PlayerPos, Math.Max(0, Camera.Altitude - Camera.GroundHeight));
        double spacing = t.Spacing;
        Task.Run(async () =>
        {
            RoadMeshData data = null;
            try
            {
                t.Mvt ??= await archive.GetTileAsync(t.Z, t.X, t.Y) ?? Array.Empty<byte>();
                var layers = Mvt.Decode(t.Mvt, new HashSet<string> { "segment" }, RoadBuilder.Keys);
                var segs = layers.TryGetValue("segment", out var s) ? s : new List<Mvt.Feature>();
                // tuiles voisines (cache disque) pour recomposer les tronçons coupés qui ont des ponts ou des tunnels
                var near = new Dictionary<(int, int), List<Mvt.Feature>>();
                List<Mvt.Feature> Neighbour(int nx, int ny)
                {
                    if (near.TryGetValue((nx, ny), out var l)) return l;
                    try
                    {
                        var b = archive.GetTileAsync(DetailZoom, nx, ny).GetAwaiter().GetResult();
                        l = b == null ? new List<Mvt.Feature>()
                            : Mvt.Decode(b, new HashSet<string> { "segment" }, RoadBuilder.Keys).GetValueOrDefault("segment") ?? new List<Mvt.Feature>();
                    }
                    catch (Exception) { l = null; } // hors ligne : le tronçon garde ses règles « entières » seulement
                    near[(nx, ny)] = l;
                    return l;
                }
                data = t.Z == DetailZoom ? RoadBuilder.BuildDetailed(segs, t.Z, t.X, t.Y, spacing, Neighbour) : RoadBuilder.BuildOverview(segs, t.Z, t.X, t.Y);
            }
            catch (Exception e) { GD.PrintErr($"[RoadLayer] tuile {t.Z}/{t.X}/{t.Y} : {e.Message}"); }
            _done.Enqueue((t, data));
        });
    }

    void Install(Tile t, RoadMeshData data)
    {
        _building--;
        t.Loading = false;
        if (t.Dead) return;
        if (data == null) { t.Mvt = null; return; } // réessai au prochain passage
        t.Origin = data.Origin;
        t.Incomplete = data.Incomplete;
        t.BuiltVersion = data.DataVersion;
        t.Segments = data.Segments;
        if (t.Z == DetailZoom)
        {
            ArrayMesh mesh = null;
            if (data.Indices.Length > 0)
            {
                var arrays = new Godot.Collections.Array();
                arrays.Resize((int)Mesh.ArrayType.Max);
                arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
                arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
                arrays[(int)Mesh.ArrayType.Color] = data.Colors;
                arrays[(int)Mesh.ArrayType.TexUV] = data.UVs;
                arrays[(int)Mesh.ArrayType.TexUV2] = data.UV2s;
                arrays[(int)Mesh.ArrayType.Index] = data.Indices;
                mesh = new ArrayMesh();
                mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
            }
            if (t.Mesh == null)
            {
                t.Mesh = new MeshInstance3D { MaterialOverride = _ribbonMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                Planet.AddChild(t.Mesh);
            }
            t.Mesh.Mesh = mesh;
            Obstacles.Set(t.Mesh, data.Clearings); // retire les arbres sur les routes, chemins et voies ferrées
        }
        else
        {
            t.Sub ??= new MeshInstance3D[16];
            for (int i = 0; i < 16; i++)
            {
                ArrayMesh mesh = null;
                if (data.SubLines[i].Length > 0)
                {
                    var arrays = new Godot.Collections.Array();
                    arrays.Resize((int)Mesh.ArrayType.Max);
                    arrays[(int)Mesh.ArrayType.Vertex] = data.SubLines[i];
                    arrays[(int)Mesh.ArrayType.Color] = data.SubColors[i];
                    mesh = new ArrayMesh();
                    mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Lines, arrays);
                }
                if (mesh == null) { if (t.Sub[i] != null) { t.Sub[i].QueueFree(); t.Sub[i] = null; } continue; }
                if (t.Sub[i] == null)
                {
                    t.Sub[i] = new MeshInstance3D { MaterialOverride = _lineMat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
                    Planet.AddChild(t.Sub[i]);
                }
                t.Sub[i].Mesh = mesh;
            }
        }
        t.Ready = true;
    }

    void Free(Tile t)
    {
        t.Dead = true;
        t.Mesh?.QueueFree();
        if (t.Sub != null) foreach (var m in t.Sub) m?.QueueFree();
        t.Mesh = null; t.Sub = null; t.Mvt = null;
    }

    // Rubans éclairés par le soleil ; chaque sommet est rapproché de la caméra le long du rayon de vue
    // (ne change pas l'image, seulement la profondeur) pour rester au-dessus d'un relief affiché plus grossièrement.
    // Revêtements procéduraux (pas de texture à charger) : asphalte avec lignes blanches, pavés, gravier avec bande
    // d'herbe, terre, ballast de voie ferrée avec traverses, rails en acier. Les détails fins s'estompent avec la
    // distance (pas de scintillement). Chaque sommet est rapproché de la caméra le long du rayon de vue
    // (ne change pas l'image, seulement la profondeur) pour rester au-dessus d'un relief affiché plus grossièrement.
    const string RibbonShader = @"
shader_type spatial;
render_mode cull_disabled, depth_draw_opaque;
void vertex() {
    vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    float d = length(v.xyz);
    // décalage borné à 20 % de la distance : un sommet ne doit jamais arriver sur la caméra
    float pull = min(0.003 * d + 0.25, 0.2 * d);
    v.xyz *= 1.0 - pull / max(d, 0.001);
    POSITION = PROJECTION_MATRIX * v;
}
float hash(vec2 p) { return fract(sin(dot(p, vec2(127.1, 311.7))) * 43758.5453); }
float vnoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(hash(i), hash(i + vec2(1.0, 0.0)), f.x), mix(hash(i + vec2(0.0, 1.0)), hash(i + vec2(1.0, 1.0)), f.x), f.y);
}
// bande de demi-largeur hw centrée en c, antialiasée
float band(float x, float c, float hw, float aa) { return 1.0 - smoothstep(hw - aa, hw + aa, abs(x - c)); }
void fragment() {
    int kind = int(floor(COLOR.a * 10.0 + 0.5));
    float w = UV2.x;
    float xm = UV.x * w;   // mètres depuis le bord gauche
    float ym = UV.y;       // mètres le long de la voie
    vec2 pm = vec2(xm, ym);
    float aa = max(max(fwidth(xm), fwidth(ym)), 0.001);
    float detail = 1.0 - smoothstep(0.06, 0.35, aa);   // 1 = de près, 0 = au loin
    vec3 col = pow(COLOR.rgb, vec3(2.2));
    float n = vnoise(pm * 2.0) * 0.5 + vnoise(pm * 9.0) * 0.5;
    float rough = 0.92;
    float metal = 0.0;
    if (kind == 0 || kind == 1) {
        // asphalte : grain fin et taches ; lignes blanches sur les grandes routes
        col *= mix(1.0, 0.86 + 0.28 * n, detail);
        if (kind == 0 && w > 4.5) {
            float edge = max(band(xm, 0.35, 0.075, aa), band(xm, w - 0.35, 0.075, aa));
            float dash = 1.0 - smoothstep(0.30, 0.34, fract(ym / 9.0));   // trait 3 m, espace 6 m
            float mid = band(xm, w * 0.5, 0.07, aa) * dash;
            col = mix(col, vec3(0.80), max(edge, mid) * mix(0.35, 1.0, detail));
        }
    } else if (kind == 2) {
        // pavés / dalles : joints de 0,5 m
        vec2 g = fract(pm / vec2(0.5, 0.25) + vec2(0.0, step(0.5, fract(xm))));
        float joint = 1.0 - min(smoothstep(0.0, 0.08, min(g.x, 1.0 - g.x)), smoothstep(0.0, 0.12, min(g.y, 1.0 - g.y)));
        col *= mix(1.0, (0.9 + 0.2 * hash(floor(pm / vec2(0.5, 0.25)))) * (1.0 - 0.35 * joint), detail);
    } else if (kind == 3) {
        // chemin de gravier : deux traces de roues, herbe au milieu
        col *= mix(1.0, 0.8 + 0.4 * vnoise(pm * 14.0), detail);
        float grass = band(xm, w * 0.5, w * 0.12, max(aa, 0.15)) * (0.6 + 0.4 * vnoise(pm * 1.5));
        col = mix(col, vec3(0.10, 0.16, 0.05), grass * mix(0.5, 1.0, detail));
    } else if (kind == 4) {
        // sentier en terre, bords irréguliers
        col *= mix(1.0, 0.75 + 0.5 * n, detail);
    } else if (kind == 5) {
        // ballast : gravier gris-brun + traverses tous les 0,6 m
        float stones = vnoise(pm * 22.0) * 0.6 + vnoise(pm * 6.0) * 0.4;
        col *= mix(1.0, 0.75 + 0.5 * stones, detail);
        float half_len = min(1.3, w * 0.5 - 0.25);
        float across = 1.0 - smoothstep(half_len - aa, half_len + aa, abs(xm - w * 0.5));
        float sleeper = band(fract(ym / 0.6) * 0.6, 0.3, 0.12, aa) * across;
        vec3 wood = vec3(0.13, 0.10, 0.08) * (0.8 + 0.4 * vnoise(pm * vec2(3.0, 30.0)));
        // au loin, les traverses se fondent en une bande plus sombre au milieu de la voie
        col = mix(col, wood, mix(across * 0.3, sleeper, detail));
    } else {
        // rail : acier brillant sur le dessus, rouille sur les côtés
        float top = band(UV.x, 0.5, 0.22, fwidth(UV.x) + 0.001);
        col = mix(vec3(0.25, 0.13, 0.07), vec3(0.65, 0.65, 0.68), top);
        metal = 0.9 * top;
        rough = mix(0.8, 0.3, top);
    }
    ALBEDO = col;
    ROUGHNESS = rough;
    METALLIC = metal;
    SPECULAR = 0.3;
}";

    const string LineShader = @"
shader_type spatial;
render_mode unshaded, blend_mix, cull_disabled, depth_draw_never;
uniform float opacity = 0.8;
void vertex() {
    vec4 v = MODELVIEW_MATRIX * vec4(VERTEX, 1.0);
    float d = length(v.xyz);
    float pull = min(0.004 * d + 10.0, 0.2 * d);
    v.xyz *= 1.0 - pull / max(d, 0.001);
    POSITION = PROJECTION_MATRIX * v;
}
void fragment() {
    ALBEDO = COLOR.rgb;
    ALPHA = opacity;
}";
}
