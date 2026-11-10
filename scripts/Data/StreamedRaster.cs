using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace SteamEarth.Data;

/// <summary>
/// Raster mondial découpé en fichiers COG par cases de N degrés (ex. Copernicus 1°, WorldCover 3°),
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

    readonly string _name;
    readonly double _cellDeg;
    readonly Func<int, int, string> _urlFor; // (latCase, lonCase) → URL
    readonly string _cacheRoot;
    readonly int _maxTiles;

    readonly ConcurrentDictionary<string, Task<CogReader>> _readers = new();
    readonly ConcurrentDictionary<(string, int, int, int), Entry> _tiles = new();
    readonly ConcurrentDictionary<(string, int, int, int), byte> _pending = new();
    long _clock;

    sealed class Entry { public float[] Data; public long LastUse; }
    static readonly float[] Empty = Array.Empty<float>();

    public StreamedRaster(string name, double cellDeg, Func<int, int, string> urlFor, int maxTilesInMemory)
    {
        _name = name; _cellDeg = cellDeg; _urlFor = urlFor; _maxTiles = maxTilesInMemory;
        _cacheRoot = Path.Combine(OS.GetUserDataDir(), "cache", name);
    }

    public int TilesInMemory => _tiles.Count;

    /// <summary>
    /// Valeur interpolée (bilinéaire) ou au plus proche, au niveau d'aperçu demandé (0 = pleine résolution).
    /// <paramref name="fetch"/> = false : ne rien télécharger, répondre seulement avec ce qui est déjà en mémoire.
    /// </summary>
    public Result Sample(double lat, double lon, int level, bool bilinear, bool fetch, out float value)
    {
        value = float.NaN;
        int cLat = (int)(Math.Floor(lat / _cellDeg) * _cellDeg);
        int cLon = (int)(Math.Floor(lon / _cellDeg) * _cellDeg);
        string key = $"{cLat}_{cLon}";
        if (!_readers.TryGetValue(key, out var task))
        {
            if (!fetch) return Result.Pending;
            string url = _urlFor(cLat, cLon);
            string dir = Path.Combine(_cacheRoot, key);
            task = _readers.GetOrAdd(key, _ => Task.Run(async () =>
            {
                try { return await CogReader.OpenAsync(url, dir); }
                catch (Exception e) { GD.PrintErr($"[{_name}] {url} : {e.Message}"); return null; }
                finally { Interlocked.Increment(ref DataVersion); }
            }));
        }
        if (!task.IsCompleted) return Result.Pending;
        var r = task.Result;
        if (r == null) return Result.Absent;

        level = Math.Clamp(level, 0, r.Levels.Count - 1);
        var l0 = r.Levels[0];
        var lv = r.Levels[level];
        double f = (double)lv.Width / l0.Width;
        double px = ((lon - r.OriginX) / r.PixelX) * f - 0.5;
        double py = ((r.OriginY - lat) / r.PixelY) * f - 0.5;

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
            if (e.Data.Length == 0) return Result.Ok; // tuile vide : NaN
            v = e.Data[(y - tY * lv.TileHeight) * lv.TileWidth + (x - tX * lv.TileWidth)];
            return Result.Ok;
        }
        if (fetch && _pending.TryAdd(k, 0))
        {
            Interlocked.Increment(ref _inFlight);
            Task.Run(async () =>
            {
                float[] data = null;
                try { data = await r.ReadTileAsync(level, tX, tY); }
                catch (Exception ex) { GD.PrintErr($"[{_name}] tuile {k} : {ex.Message}"); }
                _tiles[k] = new Entry { Data = data ?? Empty, LastUse = Interlocked.Increment(ref _clock) };
                _pending.TryRemove(k, out _);
                Interlocked.Decrement(ref _inFlight);
                Interlocked.Increment(ref DataVersion);
                Evict();
            });
        }
        return Result.Pending;
    }

    void Evict()
    {
        while (_tiles.Count > _maxTiles)
        {
            (string, int, int, int) oldest = default;
            long min = long.MaxValue;
            foreach (var kv in _tiles) if (kv.Value.LastUse < min) { min = kv.Value.LastUse; oldest = kv.Key; }
            if (!_tiles.TryRemove(oldest, out _)) break;
        }
    }
}
