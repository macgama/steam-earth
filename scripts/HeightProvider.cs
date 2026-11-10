using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Fournit l'altitude (m) en tout point du globe, au niveau de détail demandé.
/// - Suisse : swissALTI3D à 0,5 m (swisstopo), téléchargé à la demande quand on est près du sol (voir SwissAlti3D).
/// - Copernicus DEM GLO-30 (30 m, monde entier) et ESA WorldCover (eau, 10 m) téléchargés à la demande
///   depuis AWS Open Data, par tuiles internes de COG, mis en cache sur disque (voir StreamedRaster).
/// - Carte mondiale de base (équirectangulaire 4096×2048, ~10 km, avec bathymétrie).
/// - Tuiles de détail 1°×1° à 30 m (Copernicus GLO-30) + masque d'eau (ESA WorldCover),
///   chargées à la demande depuis res://data/detail/ et gardées en cache.
/// Thread-safe : appelé depuis les threads de construction des chunks.
/// </summary>
public static class HeightProvider
{
    const string BasePath = "res://data/world_base.hgt.gz";
    const string DetailDir = "res://data/detail";

    static short[] _base;
    static int _bw, _bh;
    static readonly HashSet<int> Available = new();
    static readonly ConcurrentDictionary<int, Lazy<DetailTile>> Tiles = new();

    static string Hemi(int v, char pos, char neg, string fmt) => (v >= 0 ? pos : neg) + Math.Abs(v).ToString(fmt);

    /// <summary>Copernicus DEM GLO-30 : un COG par case de 1°, aperçus ×2, ×4, ×8.</summary>
    public static readonly StreamedRaster Copernicus = new("copernicus30", 1, (lat, lon) =>
    {
        string n = $"Copernicus_DSM_COG_10_{Hemi(lat, 'N', 'S', "00")}_00_{Hemi(lon, 'E', 'W', "000")}_00_DEM";
        return $"https://copernicus-dem-30m.s3.amazonaws.com/{n}/{n}.tif";
    }, 96);

    /// <summary>ESA WorldCover 2021 (10 m) : un COG par case de 3°, classe 80 = eau permanente.</summary>
    public static readonly StreamedRaster WorldCover = new("worldcover", 3, (lat, lon) =>
        $"https://esa-worldcover.s3.eu-central-1.amazonaws.com/v200/2021/map/ESA_WorldCover_10m_2021_v200_{Hemi(lat, 'N', 'S', "00")}{Hemi(lon, 'E', 'W', "000")}_Map.tif", 64);

    /// <summary>Au-delà de cet écart entre sommets, la carte mondiale de base suffit.</summary>
    public const double BaseOnlySpacing = 2000;

    sealed class DetailTile
    {
        public short[] H;
        public byte[] W;
        public int N;
    }

    public static int LoadedTiles => Tiles.Count;
    public static int AvailableTiles => Available.Count;

    public static void Init()
    {
        _base = ReadHeights(BasePath, out _bw, out _bh);
        using var dir = DirAccess.Open(DetailDir);
        if (dir != null)
        {
            foreach (var f in dir.GetFiles())
            {
                // Dans un export, les fichiers peuvent être listés avec le suffixe .remap ; on l'ignore.
                var name = f.Replace(".remap", "");
                if (!name.EndsWith(".hgt.gz")) continue;
                if (TryParseName(name, out int lat, out int lon)) Available.Add(Key(lat, lon));
            }
        }
        GD.Print($"[HeightProvider] base {_bw}x{_bh}, {Available.Count} tuiles de détail disponibles");
        SwissAlti3D.Init();
    }

    static int Key(int lat, int lon) => (lat + 90) * 360 + (lon + 180);

    static bool TryParseName(string n, out int lat, out int lon)
    {
        lat = lon = 0;
        if (n.Length < 7) return false;
        if (!int.TryParse(n.Substring(1, 2), out lat) || !int.TryParse(n.Substring(4, 3), out lon)) return false;
        if (n[0] == 'S') lat = -lat;
        if (n[3] == 'W') lon = -lon;
        return true;
    }

    static string TileName(int lat, int lon) =>
        $"{(lat >= 0 ? 'N' : 'S')}{Math.Abs(lat):00}{(lon >= 0 ? 'E' : 'W')}{Math.Abs(lon):000}";

    static byte[] Decompress(string path, out int w, out int h, string magic)
    {
        var raw = Godot.FileAccess.GetFileAsBytes(path);
        if (raw == null || raw.Length == 0) throw new IOException("Fichier introuvable : " + path);
        using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        var buf = ms.ToArray();
        if (System.Text.Encoding.ASCII.GetString(buf, 0, 4) != magic) throw new IOException("Format inconnu : " + path);
        w = BitConverter.ToInt32(buf, 4);
        h = BitConverter.ToInt32(buf, 8);
        return buf;
    }

    static short[] ReadHeights(string path, out int w, out int h)
    {
        var buf = Decompress(path, out w, out h, "SEH1");
        var data = new short[w * h];
        Buffer.BlockCopy(buf, 16, data, 0, w * h * 2);
        // Décodage delta par ligne
        for (int y = 0; y < h; y++)
        {
            int row = y * w;
            for (int x = 1; x < w; x++) data[row + x] = (short)(data[row + x] + data[row + x - 1]);
        }
        return data;
    }

    static DetailTile LoadTile(int lat, int lon)
    {
        string n = TileName(lat, lon);
        var t = new DetailTile();
        t.H = ReadHeights($"{DetailDir}/{n}.hgt.gz", out t.N, out _);
        try
        {
            var buf = Decompress($"{DetailDir}/{n}.wat.gz", out int ww, out int wh, "SEW1");
            t.W = new byte[ww * wh];
            Buffer.BlockCopy(buf, 16, t.W, 0, t.W.Length);
        }
        catch (Exception) { t.W = null; }
        GD.Print($"[HeightProvider] tuile {n} chargée");
        return t;
    }

    /// <summary>
    /// Altitude (m) pour un maillage dont les sommets sont espacés de <paramref name="spacing"/> mètres.
    /// <paramref name="pending"/> = true si une donnée plus fine est en cours de téléchargement
    /// (la valeur rendue est alors provisoire).
    /// </summary>
    public static float Sample(double latDeg, double lonDeg, double spacing, out bool water, out bool pending)
    {
        pending = false;
        float hb = SampleBase(latDeg, lonDeg);
        if (spacing >= BaseOnlySpacing) { water = hb <= 0; return hb; }

        // 0. Zones haute définition (Suisse, 0,5 m) quand on est près du sol
        if (spacing < SwissAlti3D.MaxSpacing && SwissAlti3D.Covers(latDeg, lonDeg))
        {
            int hl = spacing < 0.9 ? 0 : spacing < 1.8 ? 1 : spacing < 3.6 ? 2 : 3; // 0,5 / 1 / 2 / 4 m
            var hr = SwissAlti3D.Raster.Sample(latDeg, lonDeg, hl, true, true, out float hv);
            if (hr == StreamedRaster.Result.Pending)
            {
                pending = true;
                for (int l = hl + 1; l <= 3 && hr != StreamedRaster.Result.Ok; l++)
                    hr = SwissAlti3D.Raster.Sample(latDeg, lonDeg, l, true, false, out hv);
            }
            if (hr == StreamedRaster.Result.Ok)
            {
                water = WaterAt(latDeg, lonDeg, spacing, hv, hb, false, ref pending);
                return hv;
            }
            // sinon (téléchargement en cours ou tuile absente) : Copernicus 30 m en attendant
        }

        // 1. Tuiles locales (incluses dans le jeu) : disponibles hors ligne, sans attente
        float h = Sample(latDeg, lonDeg, true, out water, baseFallback: false, out bool local);
        if (local) return h;

        // 2. Copernicus 30 m en ligne, à l'aperçu adapté à l'espacement
        int level = spacing < 45 ? 0 : spacing < 90 ? 1 : spacing < 180 ? 2 : 3;
        var res = Copernicus.Sample(latDeg, lonDeg, level, true, true, out float v);
        if (res == StreamedRaster.Result.Pending)
        {
            pending = true;
            // en attendant : un aperçu plus grossier déjà en mémoire, sinon la carte de base
            for (int l = level + 1; l <= 3 && res != StreamedRaster.Result.Ok; l++)
                res = Copernicus.Sample(latDeg, lonDeg, l, true, false, out v);
        }
        h = res == StreamedRaster.Result.Ok ? v : hb;
        bool ocean = res == StreamedRaster.Result.Absent; // pas de tuile Copernicus = mer

        water = WaterAt(latDeg, lonDeg, spacing, h, hb, ocean, ref pending);
        return h;
    }

    /// <summary>Eau (lacs, rivières, mer) : WorldCover, aperçu adapté à l'espacement.</summary>
    static bool WaterAt(double latDeg, double lonDeg, double spacing, float h, float hb, bool ocean, ref bool pending)
    {
        bool water;
        int wl = Math.Clamp((int)Math.Round(Math.Log2(Math.Max(spacing, 10) / 10)), 0, 6);
        var wres = WorldCover.Sample(latDeg, lonDeg, wl, false, true, out float cls);
        if (wres == StreamedRaster.Result.Ok) water = cls == 80;
        else
        {
            if (wres == StreamedRaster.Result.Pending) pending = true;
            water = ocean ? hb <= 0 || h <= 0 : h <= 0 && hb <= 0;
        }
        if (ocean) water = water || h <= 0;
        return water;
    }

    /// <summary>Altitude en mètres. <paramref name="useDetail"/> : utiliser les tuiles 30 m locales si présentes.</summary>
    public static float Sample(double latDeg, double lonDeg, bool useDetail, out bool water)
        => Sample(latDeg, lonDeg, useDetail, out water, baseFallback: true, out _);

    static float Sample(double latDeg, double lonDeg, bool useDetail, out bool water, bool baseFallback, out bool fromLocal)
    {
        fromLocal = false;
        if (useDetail)
        {
            int la = (int)Math.Floor(latDeg), lo = (int)Math.Floor(lonDeg);
            int key = Key(la, lo);
            if (Available.Contains(key))
            {
                var tile = Tiles.GetOrAdd(key, _ => new Lazy<DetailTile>(() => LoadTile(la, lo))).Value;
                fromLocal = true;
                return SampleTile(tile, latDeg - la, lonDeg - lo, out water);
            }
        }
        if (!baseFallback) { water = false; return 0; }
        float hb = SampleBase(latDeg, lonDeg);
        water = hb <= 0;
        return hb;
    }

    static float SampleTile(DetailTile t, double fy, double fx, out bool water)
    {
        int n = t.N;
        double px = fx * (n - 1), py = (1 - fy) * (n - 1); // ligne 0 = nord
        int x0 = Math.Clamp((int)px, 0, n - 2), y0 = Math.Clamp((int)py, 0, n - 2);
        double tx = px - x0, ty = py - y0;
        int i = y0 * n + x0;
        double h = (t.H[i] * (1 - tx) + t.H[i + 1] * tx) * (1 - ty) + (t.H[i + n] * (1 - tx) + t.H[i + n + 1] * tx) * ty;
        water = t.W != null && t.W[(int)Math.Round(py) * n + (int)Math.Round(px)] != 0;
        return (float)h;
    }

    static float SampleBase(double latDeg, double lonDeg)
    {
        double px = (lonDeg + 180) / 360 * _bw - 0.5;
        double py = (90 - latDeg) / 180 * _bh - 0.5;
        int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
        double tx = px - x0, ty = py - y0;
        int x1 = ((x0 + 1) % _bw + _bw) % _bw; x0 = (x0 % _bw + _bw) % _bw;
        int y1 = Math.Clamp(y0 + 1, 0, _bh - 1); y0 = Math.Clamp(y0, 0, _bh - 1);
        double a = _base[y0 * _bw + x0], b = _base[y0 * _bw + x1], c = _base[y1 * _bw + x0], d = _base[y1 * _bw + x1];
        return (float)((a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty);
    }

    /// <summary>Altitude de la surface praticable (l'eau compte comme surface).</summary>
    /// <param name="spacing">précision voulue (m) : 0,5 sous les pieds du joueur (le maillage le plus fin) ;
    /// plus grossier pour les objets lointains (frontières…), afin de ne pas télécharger toute la planète.</param>
    public static float SurfaceHeight(double latDeg, double lonDeg, double spacing = 0.5)
    {
        float h = Sample(latDeg, lonDeg, spacing, out bool water, out _);
        return water ? Math.Max(h, 0) : h;
    }
}
