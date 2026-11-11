using System;
using Godot;

namespace SteamEarth.Data;

/// <summary>
/// swissALTI3D (swisstopo) : modèle numérique du terrain de la Suisse à 0,5 m (sol nu, sans bâtiments ni forêts),
/// en tuiles de 1 km × 1 km, projection suisse LV95 (EPSG:2056). Données ouvertes (OGD), usage commercial
/// autorisé avec mention « © swisstopo ».
///
/// Chaque fichier est un COG à 0,5 m avec trois aperçus (1, 2 et 4 m) : on ne télécharge que les blocs
/// de 128 × 128 pixels utiles, au niveau adapté à la distance (voir <see cref="StreamedRaster"/>).
/// L'URL contient l'année du relevé, propre à chaque tuile : elle est lue dans un petit index
/// (data/swissalti3d_index.bin, construit par tools/swissalti3d/build_index.py).
/// </summary>
public static class SwissAlti3D
{
    const string IndexPath = "res://data/swissalti3d_index.bin";

    /// <summary>Au-delà de cet écart entre sommets (m), on garde Copernicus 30 m : le 4 m suffirait mais
    /// télécharger toute la Suisse visible coûterait trop cher.</summary>
    public const double MaxSpacing = 16;

    static byte[] _years; // année − 2000 par km², 0 = pas de tuile ; ligne 0 = sud
    static int _e0, _n0, _w, _h;

    public static int Tiles { get; private set; }
    public static StreamedRaster Raster { get; private set; }

    public static void Init()
    {
        var raw = Godot.FileAccess.GetFileAsBytes(IndexPath);
        if (raw == null || raw.Length < 20 || System.Text.Encoding.ASCII.GetString(raw, 0, 4) != "SAI1")
        {
            GD.PrintErr("[swissALTI3D] index introuvable : " + IndexPath);
            return;
        }
        _e0 = BitConverter.ToInt32(raw, 4); _n0 = BitConverter.ToInt32(raw, 8);
        _w = BitConverter.ToInt32(raw, 12); _h = BitConverter.ToInt32(raw, 16);
        _years = new byte[_w * _h];
        Buffer.BlockCopy(raw, 20, _years, 0, _years.Length);
        int n = 0;
        foreach (var y in _years) if (y != 0) n++;
        Tiles = n;
        // 128×128 float = 64 Ko par bloc ; mémoire réglée ensuite selon la qualité (HeightProvider.ApplyQuality).
        Raster = new StreamedRaster("swissalti3d", Locate, 160, headBytes: 16384);
        GD.Print($"[swissALTI3D] {Tiles} km² disponibles à 0,5 m");
    }

    static int Year(double e, double n)
    {
        if (_years == null) return 0;
        int x = (int)Math.Floor(e / 1000) - _e0, y = (int)Math.Floor(n / 1000) - _n0;
        if (x < 0 || y < 0 || x >= _w || y >= _h) return 0;
        return _years[y * _w + x];
    }

    /// <summary>Vrai si le point est couvert par swissALTI3D.</summary>
    public static bool Covers(double lat, double lon)
    {
        if (_years == null || lat < 45.7 || lat > 47.9 || lon < 5.8 || lon > 10.6) return false;
        ToLV95(lat, lon, out double e, out double n);
        return Year(e, n) != 0;
    }

    static bool Locate(double lat, double lon, out string key, out string url, out double x, out double y)
    {
        ToLV95(lat, lon, out x, out y);
        int year = Year(x, y);
        if (year == 0) { key = url = null; return false; }
        int ek = (int)Math.Floor(x / 1000), nk = (int)Math.Floor(y / 1000);
        string id = $"swissalti3d_{2000 + year}_{ek}-{nk}";
        key = $"{ek}-{nk}";
        url = $"https://data.geo.admin.ch/ch.swisstopo.swissalti3d/{id}/{id}_0.5_2056_5728.tif";
        return true;
    }

    /// <summary>
    /// WGS84 (degrés) → coordonnées suisses LV95 (m), formules approchées de swisstopo
    /// (précision ≈ 1 m, sans conséquence : le décalage est le même pour toutes les tuiles voisines).
    /// </summary>
    public static void ToLV95(double lat, double lon, out double e, out double n)
    {
        double p = (lat * 3600 - 169028.66) / 10000;
        double l = (lon * 3600 - 26782.5) / 10000;
        e = 2600072.37 + 211455.93 * l - 10938.51 * l * p - 0.36 * l * p * p - 44.54 * l * l * l;
        n = 1200147.07 + 308807.95 * p + 3745.25 * l * l + 76.63 * p * p - 194.56 * l * l * p + 119.79 * p * p * p;
    }
}
