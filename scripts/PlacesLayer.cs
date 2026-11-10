using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Noms de toutes les villes, villes moyennes, villages et hameaux du monde (OpenStreetMap via Overture Maps,
/// couche « divisions », type « locality »), chargés par tuiles autour du joueur :
/// - tuiles de zoom 10 (~40 km) jusqu'à ~60 km : toutes les localités ;
/// - tuiles de zoom 6 (~600 km) jusqu'à ~1 200 km : les villes, pour les voir de loin ou d'avion.
/// Nom en français quand il existe (Genève, Lucerne, Munich…). Plus la localité est petite, plus il faut
/// s'en approcher pour voir son nom. Touche N : afficher / masquer.
/// </summary>
public partial class PlacesLayer : Node3D
{
    public FlyCamera Camera;
    public Planet Planet;
    public bool ShowLayer = true;

    sealed class Place
    {
        public string Id, Name, Class;
        public double Lat, Lon, Population;
        public Vec3d Pos;
        public double MaxDistance;
        public Label3D Label;
        public int Refs;
        public bool Grounded; // altitude calculée avec le relief fin
    }

    sealed class Tile { public int Z, X, Y; public Vec3d Center; public double HalfSize; public bool Loading, Ready, Dead; public List<string> Ids = new(); }

    readonly Dictionary<(int, int, int), Tile> _tiles = new();
    readonly Dictionary<string, Place> _places = new();
    readonly ConcurrentQueue<(Tile, List<Place>)> _done = new();
    Task<PmTilesArchive> _archive;
    double _clock, _nextScan;
    int _loading;
    List<(double lat, double lon)> _capitals;

    public int Count => _places.Count;
    public bool Idle => _loading == 0 && _done.IsEmpty;

    public override void _Ready()
    {
        _archive = Overture.OpenAsync("divisions");
        _capitals = new List<(double, double)>();
        foreach (var c in Geography.Countries) _capitals.Add((c.Lat, c.Lon));
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode == Key.N) ShowLayer = !ShowLayer;
    }

    static double MaxDistanceFor(string cls, double pop) => cls switch
    {
        "megacity" => 1_200_000,
        "city" => Math.Clamp(60_000 + Math.Sqrt(Math.Max(pop, 0)) * 600, 80_000, 1_000_000),
        "town" => 35_000,
        "village" => 12_000,
        "hamlet" => 4_000,
        _ => 8_000,
    };

    public override void _Process(double delta)
    {
        _clock += delta;
        while (_done.TryDequeue(out var item)) Install(item.Item1, item.Item2);
        var cam = Camera.PosD;
        if (_clock >= _nextScan && _archive.IsCompletedSuccessfully && _archive.Result != null)
        {
            _nextScan = _clock + 0.5;
            Scan(Camera.PlayerPos.Normalized());
        }
        foreach (var p in _places.Values)
        {
            double d = (p.Pos - cam).Length();
            bool vis = ShowLayer && d < p.MaxDistance;
            p.Label.Visible = vis;
            if (!vis) continue;
            // Près du lieu : altitude recalculée avec le relief fin (le nom ne reste pas enterré sous la colline)
            if (!p.Grounded && d < 30_000)
            {
                float h = HeightProvider.Sample(p.Lat, p.Lon, 60, out bool water, out bool pending);
                p.Grounded = !pending; // sinon : on réessaie à l'image suivante, une fois le relief arrivé
                p.Pos = Geo.FromLatLon(p.Lat, p.Lon) * (Geo.Radius + (water ? Math.Max(h, 0) : h) + 25);
            }
            p.Label.Position = (p.Pos - cam).ToVector3();
            float fade = (float)Math.Clamp((p.MaxDistance - d) / (p.MaxDistance * 0.25), 0, 1);
            p.Label.Modulate = new Color(1, 1, 1, fade);
            p.Label.OutlineModulate = new Color(0.05f, 0.05f, 0.08f, 0.75f * fade);
        }
    }

    /// <summary>Localités affichées, pour l'invite « on construit ? » (<see cref="BuildingsLayer"/>).</summary>
    public void CollectTargets(List<BuildingsLayer.Target> list)
    {
        foreach (var p in _places.Values)
            if (p.Label.Visible)
                list.Add(new BuildingsLayer.Target(p.Name, p.Lat, p.Lon, BuildingsLayer.RadiusFor(p.Class, p.Population), BuildingsLayer.MaxRadiusFor(p.Class, p.Population), p.Label));
    }

    void Scan(Vec3d dir)
    {
        Geo.ToLatLon(dir, out double lat, out double lon);
        double agl = Math.Max(0, Camera.Altitude - Camera.GroundHeight);
        var wanted = new HashSet<(int, int, int)>();
        if (agl < 150_000) Want(10, Math.Clamp(45_000 + agl, 45_000, 120_000));
        if (agl < 3_000_000) Want(6, 1_200_000);

        void Want(int z, double radius)
        {
            double dLat = radius / 111_320, dLon = Math.Min(radius / (111_320 * Math.Max(Math.Cos(lat * Math.PI / 180), 0.05)), 179);
            PmTilesArchive.TileXY(Math.Min(lat + dLat, 85), lon - dLon, z, out int x0, out int y0);
            PmTilesArchive.TileXY(Math.Max(lat - dLat, -85), lon + dLon, z, out int x1, out int y1);
            int n = 1 << z;
            if (lon - dLon < -180) x0 -= n; // traverse l'antiméridien
            if (lon + dLon > 180) x1 += n;
            for (int y = y0; y <= y1; y++)
                for (int xx = x0; xx <= x1; xx++)
                {
                    int x = ((xx % n) + n) % n;
                    var key = (z, x, y);
                    if (!_tiles.TryGetValue(key, out var t))
                    {
                        PmTilesArchive.TileToLatLon(z, x, y, 0.5, 0.5, out double cla, out double clo);
                        PmTilesArchive.TileToLatLon(z, x, y, 0, 0, out double ala, out double alo);
                        var c = Geo.FromLatLon(cla, clo);
                        t = new Tile { Z = z, X = x, Y = y, Center = c, HalfSize = (Geo.FromLatLon(ala, alo) - c).Length() * Geo.Radius };
                    }
                    if ((t.Center - dir).Length() * Geo.Radius - t.HalfSize > radius) continue;
                    wanted.Add(key);
                    _tiles.TryAdd(key, t);
                }
        }

        var remove = new List<(int, int, int)>();
        foreach (var (key, t) in _tiles)
            if (!wanted.Contains(key) && !t.Loading) remove.Add(key);
        foreach (var key in remove)
        {
            var t = _tiles[key];
            t.Dead = true;
            foreach (var id in t.Ids) Release(id);
            _tiles.Remove(key);
        }
        foreach (var key in wanted)
        {
            var t = _tiles[key];
            if (t.Ready || t.Loading || _loading >= 4) continue;
            Load(t);
        }
    }

    void Release(string id)
    {
        if (!_places.TryGetValue(id, out var p) || --p.Refs > 0) return;
        p.Label.QueueFree();
        _places.Remove(id);
    }

    void Load(Tile t)
    {
        t.Loading = true;
        _loading++;
        var archive = _archive.Result;
        var capitals = _capitals;
        Task.Run(async () =>
        {
            List<Place> list = null;
            try
            {
                var bytes = await archive.GetTileAsync(t.Z, t.X, t.Y);
                list = new List<Place>();
                if (bytes != null)
                {
                    var layers = Mvt.Decode(bytes, new HashSet<string> { "division" },
                        new HashSet<string> { "subtype", "class", "@name", "names", "population", "id" });
                    if (layers.TryGetValue("division", out var feats))
                        foreach (var f in feats)
                        {
                            if (f.Str("subtype") != "locality" || f.Type != Mvt.GeomType.Point || f.Parts.Count == 0) continue;
                            string cls = f.Str("class") ?? "village";
                            if (t.Z < 10 && cls is not ("city" or "megacity")) continue;
                            var pt = f.Parts[0];
                            if (pt[0] < 0 || pt[0] >= 1 || pt[1] < 0 || pt[1] >= 1) continue; // dans la marge : tuile voisine
                            PmTilesArchive.TileToLatLon(t.Z, t.X, t.Y, pt[0], pt[1], out double la, out double lo);
                            // Les capitales ont déjà leur étiquette (◆, couche politique)
                            bool capital = false;
                            foreach (var (cla, clo) in capitals)
                                if (Math.Abs(cla - la) < 0.06 && Math.Abs(clo - lo) < 0.08) { capital = true; break; }
                            if (capital) continue;
                            string name = FrenchName(f.Str("names")) ?? f.Str("@name");
                            if (string.IsNullOrEmpty(name)) continue;
                            double pop = f.Num("population", 0);
                            list.Add(new Place
                            {
                                Id = f.Str("id") ?? $"{la:F5},{lo:F5}", Name = name, Class = cls, Lat = la, Lon = lo, Population = pop,
                                MaxDistance = MaxDistanceFor(cls, pop),
                                // altitude grossière d'abord (aucun téléchargement), affinée en s'approchant
                                Pos = Geo.FromLatLon(la, lo) * (Geo.Radius + HeightProvider.SurfaceHeight(la, lo, 3000) + 40),
                            });
                        }
                }
            }
            catch (Exception e) { GD.PrintErr($"[PlacesLayer] tuile {t.Z}/{t.X}/{t.Y} : {e.Message}"); }
            _done.Enqueue((t, list));
        });
    }

    /// <summary>Nom français (names.common.fr) s'il existe.</summary>
    static string FrenchName(string json)
    {
        if (json == null || !json.Contains("\"fr\"")) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("common", out var c) && c.TryGetProperty("fr", out var fr)) return fr.GetString();
        }
        catch (JsonException) { }
        return null;
    }

    void Install(Tile t, List<Place> list)
    {
        _loading--;
        t.Loading = false;
        if (t.Dead) return;
        if (list == null) return; // réessai plus tard
        t.Ready = true;
        foreach (var p in list)
        {
            t.Ids.Add(p.Id);
            if (_places.TryGetValue(p.Id, out var existing)) { existing.Refs++; continue; }
            // Même nom à moins de 4 km (le lieu est parfois présent deux fois dans OSM) : une seule étiquette
            Place twin = null;
            foreach (var q in _places.Values)
                if (q.Name == p.Name && (q.Pos - p.Pos).Length() < 4_000) { twin = q; break; }
            if (twin != null) { twin.Refs++; t.Ids[^1] = twin.Id; continue; }
            p.Refs = 1;
            (int size, float pix) = p.Class switch
            {
                "megacity" or "city" => (34, 0.0011f),
                "town" => (30, 0.0009f),
                "village" => (26, 0.0008f),
                _ => (22, 0.0007f),
            };
            p.Label = new Label3D
            {
                Text = p.Name,
                FixedSize = true,
                PixelSize = pix,
                FontSize = size,
                OutlineSize = 7,
                Billboard = BaseMaterial3D.BillboardModeEnum.Enabled,
                Visible = false,
            };
            Planet.AddChild(p.Label);
            _places[p.Id] = p;
        }
    }
}
