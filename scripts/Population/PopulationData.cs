using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Godot;

namespace SteamEarth.Population;

/// <summary>Langue parlée (Suisses : selon la commune ; étrangers : selon leur pays).</summary>
public enum Lang : byte { De = 0, Fr = 1, It = 2, Rm = 3 }

/// <summary>Commune suisse (swissBOUNDARIES3D © swisstopo).</summary>
public sealed class Commune
{
    public int Index, Bfs, Canton, Population;
    public string Name;
    public Lang Lang1, Lang2;
    public double Lang1Share;
    /// <summary>Centre habité (LV95, m).</summary>
    public double E, N;
}

/// <summary>Profil d'un carré de 1 km : âges et sexes, nationalités, tailles des ménages (proportions relatives).</summary>
public sealed class BlockProfile
{
    /// <summary>[0..18] hommes 0-4, 5-9… 90+ ; [19..37] femmes.</summary>
    public readonly float[] AgeSex = new float[38];
    /// <summary>Suisses, UE/AELE, reste de l'Europe, hors Europe.</summary>
    public readonly float[] Nationality = new float[4];
    /// <summary>Ménages de 1, 2… 6+ personnes.</summary>
    public readonly float[] Households = new float[6];
}

/// <summary>
/// Habitants de la Suisse par hectare (STATPOP 2025, Office fédéral de la statistique), chargés de
/// res://data/population/statpop_ch.bin.gz (voir tools/population/build_population.py et docs/POPULATION.md).
/// Lecture seule après <see cref="Init"/> : utilisable depuis tous les threads.
/// </summary>
public static class PopulationData
{
    const string DataPath = "res://data/population/statpop_ch.bin.gz";

    public static bool Loaded { get; private set; }
    public static int Year { get; private set; }
    public static long TotalResidents { get; private set; }
    public static Commune[] Communes { get; private set; } = Array.Empty<Commune>();

    // hectare (e, n en centaines de m LV95) → (habitants, commune)
    static readonly Dictionary<int, (ushort pop, ushort commune)> Cells = new();
    static readonly Dictionary<int, BlockProfile> Blocks = new();
    static readonly BlockProfile Fallback = new();

    static int Key(int e, int n) => e * 20000 + n; // e ∈ [24800, 28400], n ∈ [10700, 13000]

    public static void Init()
    {
        if (Loaded) return;
        try
        {
            var raw = Godot.FileAccess.GetFileAsBytes(DataPath);
            if (raw == null || raw.Length == 0) { GD.PushWarning("[Population] données introuvables : " + DataPath); return; }
            using var gz = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            gz.CopyTo(ms);
            Parse(ms.ToArray());
            Loaded = true;
            GD.Print($"[Population] STATPOP {Year} : {Cells.Count:N0} hectares habités, {TotalResidents:N0} habitants, {Communes.Length} communes");
        }
        catch (Exception ex) { GD.PushWarning("[Population] lecture impossible : " + ex.Message); }
    }

    static void Parse(byte[] b)
    {
        var r = new BinaryReader(new MemoryStream(b));
        if (Encoding.ASCII.GetString(r.ReadBytes(4)) != "POPC" || r.ReadInt32() != 1) throw new InvalidDataException("format inconnu");
        Year = r.ReadInt32();
        int nc = r.ReadInt32();
        var communes = new Commune[nc];
        for (int i = 0; i < nc; i++)
        {
            var c = new Commune { Index = i, Bfs = r.ReadUInt16(), Canton = r.ReadByte(), Lang1 = (Lang)r.ReadByte(), Lang2 = (Lang)r.ReadByte() };
            c.Lang1Share = r.ReadByte() / 255.0;
            c.E = (r.ReadUInt16() + 24000) * 100.0 + 50;
            c.N = (r.ReadUInt16() + 10000) * 100.0 + 50;
            c.Population = (int)r.ReadUInt32();
            c.Name = Encoding.UTF8.GetString(r.ReadBytes(r.ReadByte()));
            communes[i] = c;
        }
        Communes = communes;
        int nb = r.ReadInt32();
        for (int i = 0; i < nb; i++)
        {
            int bx = r.ReadUInt16() + 2480, by = r.ReadUInt16() + 1070;
            var p = new BlockProfile();
            for (int k = 0; k < 38; k++) p.AgeSex[k] = r.ReadByte();
            for (int k = 0; k < 4; k++) p.Nationality[k] = r.ReadByte();
            for (int k = 0; k < 6; k++) p.Households[k] = r.ReadByte();
            Blocks[bx * 2000 + by] = p;
        }
        int n = r.ReadInt32();
        var es = new ushort[n]; var ns = new ushort[n]; var pops = new ushort[n];
        for (int i = 0; i < n; i++) es[i] = r.ReadUInt16();
        for (int i = 0; i < n; i++) ns[i] = r.ReadUInt16();
        for (int i = 0; i < n; i++) pops[i] = r.ReadUInt16();
        long total = 0;
        for (int i = 0; i < n; i++)
        {
            ushort com = r.ReadUInt16();
            Cells[Key(es[i] + 24800, ns[i] + 10700)] = (pops[i], com);
            total += pops[i];
        }
        TotalResidents = total;
        // profil national (blocs sans données)
        foreach (var p in Blocks.Values)
            for (int k = 0; k < 38; k++) Fallback.AgeSex[k] += p.AgeSex[k];
        for (int k = 0; k < 4; k++) Fallback.Nationality[k] = new[] { 70f, 18, 6, 6 }[k];
        for (int k = 0; k < 6; k++) Fallback.Households[k] = new[] { 40f, 36, 16, 16, 7, 3 }[k];
    }

    /// <summary>Habitants de l'hectare (e, n en centaines de mètres LV95) ; 0 si inhabité ou hors de Suisse.</summary>
    public static int Residents(int e, int n, out Commune commune)
    {
        commune = null;
        if (!Cells.TryGetValue(Key(e, n), out var c)) return 0;
        commune = c.commune < Communes.Length ? Communes[c.commune] : null;
        return c.pop;
    }

    public static BlockProfile Profile(int e, int n) =>
        Blocks.TryGetValue((e / 10) * 2000 + n / 10, out var p) ? p : Fallback;

    /// <summary>Hectare (centaines de m LV95) qui contient (lat, lon).</summary>
    public static void CellAt(double lat, double lon, out int e, out int n)
    {
        Data.SwissAlti3D.ToLV95(lat, lon, out double ee, out double nn);
        e = (int)Math.Floor(ee / 100); n = (int)Math.Floor(nn / 100);
    }

    /// <summary>LV95 (m) → WGS84 (degrés), formules approchées de swisstopo (≈ 1 m).</summary>
    public static void ToWgs84(double e, double n, out double lat, out double lon)
    {
        double y = (e - 2_600_000) / 1e6, x = (n - 1_200_000) / 1e6;
        double lam = 2.6779094 + 4.728982 * y + 0.791484 * y * x + 0.1306 * y * x * x - 0.0436 * y * y * y;
        double phi = 16.9023892 + 3.238272 * x - 0.270978 * y * y - 0.002528 * x * x - 0.0447 * y * y * x - 0.0140 * x * x * x;
        lat = phi * 100 / 36; lon = lam * 100 / 36;
    }

    /// <summary>Commune habitée la plus proche de (e, n) en m, d'au moins <paramref name="minPop"/> habitants.</summary>
    public static Commune NearestCommune(double e, double n, int minPop, Commune except = null)
    {
        Commune best = null; double bd = double.MaxValue;
        foreach (var c in Communes)
        {
            if (c.Population < minPop || c == except) continue;
            double d = (c.E - e) * (c.E - e) + (c.N - n) * (c.N - n);
            if (d < bd) { bd = d; best = c; }
        }
        return best;
    }

    /// <summary>Communes d'un canton (pour le lieu d'origine des Suisses).</summary>
    public static List<Commune> InCanton(int canton)
    {
        var l = new List<Commune>();
        foreach (var c in Communes) if (c.Canton == canton) l.Add(c);
        return l;
    }

    public static readonly string[] CantonCodes =
    {
        "", "ZH", "BE", "LU", "UR", "SZ", "OW", "NW", "GL", "ZG", "FR", "SO", "BS", "BL", "SH", "AR", "AI", "SG", "GR",
        "AG", "TG", "TI", "VD", "VS", "NE", "GE", "JU",
    };
}
