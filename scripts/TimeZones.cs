using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using Godot;

namespace SteamEarth;

/// <summary>
/// Fuseaux horaires réels (frontières IANA de timezone-boundary-builder, ODbL) : donne le fuseau
/// et l'heure légale locale (heure d'été comprise) en tout point du globe.
/// La grille (0,025°, ~2,8 km) est gardée en mémoire sous forme de plages par ligne (quelques centaines de Ko).
/// Les règles (décalage, heure d'été) viennent de la base IANA du système via .NET <see cref="TimeZoneInfo"/>.
/// </summary>
public static class TimeZones
{
    const string Path = "res://data/timezones/timezones.bin.gz";

    static string[] _names;
    static int _w, _h;
    static int[][] _runStart;   // par ligne : colonne de début de chaque plage
    static ushort[][] _runId;   // par ligne : fuseau de chaque plage
    static readonly ConcurrentDictionary<string, TimeZoneInfo> Cache = new();

    public static bool Loaded => _names != null;

    public static void Init()
    {
        var raw = Godot.FileAccess.GetFileAsBytes(Path);
        if (raw == null || raw.Length == 0) { GD.PrintErr("[TimeZones] fichier absent : " + Path); return; }
        using var z = new GZipStream(new MemoryStream(raw), CompressionMode.Decompress);
        using var br = new BinaryReader(z);
        if (new string(br.ReadChars(4)) != "SETZ") { GD.PrintErr("[TimeZones] format inconnu"); return; }
        _w = br.ReadInt32(); _h = br.ReadInt32();
        int n = br.ReadInt32();
        var names = new string[n];
        for (int i = 0; i < n; i++) names[i] = System.Text.Encoding.UTF8.GetString(br.ReadBytes(br.ReadByte()));

        _runStart = new int[_h][];
        _runId = new ushort[_h][];
        var row = new byte[_w * 2];
        var starts = new List<int>();
        var ids = new List<ushort>();
        for (int y = 0; y < _h; y++)
        {
            int read = 0;
            while (read < row.Length) { int r = z.Read(row, read, row.Length - read); if (r <= 0) throw new EndOfStreamException(); read += r; }
            starts.Clear(); ids.Clear();
            ushort prev = 0xFFFF;
            for (int x = 0; x < _w; x++)
            {
                ushort id = (ushort)(row[2 * x] | (row[2 * x + 1] << 8));
                if (id != prev || x == 0) { starts.Add(x); ids.Add(id); prev = id; }
            }
            _runStart[y] = starts.ToArray();
            _runId[y] = ids.ToArray();
        }
        _names = names;
        GD.Print($"[TimeZones] {n} fuseaux chargés ({_w}x{_h})");
    }

    /// <summary>Identifiant IANA du fuseau (ex. « Europe/Zurich »), ou null si inconnu.</summary>
    public static string ZoneId(double latDeg, double lonDeg)
    {
        if (_names == null) return null;
        int y = Math.Clamp((int)((90 - latDeg) / 180 * _h), 0, _h - 1);
        int x = Math.Clamp((int)(((lonDeg + 180) % 360 + 360) % 360 / 360 * _w), 0, _w - 1);
        var st = _runStart[y];
        int i = Array.BinarySearch(st, x);
        if (i < 0) i = ~i - 1;
        ushort id = _runId[y][i];
        return id < _names.Length ? _names[id] : null;
    }

    static TimeZoneInfo Find(string id) => Cache.GetOrAdd(id, k =>
    {
        try { return TimeZoneInfo.FindSystemTimeZoneById(k); }
        catch (Exception) { return null; }
    });

    /// <summary>
    /// Heure légale locale. Si le fuseau est introuvable sur la machine, on se rabat sur
    /// le fuseau nautique (longitude / 15 h, sans heure d'été).
    /// </summary>
    public static DateTime LocalTime(DateTime utc, double latDeg, double lonDeg, out string zoneId, out TimeSpan offset, out bool dst)
    {
        zoneId = ZoneId(latDeg, lonDeg);
        var tz = zoneId != null ? Find(zoneId) : null;
        if (tz != null)
        {
            offset = tz.GetUtcOffset(utc);
            dst = tz.IsDaylightSavingTime(utc);
            return DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified);
        }
        offset = TimeSpan.FromHours(Math.Round(lonDeg / 15));
        dst = false;
        zoneId ??= "nautique";
        return DateTime.SpecifyKind(utc + offset, DateTimeKind.Unspecified);
    }

    /// <summary>Texte « UTC+1 », « UTC−3:30 »…</summary>
    public static string FormatOffset(TimeSpan o)
    {
        if (o == TimeSpan.Zero) return "UTC";
        string sign = o < TimeSpan.Zero ? "−" : "+";
        o = o.Duration();
        return o.Minutes == 0 ? $"UTC{sign}{o.Hours}" : $"UTC{sign}{o.Hours}:{o.Minutes:00}";
    }
}
