using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace SteamEarth.Data;

/// <summary>
/// Raster découpé en fichiers COG (ex. Copernicus par cases de 1°, WorldCover 3°, swissALTI3D 1 km en LV95),
/// lu à la demande et sans jamais bloquer : si une tuile n'est pas encore là, on lance son téléchargement
/// et on répond « en attente » ; l'appelant utilise une donnée plus grossière en attendant.
/// </summary>
public sealed class StreamedRaster
{
    public enum Result { Ok, Pending, Absent }

    /// <summary>Incrémenté à chaque tuile arrivée : les chunks construits avec des données provisoires se reconstruisent.</summary>
    public static int DataVersion;
    public static int Downloading => _inFlight;
    static int _inFlight;

    /// <summary>
    /// Trouve le fichier qui couvre (lat, lon) : clé de cache, URL, et coordonnées du point dans la projection
    /// du fichier (x vers l'est, y vers le nord). Retourne false si aucun fichier ne couvre ce point.
    /// </summary>
    public delegate bool Locator(double lat, double lon, out string key, out string url, out double x, out double y);

    readonly string _name;
    readonly Locator _locate;
    readonly string _cacheRoot;
    readonly int _headBytes;
    readonly bool _classes;
    long _bytes;

    /// <summary>
    /// Mémoire maximale gardée pour ce raster (octets). Réglée selon la qualité graphique (voir HeightProvider.ApplyQuality).
    /// Trop petite, elle faisait « tourner » le cache : une tuile arrivée en chassait une autre encore attendue,
    /// les chunks restaient provisoires (relief 30 m, toits et arbres compris) et se reconstruisaient sans fin.
    /// </summary>
    public long BudgetBytes;

    readonly ConcurrentDictionary<string, Task<CogReader>> _readers = new();
    readonly ConcurrentDictionary<(string, int, int, int), Entry> _tiles = new();
    readonly ConcurrentDictionary<(string, int, int, int), byte> _pending = new();
    // Échecs réseau : nombre d'essais et heure du prochain essai (au lieu d'un trou définitif dans le relief)
    readonly ConcurrentDictionary<(string, int, int, int), (int tries, DateTime retry)> _failed = new();
    readonly ConcurrentDictionary<string, int> _openFailures = new();
    const int MaxTries = 4;
    long _clock;
    int _evicting;

    /// <summary>
    /// Bloc en mémoire, rogné à sa partie utile (les blocs du bord d'un fichier, et ceux des petits aperçus,
    /// sont en grande partie vides). Rasters de classes (WorldCover) : un octet par pixel, 255 = pas de donnée.
    /// </summary>
    sealed class Entry
    {
        public float[] F; public byte[] B; public int W, H; public long LastUse;
        public long Bytes => (F?.Length ?? 0) * 4L + (B?.Length ?? 0) + 64;
        public bool Empty => F == null && B == null;
    }

    /// <summary>Raster en latitude/longitude, un fichier par case de <paramref name="cellDeg"/> degrés.</summary>
    /// <param name="budgetMb">mémoire max (Mo), modifiable ensuite par <see cref="BudgetBytes"/>.</param>
    /// <param name="classes">valeurs entières 0..254 (classes) : gardées sur un octet par pixel.</param>
    public StreamedRaster(string name, double cellDeg, Func<int, int, string> urlFor, int budgetMb, bool classes = false)
        : this(name, (double lat, double lon, out string key, out string url, out double x, out double y) =>
        {
            int cLat = (int)(Math.Floor(lat / cellDeg) * cellDeg);
            int cLon = (int)(Math.Floor(lon / cellDeg) * cellDeg);
            key = $"{cLat}_{cLon}";
            url = urlFor(cLat, cLon);
            x = lon; y = lat;
            return true;
        }, budgetMb, classes: classes) { }

    /// <summary>Raster dans une projection quelconque : <paramref name="locate"/> choisit le fichier et projette le point.</summary>
    public StreamedRaster(string name, Locator locate, int budgetMb, int headBytes = 65536, bool classes = false)
    {
        _name = name; _locate = locate; BudgetBytes = budgetMb * 1024L * 1024; _headBytes = headBytes; _classes = classes;
        _cacheRoot = Path.Combine(OS.GetUserDataDir(), "cache", name);
    }

    public int TilesInMemory => _tiles.Count;
    public long BytesInMemory => Interlocked.Read(ref _bytes);

    /// <summary>
    /// Valeur interpolée (bilinéaire) ou au plus proche, au niveau d'aperçu demandé (0 = pleine résolution).
    /// <paramref name="fetch"/> = false : ne rien télécharger, répondre seulement avec ce qui est déjà en mémoire.
    /// </summary>
    public Result Sample(double lat, double lon, int level, bool bilinear, bool fetch, out float value)
    {
        value = float.NaN;
        if (!_locate(lat, lon, out string key, out string url, out double gx, out double gy)) return Result.Absent;
        if (!_readers.TryGetValue(key, out var task))
        {
            if (!fetch) return Result.Pending;
            string dir = Path.Combine(_cacheRoot, key);
            int head = _headBytes;
            task = _readers.GetOrAdd(key, _k => Task.Run(async () =>
            {
                try { return await CogReader.OpenAsync(url, dir, head); }
                catch (Exception e)
                {
                    // Erreur réseau (et non fichier absent) : on réessaie un peu plus tard, quelques fois.
                    int n = _openFailures.AddOrUpdate(key, 1, (_, v) => v + 1);
                    GD.PrintErr($"[{_name}] {url} : {e.Message}" + (n < MaxTries ? " (nouvel essai)" : ""));
                    if (n < MaxTries)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5 * n * n));
                        _readers.TryRemove(key, out _);
                        return null;
                    }
                    return null;
                }
                finally { Interlocked.Increment(ref DataVersion); }
            }));
        }
        if (!task.IsCompleted) return Result.Pending;
        var r = task.Result;
        if (r == null) return _readers.ContainsKey(key) ? Result.Absent : Result.Pending;

        level = Math.Clamp(level, 0, r.Levels.Count - 1);
        var l0 = r.Levels[0];
        var lv = r.Levels[level];
        double f = (double)lv.Width / l0.Width;
        double px = ((gx - r.OriginX) / r.PixelX) * f - 0.5;
        double py = ((r.OriginY - gy) / r.PixelY) * f - 0.5;

        if (!bilinear)
        {
            int x = Math.Clamp((int)Math.Round(px), 0, lv.Width - 1), y = Math.Clamp((int)Math.Round(py), 0, lv.Height - 1);
            var res = Pixel(r, key, level, x, y, fetch, out value);
            return res == Result.Ok && float.IsNaN(value) ? Result.Absent : res;
        }
        int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
        double tx = px - x0, ty = py - y0;
        int xa = Math.Clamp(x0, 0, lv.Width - 1), xb = Math.Clamp(x0 + 1, 0, lv.Width - 1);
        int ya = Math.Clamp(y0, 0, lv.Height - 1), yb = Math.Clamp(y0 + 1, 0, lv.Height - 1);
        Result worst = Result.Ok;
        float a = Get(xa, ya), b = Get(xb, ya), c = Get(xa, yb), d = Get(xb, yb);
        if (worst != Result.Ok) return worst;
        if (float.IsNaN(a) || float.IsNaN(b) || float.IsNaN(c) || float.IsNaN(d))
        {
            // bord de données (côte) : moyenne des valeurs disponibles
            float s = 0; int n = 0;
            foreach (var v in new[] { a, b, c, d }) if (!float.IsNaN(v)) { s += v; n++; }
            if (n == 0) return Result.Absent;
            value = s / n;
            return Result.Ok;
        }
        value = (float)((a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty);
        return Result.Ok;

        float Get(int x, int y)
        {
            var res = Pixel(r, key, level, x, y, fetch, out float v);
            if (res == Result.Pending) worst = Result.Pending;
            return v;
        }
    }

    Result Pixel(CogReader r, string key, int level, int x, int y, bool fetch, out float v)
    {
        v = float.NaN;
        var lv = r.Levels[level];
        int tX = x / lv.TileWidth, tY = y / lv.TileHeight;
        var k = (key, level, tX, tY);
        if (_tiles.TryGetValue(k, out var e))
        {
            e.LastUse = Interlocked.Increment(ref _clock);
            if (e.Empty) return Result.Ok; // tuile vide : NaN
            int lx = x - tX * lv.TileWidth, ly = y - tY * lv.TileHeight;
            if (lx >= e.W || ly >= e.H) return Result.Ok;
            int i = ly * e.W + lx;
            if (e.F != null) v = e.F[i];
            else { byte b = e.B[i]; v = b == 255 ? float.NaN : b; }
            return Result.Ok;
        }
        if (_failed.TryGetValue(k, out var f) && DateTime.UtcNow < f.retry) return Result.Pending;
        if (fetch && _pending.TryAdd(k, 0))
        {
            Interlocked.Increment(ref _inFlight);
            Task.Run(async () =>
            {
                float[] data = null;
                bool ok = true;
                try { data = await r.ReadTileAsync(level, tX, tY); }
                catch (Exception ex)
                {
                    ok = false;
                    var fl = _failed.AddOrUpdate(k, (1, DateTime.UtcNow.AddSeconds(3)),
                        (_, o) => (o.tries + 1, DateTime.UtcNow.AddSeconds(3 * (o.tries + 1) * (o.tries + 1))));
                    GD.PrintErr($"[{_name}] tuile {k} : {ex.Message}" + (fl.tries < MaxTries ? " (nouvel essai)" : ""));
                    // Après plusieurs échecs : tuile marquée vide (relief plus grossier à cet endroit) plutôt que d'attendre sans fin.
                    if (fl.tries >= MaxTries) ok = true;
                }
                if (ok)
                {
                    _failed.TryRemove(k, out _);
                    var entry = Pack(data, lv, tX, tY);
                    entry.LastUse = Interlocked.Increment(ref _clock);
                    if (_tiles.TryAdd(k, entry)) Interlocked.Add(ref _bytes, entry.Bytes);
                }
                _pending.TryRemove(k, out _);
                Interlocked.Decrement(ref _inFlight);
                Interlocked.Increment(ref DataVersion);
                if (Interlocked.Read(ref _bytes) > BudgetBytes) Evict();
            });
        }
        return Result.Pending;
    }

    /// <summary>Garde seulement la partie utile du bloc décodé ; un octet par pixel pour un raster de classes.</summary>
    Entry Pack(float[] data, CogReader.Level lv, int tX, int tY)
    {
        if (data == null) return new Entry();
        int w = Math.Min(lv.TileWidth, lv.Width - tX * lv.TileWidth), h = Math.Min(lv.TileHeight, lv.Height - tY * lv.TileHeight);
        w = Math.Max(w, 1); h = Math.Max(h, 1);
        if (_classes)
        {
            var b = new byte[w * h];
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float f = data[y * lv.TileWidth + x];
                b[y * w + x] = float.IsNaN(f) || f < 0 || f > 254 ? (byte)255 : (byte)f;
            }
            return new Entry { B = b, W = w, H = h };
        }
        if (w == lv.TileWidth && h == lv.TileHeight) return new Entry { F = data, W = w, H = h };
        var c = new float[w * h];
        for (int y = 0; y < h; y++) Array.Copy(data, y * lv.TileWidth, c, y * w, w);
        return new Entry { F = c, W = w, H = h };
    }

    /// <summary>
    /// Libère les blocs les moins récemment lus jusqu'à 85 % du budget (d'un coup, pour ne pas trier à chaque arrivée).
    /// </summary>
    void Evict()
    {
        if (Interlocked.Exchange(ref _evicting, 1) == 1) return;
        try
        {
            var all = new List<KeyValuePair<(string, int, int, int), Entry>>(_tiles);
            all.Sort((a, b) => a.Value.LastUse.CompareTo(b.Value.LastUse));
            long target = BudgetBytes * 85 / 100;
            foreach (var kv in all)
            {
                if (Interlocked.Read(ref _bytes) <= target) break;
                if (_tiles.TryRemove(kv.Key, out var e)) Interlocked.Add(ref _bytes, -e.Bytes);
            }
        }
        finally { Interlocked.Exchange(ref _evicting, 0); }
    }
}
