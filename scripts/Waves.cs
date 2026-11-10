using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Godot;

namespace SteamEarth;

/// <summary>Un système de vagues : hauteur significative, période, direction de propagation.</summary>
public struct WaveSystem
{
    public float Height;    // m (hauteur significative Hs)
    public float Period;    // s
    public float ToEast, ToNorth; // direction de propagation (vecteur unitaire)
    /// <summary>Direction d'où viennent les vagues, en degrés (0 = nord, 90 = est).</summary>
    public float FromDeg => (MathF.Atan2(-ToEast, -ToNorth) * 180 / MathF.PI + 360) % 360;
    public float Wavelength => 9.81f * Period * Period / (2 * MathF.PI); // eau profonde
}

/// <summary>État de la mer en un point : mer du vent + deux houles (NOAA GFS-Wave / WAVEWATCH III).</summary>
public struct SeaState
{
    public bool Valid;           // false à terre, sous la banquise ou sans données
    public float Hs;             // hauteur significative totale (m)
    public WaveSystem Wind, Swell1, Swell2;
}

/// <summary>
/// Vagues et houle réelles : fichier waves.bin.gz de la release « weather-latest », régénéré toutes les 6 h
/// par le workflow Météo GFS (tools/ocean/build_waves.py). Même fonctionnement que <see cref="Weather"/> :
/// cache dans user://, fichier de secours inclus dans le jeu, interpolation dans l'espace et le temps.
/// </summary>
public partial class Waves : Node
{
    public const string RemoteUrl = "https://github.com/macgama/steam-earth/releases/download/weather-latest/waves.bin.gz";
    const string CachePath = "user://waves.bin.gz";
    const string FallbackPath = "res://data/ocean/waves_fallback.bin.gz";
    const double RefreshSeconds = 30 * 60;

    public string Status { get; private set; } = "aucune donnée";
    public DateTime RunTime { get; private set; }

    byte[] _data;
    int _w, _h, _channels, _frames;
    double _step;
    DateTime[] _valid;
    string[] _enc;
    float[] _min, _max;
    int _iHs, _iWh, _iWt, _iWd, _iS1h, _iS1t, _iS1d, _iS2h, _iS2t, _iS2d;
    HttpRequest _http;
    double _sinceRefresh;

    public override void _Ready()
    {
        if (!(Godot.FileAccess.FileExists(CachePath) && TryLoad(Godot.FileAccess.GetFileAsBytes(CachePath), "cache")))
            TryLoad(Godot.FileAccess.GetFileAsBytes(FallbackPath), "secours");
        _http = new HttpRequest { MaxRedirects = 8, Timeout = 90 };
        AddChild(_http);
        _http.RequestCompleted += OnDownloaded;
        Download();
    }

    public override void _Process(double delta)
    {
        _sinceRefresh += delta;
        if (_sinceRefresh > RefreshSeconds) Download();
    }

    void Download()
    {
        _sinceRefresh = 0;
        if (_http.GetHttpClientStatus() != HttpClient.Status.Disconnected) return;
        _http.Request(RemoteUrl);
    }

    void OnDownloaded(long result, long code, string[] headers, byte[] body)
    {
        if (result != (long)HttpRequest.Result.Success || code != 200)
        {
            GD.Print($"[Waves] téléchargement indisponible (résultat {result}, HTTP {code}), données {Status}");
            return;
        }
        var previous = RunTime;
        if (TryLoad(body, "en ligne") && RunTime >= previous)
        {
            using var f = Godot.FileAccess.Open(CachePath, Godot.FileAccess.ModeFlags.Write);
            f?.StoreBuffer(body);
        }
    }

    bool TryLoad(byte[] gz, string origin)
    {
        if (gz == null || gz.Length == 0) return false;
        try
        {
            using var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress);
            using var ms = new MemoryStream();
            z.CopyTo(ms);
            var buf = ms.ToArray();
            if (System.Text.Encoding.ASCII.GetString(buf, 0, 4) != "SEWX") throw new InvalidDataException("format");
            int jl = BitConverter.ToInt32(buf, 4);
            using var doc = JsonDocument.Parse(new ReadOnlyMemory<byte>(buf, 8, jl));
            var root = doc.RootElement;
            var run = DateTime.Parse(root.GetProperty("run").GetString()).ToUniversalTime();
            if (_data != null && run < RunTime) return false;
            int w = root.GetProperty("width").GetInt32(), h = root.GetProperty("height").GetInt32();
            var valid = root.GetProperty("valid");
            var chans = root.GetProperty("channels");
            int nf = valid.GetArrayLength(), nc = chans.GetArrayLength();
            var data = new byte[nf * nc * w * h];
            Buffer.BlockCopy(buf, 8 + jl, data, 0, data.Length);
            _valid = new DateTime[nf];
            for (int i = 0; i < nf; i++) _valid[i] = DateTime.Parse(valid[i].GetString()).ToUniversalTime();
            _enc = new string[nc]; _min = new float[nc]; _max = new float[nc];
            var names = new string[nc];
            for (int c = 0; c < nc; c++)
            {
                var ch = chans[c];
                names[c] = ch.GetProperty("name").GetString();
                _enc[c] = ch.GetProperty("encoding").GetString();
                _min[c] = (float)ch.GetProperty("min").GetDouble();
                _max[c] = (float)ch.GetProperty("max").GetDouble();
            }
            int I(string n) => Array.IndexOf(names, n);
            _iHs = I("hs"); _iWh = I("wind_h"); _iWt = I("wind_t"); _iWd = I("wind_dir");
            _iS1h = I("swell1_h"); _iS1t = I("swell1_t"); _iS1d = I("swell1_dir");
            _iS2h = I("swell2_h"); _iS2t = I("swell2_t"); _iS2d = I("swell2_dir");
            _step = root.GetProperty("step").GetDouble();
            _w = w; _h = h; _frames = nf; _channels = nc; _data = data;
            RunTime = run;
            Status = $"GFS-Wave {run:dd.MM HH}h UTC ({origin})";
            GD.Print($"[Waves] {Status}, {nf} échéances");
            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Waves] fichier de vagues illisible ({origin}) : {e.Message}");
            return false;
        }
    }

    void FrameAt(DateTime utc, out int f0, out int f1, out float t)
    {
        f0 = 0; f1 = 0; t = 0;
        if (utc <= _valid[0]) return;
        if (utc >= _valid[_frames - 1]) { f0 = f1 = _frames - 1; return; }
        for (int i = 0; i < _frames - 1; i++)
            if (utc < _valid[i + 1])
            {
                f0 = i; f1 = i + 1;
                t = (float)((utc - _valid[i]).TotalSeconds / (_valid[i + 1] - _valid[i]).TotalSeconds);
                return;
            }
    }

    float Decode(int c, int q)
    {
        float t = q / 254f;
        return _enc[c] switch { "sqrt" => _max[c] * t * t, "angle" => t * 360, _ => _min[c] + t * (_max[c] - _min[c]) };
    }

    /// <summary>
    /// Moyenne pondérée des 4 points de grille voisins et des 2 échéances, en ignorant les points sans donnée.
    /// Les directions sont moyennées comme des vecteurs (pondérés par la hauteur de la houle).
    /// </summary>
    void Accumulate(int f0, int f1, float t, double lat, double lon, Action<int, int, float> add)
    {
        double px = (((lon % 360) + 360) % 360) / _step, py = Math.Clamp((90 - lat) / _step, 0, _h - 1.001);
        int x0 = (int)px, y0 = (int)py, x1 = (x0 + 1) % _w;
        float tx = (float)(px - x0), ty = (float)(py - y0);
        int n = _w * _h;
        for (int f = 0; f < 2; f++)
        {
            float wf = f == 0 ? 1 - t : t;
            if (wf <= 0) continue;
            int frame = f == 0 ? f0 : f1;
            add(frame * _channels * n + y0 * _w + x0, n, wf * (1 - tx) * (1 - ty));
            add(frame * _channels * n + y0 * _w + x1, n, wf * tx * (1 - ty));
            add(frame * _channels * n + (y0 + 1) * _w + x0, n, wf * (1 - tx) * ty);
            add(frame * _channels * n + (y0 + 1) * _w + x1, n, wf * tx * ty);
        }
    }

    /// <summary>
    /// État de la mer au point donné ; près des côtes (points de grille à terre), on prend le point de mer le plus
    /// proche dans un rayon d'environ 1,5°.
    /// </summary>
    public SeaState Sample(double lat, double lon, DateTime utc)
    {
        var s = SampleAt(lat, lon, utc);
        if (s.Valid || _data == null) return s;
        for (int r = 1; r <= 3; r++)
        {
            double best = double.MaxValue;
            SeaState found = default;
            for (int dy = -r; dy <= r; dy++)
            for (int dx = -r; dx <= r; dx++)
            {
                if (Math.Max(Math.Abs(dx), Math.Abs(dy)) != r) continue;
                var c = SampleAt(lat + dy * _step, lon + dx * _step / Math.Max(0.2, Math.Cos(lat * Math.PI / 180)), utc);
                double dist = dx * dx + dy * dy;
                if (c.Valid && dist < best) { best = dist; found = c; }
            }
            if (found.Valid) return found;
        }
        return s;
    }

    SeaState SampleAt(double lat, double lon, DateTime utc)
    {
        var s = new SeaState();
        if (_data == null || _iHs < 0) return s;
        FrameAt(utc, out int f0, out int f1, out float t);
        float wsum = 0, hs = 0;
        var acc = new float[3, 4]; // [système][h, t, e, n]
        var hIdx = new[] { _iWh, _iS1h, _iS2h };
        var tIdx = new[] { _iWt, _iS1t, _iS2t };
        var dIdx = new[] { _iWd, _iS1d, _iS2d };
        Accumulate(f0, f1, t, lat, lon, (b, n, w) =>
        {
            int q = _data[b + _iHs * n];
            if (q == 255 || w <= 0) return;
            wsum += w;
            hs += Decode(_iHs, q) * w;
            for (int k = 0; k < 3; k++)
            {
                if (hIdx[k] < 0) continue;
                int qh = _data[b + hIdx[k] * n], qt = _data[b + tIdx[k] * n], qd = _data[b + dIdx[k] * n];
                if (qh == 255 || qt == 255 || qd == 255) continue; // système absent ici : hauteur nulle
                float h = Decode(hIdx[k], qh);
                double from = Decode(dIdx[k], qd) * Math.PI / 180;
                acc[k, 0] += h * w;
                acc[k, 1] += Decode(tIdx[k], qt) * h * w;
                acc[k, 2] += (float)(-Math.Sin(from)) * h * w; // propagation = provenance + 180°
                acc[k, 3] += (float)(-Math.Cos(from)) * h * w;
            }
        });
        if (wsum < 0.25f) return s; // surtout de la terre : pas d'état de mer fiable
        s.Valid = true;
        s.Hs = hs / wsum;
        WaveSystem Sys(int k)
        {
            var w = new WaveSystem();
            if (acc[k, 0] <= 1e-4f) return w;
            w.Height = acc[k, 0] / wsum;
            w.Period = acc[k, 1] / acc[k, 0];
            float len = MathF.Sqrt(acc[k, 2] * acc[k, 2] + acc[k, 3] * acc[k, 3]);
            if (len > 1e-6f) { w.ToEast = acc[k, 2] / len; w.ToNorth = acc[k, 3] / len; } else w.ToNorth = 1;
            return w;
        }
        s.Wind = Sys(0); s.Swell1 = Sys(1); s.Swell2 = Sys(2);
        return s;
    }
}
