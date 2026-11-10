using System;
using System.Collections.Generic;
using System.Text;

namespace SteamEarth.Data;

/// <summary>
/// Décodeur minimal de tuiles vectorielles Mapbox Vector Tile (protobuf), sans dépendance.
/// Les coordonnées sont rendues en fraction de tuile (0..1, peuvent dépasser un peu : marge de la tuile).
/// </summary>
public static class Mvt
{
    public enum GeomType { Unknown = 0, Point = 1, Line = 2, Polygon = 3 }

    public sealed class Feature
    {
        public GeomType Type;
        public Dictionary<string, object> Props = new();
        /// <summary>Points (x, y) en fraction de tuile ; une liste par partie (ligne, anneau, point).</summary>
        public List<double[]> Parts = new();

        public string Str(string key) => Props.TryGetValue(key, out var v) ? v as string : null;
        public double Num(string key, double def = double.NaN) =>
            Props.TryGetValue(key, out var v) ? v switch { double d => d, float f => f, long l => l, ulong u => u, bool b => b ? 1 : 0, _ => def } : def;
    }

    /// <summary>Décode les couches demandées (toutes si <paramref name="wanted"/> est null).
    /// <paramref name="keepKeys"/> : propriétés à garder (toutes si null) — évite de construire des chaînes inutiles.</summary>
    public static Dictionary<string, List<Feature>> Decode(byte[] b, ISet<string> wanted = null, ISet<string> keepKeys = null)
    {
        var result = new Dictionary<string, List<Feature>>();
        int p = 0;
        while (p < b.Length)
        {
            ulong key = Varint(b, ref p);
            if ((key >> 3) == 3 && (key & 7) == 2)
            {
                int len = (int)Varint(b, ref p);
                DecodeLayer(b, p, p + len, wanted, keepKeys, result);
                p += len;
            }
            else Skip(b, ref p, (int)(key & 7));
        }
        return result;
    }

    static void DecodeLayer(byte[] b, int start, int end, ISet<string> wanted, ISet<string> keepKeys, Dictionary<string, List<Feature>> result)
    {
        // Premier passage : nom, étendue, clés et valeurs (ils peuvent être après les entités)
        string name = null; uint extent = 4096;
        var keys = new List<string>(); var values = new List<object>(); var feats = new List<(int, int)>();
        int p = start;
        while (p < end)
        {
            ulong key = Varint(b, ref p);
            int field = (int)(key >> 3), wt = (int)(key & 7);
            if (wt == 2)
            {
                int len = (int)Varint(b, ref p);
                switch (field)
                {
                    case 1: name = Encoding.UTF8.GetString(b, p, len); break;
                    case 2: feats.Add((p, p + len)); break;
                    case 3: keys.Add(Encoding.UTF8.GetString(b, p, len)); break;
                    case 4: values.Add(null); break; // décodé à la demande
                }
                if (field == 4) values[^1] = (p, p + len);
                p += len;
            }
            else if (field == 5 && wt == 0) extent = (uint)Varint(b, ref p);
            else Skip(b, ref p, wt);
        }
        if (name == null || (wanted != null && !wanted.Contains(name))) return;
        var list = new List<Feature>(feats.Count);
        double inv = 1.0 / extent;
        foreach (var (fs, fe) in feats)
        {
            var f = new Feature();
            int q = fs;
            int geomStart = -1, geomEnd = -1;
            while (q < fe)
            {
                ulong key = Varint(b, ref q);
                int field = (int)(key >> 3), wt = (int)(key & 7);
                if (field == 3 && wt == 0) f.Type = (GeomType)Varint(b, ref q);
                else if (field == 2 && wt == 2)
                {
                    int len = (int)Varint(b, ref q), te = q + len;
                    while (q < te)
                    {
                        int k = (int)Varint(b, ref q), v = (int)Varint(b, ref q);
                        if (k >= keys.Count || v >= values.Count) continue;
                        string kn = keys[k];
                        if (keepKeys != null && !keepKeys.Contains(kn)) continue;
                        if (values[v] is ValueTuple<int, int> range) values[v] = DecodeValue(b, range.Item1, range.Item2);
                        f.Props[kn] = values[v];
                    }
                }
                else if (field == 4 && wt == 2)
                {
                    int len = (int)Varint(b, ref q);
                    geomStart = q; geomEnd = q + len; q += len;
                }
                else Skip(b, ref q, wt);
            }
            if (geomStart >= 0) DecodeGeometry(b, geomStart, geomEnd, inv, f);
            list.Add(f);
        }
        result[name] = list;
    }

    static void DecodeGeometry(byte[] b, int p, int end, double inv, Feature f)
    {
        long x = 0, y = 0;
        List<double> cur = null;
        while (p < end)
        {
            uint cmd = (uint)Varint(b, ref p);
            int id = (int)(cmd & 7), count = (int)(cmd >> 3);
            if (id == 7) { if (cur != null && cur.Count >= 2) { cur.Add(cur[0]); cur.Add(cur[1]); } continue; } // ClosePath
            for (int i = 0; i < count; i++)
            {
                x += Zigzag(Varint(b, ref p));
                y += Zigzag(Varint(b, ref p));
                if (id == 1) // MoveTo : nouvelle partie
                {
                    if (cur != null && cur.Count > 0) f.Parts.Add(cur.ToArray());
                    cur = new List<double>();
                }
                cur ??= new List<double>();
                cur.Add(x * inv); cur.Add(y * inv);
            }
        }
        if (cur != null && cur.Count > 0) f.Parts.Add(cur.ToArray());
    }

    static object DecodeValue(byte[] b, int p, int end)
    {
        object v = null;
        while (p < end)
        {
            ulong key = Varint(b, ref p);
            int field = (int)(key >> 3), wt = (int)(key & 7);
            switch (field)
            {
                case 1: { int len = (int)Varint(b, ref p); v = Encoding.UTF8.GetString(b, p, len); p += len; break; }
                case 2: v = (double)BitConverter.ToSingle(b, p); p += 4; break;
                case 3: v = BitConverter.ToDouble(b, p); p += 8; break;
                case 4: v = (double)(long)Varint(b, ref p); break;
                case 5: v = (double)Varint(b, ref p); break;
                case 6: v = (double)Zigzag(Varint(b, ref p)); break;
                case 7: v = Varint(b, ref p) != 0; break;
                default: Skip(b, ref p, wt); break;
            }
        }
        return v;
    }

    static long Zigzag(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);

    static ulong Varint(byte[] b, ref int p)
    {
        ulong r = 0; int s = 0;
        while (true)
        {
            byte x = b[p++];
            r |= (ulong)(x & 0x7f) << s;
            if (x < 0x80) return r;
            s += 7;
        }
    }

    static void Skip(byte[] b, ref int p, int wt)
    {
        switch (wt)
        {
            case 0: Varint(b, ref p); break;
            case 1: p += 8; break;
            case 2: { int len = (int)Varint(b, ref p); p += len; break; }
            case 5: p += 4; break;
            default: throw new FormatException("protobuf : type de champ inconnu " + wt);
        }
    }
}
