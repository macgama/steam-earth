using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading.Tasks;

namespace SteamEarth.Data;

/// <summary>
/// Horaires officiels des transports publics suisses (opentransportdata.swiss, GTFS), au format compact
/// produit par tools/transport/build_timetable.py (voir ce script pour le format) : toutes les courses de
/// l'année (trains, trams, bus, bateaux, funiculaires, téléphériques), en heure légale suisse.
/// Le fichier est publié chaque semaine par GitHub Actions dans la release « timetable-latest » ; le jeu le
/// télécharge (≈ 20 Mo) au plus une fois par jour et le garde en cache pour jouer hors ligne.
/// </summary>
public sealed class Timetable
{
    public const string RemoteUrl = "https://github.com/macgama/steam-earth/releases/download/timetable-latest/timetable_ch.bin.gz";
    public const string Attribution = "Horaires : opentransportdata.swiss";

    public string FeedVersion;
    public DateTime FirstDay;   // date locale du jour 0
    public int Days;
    public string[] Strings;
    // gares
    public int[] StationName; public double[] StationLat, StationLon;
    // arrêts (quai / poteau) → gare
    public int[] StopStation, StopPlatform;
    public int[] AgencyName;
    public int[] RouteName, RouteType, RouteAgency;
    byte[][] _services;
    // motifs : arrêts PatStart[p] .. PatStart[p+1]-1
    public int[] PatStart, PatStop, PatArr, PatDwell; public byte[] PatFlags;
    // courses
    public int[] TripPattern, TripRoute, TripService, TripStart, TripHeadsign, TripNumber;
    // index : courses de chaque motif triées par heure de départ ; passages (motif, rang) par gare
    int[] _patTripStart, _patTrips;
    int[] _staRefStart, _staRefPat, _staRefPos;
    // grille 0,02° pour trouver la gare la plus proche
    readonly Dictionary<long, List<int>> _grid = new();

    public int Stations => StationName.Length;
    public int Trips => TripPattern.Length;

    // ------------------------------------------------------------------ chargement

    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Charge le cache, et le met à jour en ligne s'il a plus d'un jour (ou s'il manque).
    /// Null si aucun horaire n'est disponible (hors ligne sans cache).</summary>
    public static async Task<Timetable> LoadAsync(string cachePath, Action<string> status)
    {
        Timetable tt = null;
        bool fresh = File.Exists(cachePath) && (DateTime.UtcNow - File.GetLastWriteTimeUtc(cachePath)).TotalHours < 24;
        if (File.Exists(cachePath))
        {
            try { tt = Parse(await File.ReadAllBytesAsync(cachePath)); }
            catch (Exception e) { status?.Invoke("cache illisible : " + e.Message); }
        }
        if (tt != null && fresh) return tt;
        try
        {
            status?.Invoke("téléchargement des horaires…");
            var gz = await Http.GetByteArrayAsync(RemoteUrl);
            var fresher = Parse(gz);
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
            string tmp = cachePath + ".tmp";
            await File.WriteAllBytesAsync(tmp, gz);
            File.Move(tmp, cachePath, true);
            return fresher;
        }
        catch (Exception e)
        {
            status?.Invoke("horaires hors ligne (" + e.Message + ")");
            return tt;
        }
    }

    public static Timetable Parse(byte[] gz)
    {
        byte[] b;
        using (var z = new GZipStream(new MemoryStream(gz), CompressionMode.Decompress))
        using (var ms = new MemoryStream())
        {
            z.CopyTo(ms);
            b = ms.ToArray();
        }
        if (b.Length < 5 || b[0] != 'S' || b[1] != 'E' || b[2] != 'T' || b[3] != 'T' || b[4] != 1) throw new InvalidDataException("format d'horaire inconnu");
        int p = 5;
        var t = new Timetable();
        t.FeedVersion = Str(b, ref p);
        int first = (int)V(b, ref p);
        t.FirstDay = new DateTime(first / 10000, first / 100 % 100, first % 100);
        t.Days = (int)V(b, ref p);
        int n = (int)V(b, ref p);
        t.Strings = new string[n];
        for (int i = 0; i < n; i++) t.Strings[i] = Str(b, ref p);

        n = (int)V(b, ref p);
        t.StationName = new int[n]; t.StationLat = new double[n]; t.StationLon = new double[n];
        for (int i = 0; i < n; i++)
        {
            t.StationName[i] = (int)V(b, ref p);
            t.StationLat[i] = Zz(V(b, ref p)) * 1e-6;
            t.StationLon[i] = Zz(V(b, ref p)) * 1e-6;
            long key = GridKey(t.StationLat[i], t.StationLon[i]);
            if (!t._grid.TryGetValue(key, out var cell)) t._grid[key] = cell = new List<int>();
            cell.Add(i);
        }
        n = (int)V(b, ref p);
        t.StopStation = new int[n]; t.StopPlatform = new int[n];
        for (int i = 0; i < n; i++) { t.StopStation[i] = (int)V(b, ref p); t.StopPlatform[i] = (int)V(b, ref p); }
        n = (int)V(b, ref p);
        t.AgencyName = new int[n];
        for (int i = 0; i < n; i++) t.AgencyName[i] = (int)V(b, ref p);
        n = (int)V(b, ref p);
        t.RouteName = new int[n]; t.RouteType = new int[n]; t.RouteAgency = new int[n];
        for (int i = 0; i < n; i++) { t.RouteName[i] = (int)V(b, ref p); t.RouteType[i] = (int)V(b, ref p); t.RouteAgency[i] = (int)V(b, ref p); }
        n = (int)V(b, ref p);
        int nb = (t.Days + 7) / 8;
        t._services = new byte[n][];
        for (int i = 0; i < n; i++) { t._services[i] = new byte[nb]; Buffer.BlockCopy(b, p, t._services[i], 0, nb); p += nb; }

        n = (int)V(b, ref p);
        t.PatStart = new int[n + 1];
        var stop = new List<int>(); var arr = new List<int>(); var dwell = new List<int>(); var fl = new List<byte>();
        for (int i = 0; i < n; i++)
        {
            t.PatStart[i] = stop.Count;
            int k = (int)V(b, ref p);
            for (int j = 0; j < k; j++)
            {
                stop.Add((int)V(b, ref p)); arr.Add((int)V(b, ref p)); dwell.Add((int)V(b, ref p)); fl.Add((byte)V(b, ref p));
            }
        }
        t.PatStart[n] = stop.Count;
        t.PatStop = stop.ToArray(); t.PatArr = arr.ToArray(); t.PatDwell = dwell.ToArray(); t.PatFlags = fl.ToArray();

        n = (int)V(b, ref p);
        t.TripPattern = new int[n]; t.TripRoute = new int[n]; t.TripService = new int[n];
        t.TripStart = new int[n]; t.TripHeadsign = new int[n]; t.TripNumber = new int[n];
        for (int i = 0; i < n; i++)
        {
            t.TripPattern[i] = (int)V(b, ref p); t.TripRoute[i] = (int)V(b, ref p); t.TripService[i] = (int)V(b, ref p);
            t.TripStart[i] = (int)V(b, ref p); t.TripHeadsign[i] = (int)V(b, ref p); t.TripNumber[i] = (int)V(b, ref p);
        }
        t.BuildIndex();
        return t;
    }

    void BuildIndex()
    {
        int np = PatStart.Length - 1, nt = TripPattern.Length;
        _patTripStart = new int[np + 1];
        foreach (int pat in TripPattern) _patTripStart[pat + 1]++;
        for (int i = 0; i < np; i++) _patTripStart[i + 1] += _patTripStart[i];
        _patTrips = new int[nt];
        var fill = (int[])_patTripStart.Clone();
        for (int i = 0; i < nt; i++) _patTrips[fill[TripPattern[i]]++] = i;
        for (int pat = 0; pat < np; pat++)
            Array.Sort(_patTrips, _patTripStart[pat], _patTripStart[pat + 1] - _patTripStart[pat],
                Comparer<int>.Create((a, c) => TripStart[a].CompareTo(TripStart[c])));

        int ns = StationName.Length;
        _staRefStart = new int[ns + 1];
        for (int pat = 0; pat < np; pat++)
            for (int k = PatStart[pat]; k < PatStart[pat + 1]; k++) _staRefStart[StopStation[PatStop[k]] + 1]++;
        for (int i = 0; i < ns; i++) _staRefStart[i + 1] += _staRefStart[i];
        _staRefPat = new int[_staRefStart[ns]]; _staRefPos = new int[_staRefStart[ns]];
        var f2 = (int[])_staRefStart.Clone();
        for (int pat = 0; pat < np; pat++)
            for (int k = PatStart[pat]; k < PatStart[pat + 1]; k++)
            {
                int s = StopStation[PatStop[k]], j = f2[s]++;
                _staRefPat[j] = pat; _staRefPos[j] = k - PatStart[pat];
            }
    }

    static long GridKey(double lat, double lon) => ((long)Math.Floor(lat * 50) << 32) ^ (uint)(int)Math.Floor(lon * 50);

    static ulong V(byte[] b, ref int p)
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

    static long Zz(ulong n) => (long)(n >> 1) ^ -(long)(n & 1);

    static string Str(byte[] b, ref int p)
    {
        int len = (int)V(b, ref p);
        var s = System.Text.Encoding.UTF8.GetString(b, p, len);
        p += len;
        return s;
    }

    // ------------------------------------------------------------------ requêtes

    public string S(int i) => Strings[i];
    public string StationNameOf(int station) => Strings[StationName[station]];

    /// <summary>Gare la plus proche (≤ <paramref name="maxM"/> m), -1 sinon.</summary>
    public int NearestStation(double lat, double lon, double maxM, out double distM)
    {
        int best = -1; distM = maxM;
        double cos = Math.Cos(lat * Math.PI / 180);
        int r = (int)Math.Ceiling(maxM / 111_320.0 * 50 / Math.Max(cos, 0.2)) + 1;
        int la0 = (int)Math.Floor(lat * 50), lo0 = (int)Math.Floor(lon * 50);
        for (int a = -r; a <= r; a++)
            for (int c = -r; c <= r; c++)
            {
                long key = ((long)(la0 + a) << 32) ^ (uint)(lo0 + c);
                if (!_grid.TryGetValue(key, out var cell)) continue;
                foreach (int s in cell)
                {
                    if (_staRefStart[s + 1] == _staRefStart[s]) continue; // aucune course
                    double dy = (StationLat[s] - lat) * 111_320, dx = (StationLon[s] - lon) * 111_320 * cos;
                    double d = Math.Sqrt(dx * dx + dy * dy);
                    if (d < distM) { distM = d; best = s; }
                }
            }
        return best;
    }

    /// <summary>Toutes les gares (avec des courses) à moins de <paramref name="maxM"/> m : une grande gare et
    /// les arrêts de bus et de tram devant elle forment un seul pôle d'échange.</summary>
    public List<int> StationsNear(double lat, double lon, double maxM)
    {
        var res = new List<int>();
        double cos = Math.Cos(lat * Math.PI / 180);
        int r = (int)Math.Ceiling(maxM / 111_320.0 * 50 / Math.Max(cos, 0.2)) + 1;
        int la0 = (int)Math.Floor(lat * 50), lo0 = (int)Math.Floor(lon * 50);
        for (int a = -r; a <= r; a++)
            for (int c = -r; c <= r; c++)
            {
                long key = ((long)(la0 + a) << 32) ^ (uint)(lo0 + c);
                if (!_grid.TryGetValue(key, out var cell)) continue;
                foreach (int s in cell)
                {
                    if (_staRefStart[s + 1] == _staRefStart[s]) continue;
                    double dy = (StationLat[s] - lat) * 111_320, dx = (StationLon[s] - lon) * 111_320 * cos;
                    if (dx * dx + dy * dy <= maxM * maxM) res.Add(s);
                }
            }
        return res;
    }

    /// <summary>Nombre de passages (motifs) d'une gare : sert à choisir le nom du pôle.</summary>
    public int Importance(int station) => _staRefStart[station + 1] - _staRefStart[station];

    public bool Runs(int trip, int day) =>
        day >= 0 && day < Days && (_services[TripService[trip]][day >> 3] & (1 << (day & 7))) != 0;

    /// <summary>Minutes locales depuis minuit du jour 0.</summary>
    public double MinutesOf(DateTime local) => (local - FirstDay).TotalMinutes;

    public DateTime LocalOf(double minutes) => FirstDay.AddMinutes(minutes);

    public int PatternLength(int trip) => PatStart[TripPattern[trip] + 1] - PatStart[TripPattern[trip]];
    public int StopAt(int trip, int k) => PatStop[PatStart[TripPattern[trip]] + k];
    public int StationAt(int trip, int k) => StopStation[StopAt(trip, k)];
    /// <summary>Arrivée / départ au k-ième arrêt (minutes depuis minuit du jour 0), pour la course du jour <paramref name="day"/>.</summary>
    public int Arrival(int trip, int day, int k) => day * 1440 + TripStart[trip] + PatArr[PatStart[TripPattern[trip]] + k];
    public int Departure(int trip, int day, int k) => Arrival(trip, day, k) + PatDwell[PatStart[TripPattern[trip]] + k];
    public bool CanBoard(int trip, int k) => (PatFlags[PatStart[TripPattern[trip]] + k] & 1) == 0 && k < PatternLength(trip) - 1;
    public bool CanAlight(int trip, int k) => (PatFlags[PatStart[TripPattern[trip]] + k] & 2) == 0;

    public struct Dep
    {
        public int Trip, Day, Pos;   // course, jour de service, rang de l'arrêt dans la course
        public int Minutes;          // départ (minutes depuis minuit du jour 0)
    }

    /// <summary>Prochains départs d'une gare entre <paramref name="fromMin"/> et +<paramref name="windowMin"/> minutes.</summary>
    public List<Dep> Departures(int station, double fromMin, int windowMin, int max)
    {
        var res = new List<Dep>();
        int today = (int)Math.Floor(fromMin / 1440);
        for (int j = _staRefStart[station]; j < _staRefStart[station + 1]; j++)
        {
            int pat = _staRefPat[j], pos = _staRefPos[j];
            int idx = PatStart[pat] + pos;
            if ((PatFlags[idx] & 1) != 0 || pos == PatStart[pat + 1] - PatStart[pat] - 1) continue; // pas de montée / terminus
            int off = PatArr[idx] + PatDwell[idx];
            for (int day = today - 1; day <= today; day++) // une course partie la veille peut passer après minuit
            {
                double lo = fromMin - day * 1440 - off, hi = lo + windowMin;
                int a = _patTripStart[pat], z = _patTripStart[pat + 1];
                // première course dont le départ ≥ lo (recherche dichotomique)
                int l = a, r = z;
                while (l < r) { int m = (l + r) / 2; if (TripStart[_patTrips[m]] < lo) l = m + 1; else r = m; }
                for (int q = l; q < z && TripStart[_patTrips[q]] <= hi; q++)
                {
                    int trip = _patTrips[q];
                    if (!Runs(trip, day)) continue;
                    res.Add(new Dep { Trip = trip, Day = day, Pos = pos, Minutes = day * 1440 + TripStart[trip] + off });
                }
            }
        }
        res.Sort((x, y) => x.Minutes != y.Minutes ? x.Minutes.CompareTo(y.Minutes) : TripRoute[x.Trip].CompareTo(TripRoute[y.Trip]));
        if (res.Count > max) res.RemoveRange(max, res.Count - max);
        return res;
    }

    /// <summary>Catégorie de transport (types GTFS de base et étendus).</summary>
    public static TransitLayer.Mode ModeOf(int routeType) => routeType switch
    {
        0 or (>= 900 and < 1000) => TransitLayer.Mode.Tram,
        1 or 401 or 402 or (>= 500 and < 600) => TransitLayer.Mode.Metro,
        2 or (>= 100 and < 200) or (>= 300 and < 400) or 403 or 404 => TransitLayer.Mode.Train,
        4 or (>= 1000 and < 1300) => TransitLayer.Mode.Ferry,
        6 or (>= 1300 and < 1400) => TransitLayer.Mode.CableCar,
        7 or (>= 1400 and < 1500) => TransitLayer.Mode.Funicular,
        _ => TransitLayer.Mode.Bus,
    };
}
