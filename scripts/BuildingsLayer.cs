using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Bâtiments réels (Overture Maps « buildings » : OpenStreetMap, Microsoft, Google Open Buildings…), construits
/// seulement dans les villes et villages que le joueur choisit :
/// - en survolant le nom d'une localité (souris libre : pointeur ; en vol : viseur au centre de l'écran),
///   l'invite « Berne — on construit ? » apparaît : clic ou touche K pour construire, Maj + clic / Maj + K pour démolir ;
/// - en choisissant un pays dans le menu (Tab), sa capitale est construite.
/// La zone construite est un disque autour du centre, d'autant plus grand que la localité est peuplée.
/// Les villes construites sont gardées (user://cities.json) ; leurs tuiles (zoom 14) ne sont chargées
/// que lorsque le joueur est à moins de ~60 km.
/// </summary>
public partial class BuildingsLayer : Node3D
{
    public FlyCamera Camera;
    public Planet Planet;
    public PlacesLayer Places;
    public PoliticalLayer Political;
    public CountryMenu Menu;
    public float Night;                  // 0 = jour, 1 = nuit (fenêtres éclairées)

    public const string Attribution = "bâtiments : OSM, Microsoft, Google Open Buildings (CC BY 4.0), USGS";
    const int Zoom = 14, MaxBuilds = 3;
    const double ShowDistance = 40_000, LoadDistance = 60_000;

    /// <summary>Localité qu'on peut construire (étiquette affichée par une autre couche).</summary>
    public readonly record struct Target(string Name, double Lat, double Lon, double Radius, Label3D Label);

    public sealed class City
    {
        public string Name { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Radius { get; set; }
        internal Vec3d Dir;
        internal int Buildings, TilesTotal, TilesDone;
    }

    sealed class Tile
    {
        public int X, Y;
        public Vec3d Center; public double HalfSize;
        public bool Loading, Ready, Dead, Incomplete;
        public int BuiltVersion, Stamp, BuiltStamp;
        public byte[] Mvt;
        public Vec3d Origin;
        public MeshInstance3D Mesh;
        public double LastBuild;
        public readonly List<City> Cities = new();
        public readonly Dictionary<City, int> Counts = new();
    }

    readonly List<City> _cities = new();
    readonly Dictionary<(int, int), Tile> _tiles = new();
    readonly ConcurrentQueue<(Tile tile, BuildingMeshData data, City[] cities, int stamp)> _done = new();
    readonly List<Target> _targets = new();
    Task<PmTilesArchive> _archive;
    ShaderMaterial _mat;
    int _building;
    bool _wanting;
    double _clock, _nextScan;
    Target? _hover;
    PanelContainer _prompt;
    Label _promptText;

    public int Shown { get; private set; }
    public bool Idle => _building == 0 && _done.IsEmpty && !_wanting;
    public string Status
    {
        get
        {
            if (_cities.Count == 0) return "aucune ville construite (survoler un nom : « on construit ? »)";
            int pending = 0; foreach (var t in _tiles.Values) if (!t.Ready || t.Stamp != t.BuiltStamp) pending++;
            return $"{_cities.Count} {(_cities.Count > 1 ? "villes" : "ville")} construite{(_cities.Count > 1 ? "s" : "")}, {Shown:N0} affichés{(pending > 0 ? $", {pending} tuiles en cours" : "")}";
        }
    }

    static string SavePath => Path.Combine(OS.GetUserDataDir(), "cities.json");

    public override void _Ready()
    {
        _mat = new ShaderMaterial { Shader = new Shader { Code = Shader } };
        _archive = Overture.OpenAsync("buildings");
        try
        {
            if (File.Exists(SavePath))
                foreach (var c in JsonSerializer.Deserialize<List<City>>(File.ReadAllText(SavePath)) ?? new()) AddCity(c, false);
        }
        catch (Exception e) { GD.PrintErr($"[Buildings] {SavePath} : {e.Message}"); }

        var layer = new CanvasLayer { Layer = 1 };
        _prompt = new PanelContainer { Visible = false, MouseFilter = Control.MouseFilterEnum.Ignore };
        _prompt.AddThemeStyleboxOverride("panel", new StyleBoxFlat
        {
            BgColor = new Color(0.08f, 0.09f, 0.11f, 0.85f), BorderColor = new Color(1, 0.8f, 0.3f, 0.9f),
            BorderWidthBottom = 1, BorderWidthTop = 1, BorderWidthLeft = 1, BorderWidthRight = 1,
            ContentMarginLeft = 10, ContentMarginRight = 10, ContentMarginTop = 4, ContentMarginBottom = 4,
            CornerRadiusTopLeft = 5, CornerRadiusTopRight = 5, CornerRadiusBottomLeft = 5, CornerRadiusBottomRight = 5,
        });
        _promptText = new Label();
        _promptText.AddThemeColorOverride("font_color", new Color(1, 0.88f, 0.55f));
        _promptText.AddThemeFontSizeOverride("font_size", 17);
        _prompt.AddChild(_promptText);
        layer.AddChild(_prompt);
        AddChild(layer);
    }

    // ------------------------------------------------------------------ villes

    /// <summary>Rayon de la zone construite selon la taille de la localité.</summary>
    public static double RadiusFor(string cls, double population) => cls switch
    {
        "megacity" => 8_000,
        "city" => Math.Clamp(1_500 + Math.Sqrt(Math.Max(population, 0)) * 8, 2_000, 7_000),
        "capital" => 4_000,
        "town" => 1_500,
        "village" => 700,
        "hamlet" => 350,
        _ => 500,
    };

    City Find(Target t)
    {
        var d = Geo.FromLatLon(t.Lat, t.Lon);
        foreach (var c in _cities) if ((c.Dir - d).Length() * Geo.Radius < 1_500) return c;
        return null;
    }

    /// <summary>Construit une localité (sans effet si elle l'est déjà).</summary>
    public void Build(string name, double lat, double lon, double radius)
    {
        var t = new Target(name, lat, lon, radius, null);
        if (Find(t) != null) return;
        AddCity(new City { Name = name, Lat = lat, Lon = lon, Radius = radius }, true);
        GD.Print($"[Buildings] construction de {name} ({lat:F4}, {lon:F4}, rayon {radius:N0} m)");
    }

    void AddCity(City c, bool save)
    {
        c.Dir = Geo.FromLatLon(c.Lat, c.Lon);
        _cities.Add(c);
        _nextScan = 0;
        if (save) Save();
    }

    void Demolish(City c)
    {
        _cities.Remove(c);
        var free = new List<(int, int)>();
        foreach (var (key, t) in _tiles)
        {
            if (!t.Cities.Remove(c)) continue;
            t.Counts.Remove(c);
            t.Stamp++;
            if (t.Cities.Count == 0) free.Add(key);
        }
        foreach (var key in free) { Free(_tiles[key]); _tiles.Remove(key); }
        Save();
        GD.Print($"[Buildings] {c.Name} démolie");
    }

    void Save()
    {
        try { File.WriteAllText(SavePath, JsonSerializer.Serialize(_cities, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception e) { GD.PrintErr($"[Buildings] {SavePath} : {e.Message}"); }
    }

    // ------------------------------------------------------------------ boucle

    public override void _Process(double delta)
    {
        _clock += delta;
        while (_done.TryDequeue(out var item)) Install(item.tile, item.data, item.cities, item.stamp);

        var cam = Camera.PosD;
        var player = Camera.PlayerPos;
        if (_clock >= _nextScan && _archive.IsCompletedSuccessfully && _archive.Result != null)
        {
            _nextScan = _clock + 0.5;
            Scan(player);
        }

        Shown = 0;
        foreach (var t in _tiles.Values)
        {
            if (t.Mesh != null)
            {
                bool vis = (t.Center - cam.Normalized()).Length() * Geo.Radius - t.HalfSize < ShowDistance;
                t.Mesh.Visible = vis;
                t.Mesh.Position = (t.Origin - cam).ToVector3();
                if (vis) foreach (var n in t.Counts.Values) Shown += n;
            }
            // Relief plus précis arrivé depuis la construction : on reconstruit (au plus toutes les 3 s)
            if (t.Ready && !t.Loading && t.Incomplete && t.BuiltVersion != StreamedRaster.DataVersion
                && _clock - t.LastBuild > 3 && _building < MaxBuilds)
                Build(t);
        }
        _mat.SetShaderParameter("night", Night);
        UpdateHover();
    }

    /// <summary>Tuiles voulues : celles qui touchent une ville construite proche du joueur.</summary>
    void Scan(Vec3d player)
    {
        var dir = player.Normalized();
        var wanted = new HashSet<(int, int)>();
        foreach (var c in _cities)
        {
            c.TilesTotal = 0; c.TilesDone = 0; c.Buildings = 0;
            if ((c.Dir - dir).Length() * Geo.Radius - c.Radius > LoadDistance) continue;
            double dLat = c.Radius / 111_320, dLon = c.Radius / (111_320 * Math.Max(Math.Cos(c.Lat * Math.PI / 180), 0.05));
            PmTilesArchive.TileXY(c.Lat + dLat, c.Lon - dLon, Zoom, out int x0, out int y0);
            PmTilesArchive.TileXY(c.Lat - dLat, c.Lon + dLon, Zoom, out int x1, out int y1);
            for (int y = y0; y <= y1; y++)
                for (int x = x0; x <= x1; x++)
                {
                    if (!_tiles.TryGetValue((x, y), out var t))
                    {
                        PmTilesArchive.TileToLatLon(Zoom, x, y, 0.5, 0.5, out double cla, out double clo);
                        PmTilesArchive.TileToLatLon(Zoom, x, y, 0, 0, out double ala, out double alo);
                        var ctr = Geo.FromLatLon(cla, clo);
                        t = new Tile { X = x, Y = y, Center = ctr, HalfSize = (Geo.FromLatLon(ala, alo) - ctr).Length() * Geo.Radius };
                    }
                    if ((t.Center - c.Dir).Length() * Geo.Radius - t.HalfSize > c.Radius) continue;
                    _tiles.TryAdd((x, y), t);
                    wanted.Add((x, y));
                    if (!t.Cities.Contains(c)) { t.Cities.Add(c); t.Stamp++; }
                    c.TilesTotal++;
                    if (t.Ready && t.Counts.TryGetValue(c, out int n)) { c.TilesDone++; c.Buildings += n; }
                }
        }

        var remove = new List<(int, int)>();
        foreach (var (key, t) in _tiles) if (!wanted.Contains(key) && !t.Loading) remove.Add(key);
        foreach (var key in remove) { Free(_tiles[key]); _tiles.Remove(key); }

        _wanting = false;
        var todo = new List<Tile>();
        foreach (var key in wanted) { var t = _tiles[key]; if (!t.Loading && (!t.Ready || t.Stamp != t.BuiltStamp)) todo.Add(t); }
        todo.Sort((a, b) => (a.Center - dir).Length().CompareTo((b.Center - dir).Length()));
        foreach (var t in todo)
        {
            if (_building >= MaxBuilds) { _wanting = true; break; }
            Build(t);
        }
    }

    void Build(Tile t)
    {
        t.Loading = true;
        _building++;
        t.LastBuild = _clock;
        var archive = _archive.Result;
        var cities = t.Cities.ToArray();
        var areas = Array.ConvertAll(cities, c => new BuildingBuilder.Area(c.Dir, c.Radius));
        int stamp = t.Stamp;
        Task.Run(async () =>
        {
            BuildingMeshData data = null;
            try
            {
                t.Mvt ??= await archive.GetTileAsync(Zoom, t.X, t.Y) ?? Array.Empty<byte>();
                // Relief à 4 m : précis à 1 m près, et léger à télécharger en Suisse (aperçu swissALTI3D)
                data = BuildingBuilder.Build(t.Mvt, Zoom, t.X, t.Y, areas, 4);
            }
            catch (Exception e) { GD.PrintErr($"[Buildings] tuile {Zoom}/{t.X}/{t.Y} : {e.Message}"); }
            _done.Enqueue((t, data, cities, stamp));
        });
    }

    void Install(Tile t, BuildingMeshData data, City[] cities, int stamp)
    {
        _building--;
        t.Loading = false;
        if (t.Dead) return;
        if (data == null) { t.Mvt = null; return; } // réessai au prochain passage
        t.Origin = data.Origin;
        t.Incomplete = data.Incomplete;
        t.BuiltVersion = data.DataVersion;
        t.BuiltStamp = stamp;
        t.Counts.Clear();
        for (int i = 0; i < cities.Length; i++) if (t.Cities.Contains(cities[i])) t.Counts[cities[i]] = data.PerArea[i];
        ArrayMesh mesh = null;
        if (data.Indices.Length > 0)
        {
            var arrays = new Godot.Collections.Array();
            arrays.Resize((int)Mesh.ArrayType.Max);
            arrays[(int)Mesh.ArrayType.Vertex] = data.Vertices;
            arrays[(int)Mesh.ArrayType.Normal] = data.Normals;
            arrays[(int)Mesh.ArrayType.Color] = data.Colors;
            arrays[(int)Mesh.ArrayType.TexUV] = data.Uv;
            arrays[(int)Mesh.ArrayType.TexUV2] = data.Uv2;
            arrays[(int)Mesh.ArrayType.Index] = data.Indices;
            mesh = new ArrayMesh();
            mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        }
        if (t.Mesh == null)
        {
            t.Mesh = new MeshInstance3D { MaterialOverride = _mat, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
            Planet.AddChild(t.Mesh);
        }
        t.Mesh.Mesh = mesh;
        t.Ready = true;
    }

    void Free(Tile t)
    {
        t.Dead = true;
        t.Mesh?.QueueFree();
        t.Mesh = null; t.Mvt = null;
    }

    // ------------------------------------------------------------------ invite « on construit ? »

    void UpdateHover()
    {
        _hover = null;
        if (Menu != null && Menu.IsOpen) { _prompt.Visible = false; return; }
        _targets.Clear();
        Places?.CollectTargets(_targets);
        Political?.CollectTargets(_targets);
        bool free = Input.MouseMode != Input.MouseModeEnum.Captured;
        var vp = GetViewport();
        var pointer = free ? vp.GetMousePosition() : vp.GetVisibleRect().Size / 2;
        float best = free ? 45 : 70;
        Vector2 at = default;
        foreach (var t in _targets)
        {
            if (t.Label == null || !t.Label.Visible || t.Label.Modulate.A < 0.4f) continue;
            var gp = t.Label.GlobalPosition;
            if (Camera.IsPositionBehind(gp)) continue;
            var sp = Camera.UnprojectPosition(gp);
            float d = sp.DistanceTo(pointer);
            if (d >= best) continue;
            best = d; _hover = t; at = sp;
        }
        if (_hover is not Target h) { _prompt.Visible = false; return; }
        var city = Find(h);
        string key = free ? "clic" : "K";
        _promptText.Text = city == null ? $"{h.Name} — on construit ?  ({key})"
            : city.TilesDone < city.TilesTotal ? $"{h.Name} — construction… {city.TilesDone}/{city.TilesTotal}  ({city.Buildings:N0} bâtiments)"
            : $"{h.Name} — {city.Buildings:N0} bâtiments  (Maj+{key} : démolir)";
        _prompt.Visible = true;
        _prompt.ResetSize();
        _prompt.Position = at + new Vector2(-_prompt.Size.X / 2, 16);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (_hover is not Target h) return;
        bool act = false, shift = false;
        if (e is InputEventMouseButton mb && mb.Pressed && mb.ButtonIndex == MouseButton.Left && Input.MouseMode != Input.MouseModeEnum.Captured)
        { act = true; shift = mb.ShiftPressed; }
        else if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode == Key.K)
        { act = true; shift = k.ShiftPressed; }
        if (!act) return;
        var city = Find(h);
        if (city == null) Build(h.Name, h.Lat, h.Lon, h.Radius);
        else if (shift) Demolish(city);
        GetViewport().SetInputAsHandled(); // le clic ne capture pas la souris
    }

    // Murs éclairés par le soleil ; fenêtres dessinées selon (abscisse le long du mur, hauteur), allumées la nuit.
    const string Shader = @"
shader_type spatial;
render_mode cull_disabled;
uniform float night = 0.0;
float hash(vec3 p) { return fract(sin(dot(p, vec3(12.9898, 78.233, 37.719))) * 43758.5453); }
void fragment() {
    vec3 base = pow(COLOR.rgb, vec3(2.2));
    ROUGHNESS = 0.9;
    SPECULAR = 0.25;
    if (UV2.x > 0.5) {
        vec2 cell = vec2(UV.x / 3.2, UV.y / 3.0);
        vec2 f = fract(cell);
        float win = step(0.30, f.x) * step(f.x, 0.72) * step(0.32, f.y) * step(f.y, 0.78) * step(0.5, UV.y);
        // au loin, les fenêtres se fondent dans la façade (pas de scintillement)
        float blur = clamp(max(fwidth(cell.x), fwidth(cell.y)) * 2.5 - 0.5, 0.0, 1.0);
        float m = mix(win, 0.2, blur);
        vec3 glass = vec3(0.05, 0.065, 0.08);
        ALBEDO = mix(base, glass, m);
        ROUGHNESS = mix(0.9, 0.25, m);
        float lit = step(0.55, hash(vec3(floor(cell), UV2.y * 97.0)));
        EMISSION = vec3(1.0, 0.72, 0.38) * 1.6 * night * mix(win * lit, 0.2 * 0.45, blur);
    } else {
        ALBEDO = base;
    }
}";
}
