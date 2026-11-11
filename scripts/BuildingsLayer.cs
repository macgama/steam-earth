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
    public StartMenu Start;
    public float Night;                  // 0 = jour, 1 = nuit (fenêtres éclairées)
    /// <summary>Heure locale (0–24) : la part de fenêtres allumées en dépend (soirée animée, nuit presque éteinte).</summary>
    public double LocalHour = 20;
    /// <summary>L'invite « on construit ? » n'apparaît qu'à plus de cette hauteur au-dessus du sol (pas en pleine ville).</summary>
    public const double PromptMinHeight = 500;

    // Part de fenêtres allumées selon l'heure locale (heure, part), interpolée
    static readonly (double h, double f)[] LitByHour =
    {
        (0, 0.22), (1, 0.10), (2, 0.06), (5, 0.06), (6, 0.18), (7.5, 0.30), (9, 0.15),
        (16, 0.15), (18, 0.45), (21, 0.55), (23, 0.38), (24, 0.22),
    };

    public static double LitFraction(double hour)
    {
        hour = ((hour % 24) + 24) % 24;
        for (int i = 1; i < LitByHour.Length; i++)
            if (hour <= LitByHour[i].h)
            {
                var (h0, f0) = LitByHour[i - 1]; var (h1, f1) = LitByHour[i];
                return f0 + (f1 - f0) * (hour - h0) / (h1 - h0);
            }
        return LitByHour[^1].f;
    }

    public const string Attribution = "bâtiments : OSM, Microsoft, Google Open Buildings (CC BY 4.0), USGS ; commerces : Overture Places (CDLA Permissive 2.0)";
    const int Zoom = 14, MaxBuilds = 3;
    /// <summary>Les portes s'ouvrent toutes seules quand le joueur est à moins de cette distance (m).</summary>
    public const double DoorOpenDistance = 7;
    // Distances selon le niveau de qualité graphique (Élevé : 40 km / 60 km)
    static double ShowDistance => GraphicsQuality.Current.BuildingsShowDistance;
    static double LoadDistance => GraphicsQuality.Current.BuildingsShowDistance * 1.5;

    /// <summary>Localité qu'on peut construire (étiquette affichée par une autre couche).</summary>
    public readonly record struct Target(string Name, double Lat, double Lon, double Radius, double MaxRadius, Label3D Label);

    public sealed class City
    {
        public string Name { get; set; }
        public double Lat { get; set; }
        public double Lon { get; set; }
        public double Radius { get; set; }
        /// <summary>Taille finale : la zone construite s'agrandit jusqu'ici quand le joueur s'approche de son bord.</summary>
        public double MaxRadius { get; set; }
        internal Vec3d Dir;
        internal int Buildings, TilesTotal, TilesDone;
    }

    sealed class Tile
    {
        public int X, Y;
        public Vec3d Center; public double HalfSize;
        public bool Loading, Ready, Dead, Incomplete;
        public int BuiltVersion, Stamp, BuiltStamp;
        public byte[] Mvt, Places, Land;
        public List<BuildingInfo> Infos;
        public Vec3d Origin;
        public MeshInstance3D Mesh;
        public double LastBuild;
        public readonly List<City> Cities = new();
        public readonly Dictionary<City, int> Counts = new();
        public readonly Dictionary<City, double> Radii = new();
    }

    readonly List<City> _cities = new();
    readonly Dictionary<(int, int), Tile> _tiles = new();
    readonly ConcurrentQueue<(Tile tile, BuildingMeshData data, City[] cities, int stamp)> _done = new();
    readonly List<Target> _targets = new();
    Task<PmTilesArchive> _archive, _placesArchive, _baseArchive;
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

    public override void _ExitTree() => GraphicsQuality.Changed -= OnQualityChanged;

    /// <summary>Ombres des bâtiments déjà construits selon le nouveau niveau (les distances sont relues à chaque image).</summary>
    void OnQualityChanged(QualityProfile q, QualityProfile old)
    {
        var shadow = GraphicsQuality.ShadowFor(ShadowCaster.Buildings);
        foreach (var t in _tiles.Values) if (t.Mesh != null) t.Mesh.CastShadow = shadow;
        if (q.BuildingDetail == old.BuildingDetail) return;
        // façades : variante du shader de ce niveau ; balcons ajoutés ou retirés (à partir de Moyen) : on reconstruit
        _mat.Shader = BuildingShader.Get(q.BuildingDetail);
        if ((q.BuildingDetail >= 1) != (old.BuildingDetail >= 1))
        {
            foreach (var t in _tiles.Values) t.Stamp++;
            _nextScan = 0;
        }
    }

    public override void _Ready()
    {
        GraphicsQuality.Changed += OnQualityChanged;
        _mat = new ShaderMaterial { Shader = BuildingShader.Get(GraphicsQuality.Current.BuildingDetail) };
        _archive = Overture.OpenAsync("buildings");
        _placesArchive = Overture.OpenAsync("places"); // commerces, écoles, églises… (fonction des bâtiments)
        _baseArchive = Overture.OpenAsync("base");     // zones OSM : écoles, hôpitaux, stades (zoom 13)
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

    /// <summary>Taille jusqu'à laquelle une grande ville s'agrandit au fur et à mesure (les petites ne grandissent pas).</summary>
    public static double MaxRadiusFor(string cls, double population) => cls switch
    {
        "megacity" => 30_000,
        "city" => Math.Clamp(RadiusFor(cls, population) * 3, 6_000, 20_000),
        "capital" => 15_000,
        _ => RadiusFor(cls, population),
    };

    const double GrowStep = 1_500, GrowMargin = 1_500;

    City Find(Target t)
    {
        var d = Geo.FromLatLon(t.Lat, t.Lon);
        foreach (var c in _cities) if ((c.Dir - d).Length() * Geo.Radius < 1_500) return c;
        return null;
    }

    /// <summary>Construit une localité (sans effet si elle l'est déjà).</summary>
    public void Build(string name, double lat, double lon, double radius, double maxRadius = 0)
    {
        var t = new Target(name, lat, lon, radius, maxRadius, null);
        if (Find(t) != null) return;
        AddCity(new City { Name = name, Lat = lat, Lon = lon, Radius = radius, MaxRadius = maxRadius }, true);
        GD.Print($"[Buildings] construction de {name} ({lat:F4}, {lon:F4}, rayon {radius:N0} m)");
    }

    void AddCity(City c, bool save)
    {
        c.Dir = Geo.FromLatLon(c.Lat, c.Lon);
        c.MaxRadius = Math.Max(c.MaxRadius, c.Radius);
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
            t.Radii.Remove(c);
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
        _mat.SetShaderParameter("lit_fraction", (float)LitFraction(LocalHour));
        double h = ((LocalHour % 24) + 24) % 24;
        _mat.SetShaderParameter("shop_lit", h >= 7 && h < 21.5 ? 1f : 0.15f); // vitrines éclairées aux heures d'ouverture
        // portes : effacées près du joueur (l'intérieur dessine alors la porte ouverte)
        _mat.SetShaderParameter("player_world", Planet.GlobalTransform * (player - cam).ToVector3());
        _mat.SetShaderParameter("door_open", (float)DoorOpenDistance);
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
                    // ville agrandie : seules les tuiles qui débordent de l'ancien disque changent
                    else if (t.Radii.TryGetValue(c, out double r) && r != c.Radius
                             && (t.Center - c.Dir).Length() * Geo.Radius + t.HalfSize > r) t.Stamp++;
                    t.Radii[c] = c.Radius;
                    c.TilesTotal++;
                    if (t.Ready && t.Counts.TryGetValue(c, out int n)) { c.TilesDone++; c.Buildings += n; }
                }
        }

        // Agrandissement au fur et à mesure : le joueur approche du bord d'une grande ville déjà construite
        bool grown = false;
        foreach (var c in _cities)
        {
            if (c.Radius >= c.MaxRadius || c.TilesTotal == 0 || c.TilesDone < c.TilesTotal) continue;
            double d = (c.Dir - dir).Length() * Geo.Radius;
            if (d < c.Radius - GrowMargin || d > c.Radius + GrowMargin) continue;
            c.Radius = Math.Min(c.MaxRadius, Math.Max(c.Radius + GrowStep, d + 2 * GrowMargin));
            GD.Print($"[Buildings] {c.Name} s'agrandit : rayon {c.Radius:N0} m (max {c.MaxRadius:N0})");
            grown = true;
        }
        if (grown) { Save(); _nextScan = 0; }

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
        var placesArchive = _placesArchive;
        var baseArchive = _baseArchive;
        var cities = t.Cities.ToArray();
        var areas = Array.ConvertAll(cities, c => new BuildingBuilder.Area(c.Dir, c.Radius));
        int stamp = t.Stamp;
        Task.Run(async () =>
        {
            BuildingMeshData data = null;
            try
            {
                t.Mvt ??= await archive.GetTileAsync(Zoom, t.X, t.Y) ?? Array.Empty<byte>();
                // Fonction des bâtiments : lieux Overture (même tuile) et zones OSM (tuile de zoom 13 qui la contient).
                // Hors ligne sans cache : les bâtiments se construisent quand même, d'après leur seule classe.
                if (t.Places == null)
                {
                    try { var pa = await placesArchive; t.Places = pa != null ? await pa.GetTileAsync(Zoom, t.X, t.Y) ?? Array.Empty<byte>() : Array.Empty<byte>(); }
                    catch (Exception) { t.Places = Array.Empty<byte>(); }
                }
                if (t.Land == null)
                {
                    try { var ba = await baseArchive; t.Land = ba != null ? await ba.GetTileAsync(Zoom - 1, t.X >> 1, t.Y >> 1) ?? Array.Empty<byte>() : Array.Empty<byte>(); }
                    catch (Exception) { t.Land = Array.Empty<byte>(); }
                }
                // Relief à 4 m : précis à 1 m près, et léger à télécharger en Suisse (aperçu swissALTI3D)
                data = BuildingBuilder.Build(t.Mvt, t.Places, t.Land, Zoom - 1, t.X >> 1, t.Y >> 1, Zoom, t.X, t.Y, areas, 4);
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
            t.Mesh = new MeshInstance3D { MaterialOverride = _mat, CastShadow = GraphicsQuality.ShadowFor(ShadowCaster.Buildings) };
            Planet.AddChild(t.Mesh);
        }
        t.Mesh.Mesh = mesh;
        Obstacles.Set(t.Mesh, data.Footprints);
        t.Infos = data.Infos;
        InfosVersion++;
        t.Ready = true;
    }

    void Free(Tile t)
    {
        t.Dead = true;
        t.Mesh?.QueueFree();
        t.Mesh = null; t.Mvt = null; t.Places = null; t.Land = null; t.Infos = null;
        InfosVersion++;
    }

    /// <summary>Change à chaque tuile installée ou retirée (les intérieurs vérifient alors leurs bâtiments).</summary>
    public int InfosVersion { get; private set; }

    /// <summary>Bâtiments où l'on peut entrer dont le contour passe à moins de <paramref name="radius"/> m de <paramref name="p"/>
    /// (distance au cercle englobant), avec cette distance.</summary>
    public void InfosNear(Vec3d p, double radius, List<(BuildingInfo info, double dist)> result)
    {
        var dir = p.Normalized();
        foreach (var t in _tiles.Values)
        {
            if (t.Infos == null || t.Mesh == null || !t.Mesh.Visible) continue;
            if ((t.Center - dir).Length() * Geo.Radius - t.HalfSize * 1.5 > radius) continue;
            foreach (var b in t.Infos)
            {
                double d = (b.Frame.C - dir).Length() * Geo.Radius - b.BoundRadius;
                if (d <= radius) result.Add((b, Math.Max(d, 0)));
            }
        }
    }

    /// <summary>Bâtiment (actuel) qui porte cet identifiant, près de <paramref name="p"/>.</summary>
    public BuildingInfo FindInfo(string id, Vec3d p)
    {
        var dir = p.Normalized();
        foreach (var t in _tiles.Values)
        {
            if (t.Infos == null || (t.Center - dir).Length() * Geo.Radius - t.HalfSize * 1.5 > 500) continue;
            foreach (var b in t.Infos) if (b.Id == id) return b;
        }
        return null;
    }

    // ------------------------------------------------------------------ invite « on construit ? »

    void UpdateHover()
    {
        _hover = null;
        if ((Menu != null && Menu.IsOpen) || (Start != null && Start.IsOpen) || GameUi.MenuOpen
            || Camera.Altitude - Camera.GroundHeight < PromptMinHeight) { _prompt.Visible = false; return; }
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
            : $"{h.Name} — {city.Buildings:N0} bâtiments, {city.Radius / 1000:0.#} km{(city.Radius < city.MaxRadius ? " (s'agrandit quand on s'approche du bord)" : "")}  (Maj+{key} : démolir)";
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
        if (city == null) Build(h.Name, h.Lat, h.Lon, h.Radius, h.MaxRadius);
        else if (shift) Demolish(city);
        GetViewport().SetInputAsHandled(); // le clic ne capture pas la souris
    }
}
