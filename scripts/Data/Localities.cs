using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading.Tasks;

namespace SteamEarth.Data;

/// <summary>
/// Toutes les villes, villes moyennes et villages de la Terre (~1,7 million, OpenStreetMap via Overture Maps,
/// licence ODbL), pour la recherche du menu de départ. Fichier <c>data/geo/localities.tsv.br</c> généré par
/// <c>tools/geo/build_localities.py</c>, chargé en arrière-plan au démarrage (~2 s).
/// Mémoire : le texte brut (UTF-8) et une chaîne de recherche normalisée (minuscules, sans accents), où l'on cherche
/// avec <see cref="string.IndexOf(string, int, StringComparison)"/> : quelques millisecondes pour toute la Terre.
/// </summary>
public static class Localities
{
    public sealed class Entry
    {
        public string Name, Other, Country, Region;
        public double Lat, Lon, Population;
        public char Class;   // c = ville, t = ville moyenne, v = village, l = autre localité
    }

    static byte[] _raw;
    static int[] _lineStart;     // début de chaque ligne dans _raw
    static string _keys;         // « \n clé0 \n clé1 \n … » : noms normalisés
    static int[] _keyStart;      // début de chaque clé dans _keys
    static Task _loading;

    public static int Count => _lineStart?.Length ?? 0;
    public static bool Ready => _loading != null && _loading.IsCompletedSuccessfully;
    public static string Error { get; private set; }

    /// <summary>Lance le chargement en arrière-plan (les octets du fichier sont lus par Godot sur le fil principal).</summary>
    public static void Init(byte[] compressed)
    {
        _loading = Task.Run(() =>
        {
            try { Load(compressed); }
            catch (Exception e) { Error = e.Message; throw; }
        });
    }

    static void Load(byte[] compressed)
    {
        using var ms = new MemoryStream();
        using (var br = new BrotliStream(new MemoryStream(compressed), CompressionMode.Decompress)) br.CopyTo(ms);
        var raw = ms.ToArray();
        var starts = new List<int>(1_800_000);
        var keyStarts = new List<int>(1_800_000);
        var keys = new StringBuilder(40_000_000);
        keys.Append('\n');
        int p = 0;
        while (p < raw.Length)
        {
            int end = Array.IndexOf(raw, (byte)'\n', p);
            if (end < 0) end = raw.Length;
            starts.Add(p);
            // clé : nom affiché (+ autre nom s'il est en lettres latines, ex. « Bern » pour « Berne »)
            int t1 = Array.IndexOf(raw, (byte)'\t', p, end - p);
            int t2 = t1 < 0 ? -1 : Array.IndexOf(raw, (byte)'\t', t1 + 1, end - t1 - 1);
            keyStarts.Add(keys.Length);
            if (t1 > p) AppendNormalized(keys, Encoding.UTF8.GetString(raw, p, t1 - p));
            if (t2 > t1 + 1)
            {
                var other = Encoding.UTF8.GetString(raw, t1 + 1, t2 - t1 - 1);
                keys.Append(" / ");
                AppendNormalized(keys, other);
            }
            keys.Append('\n');
            p = end + 1;
        }
        _keys = keys.ToString();
        _keyStart = keyStarts.ToArray();
        _raw = raw;
        _lineStart = starts.ToArray();
    }

    /// <summary>Minuscules sans accents (é → e, ß reste ß) : « Genève » se trouve en tapant « geneve ».</summary>
    public static string Normalize(string s)
    {
        var sb = new StringBuilder(s.Length);
        AppendNormalized(sb, s);
        return sb.ToString();
    }

    static void AppendNormalized(StringBuilder sb, string s)
    {
        bool ascii = true;
        foreach (var ch in s) if (ch > 127) { ascii = false; break; }
        if (ascii) { foreach (var ch in s) sb.Append(ch is >= 'A' and <= 'Z' ? (char)(ch + 32) : ch); return; }
        foreach (var ch in s.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark) sb.Append(char.ToLowerInvariant(ch));
    }

    public static Entry Get(int i)
    {
        int s = _lineStart[i], e = i + 1 < _lineStart.Length ? _lineStart[i + 1] - 1 : _raw.Length;
        var f = Encoding.UTF8.GetString(_raw, s, e - s).Split('\t');
        var inv = CultureInfo.InvariantCulture;
        return new Entry
        {
            Name = f[0], Other = f[1], Country = f[2], Region = f[3],
            Lat = double.Parse(f[4], inv), Lon = double.Parse(f[5], inv),
            Population = f[6].Length > 0 ? double.Parse(f[6], inv) : 0,
            Class = f[7].Length > 0 ? f[7][0] : 'l',
        };
    }

    /// <summary>
    /// Localités dont le nom contient <paramref name="query"/> : nom exact d'abord, puis nom qui commence par la
    /// recherche, puis le reste ; à rang égal, l'ordre du fichier (villes, puis villes moyennes, puis villages ;
    /// les plus peuplées d'abord).
    /// </summary>
    public static List<int> Search(string query, int max)
    {
        var result = new List<int>();
        if (!Ready) return result;
        var q = Normalize(query.Trim());
        if (q.Length == 0) return result;
        var exact = new List<int>(); var prefix = new List<int>(); var inside = new List<int>();
        int pos = 0, scanned = 0;
        while (scanned < 20_000 && (pos = _keys.IndexOf(q, pos, StringComparison.Ordinal)) >= 0)
        {
            int i = Array.BinarySearch(_keyStart, pos);
            if (i < 0) i = ~i - 1;
            int ks = _keyStart[i];
            int ke = _keys.IndexOf('\n', ks);
            // nom (ou autre nom) égal à la recherche, ou qui commence par elle ?
            bool atStart = false, whole = false;
            foreach (var part in _keys.Substring(ks, ke - ks).Split(" / "))
            {
                if (part == q) whole = true;
                if (part.StartsWith(q, StringComparison.Ordinal)) atStart = true;
            }
            (whole ? exact : atStart ? prefix : inside).Add(i);
            scanned++;
            if (exact.Count >= max) break;
            pos = ke + 1; // une seule fois par localité
        }
        foreach (var l in new[] { exact, prefix, inside })
            foreach (var i in l)
            {
                if (result.Count >= max) return result;
                if (!result.Contains(i)) result.Add(i);
            }
        return result;
    }
}
