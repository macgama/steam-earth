using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Godot;

namespace SteamEarth.Data;

/// <summary>
/// swissBUILDINGS3D 3.0 (swisstopo) : les vrais bâtiments de Suisse et du Liechtenstein en 3D (toits à pans, débords,
/// lucarnes, clochers, chalets…). Licence OGD, usage commercial autorisé avec la mention « © swisstopo ».
/// Les fichiers d'origine (FileGDB, ~45 Mo par feuille de ville) sont convertis une fois pour toutes par
/// tools/swissbuildings3d/build_tiles.py en une archive PMTiles (zoom 14) publiée dans la release
/// « swissbuildings3d » du dépôt ; le jeu n'en lit que les tuiles des villes construites (requêtes partielles,
/// ~0,5–4 Mo par tuile de ville, gardées en cache).
/// </summary>
public static class SwissBuildings3D
{
    public const string Url = "https://github.com/macgama/steam-earth/releases/download/swissbuildings3d/swissbuildings3d.pmtiles";
    public const string Attribution = "Bâtiments en Suisse : swissBUILDINGS3D 3.0 © swisstopo";

    static Task<PmTilesArchive> _archive;
    static readonly object Lock = new();

    /// <summary>Archive (ouverte au premier appel). Null hors ligne sans cache, ou si la release n'existe pas encore.</summary>
    public static Task<PmTilesArchive> Archive
    {
        get
        {
            lock (Lock)
            {
                if (_archive == null || (_archive.IsCompleted && _archive.Result == null && System.Environment.TickCount64 > _retryAt))
                {
                    _retryAt = System.Environment.TickCount64 + 120_000; // pas encore publiée / hors ligne : nouvel essai dans 2 min
                    string root = Path.Combine(OS.GetUserDataDir(), "cache", "swissbuildings3d");
                    _archive = Task.Run(async () =>
                    {
                        try { return await PmTilesArchive.OpenAsync(Url, root, Path.Combine(root, "tiles")); }
                        catch (Exception e) { GD.PrintErr($"[swissBUILDINGS3D] {e.Message}"); return null; }
                    });
                }
                return _archive;
            }
        }
    }
    static long _retryAt;

    /// <summary>Emprise grossière de la Suisse et du Liechtenstein (les tuiles absentes de l'archive restent génériques).</summary>
    public static bool Covers(double lat, double lon) => lat > 45.80 && lat < 47.82 && lon > 5.94 && lon < 10.50;

    /// <summary>Un bâtiment (ou un toit seul : abri, couvert) de swissBUILDINGS3D, dans le plan tangent à son point d'ancrage.</summary>
    public sealed class Building
    {
        public ulong Id;
        public double Lat, Lon;
        public bool RoofOnly;
        public string Type, Name, Usage;     // OBJEKTART, NAME_KOMPLETT, GEBAEUDE_NUTZUNG (allemand, comme chez swisstopo)
        public float Z0;                     // altitude du pied des murs (LN02), pour information
        public float RoofMin, RoofMax;       // bas et haut du toit, en m au-dessus du pied des murs
        /// <summary>Contours au sol (est, nord en m) : aire positive = contour extérieur, négative = cour intérieure
        /// du contour extérieur qui précède. Le premier est le plus grand.</summary>
        public List<List<V2>> Rings = new();
        public Vector3[] Verts;              // x = est, y = nord, z = hauteur au-dessus du pied des murs (m)
        public int WallTris, RoofTris;
        public ushort[] Idx;                 // triangles des murs, puis du toit
    }

    /// <summary>Décode une tuile (déjà décompressée). Liste vide si le format est inconnu.</summary>
    public static List<Building> Decode(byte[] data)
    {
        var list = new List<Building>();
        if (data == null || data.Length < 10 || data[0] != 'S' || data[1] != 'B' || data[2] != '3' || data[3] != 'D') return list;
        int p = 4;
        int version = BitConverter.ToUInt16(data, p); p += 2;
        if (version != 1) return list;
        int count = (int)BitConverter.ToUInt32(data, p); p += 4;
        try
        {
            for (int k = 0; k < count; k++)
            {
                var b = new Building();
                b.Id = BitConverter.ToUInt64(data, p); p += 8;
                b.Lat = BitConverter.ToInt32(data, p) * 1e-7; p += 4;
                b.Lon = BitConverter.ToInt32(data, p) * 1e-7; p += 4;
                b.RoofOnly = (data[p++] & 1) != 0;
                b.Type = Str(data, ref p); b.Name = Str(data, ref p); b.Usage = Str(data, ref p);
                double unit = data[p++] * 0.01;
                b.Z0 = BitConverter.ToSingle(data, p); p += 4;
                b.RoofMin = BitConverter.ToSingle(data, p); p += 4;
                b.RoofMax = BitConverter.ToSingle(data, p); p += 4;
                int rings = data[p++];
                for (int r = 0; r < rings; r++)
                {
                    int n = BitConverter.ToUInt16(data, p); p += 2;
                    var ring = new List<V2>(n);
                    for (int i = 0; i < n; i++, p += 4)
                        ring.Add(new V2(BitConverter.ToInt16(data, p) * unit, BitConverter.ToInt16(data, p + 2) * unit));
                    b.Rings.Add(ring);
                }
                int nv = BitConverter.ToUInt16(data, p); p += 2;
                b.Verts = new Vector3[nv];
                for (int i = 0; i < nv; i++, p += 6)
                    b.Verts[i] = new Vector3((float)(BitConverter.ToInt16(data, p) * unit), (float)(BitConverter.ToInt16(data, p + 2) * unit),
                        (float)(BitConverter.ToInt16(data, p + 4) * unit));
                b.WallTris = BitConverter.ToUInt16(data, p); p += 2;
                b.RoofTris = BitConverter.ToUInt16(data, p); p += 2;
                int ni = (b.WallTris + b.RoofTris) * 3;
                b.Idx = new ushort[ni];
                Buffer.BlockCopy(data, p, b.Idx, 0, ni * 2); p += ni * 2;
                if (string.IsNullOrEmpty(b.Name)) b.Name = null;
                if (string.IsNullOrEmpty(b.Usage)) b.Usage = null;
                bool ok = b.Rings.Count > 0 && b.Rings[0].Count >= 3;
                foreach (var i in b.Idx) if (i >= nv) { ok = false; break; }
                if (ok) list.Add(b);
            }
        }
        catch (ArgumentException) { } // tuile tronquée : on garde ce qui a été lu
        return list;
    }

    static string Str(byte[] d, ref int p)
    {
        int n = d[p++];
        var s = n > 0 ? Encoding.UTF8.GetString(d, p, n) : "";
        p += n;
        return s;
    }

    /// <summary>Classe OSM équivalente (pour <see cref="BuildingKinds.Classify"/>) d'après OBJEKTART et GEBAEUDE_NUTZUNG ;
    /// null quand swisstopo ne dit rien de plus qu'« un bâtiment » (la fonction vient alors d'OSM / Overture).</summary>
    public static string OsmClass(Building b)
    {
        string u = b.Usage?.ToLowerInvariant() ?? "";
        if (u.Contains("stadion")) return "stadium";
        if (u.Contains("parkhaus")) return "parking";
        if (u.Contains("reservoir") || u.Contains("tank")) return "storage_tank";
        if (u.Contains("bahnhof")) return "train_station";
        if (u.Contains("spital") || u.Contains("klinik")) return "hospital";
        if (u.Contains("schul")) return "school";
        if (u.Contains("kirche")) return "church";
        if (u.Contains("kapelle")) return "chapel";
        if (u.Contains("gasthof") || u.Contains("hotel") || u.Contains("berghaus") || u.Contains("hütte") || u.Contains("huette")) return "hotel";
        if (u.Contains("museum")) return "museum";
        if (u.Contains("observatorium") || u.Contains("sternwarte")) return "civic";
        return b.Type switch
        {
            "Sakrales Gebaeude" => "church",
            "Kapelle" => "chapel",
            "Sakraler Turm" => "church",
            "Treibhaus" => "greenhouse",
            "Offenes Gebaeude" or "Flugdach" or "Tankstellendach" or "Bruecke gedeckt" => "roof",
            "Lagertank" or "Kuehlturm" or "Hochkamin" or "Turm" or "Mauer gross" or "Mauer gross gedeckt" or "Lueftungsschacht" => "storage_tank",
            "Hochhaus" => "apartments",
            _ => null,
        };
    }

    /// <summary>Ouvrages où l'on n'entre pas (tours, cheminées, murs, citernes, toits seuls).</summary>
    public static bool Closed(Building b) => b.RoofOnly || b.Type is "Sakraler Turm" or "Hochkamin" or "Turm" or "Kuehlturm"
        or "Lagertank" or "Mauer gross" or "Mauer gross gedeckt" or "Lueftungsschacht" or "Im Bau" or "Bruecke gedeckt";
}
