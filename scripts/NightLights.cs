using System;
using System.IO;
using System.IO.Compression;
using Godot;

namespace SteamEarth;

/// <summary>
/// Lumières nocturnes des villes : NASA Black Marble 2016 (Suomi NPP VIIRS, domaine public),
/// grille lat/lon 8192×4096 (~5 km) en octets. Le terrain les affiche en émission là où il fait nuit.
/// </summary>
public static class NightLights
{
    const string Path = "res://data/nightlights/nightlights.bin.gz";
    static byte[] _grid;
    static int _w, _h;

    public static bool Loaded => _grid != null;

    public static void Init()
    {
        if (!Godot.FileAccess.FileExists(Path)) { GD.PrintErr("[NightLights] fichier absent : " + Path); return; }
        try
        {
            var raw = Godot.FileAccess.GetFileAsBytes(Path);
            using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            var b = ms.GetBuffer();
            if (b[0] != 'S' || b[1] != 'E' || b[2] != 'N' || b[3] != 'L') throw new InvalidDataException("en-tête SENL attendu");
            _w = BitConverter.ToInt32(b, 4);
            _h = BitConverter.ToInt32(b, 8);
            _grid = new byte[_w * _h];
            Buffer.BlockCopy(b, 16, _grid, 0, _grid.Length);
            GD.Print($"[NightLights] lumières des villes chargées ({_w}x{_h})");
        }
        catch (Exception e) { GD.PrintErr("[NightLights] " + e.Message); _grid = null; }
    }

    /// <summary>Texture mondiale (avec mipmaps, pour rester lisse vue de l'espace) lue par le shader du terrain.</summary>
    public static ImageTexture CreateTexture()
    {
        if (_grid == null) return null;
        var img = Image.CreateFromData(_w, _h, false, Image.Format.L8, _grid);
        img.GenerateMipmaps();
        return ImageTexture.CreateFromImage(img);
    }

    /// <summary>Intensité lumineuse 0..1 (interpolation bilinéaire).</summary>
    public static float Sample(double lat, double lon)
    {
        if (_grid == null) return 0;
        double x = (lon + 180) / 360 * _w - 0.5, y = (90 - lat) / 180 * _h - 0.5;
        int x0 = (int)Math.Floor(x), y0 = (int)Math.Floor(y);
        double fx = x - x0, fy = y - y0;
        float v00 = At(x0, y0), v10 = At(x0 + 1, y0), v01 = At(x0, y0 + 1), v11 = At(x0 + 1, y0 + 1);
        double v = (v00 * (1 - fx) + v10 * fx) * (1 - fy) + (v01 * (1 - fx) + v11 * fx) * fy;
        return (float)(v / 255.0);
    }

    static float At(int x, int y)
    {
        x = ((x % _w) + _w) % _w;
        y = Math.Clamp(y, 0, _h - 1);
        return _grid[y * _w + x];
    }
}
