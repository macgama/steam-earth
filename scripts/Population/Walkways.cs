using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth.Population;

/// <summary>
/// Ligne où marchent les passants : bord d'une rue (trottoir) ou milieu d'un chemin, d'une zone piétonne, d'un
/// escalier. Points dans le repère planète, à la hauteur de la chaussée (ponts compris, tunnels exclus).
/// </summary>
public sealed class Walkway
{
    public Vec3d[] Points;
    /// <summary>Distance cumulée (m) de chaque point depuis le premier.</summary>
    public double[] Along;
    public double Length => Along[^1];
    /// <summary>Décalage latéral des passants depuis l'axe (m) : bord de la chaussée pour une rue.</summary>
    public double SideOffset;
    /// <summary>Chemin piéton (on marche au milieu, des deux côtés) plutôt qu'une rue (trottoirs).</summary>
    public bool Footway;
    internal Node Owner;
    internal double Lat0, Lat1, Lon0, Lon1;

    public Walkway(Vec3d[] pts, double sideOffset, bool footway)
    {
        Points = pts; SideOffset = sideOffset; Footway = footway;
        Along = new double[pts.Length];
        for (int i = 1; i < pts.Length; i++) Along[i] = Along[i - 1] + (pts[i] - pts[i - 1]).Length();
    }

    /// <summary>Point de l'axe à <paramref name="s"/> m du début, et direction de la ligne à cet endroit.</summary>
    public Vec3d At(double s, out Vec3d dir)
    {
        s = Math.Clamp(s, 0, Length);
        int i = Array.BinarySearch(Along, s);
        if (i < 0) i = ~i - 1;
        i = Math.Clamp(i, 0, Points.Length - 2);
        double seg = Along[i + 1] - Along[i];
        double t = seg > 1e-9 ? (s - Along[i]) / seg : 0;
        var d = Points[i + 1] - Points[i];
        dir = d.Length() > 1e-9 ? d.Normalized() : new Vec3d(1, 0, 0);
        return Points[i] + d * t;
    }
}

/// <summary>
/// Les trottoirs et chemins des tuiles de routes chargées (zoom 14, ~3 km autour du joueur), rangés dans une
/// grille lat/lon comme <see cref="Obstacles"/>. Remplis par <see cref="RoadLayer"/>, lus par les passants.
/// Thread principal uniquement.
/// </summary>
public static class Walkways
{
    const double CellsPerDeg = 500; // ~220 m
    static readonly Dictionary<long, List<Walkway>> Cells = new();
    static readonly Dictionary<ulong, List<(long, Walkway)>> ByOwner = new();
    static int _version;

    public static int Count { get; private set; }
    /// <summary>Change à chaque ajout ou retrait (les passants revérifient leur chemin).</summary>
    public static int Version => _version;

    static long Key(int la, int lo) => ((long)la << 32) ^ (uint)lo;

    public static void Set(Node owner, List<Walkway> list)
    {
        ulong id = owner.GetInstanceId();
        bool known = ByOwner.ContainsKey(id);
        Remove(id);
        if (list == null || list.Count == 0) return;
        var reg = new List<(long, Walkway)>();
        foreach (var w in list)
        {
            if (w.Points.Length < 2 || w.Length < 2) continue;
            w.Owner = owner;
            w.Lat0 = w.Lon0 = double.MaxValue; w.Lat1 = w.Lon1 = double.MinValue;
            foreach (var p in w.Points)
            {
                Geo.ToLatLon(p.Normalized(), out double la, out double lo);
                w.Lat0 = Math.Min(w.Lat0, la); w.Lat1 = Math.Max(w.Lat1, la);
                w.Lon0 = Math.Min(w.Lon0, lo); w.Lon1 = Math.Max(w.Lon1, lo);
            }
            int la0 = (int)Math.Floor(w.Lat0 * CellsPerDeg), la1 = (int)Math.Floor(w.Lat1 * CellsPerDeg);
            int lo0 = (int)Math.Floor(w.Lon0 * CellsPerDeg), lo1 = (int)Math.Floor(w.Lon1 * CellsPerDeg);
            if ((la1 - la0 + 1) * (lo1 - lo0 + 1) > 64) continue;
            for (int a = la0; a <= la1; a++)
                for (int b = lo0; b <= lo1; b++)
                {
                    long k = Key(a, b);
                    if (!Cells.TryGetValue(k, out var l)) Cells[k] = l = new List<Walkway>();
                    l.Add(w);
                    reg.Add((k, w));
                }
            Count++;
        }
        ByOwner[id] = reg;
        _version++;
        if (!known) owner.TreeExiting += () => Remove(id);
    }

    static void Remove(ulong id)
    {
        if (!ByOwner.Remove(id, out var reg)) return;
        var seen = new HashSet<Walkway>();
        foreach (var (k, w) in reg)
        {
            if (seen.Add(w)) Count--;
            if (Cells.TryGetValue(k, out var l)) { l.Remove(w); if (l.Count == 0) Cells.Remove(k); }
        }
        _version++;
    }

    /// <summary>Lignes dont la boîte touche le rectangle (degrés), sur des tuiles affichées.</summary>
    public static List<Walkway> In(double lat0, double lat1, double lon0, double lon1)
    {
        var res = new List<Walkway>();
        var seen = new HashSet<Walkway>();
        int la0 = (int)Math.Floor(lat0 * CellsPerDeg), la1 = (int)Math.Floor(lat1 * CellsPerDeg);
        int lo0 = (int)Math.Floor(lon0 * CellsPerDeg), lo1 = (int)Math.Floor(lon1 * CellsPerDeg);
        for (int a = la0; a <= la1; a++)
            for (int b = lo0; b <= lo1; b++)
            {
                if (!Cells.TryGetValue(Key(a, b), out var l)) continue;
                foreach (var w in l)
                {
                    if (!seen.Add(w)) continue;
                    if (w.Lat1 < lat0 || w.Lat0 > lat1 || w.Lon1 < lon0 || w.Lon0 > lon1) continue;
                    if (!Alive(w)) continue;
                    res.Add(w);
                }
            }
        return res;
    }

    public static bool Alive(Walkway w) => w.Owner != null && GodotObject.IsInstanceValid(w.Owner) && ByOwner.ContainsKey(w.Owner.GetInstanceId())
                                           && (w.Owner is not Node3D n || n.IsVisibleInTree());

    /// <summary>Autres lignes qui commencent ou finissent à moins de <paramref name="radius"/> m de <paramref name="p"/>
    /// (carrefours) ; <c>atStart</c> vrai si c'est leur premier point.</summary>
    public static List<(Walkway w, bool atStart)> EndsNear(Vec3d p, double radius, Walkway except)
    {
        var res = new List<(Walkway, bool)>();
        Geo.ToLatLon(p.Normalized(), out double lat, out double lon);
        double dLat = radius / 111_000, dLon = dLat / Math.Max(0.2, Math.Cos(lat * Math.PI / 180));
        foreach (var w in In(lat - dLat, lat + dLat, lon - dLon, lon + dLon))
        {
            if (w == except) continue;
            if ((w.Points[0] - p).Length() < radius) res.Add((w, true));
            else if ((w.Points[^1] - p).Length() < radius) res.Add((w, false));
        }
        return res;
    }
}
