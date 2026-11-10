using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Transports publics : gares, haltes, stations de métro, arrêts de tram et de bus, gares routières,
/// débarcadères et ports, funiculaires et téléphériques (OpenStreetMap via Overture Maps, couche « base »,
/// thème « infrastructure », tuiles de zoom 13 chargées dans un rayon de ~3 km), chacun avec son pictogramme
/// et son nom (en français quand il existe). Les arrêts d'une même station (quais, poteaux de bus…) sont
/// regroupés en une seule étiquette.
/// Aéroports et aérodromes du monde entier : fichier OurAirports inclus (domaine public), visibles de loin
/// (grands aéroports jusqu'à ~350 km, pour les voir d'avion).
/// Touche P : afficher / masquer.
/// </summary>
public partial class TransitLayer : LabelTileLayer
{
    /// <summary>Même ordre que les pictogrammes de tools/transport/build_icons.py.</summary>
    public enum Mode { Train, Metro, Tram, Bus, BusStation, Ferry, Funicular, Airport, CableCar }

    const string AirportsPath = "res://data/transport/airports.tsv.gz";

    sealed class Airport
    {
        public double Lat, Lon; public Vec3d Dir; public char Type; public bool Scheduled;
        public string Iata, Icao, Name, City;
        public double MaxDistance;
        public Poi Poi;
    }

    Task<List<Airport>> _airports;
    double _clock, _nextAirportScan;
    public int Airports { get; private set; }

    protected override int Zoom => 13; // la couche « infrastructure » n'existe qu'au zoom 13

    protected override double LoadRadius(double agl) => agl < 2500 ? 3200 : 0;

    public override void _Ready()
    {
        Icons = LoadIcons("res://data/transport/icons.rgba.gz", out IconSize);
        Archive = Overture.OpenAsync("base");
        _airports = Task.Run(LoadAirports);
    }

    public override void _UnhandledInput(InputEvent e)
    {
        if (e is InputEventKey k && k.Pressed && !k.Echo && k.PhysicalKeycode == Key.P) ShowLayer = !ShowLayer;
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_clock >= _nextAirportScan && _airports.IsCompletedSuccessfully)
        {
            _nextAirportScan = _clock + 0.5;
            ScanAirports();
        }
        base._Process(delta);
    }

    // ---------------------------------------------------------------- arrêts et gares (Overture)

    static readonly HashSet<string> Layers = new() { "infrastructure" };
    static readonly HashSet<string> Keys = new() { "subtype", "class", "@name", "names", "source_tags" };

    protected override List<Poi> Extract(byte[] mvt, int z, int x, int y)
    {
        var list = new List<Poi>();
        var layers = Mvt.Decode(mvt, Layers, Keys);
        if (!layers.TryGetValue("infrastructure", out var feats)) return list;
        foreach (var f in feats)
        {
            if (f.Str("subtype") != "transit") continue;
            string cls = f.Str("class");
            if (cls == null || cls.Contains("parking") || cls == "bicycle_rental" || cls == "platform" && f.Type != Mvt.GeomType.Point) continue;
            string name = FrenchName(f.Str("names")) ?? f.Str("@name");
            if (string.IsNullOrWhiteSpace(name)) continue;
            string tags = f.Str("source_tags") ?? "";
            if (!Classify(cls, tags, out Mode mode, out int priority)) continue;
            if (!Anchor(f, out double fx, out double fy)) continue;
            PmTilesArchive.TileToLatLon(z, x, y, fx, fy, out double lat, out double lon);
            name = StopName(name);
            list.Add(new Poi
            {
                Key = $"{(int)mode}|{name}",
                Text = name,
                Icon = (int)mode,
                Lat = lat, Lon = lon,
                Priority = priority,
                FontSize = mode is Mode.Train or Mode.Ferry or Mode.BusStation ? 24 : 20,
                MaxDistance = mode switch
                {
                    Mode.Train => 4500,
                    Mode.Ferry => 3500,
                    Mode.Funicular or Mode.CableCar => 3000,
                    Mode.Metro or Mode.BusStation => 1500,
                    Mode.Tram => 900,
                    _ => 550,
                },
                // un arrêt a souvent un poteau par sens, un quai par voie : on regroupe
                MergeDistance = mode is Mode.Train or Mode.Ferry or Mode.Metro or Mode.BusStation ? 450 : 220,
            });
        }
        return Merge(list);
    }

    static readonly System.Text.RegularExpressions.Regex PlatformSuffix =
        new(@"\s*\((?:quai |voie |kante |gleis |steig )?[A-Za-z0-9]{1,3}\)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>Nom de l'arrêt sans le quai ou le poteau (« Bern Bahnhof (Z) » → « Bern Bahnhof ») : un seul
    /// pictogramme par arrêt.</summary>
    static string StopName(string name) => PlatformSuffix.Replace(name.Trim(), "");

    /// <summary>Mode de transport d'une entité « transit » (classe Overture + étiquettes OSM d'origine).</summary>
    static bool Classify(string cls, string tags, out Mode mode, out int priority)
    {
        bool T(string kv) => tags.Contains(kv, StringComparison.Ordinal);
        priority = 1;
        mode = Mode.Bus;
        bool subway = T("\"subway\":\"yes\"") || T("\"station\":\"subway\"");
        bool funicular = T("\"station\":\"funicular\"") || T("\"funicular\":\"yes\"") || T("\"railway\":\"funicular\"");
        bool aerial = cls.Contains("aerialway") || T("\"aerialway\"");
        bool tram = T("\"tram\":\"yes\"") || T("\"railway\":\"tram_stop\"") || T("\"station\":\"light_rail\"") || T("\"light_rail\":\"yes\"");
        bool train = T("\"train\":\"yes\"");
        switch (cls)
        {
            case "railway_station":
            case "railway_halt":
            case "subway_station":
            case "light_rail_station":
            case "tram_station":
                priority = 3;
                mode = cls == "subway_station" || subway ? Mode.Metro : funicular ? Mode.Funicular : aerial ? Mode.CableCar
                     : cls is "light_rail_station" or "tram_station" || T("\"station\":\"light_rail\"") ? Mode.Tram : Mode.Train;
                return true;
            case "bus_station":
                priority = 3; mode = Mode.BusStation; return true;
            case "ferry_terminal":
                priority = 3; mode = Mode.Ferry; return true;
            case "bus_stop":
            case "stop_position":
            case "platform":
            case "tram_stop":
                priority = cls == "stop_position" ? 1 : 2;
                if (subway) mode = Mode.Metro;
                else if (funicular) mode = Mode.Funicular;
                else if (aerial) mode = Mode.CableCar;
                else if (tram || cls == "tram_stop") mode = Mode.Tram;
                else if (train || T("\"railway\":\"stop\"") || T("\"railway\":\"halt\"")) mode = Mode.Train;
                else if (T("\"ferry\":\"yes\"")) mode = Mode.Ferry;
                else if (T("\"bus\":\"yes\"") || T("\"highway\":\"bus_stop\"") || cls == "bus_stop" || T("\"trolleybus\":\"yes\"")) mode = Mode.Bus;
                else return false;
                return true;
            default:
                if (aerial && (cls.Contains("station") || T("\"aerialway\":\"station\""))) { priority = 3; mode = Mode.CableCar; return true; }
                return false;
        }
    }

    /// <summary>Regroupe les entités d'une même station (même mode et même nom, proches) : la position gardée
    /// est celle de l'entité la plus représentative (gare &gt; quai &gt; poteau d'arrêt).</summary>
    static List<Poi> Merge(List<Poi> list)
    {
        list.Sort((a, b) => b.Priority.CompareTo(a.Priority));
        var outList = new List<Poi>();
        foreach (var p in list)
        {
            bool dup = false;
            foreach (var q in outList)
            {
                if (q.Key != p.Key) continue;
                double dLat = (q.Lat - p.Lat) * 111_320, dLon = (q.Lon - p.Lon) * 111_320 * Math.Cos(q.Lat * Math.PI / 180);
                if (dLat * dLat + dLon * dLon < q.MergeDistance * q.MergeDistance) { dup = true; break; }
            }
            if (!dup) outList.Add(p);
        }
        // Une gare et l'arrêt de bus du même nom devant elle gardent chacun leur pictogramme (correspondances).
        return outList;
    }

    // ---------------------------------------------------------------- aéroports (OurAirports)

    static List<Airport> LoadAirports()
    {
        var list = new List<Airport>();
        try
        {
            var gz = Godot.FileAccess.GetFileAsBytes(AirportsPath);
            using var z = new System.IO.Compression.GZipStream(new System.IO.MemoryStream(gz), System.IO.Compression.CompressionMode.Decompress);
            using var r = new System.IO.StreamReader(z, System.Text.Encoding.UTF8);
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string line;
            while ((line = r.ReadLine()) != null)
            {
                if (line.StartsWith('#')) continue;
                var c = line.Split('\t');
                if (c.Length < 8) continue;
                var a = new Airport
                {
                    Lat = double.Parse(c[0], inv), Lon = double.Parse(c[1], inv), Type = c[2][0], Scheduled = c[3] == "1",
                    Iata = c[4], Icao = c[5], Name = c[6], City = c[7],
                };
                a.Dir = Geo.FromLatLon(a.Lat, a.Lon);
                a.MaxDistance = a.Type switch
                {
                    'L' => 350_000,
                    'M' => a.Scheduled ? 120_000 : 50_000,
                    'W' => 8_000,
                    _ => 15_000,
                };
                list.Add(a);
            }
        }
        catch (Exception e) { GD.PrintErr($"[TransitLayer] aéroports illisibles : {e.Message}"); }
        GD.Print($"[TransitLayer] {list.Count} aéroports chargés");
        return list;
    }

    void ScanAirports()
    {
        var cam = Camera.PosD;
        var dir = cam.Normalized();
        double alt = cam.Length() - Geo.Radius;
        int count = 0;
        foreach (var a in _airports.Result)
        {
            // distance approchée (corde au niveau de la mer + altitude) : suffit pour décider
            double chord = (a.Dir - dir).Length() * Geo.Radius;
            if (chord > 400_000 + alt) { if (a.Poi != null) { Release(a.Poi); a.Poi = null; } continue; }
            double d = Math.Sqrt(chord * chord + alt * alt);
            bool want = d < a.MaxDistance * 1.1;
            if (want && a.Poi == null)
            {
                string code = a.Iata.Length > 0 ? a.Iata : a.Icao;
                a.Poi = Add(new Poi
                {
                    Key = $"airport|{a.Icao}|{a.Name}",
                    Text = code.Length > 0 ? $"{a.Name} ({code})" : a.Name,
                    Icon = (int)Mode.Airport,
                    Lat = a.Lat, Lon = a.Lon,
                    Lift = 20,
                    FontSize = a.Type == 'L' ? 26 : a.Type == 'M' ? 22 : 18,
                    MaxDistance = a.MaxDistance,
                    MergeDistance = 50,
                });
            }
            else if (!want && a.Poi != null && d > a.MaxDistance * 1.25) { Release(a.Poi); a.Poi = null; }
            if (a.Poi != null) count++;
        }
        Airports = count;
    }
}
