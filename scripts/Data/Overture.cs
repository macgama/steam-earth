using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace SteamEarth.Data;

/// <summary>
/// Tuiles vectorielles mondiales d'Overture Maps (données OpenStreetMap, licence ODbL), publiées en PMTiles
/// sur AWS Open Data (bucket <c>overturemaps-extras-us-west-2</c>) : routes, chemins, rails (transportation)
/// et villes / villages (divisions). Une nouvelle version sort chaque mois et les anciennes sont supprimées :
/// la liste des versions est relue au démarrage (au plus une fois par jour) et gardée en cache.
/// Mention obligatoire : « © OpenStreetMap contributors, Overture Maps Foundation ».
/// </summary>
public static class Overture
{
    public const string Bucket = "https://overturemaps-extras-us-west-2.s3.us-west-2.amazonaws.com";
    public const string Attribution = "© contributeurs OpenStreetMap (ODbL), Overture Maps Foundation";

    static Task<string[]> _releases;
    static string _cacheRoot;

    public static void Init(string cacheRoot)
    {
        _cacheRoot = cacheRoot;
        Directory.CreateDirectory(cacheRoot);
        _releases = Task.Run(() => ListReleasesAsync(false));
    }

    /// <summary>Version utilisée (affichage), une fois une couche ouverte.</summary>
    public static string Release { get; private set; }

    /// <summary>Ouvre une couche (« transportation », « divisions »…) de la version la plus récente qui la contient.
    /// Null hors ligne sans cache.</summary>
    public static async Task<PmTilesArchive> OpenAsync(string theme)
    {
        for (int pass = 0; pass < 2; pass++)
        {
            var releases = pass == 0 ? await _releases : await ListReleasesAsync(true);
            foreach (var rel in releases)
            {
                string url = $"{Bucket}/tiles/{rel}/{theme}.pmtiles";
                PmTilesArchive a = null;
                try { a = await PmTilesArchive.OpenAsync(url, Path.Combine(_cacheRoot, rel, theme), Path.Combine(_cacheRoot, "tiles", theme)); }
                catch (Exception) { } // hors ligne et pas en cache : version suivante
                if (a != null) { Release = rel; return a; }
            }
        }
        return null;
    }

    static string ReleaseFile => Path.Combine(_cacheRoot, "releases.txt");

    /// <summary>Versions disponibles, la plus récente d'abord.</summary>
    static async Task<string[]> ListReleasesAsync(bool force)
    {
        string[] cached = File.Exists(ReleaseFile) ? File.ReadAllLines(ReleaseFile).Where(l => l.Length > 0).ToArray() : Array.Empty<string>();
        if (!force && cached.Length > 0 && (DateTime.UtcNow - File.GetLastWriteTimeUtc(ReleaseFile)).TotalDays < 1) return cached;
        string xml = await PmTilesArchive.GetStringAsync($"{Bucket}/?list-type=2&prefix=tiles/&delimiter=/");
        var found = new List<string>();
        if (xml != null)
            foreach (Match m in Regex.Matches(xml, @"<Prefix>tiles/([0-9]{4}-[0-9]{2}-[0-9]{2}\.[0-9]+)/</Prefix>"))
                found.Add(m.Groups[1].Value);
        if (found.Count == 0) return cached; // hors ligne : dernières versions connues (leurs répertoires sont en cache)
        found.Sort(string.CompareOrdinal);
        found.Reverse();
        File.WriteAllLines(ReleaseFile, found);
        return found.ToArray();
    }
}
