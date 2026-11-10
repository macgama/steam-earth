using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace SteamEarth.Data;

/// <summary>
/// Lecteur minimal de GeoTIFF « cloud optimized » (COG) via requêtes HTTP partielles (Range) :
/// on ne télécharge que l'en-tête puis les tuiles internes (souvent 1024×1024) dont on a besoin.
/// Gère : TIFF classique et BigTIFF, petit-boutiste, tuilé, compression DEFLATE / sans compression,
/// prédicteurs 2 (entier) et 3 (flottant), échantillons float32 / int16 / uint16 / uint8, aperçus (overviews).
/// Les octets téléchargés sont mis en cache sur disque : le jeu fonctionne ensuite hors ligne.
/// </summary>
public sealed class CogReader
{
    public sealed class Level
    {
        public int Width, Height, TileWidth, TileHeight, TilesAcross, TilesDown;
        public long[] Offsets, Counts;
    }

    public readonly string Url;
    public readonly List<Level> Levels = new(); // 0 = pleine résolution, puis aperçus
    public int BitsPerSample, SampleFormat = 1, Compression = 1, Predictor = 1;
    public double? NoData;
    // Géoréférencement (degrés) : coin haut-gauche du pixel (0,0) et taille d'un pixel au niveau 0
    public double OriginX, OriginY, PixelX, PixelY;
    public bool PixelIsPoint;

    static readonly HttpClient Http = new(new SocketsHttpHandler { MaxConnectionsPerServer = 8, AutomaticDecompression = DecompressionMethods.None })
    { Timeout = TimeSpan.FromSeconds(60) };
    static readonly SemaphoreSlim Gate = new(8);

    readonly string _cacheDir;
    byte[] _head;

    CogReader(string url, string cacheDir) { Url = url; _cacheDir = cacheDir; }

    /// <summary>Ouvre un COG (en-tête depuis le cache disque ou le réseau). Retourne null si le fichier n'existe pas (404/403).</summary>
    public static async Task<CogReader> OpenAsync(string url, string cacheDir)
    {
        var r = new CogReader(url, cacheDir);
        Directory.CreateDirectory(cacheDir);
        string headPath = Path.Combine(cacheDir, "head.bin");
        string missingPath = Path.Combine(cacheDir, "absent");
        if (File.Exists(missingPath)) return null;
        if (File.Exists(headPath)) r._head = await File.ReadAllBytesAsync(headPath);
        else
        {
            r._head = await r.FetchAsync(0, 65536);
            if (r._head == null) { await File.WriteAllTextAsync(missingPath, url); return null; }
            await File.WriteAllBytesAsync(headPath, r._head);
        }
        await r.ParseAsync();
        return r;
    }

    async Task<byte[]> FetchAsync(long offset, long length)
    {
        await Gate.WaitAsync();
        try
        {
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, Url);
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
        finally { Gate.Release(); }
    }

    /// <summary>Lit des octets de l'en-tête, en allant les chercher sur le réseau s'ils dépassent ce qu'on a déjà.</summary>
    async Task<byte[]> HeadBytesAsync(long offset, long length)
    {
        if (offset + length <= _head.Length)
        {
            var b = new byte[length];
            Buffer.BlockCopy(_head, (int)offset, b, 0, (int)length);
            return b;
        }
        string p = Path.Combine(_cacheDir, $"h_{offset}_{length}.bin");
        if (File.Exists(p)) return await File.ReadAllBytesAsync(p);
        var data = await FetchAsync(offset, length) ?? throw new IOException("En-tête COG incomplet : " + Url);
        await File.WriteAllBytesAsync(p, data);
        return data;
    }

    async Task ParseAsync()
    {
        var h = _head;
        if (h[0] != 'I' || h[1] != 'I') throw new NotSupportedException("TIFF gros-boutiste non géré");
        int magic = BitConverter.ToUInt16(h, 2);
        bool big = magic == 43;
        long ifd = big ? BitConverter.ToInt64(h, 8) : BitConverter.ToUInt32(h, 4);
        bool first = true;
        while (ifd != 0)
        {
            var countBytes = await HeadBytesAsync(ifd, big ? 8 : 2);
            long n = big ? BitConverter.ToInt64(countBytes, 0) : BitConverter.ToUInt16(countBytes, 0);
            int entrySize = big ? 20 : 12;
            var entries = await HeadBytesAsync(ifd + (big ? 8 : 2), n * entrySize + (big ? 8 : 4));
            var tags = new Dictionary<int, (int type, long count, byte[] raw, long valueOrOffset)>();
            for (int i = 0; i < n; i++)
            {
                int o = i * entrySize;
                int tag = BitConverter.ToUInt16(entries, o);
                int type = BitConverter.ToUInt16(entries, o + 2);
                long count = big ? BitConverter.ToInt64(entries, o + 4) : BitConverter.ToUInt32(entries, o + 4);
                int vo = o + (big ? 12 : 8);
                int inline = big ? 8 : 4;
                int size = TypeSize(type) * (int)Math.Min(count, int.MaxValue / 8);
                byte[] raw;
                if (size <= inline) { raw = new byte[inline]; Buffer.BlockCopy(entries, vo, raw, 0, inline); }
                else
                {
                    long off = big ? BitConverter.ToInt64(entries, vo) : BitConverter.ToUInt32(entries, vo);
                    raw = await HeadBytesAsync(off, size);
                }
                tags[tag] = (type, count, raw, 0);
            }
            long next = big ? BitConverter.ToInt64(entries, (int)(n * entrySize)) : BitConverter.ToUInt32(entries, (int)(n * entrySize));

            long Num(int tag, int idx = 0) => tags.TryGetValue(tag, out var t) ? ReadNum(t.raw, t.type, idx) : 0;
            long[] Arr(int tag)
            {
                var t = tags[tag];
                var a = new long[t.count];
                for (int i = 0; i < t.count; i++) a[i] = ReadNum(t.raw, t.type, i);
                return a;
            }

            bool isMask = (Num(254) & 4) != 0; // NewSubfileType : masque de transparence
            if (!isMask && tags.ContainsKey(322))
            {
                var lv = new Level
                {
                    Width = (int)Num(256), Height = (int)Num(257),
                    TileWidth = (int)Num(322), TileHeight = (int)Num(323),
                    Offsets = Arr(324), Counts = Arr(325),
                };
                lv.TilesAcross = (lv.Width + lv.TileWidth - 1) / lv.TileWidth;
                lv.TilesDown = (lv.Height + lv.TileHeight - 1) / lv.TileHeight;
                Levels.Add(lv);
                if (first)
                {
                    BitsPerSample = (int)Num(258);
                    Compression = (int)Math.Max(1, Num(259));
                    Predictor = (int)Math.Max(1, Num(317));
                    if (tags.ContainsKey(339)) SampleFormat = (int)Num(339);
                    if (tags.TryGetValue(42113, out var nd))
                    {
                        var s = System.Text.Encoding.ASCII.GetString(nd.raw).TrimEnd('\0', ' ');
                        if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) NoData = v;
                    }
                    if (tags.TryGetValue(33550, out var scale) && tags.TryGetValue(33922, out var tie))
                    {
                        PixelX = BitConverter.ToDouble(scale.raw, 0);
                        PixelY = BitConverter.ToDouble(scale.raw, 8);
                        double i0 = BitConverter.ToDouble(tie.raw, 0), j0 = BitConverter.ToDouble(tie.raw, 8);
                        OriginX = BitConverter.ToDouble(tie.raw, 24) - i0 * PixelX;
                        OriginY = BitConverter.ToDouble(tie.raw, 32) + j0 * PixelY;
                    }
                    // GeoKey RasterPixelIsPoint (1025 = 2) : l'origine désigne le centre du pixel
                    if (tags.TryGetValue(34735, out var gk))
                    {
                        int keys = BitConverter.ToUInt16(gk.raw, 6);
                        for (int k = 0; k < keys; k++)
                        {
                            int b = 8 + k * 8;
                            if (b + 8 > gk.raw.Length) break;
                            if (BitConverter.ToUInt16(gk.raw, b) == 1025 && BitConverter.ToUInt16(gk.raw, b + 6) == 2) PixelIsPoint = true;
                        }
                    }
                    if (PixelIsPoint) { OriginX -= PixelX / 2; OriginY += PixelY / 2; }
                    first = false;
                }
            }
            ifd = next;
        }
        if (Levels.Count == 0) throw new NotSupportedException("COG sans tuiles : " + Url);
    }

    static int TypeSize(int type) => type switch { 1 or 2 or 6 or 7 => 1, 3 or 8 => 2, 4 or 9 or 11 => 4, 5 or 10 or 12 or 16 or 17 or 18 => 8, _ => 1 };

    static long ReadNum(byte[] raw, int type, int i) => type switch
    {
        1 or 7 => raw[i],
        3 => BitConverter.ToUInt16(raw, i * 2),
        4 => BitConverter.ToUInt32(raw, i * 4),
        16 => BitConverter.ToInt64(raw, i * 8),
        8 => BitConverter.ToInt16(raw, i * 2),
        9 => BitConverter.ToInt32(raw, i * 4),
        _ => 0,
    };

    /// <summary>
    /// Lit la tuile interne (tx, ty) du niveau donné et la convertit en float (NaN = pas de donnée).
    /// Retourne null si la tuile est vide (absente du fichier).
    /// </summary>
    public async Task<float[]> ReadTileAsync(int level, int tx, int ty)
    {
        var lv = Levels[level];
        int idx = ty * lv.TilesAcross + tx;
        long off = lv.Offsets[idx], cnt = lv.Counts[idx];
        if (cnt == 0) return null;
        string p = Path.Combine(_cacheDir, $"t{level}_{tx}_{ty}.bin");
        byte[] comp;
        if (File.Exists(p)) comp = await File.ReadAllBytesAsync(p);
        else
        {
            comp = await FetchAsync(off, cnt);
            if (comp == null) return null;
            try { await File.WriteAllBytesAsync(p + ".tmp", comp); File.Move(p + ".tmp", p, true); } catch (IOException) { }
        }
        return Decode(comp, lv.TileWidth, lv.TileHeight);
    }

    float[] Decode(byte[] comp, int w, int h)
    {
        int bps = BitsPerSample / 8;
        byte[] raw;
        if (Compression == 1) raw = comp;
        else if (Compression is 8 or 32946)
        {
            using var z = new ZLibStream(new MemoryStream(comp), CompressionMode.Decompress);
            raw = new byte[w * h * bps];
            int read = 0;
            while (read < raw.Length) { int r = z.Read(raw, read, raw.Length - read); if (r <= 0) break; read += r; }
        }
        else throw new NotSupportedException($"Compression TIFF {Compression} non gérée");

        var outp = new float[w * h];
        if (Predictor == 3)
        {
            // Prédicteur flottant : différences d'octets sur la ligne, puis octets rangés par plans (poids fort d'abord)
            var row = new byte[w * bps];
            for (int y = 0; y < h; y++)
            {
                int b = y * w * bps;
                for (int i = 1; i < w * bps; i++) raw[b + i] = (byte)(raw[b + i] + raw[b + i - 1]);
                for (int x = 0; x < w; x++)
                    for (int k = 0; k < bps; k++)
                        row[x * bps + k] = raw[b + (bps - 1 - k) * w + x];
                for (int x = 0; x < w; x++)
                    outp[y * w + x] = bps == 4 ? BitConverter.ToSingle(row, x * 4) : (float)BitConverter.ToDouble(row, x * 8);
            }
        }
        else
        {
            for (int i = 0; i < w * h; i++)
            {
                outp[i] = (bps, SampleFormat) switch
                {
                    (1, _) => raw[i],
                    (2, 2) => BitConverter.ToInt16(raw, i * 2),
                    (2, _) => BitConverter.ToUInt16(raw, i * 2),
                    (4, 3) => BitConverter.ToSingle(raw, i * 4),
                    (4, 2) => BitConverter.ToInt32(raw, i * 4),
                    _ => 0,
                };
            }
            if (Predictor == 2)
                for (int y = 0; y < h; y++)
                    for (int x = 1; x < w; x++)
                    {
                        int i = y * w + x;
                        outp[i] = bps switch
                        {
                            1 => (byte)(outp[i] + outp[i - 1]),
                            2 => SampleFormat == 2 ? (short)(outp[i] + outp[i - 1]) : (ushort)(outp[i] + outp[i - 1]),
                            _ => outp[i] + outp[i - 1],
                        };
                    }
        }
        if (NoData.HasValue)
        {
            float nd = (float)NoData.Value;
            for (int i = 0; i < outp.Length; i++) if (outp[i] == nd) outp[i] = float.NaN;
        }
        return outp;
    }
}
