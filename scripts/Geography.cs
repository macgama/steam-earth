using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Godot;

namespace SteamEarth;

/// <summary>Un pays et sa capitale (Natural Earth, domaine public).</summary>
public sealed class Country
{
    public string Name, Capital, Iso;
    public double Lat, Lon;
    public override string ToString() => $"{Name} ({Capital})";
}

/// <summary>Données géographiques politiques : pays, capitales, frontières terrestres.</summary>
public static class Geography
{
    public static List<Country> Countries { get; private set; } = new();
    /// <summary>Frontières : polylignes de (lat, lon) en degrés.</summary>
    public static List<double[]> Borders { get; private set; } = new();

    public static void Init()
    {
        try
        {
            var json = Godot.FileAccess.GetFileAsString("res://data/geo/countries.json");
            using var doc = JsonDocument.Parse(json);
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                Countries.Add(new Country
                {
                    Name = e.GetProperty("name").GetString(),
                    Capital = e.GetProperty("capital").GetString(),
                    Iso = e.GetProperty("iso").GetString(),
                    Lat = e.GetProperty("lat").GetDouble(),
                    Lon = e.GetProperty("lon").GetDouble(),
                });
            }
            var fr = new System.Globalization.CultureInfo("fr-FR");
            Countries.Sort((a, b) => string.Compare(a.Name, b.Name, fr, System.Globalization.CompareOptions.IgnoreCase));

            var raw = Godot.FileAccess.GetFileAsBytes("res://data/geo/borders.bin.gz");
            using var z = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var br = new BinaryReader(z);
            if (new string(br.ReadChars(4)) != "SEBL") throw new InvalidDataException("frontières");
            int n = br.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                int m = br.ReadInt32();
                var pts = new double[2 * m];
                for (int k = 0; k < 2 * m; k++) pts[k] = br.ReadSingle();
                Borders.Add(pts);
            }
            GD.Print($"[Geography] {Countries.Count} pays, {Borders.Count} lignes de frontière");
        }
        catch (Exception e)
        {
            GD.PrintErr("[Geography] " + e.Message);
        }
    }
}
