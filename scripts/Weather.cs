using System;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using Godot;

namespace SteamEarth;

/// <summary>Météo en un point et un instant.</summary>
public struct WeatherSample
{
    public float Wind10East, Wind10North;   // m/s, vent à 10 m
    public float Wind850East, Wind850North; // m/s, vent vers 1 500 m
    public float Gust;                      // m/s
    public float TemperatureC;
    public float CloudLow, CloudMid, CloudHigh; // 0..1
    public float PrecipMmH;
    public float FrozenFraction;            // 0..1 (neige si > 0,5)
    public float VisibilityM;

    public float CloudTotal => 1 - (1 - CloudLow) * (1 - CloudMid) * (1 - CloudHigh);
    public float Wind10Speed => MathF.Sqrt(Wind10East * Wind10East + Wind10North * Wind10North);
    /// <summary>Direction d'où vient le vent, en degrés (0 = nord, 90 = est), convention météo.</summary>
    public float Wind10FromDeg => (MathF.Atan2(-Wind10East, -Wind10North) * 180 / MathF.PI + 360) % 360;

    /// <summary>Vent (est, nord) interpolé selon l'altitude au-dessus du sol (m).</summary>
    public (float east, float north) WindAt(double heightAboveGround)
    {
        float t = (float)Math.Clamp((heightAboveGround - 10) / 1500.0, 0, 1);
        return (Wind10East + (Wind850East - Wind10East) * t, Wind10North + (Wind850North - Wind10North) * t);
    }
}

/// <summary>
/// Météo réelle mondiale (NOAA GFS) : téléchargée depuis la release GitHub « weather-latest »
/// (mise à jour toutes les 6 h par GitHub Actions), mise en cache dans user://,
/// avec un fichier de secours inclus dans le jeu. Interpolation bilinéaire (espace) et linéaire (temps).
/// </summary>
public partial class Weather : Node
{
    public const string RemoteUrl = "https://github.com/macgama/steam-earth/releases/download/weather-latest/weather.bin.gz";
    const string CachePath = "user://weather.bin.gz";
    const string FallbackPath = "res://data/weather/fallback.bin.gz";
    const double RefreshSeconds = 30 * 60;

    public string Status { get; private set; } = "aucune donnée";
    public DateTime RunTime { get; private set; }
    public bool HasData => _data != null;

    byte[] _data;
    int _w, _h, _channels, _frames;
    DateTime[] _valid;
    string[] _enc;
    float[] _min, _max;
    int _iU10, _iV10, _iU850, _iV850, _iGust, _iT, _iLow, _iMid, _iHigh, _iP, _iFz, _iVis, _iSnow = -1, _iOro = -1;

    HttpRequest _http;
    double _sinceRefresh;

    public override void _Ready()
    {
        // Le cache (plus récent) d'abord, sinon le fichier de secours
        if (!(Godot.FileAccess.FileExists(CachePath) && TryLoad(Godot.FileAccess.GetFileAsBytes(CachePath), "cache"))) TryLoad(Godot.FileAccess.GetFileAsBytes(FallbackPath), "secours");
        _http = new HttpRequest { MaxRedirects = 8, Timeout = 60 };
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
        var err = _http.Request(RemoteUrl);
        if (err != Error.Ok) GD.PrintErr($"[Weather] requête impossible : {err}");
    }

    void OnDownloaded(long result, long code, string[] headers, byte[] body)
    {
        if (result != (long)HttpRequest.Result.Success || code != 200)
        {
            GD.Print($"[Weather] téléchargement indisponible (résultat {result}, HTTP {code}), données {Status}");
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
            if (_data != null && run < RunTime) return false; // garder le plus récent
            // même échéance mais moins de champs (ancien format sans la neige) : garder la version la plus complète
            if (_data != null && run == RunTime && root.GetProperty("channels").GetArrayLength() < _enc.Length) return false;
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
            _iU10 = I("wind10_u"); _iV10 = I("wind10_v"); _iU850 = I("wind850_u"); _iV850 = I("wind850_v");
            _iGust = I("gust"); _iT = I("temp2m"); _iLow = I("cloud_low"); _iMid = I("cloud_mid"); _iHigh = I("cloud_high");
            _iP = I("precip"); _iFz = I("frozen"); _iVis = I("visibility");
            _iSnow = I("snow_depth"); _iOro = I("orography");
            _w = w; _h = h; _frames = nf; _channels = nc; _data = data;
            RunTime = run;
            Status = $"GFS {run:dd.MM HH}h UTC ({origin})";
            GD.Print($"[Weather] {Status}, {nf} échéances jusqu'au {_valid[nf - 1]:dd.MM HH}h UTC");
            return true;
        }
        catch (Exception e)
        {
            GD.PrintErr($"[Weather] fichier météo illisible ({origin}) : {e.Message}");
            return false;
        }
    }

    float Decode(int c, float q)
    {
        float t = q / 255f;
        return _enc[c] == "sqrt" ? _max[c] * t * t : _min[c] + t * (_max[c] - _min[c]);
    }

    float Raw(int frame, int c, double lat, double lon)
    {
        double px = ((lon % 360) + 360) % 360, py = Math.Clamp(90 - lat, 0, _h - 1.001);
        int x0 = (int)px, y0 = (int)py, x1 = (x0 + 1) % _w;
        double tx = px - x0, ty = py - y0;
        int b = (frame * _channels + c) * _w * _h;
        double a = _data[b + y0 * _w + x0], bb = _data[b + y0 * _w + x1];
        double cc = _data[b + (y0 + 1) * _w + x0], d = _data[b + (y0 + 1) * _w + x1];
        return (float)((a * (1 - tx) + bb * tx) * (1 - ty) + (cc * (1 - tx) + d * tx) * ty);
    }

    void FrameAt(DateTime utc, out int f0, out int f1, out float t)
    {
        f0 = 0; f1 = 0; t = 0;
        if (utc <= _valid[0]) return;
        if (utc >= _valid[_frames - 1]) { f0 = f1 = _frames - 1; return; }
        for (int i = 0; i < _frames - 1; i++)
        {
            if (utc < _valid[i + 1])
            {
                f0 = i; f1 = i + 1;
                t = (float)((utc - _valid[i]).TotalSeconds / (_valid[i + 1] - _valid[i]).TotalSeconds);
                return;
            }
        }
    }

    float Value(int c, double lat, double lon, int f0, int f1, float t)
    {
        if (c < 0) return 0;
        float q = Raw(f0, c, lat, lon) * (1 - t) + Raw(f1, c, lat, lon) * t;
        return Decode(c, q);
    }

    public WeatherSample Sample(double lat, double lon, DateTime utc)
    {
        var s = new WeatherSample { VisibilityM = 30000, TemperatureC = 15 };
        if (_data == null) return s;
        FrameAt(utc, out int f0, out int f1, out float t);
        s.Wind10East = Value(_iU10, lat, lon, f0, f1, t);
        s.Wind10North = Value(_iV10, lat, lon, f0, f1, t);
        s.Wind850East = Value(_iU850, lat, lon, f0, f1, t);
        s.Wind850North = Value(_iV850, lat, lon, f0, f1, t);
        s.Gust = Value(_iGust, lat, lon, f0, f1, t);
        s.TemperatureC = Value(_iT, lat, lon, f0, f1, t) - 273.15f;
        s.CloudLow = Value(_iLow, lat, lon, f0, f1, t) / 100f;
        s.CloudMid = Value(_iMid, lat, lon, f0, f1, t) / 100f;
        s.CloudHigh = Value(_iHigh, lat, lon, f0, f1, t) / 100f;
        s.PrecipMmH = Value(_iP, lat, lon, f0, f1, t);
        s.FrozenFraction = Value(_iFz, lat, lon, f0, f1, t) / 100f;
        s.VisibilityM = Value(_iVis, lat, lon, f0, f1, t);
        return s;
    }

    /// <summary>Carte mondiale de couverture nuageuse totale (0..255), 360×181, pour la vue depuis l'espace.</summary>
    public byte[] CloudCoverMap(DateTime utc)
    {
        if (_data == null) return null;
        FrameAt(utc, out int f0, out int f1, out float t);
        var img = new byte[_w * _h];
        int n = _w * _h;
        int b0 = f0 * _channels * n, b1 = f1 * _channels * n;
        var layers = new[] { _iLow, _iMid, _iHigh };
        var weights = new[] { 1f, 0.85f, 0.45f }; // les cirrus sont fins et peu opaques
        for (int i = 0; i < n; i++)
        {
            float clear = 1;
            for (int k = 0; k < 3; k++)
            {
                int c = layers[k];
                float v = (_data[b0 + c * n + i] * (1 - t) + _data[b1 + c * n + i] * t) / 255f;
                clear *= 1 - v * weights[k];
            }
            img[i] = (byte)Math.Clamp((1 - clear) * 255, 0, 255);
        }
        return img;
    }

    /// <summary>Le fichier météo contient l'épaisseur de neige au sol (versions récentes du pipeline).</summary>
    public bool HasSnow => _data != null && _iSnow >= 0 && _iOro >= 0;

    /// <summary>
    /// Carte mondiale de la neige pour le shader du terrain (360×181, flottants) : R = épaisseur de neige (m),
    /// G = altitude du relief du modèle GFS (m), B = température à 2 m (K). Interpolée dans le temps.
    /// </summary>
    public Image SnowMap(DateTime utc)
    {
        if (!HasSnow) return null;
        FrameAt(utc, out int f0, out int f1, out float t);
        var img = Image.CreateEmpty(_w, _h, false, Image.Format.Rgbf);
        int n = _w * _h;
        for (int y = 0; y < _h; y++)
        for (int x = 0; x < _w; x++)
        {
            int i = y * _w + x;
            float Q(int c) => _data[(f0 * _channels + c) * n + i] * (1 - t) + _data[(f1 * _channels + c) * n + i] * t;
            img.SetPixel(x, y, new Color(Decode(_iSnow, Q(_iSnow)), Decode(_iOro, Q(_iOro)), _iT >= 0 ? Decode(_iT, Q(_iT)) : 280));
        }
        return img;
    }

    public int MapWidth => _w;
    public int MapHeight => _h;
}
