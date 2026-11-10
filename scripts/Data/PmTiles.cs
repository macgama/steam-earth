using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SteamEarth.Data;

/// <summary>
/// Lecteur d'archives PMTiles v3 (https://github.com/protomaps/PMTiles) par requêtes HTTP partielles (Range) :
/// une seule grosse archive (ex. 134 Go pour toutes les routes du monde) dont on ne télécharge que les
/// répertoires et les tuiles utiles. Tuiles et répertoires sont gardés en cache sur disque.
/// Indépendant de Godot (testable dans un simple programme console).
/// </summary>
public sealed class PmTilesArchive
{
    public readonly string Url;
    public int MinZoom, MaxZoom;
    public byte TileCompression, InternalCompression; // 1 = aucune, 2 = gzip
    long _rootOff, _rootLen, _leafOff, _dataOff;
    readonly string _cacheDir;   // répertoires (propres à cette version de l'archive)
    readonly string _tileCache;  // tuiles (gardées d'une version à l'autre : le jeu marche hors ligne)
    readonly ConcurrentDictionary<long, Task<Entry[]>> _dirs = new();

    readonly struct Entry
    {
        public readonly ulong TileId; public readonly long Offset; public readonly int Length; public readonly uint RunLength;
        public Entry(ulong id, long off, int len, uint run) { TileId = id; Offset = off; Length = len; RunLength = run; }
    }

    static readonly HttpClient Http = new(new SocketsHttpHandler { MaxConnectionsPerServer = 6, AutomaticDecompression = DecompressionMethods.None })
    { Timeout = TimeSpan.FromSeconds(60) };
    static readonly SemaphoreSlim Gate = new(6);

    public static int Downloading => _inFlight;
    static int _inFlight;

    PmTilesArchive(string url, string cacheDir, string tileCache) { Url = url; _cacheDir = cacheDir; _tileCache = tileCache; }

    /// <summary>Ouvre l'archive (en-tête depuis le cache ou le réseau). Null si introuvable.</summary>
    public static async Task<PmTilesArchive> OpenAsync(string url, string cacheDir, string tileCache)
    {
        var a = new PmTilesArchive(url, cacheDir, tileCache);
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(tileCache);
        string headPath = Path.Combine(cacheDir, "header.bin");
        byte[] h = File.Exists(headPath) ? await File.ReadAllBytesAsync(headPath) : null;
        if (h == null)
        {
            h = await FetchAsync(url, 0, 127);
            if (h == null) return null;
            await File.WriteAllBytesAsync(headPath, h);
        }
        if (h.Length < 127 || h[0] != 'P' || h[1] != 'M' || h[7] != 3) throw new InvalidDataException("PMTiles v3 attendu : " + url);
        a._rootOff = BitConverter.ToInt64(h, 8);
        a._rootLen = BitConverter.ToInt64(h, 16);
        a._leafOff = BitConverter.ToInt64(h, 40);
        a._dataOff = BitConverter.ToInt64(h, 56);
        a.InternalCompression = h[97];
        a.TileCompression = h[98];
        a.MinZoom = h[100];
        a.MaxZoom = h[101];
        return a;
    }

    public static async Task<byte[]> FetchAsync(string url, long offset, long length)
    {
        await Gate.WaitAsync();
        Interlocked.Increment(ref _inFlight);
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, url);
                    req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(offset, offset + length - 1);
                    using var resp = await Http.SendAsync(req);
                    if (resp.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden) return null;
                    resp.EnsureSuccessStatusCode();
                    return await resp.Content.ReadAsByteArrayAsync();
                }
                catch (HttpRequestException) when (attempt < 2) { await Task.Delay(500 * (attempt + 1)); }
                catch (TaskCanceledException) when (attempt < 2) { await Task.Delay(500 * (attempt + 1)); }
            }
            return null;
        }
        finally { Interlocked.Decrement(ref _inFlight); Gate.Release(); }
    }

    /// <summary>Texte complet d'une petite ressource HTTP (liste S3…), null en cas d'échec.</summary>
    public static async Task<string> GetStringAsync(string url)
    {
        try { return await Http.GetStringAsync(url); }
        catch (Exception) { return null; }
    }

    readonly ConcurrentDictionary<(int, int, int), Task<byte[]>> _tilesInFlight = new();

    /// <summary>Tuile décompressée (octets MVT), null si la tuile n'existe pas (zone vide).
    /// Deux couches qui demandent la même tuile en même temps (routes et noms de rues) ne la téléchargent qu'une fois.</summary>
    public async Task<byte[]> GetTileAsync(int z, int x, int y)
    {
        var key = (z, x, y);
        var task = _tilesInFlight.GetOrAdd(key, k => GetTileCoreAsync(k.Item1, k.Item2, k.Item3));
        try { return await task; }
        finally { _tilesInFlight.TryRemove(new KeyValuePair<(int, int, int), Task<byte[]>>(key, task)); }
    }

    async Task<byte[]> GetTileCoreAsync(int z, int x, int y)
    {
        string path = Path.Combine(_tileCache, $"{z}_{x}_{y}.bin");
        if (File.Exists(path))
        {
            var cached = await File.ReadAllBytesAsync(path);
            return cached.Length == 0 ? null : Decompress(cached, TileCompression);
        }
        ulong id = ZxyToTileId(z, x, y);
        long dOff = _rootOff; int dLen = (int)_rootLen; bool root = true;
        byte[] raw = null;
        bool known = true; // false = échec réseau : ne pas mémoriser une tuile « vide »
        for (int depth = 0; depth < 4; depth++)
        {
            var dir = await GetDirectoryAsync(root ? dOff : _leafOff + dOff, dLen);
            if (dir == null) { known = false; break; }
            int i = FindEntry(dir, id);
            if (i < 0) break;
            var e = dir[i];
            if (e.RunLength == 0) { dOff = e.Offset; dLen = e.Length; root = false; continue; } // répertoire feuille
            if (id < e.TileId + e.RunLength)
            {
                raw = await FetchAsync(Url, _dataOff + e.Offset, e.Length);
                if (raw == null) known = false;
            }
            break;
        }
        if (!known) throw new IOException("tuile indisponible (réseau)");
        // écriture puis renommage : un autre lecteur ne voit jamais un fichier à moitié écrit
        string tmp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllBytesAsync(tmp, raw ?? Array.Empty<byte>());
        File.Move(tmp, path, true);
        return raw == null ? null : Decompress(raw, TileCompression);
    }

    static int FindEntry(Entry[] d, ulong id)
    {
        int lo = 0, hi = d.Length - 1, best = -1;
        while (lo <= hi)
        {
            int m = (lo + hi) >> 1;
            if (d[m].TileId <= id) { best = m; lo = m + 1; } else hi = m - 1;
        }
        return best;
    }

    Task<Entry[]> GetDirectoryAsync(long offset, int length) =>
        _dirs.GetOrAdd(offset, _ => Task.Run(async () =>
        {
            string path = Path.Combine(_cacheDir, $"dir_{offset}_{length}.bin");
            byte[] raw = File.Exists(path) ? await File.ReadAllBytesAsync(path) : null;
            if (raw == null)
            {
                raw = await FetchAsync(Url, offset, length);
                if (raw == null) { _dirs.TryRemove(offset, out Task<Entry[]> _); return null; }
                await File.WriteAllBytesAsync(path, raw);
            }
            return ParseDirectory(Decompress(raw, InternalCompression));
        }));

    static Entry[] ParseDirectory(byte[] b)
    {
        int p = 0;
        int n = (int)ReadVarint(b, ref p);
        var ids = new ulong[n]; var runs = new uint[n]; var lens = new int[n]; var offs = new long[n];
        ulong last = 0;
        for (int i = 0; i < n; i++) { last += ReadVarint(b, ref p); ids[i] = last; }
        for (int i = 0; i < n; i++) runs[i] = (uint)ReadVarint(b, ref p);
        for (int i = 0; i < n; i++) lens[i] = (int)ReadVarint(b, ref p);
        for (int i = 0; i < n; i++)
        {
            ulong v = ReadVarint(b, ref p);
            offs[i] = v == 0 && i > 0 ? offs[i - 1] + lens[i - 1] : (long)v - 1;
        }
        var d = new Entry[n];
        for (int i = 0; i < n; i++) d[i] = new Entry(ids[i], offs[i], lens[i], runs[i]);
        return d;
    }

    static ulong ReadVarint(byte[] b, ref int p)
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

    static byte[] Decompress(byte[] data, byte compression)
    {
        if (compression != 2) return data;
        using var gz = new GZipStream(new MemoryStream(data), CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gz.CopyTo(ms);
        return ms.ToArray();
    }

    /// <summary>Identifiant PMTiles d'une tuile (courbe de Hilbert par niveau de zoom).</summary>
    public static ulong ZxyToTileId(int z, int x, int y)
    {
        ulong acc = 0;
        for (int i = 0; i < z; i++) acc += 1UL << (2 * i);
        long n = 1L << z;
        long tx = x, ty = y; ulong d = 0;
        for (long s = n / 2; s > 0; s /= 2)
        {
            long rx = (tx & s) > 0 ? 1 : 0, ry = (ty & s) > 0 ? 1 : 0;
            d += (ulong)(s * s * ((3 * rx) ^ ry));
            if (ry == 0)
            {
                if (rx == 1) { tx = s - 1 - tx; ty = s - 1 - ty; }
                (tx, ty) = (ty, tx);
            }
        }
        return acc + d;
    }

    // --- Projection Web Mercator des tuiles ---
    public static void TileXY(double lat, double lon, int z, out int x, out int y)
    {
        double n = 1 << z;
        x = (int)Math.Floor((lon + 180) / 360 * n);
        double la = Math.Clamp(lat, -85.0511, 85.0511) * Math.PI / 180;
        y = (int)Math.Floor((1 - Math.Log(Math.Tan(la) + 1 / Math.Cos(la)) / Math.PI) / 2 * n);
        x = Math.Clamp(x, 0, (1 << z) - 1); y = Math.Clamp(y, 0, (1 << z) - 1);
    }

    /// <summary>Coordonnées dans la tuile (0..1) → latitude / longitude (degrés).</summary>
    public static void TileToLatLon(int z, int x, int y, double fx, double fy, out double lat, out double lon)
    {
        double n = 1 << z;
        lon = (x + fx) / n * 360 - 180;
        lat = Math.Atan(Math.Sinh(Math.PI * (1 - 2 * (y + fy) / n))) * 180 / Math.PI;
    }
}
