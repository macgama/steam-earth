using System;
using System.IO;
using System.IO.Compression;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Couverture des sols : forêts, champs, prairies, glaciers, zones urbaines…
/// Source : ESA WorldCover 2021 (10 m, monde entier, CC BY 4.0).
/// - Près du joueur : les COG WorldCover téléchargés à la demande (<see cref="HeightProvider.WorldCover"/>,
///   le même raster que pour l'eau, donc aucun téléchargement en plus).
/// - Vues lointaines : une carte mondiale incluse dans le jeu (0,05°, ~5 km), générée par
///   tools/landcover/build_landcover_base.py.
/// Thread-safe.
/// </summary>
public static class LandCover
{
    // Classes WorldCover
    public const byte Unknown = 0, Tree = 10, Shrub = 20, Grass = 30, Crop = 40, BuiltUp = 50,
        Bare = 60, SnowIce = 70, Water = 80, Wetland = 90, Mangrove = 95, Moss = 100;

    const string BasePath = "res://data/landcover/world_cover.bin.gz";
    static byte[] _base;
    static int _bw, _bh;

    public static void Init()
    {
        try
        {
            var raw = Godot.FileAccess.GetFileAsBytes(BasePath);
            using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            var buf = ms.ToArray();
            if (System.Text.Encoding.ASCII.GetString(buf, 0, 4) != "SEL1") throw new IOException("format inconnu");
            _bw = BitConverter.ToInt32(buf, 4);
            _bh = BitConverter.ToInt32(buf, 8);
            _base = new byte[_bw * _bh];
            Buffer.BlockCopy(buf, 16, _base, 0, _base.Length);
            GD.Print($"[LandCover] carte mondiale {_bw}x{_bh}");
        }
        catch (Exception e) { GD.PrintErr("[LandCover] carte mondiale absente : " + e.Message); _base = null; }
    }

    /// <summary>Classe de la carte mondiale incluse (0 = mer ou inconnu).</summary>
    public static byte SampleBase(double lat, double lon)
    {
        if (_base == null) return Unknown;
        int x = (int)((lon + 180) / 360 * _bw), y = (int)((90 - lat) / 180 * _bh);
        x = ((x % _bw) + _bw) % _bw; y = Math.Clamp(y, 0, _bh - 1);
        return _base[y * _bw + x];
    }

    /// <summary>Niveau d'aperçu WorldCover (0 = 10 m … 6 = 640 m) adapté à l'espacement des sommets.</summary>
    public static int LevelFor(double spacing) => Math.Clamp((int)Math.Round(Math.Log2(Math.Max(spacing, 10) / 10)), 0, 6);

    /// <summary>
    /// Classe au point donné, pour un maillage dont les sommets sont espacés de <paramref name="spacing"/> m.
    /// <paramref name="fetch"/> = false : ne rien télécharger (répond avec ce qui est en mémoire, sinon la carte de base).
    /// </summary>
    public static byte Sample(double lat, double lon, double spacing, bool fetch, out bool pending)
    {
        pending = false;
        if (spacing >= HeightProvider.BaseOnlySpacing) return SampleBase(lat, lon);
        int level = LevelFor(spacing);
        var res = HeightProvider.WorldCover.Sample(lat, lon, level, false, fetch, out float cls);
        if (res == StreamedRaster.Result.Pending)
        {
            pending = fetch;
            for (int l = level + 1; l <= 6 && res != StreamedRaster.Result.Ok; l++)
                res = HeightProvider.WorldCover.Sample(lat, lon, l, false, false, out cls);
        }
        if (res == StreamedRaster.Result.Ok && !float.IsNaN(cls)) return (byte)cls;
        return SampleBase(lat, lon);
    }

    public static bool IsForest(byte c) => c is Tree or Mangrove;
}
