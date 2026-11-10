using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>
/// Collisions avec les décors (troncs d'arbres, bâtiments…) pour le personnage et la caméra.
/// Chaque obstacle est une empreinte au sol (cercle, ou contour avec ses cours intérieures) avec une altitude de
/// base et un toit (plat, ou à deux pans / en croupe : on peut marcher dessus). Ils sont rangés dans une grille en
/// latitude/longitude (cases de ~200 m) et rattachés à un nœud « propriétaire » (le chunk d'arbres, la tuile de
/// bâtiments) : quand ce nœud est caché, l'obstacle ne compte pas ; quand il est supprimé, l'obstacle disparaît.
/// Thread principal uniquement.
/// </summary>
public static class Obstacles
{
    public const double StepUp = 0.4; // hauteur de marche franchissable (m)

    public sealed class Shape
    {
        public Vec3d Center;     // centre au sol (repère planète, rayon quelconque)
        public double Radius;    // cercle : rayon (m) ; 0 = polygone
        public Vec3d[] Ring;     // polygone : contour extérieur (repère planète)
        public Vec3d[][] Holes;  // cours intérieures (praticables)
        public double Base, Top; // altitudes (m) du bas de l'obstacle et du point le plus haut du toit
        // Toit en pente : faîte (RidgeA–RidgeB) à l'altitude Top, qui descend jusqu'à Eave à HalfWidth du faîte
        public Vec3d? RidgeA, RidgeB;
        public double Eave, HalfWidth;
        internal Node Owner;
        internal int Stamp;

        public static Shape Circle(Vec3d center, double radius, double baseAlt, double topAlt) =>
            new() { Center = center, Radius = radius, Base = baseAlt, Top = topAlt, Eave = topAlt };
        public static Shape Polygon(Vec3d[] ring, Vec3d[][] holes, double baseAlt, double topAlt)
        {
            var c = new Vec3d(0, 0, 0);
            foreach (var p in ring) c += p;
            return new() { Ring = ring, Holes = holes, Center = c * (1.0 / ring.Length), Base = baseAlt, Top = topAlt, Eave = topAlt };
        }
        /// <summary>Toit à pans : faîte entre <paramref name="a"/> et <paramref name="b"/>, gouttière à <paramref name="halfWidth"/> m.</summary>
        public void SetPitched(Vec3d a, Vec3d b, double ridgeAlt, double eaveAlt, double halfWidth)
        {
            RidgeA = a; RidgeB = b; Top = ridgeAlt; Eave = eaveAlt; HalfWidth = halfWidth;
        }
    }

    const double CellsPerDeg = 500; // ~220 m en latitude
    static readonly Dictionary<long, List<Shape>> Cells = new();
    static readonly Dictionary<ulong, List<(long cell, Shape shape)>> ByOwner = new();
    static int _stamp;

    public static int Count { get; private set; }

    static long Key(int la, int lo) => ((long)la << 32) ^ (uint)lo;
    static void CellOf(Vec3d p, out int la, out int lo)
    {
        Geo.ToLatLon(p.Normalized(), out double lat, out double lon);
        la = (int)Math.Floor(lat * CellsPerDeg); lo = (int)Math.Floor(lon * CellsPerDeg);
    }

    /// <summary>Remplace les obstacles de <paramref name="owner"/> (null ou vide : les retire).</summary>
    public static void Set(Node owner, IReadOnlyList<Shape> shapes)
    {
        ulong id = owner.GetInstanceId();
        bool known = ByOwner.ContainsKey(id);
        Remove(id);
        if (shapes == null || shapes.Count == 0) return;
        var list = new List<(long, Shape)>(shapes.Count);
        foreach (var s in shapes)
        {
            s.Owner = owner;
            if (s.Ring == null)
            {
                CellOf(s.Center, out int la, out int lo);
                Add(Key(la, lo), s, list);
                continue;
            }
            // polygone : toutes les cases couvertes par sa boîte englobante (bâtiments longs)
            int la0 = int.MaxValue, la1 = int.MinValue, lo0 = int.MaxValue, lo1 = int.MinValue;
            foreach (var p in s.Ring)
            {
                CellOf(p, out int la, out int lo);
                la0 = Math.Min(la0, la); la1 = Math.Max(la1, la); lo0 = Math.Min(lo0, lo); lo1 = Math.Max(lo1, lo);
            }
            if ((la1 - la0 + 1) * (lo1 - lo0 + 1) > 64) continue; // contour aberrant (ou à cheval sur l'antiméridien)
            for (int la = la0; la <= la1; la++) for (int lo = lo0; lo <= lo1; lo++) Add(Key(la, lo), s, list);
        }
        ByOwner[id] = list;
        Count += shapes.Count;
        if (!known) owner.TreeExiting += () => Remove(id);
    }

    static void Add(long key, Shape s, List<(long, Shape)> list)
    {
        if (!Cells.TryGetValue(key, out var l)) Cells[key] = l = new List<Shape>();
        l.Add(s);
        list.Add((key, s));
    }

    static void Remove(ulong id)
    {
        if (!ByOwner.Remove(id, out var list)) return;
        var seen = new HashSet<Shape>();
        foreach (var (key, s) in list)
        {
            seen.Add(s);
            if (!Cells.TryGetValue(key, out var l)) continue;
            l.Remove(s);
            if (l.Count == 0) Cells.Remove(key);
        }
        Count -= seen.Count;
    }

    // ------------------------------------------------------------------ géométrie locale

    /// <summary>Plan tangent au point étudié : coordonnées (est, nord) en mètres.</summary>
    readonly struct Local
    {
        public readonly Vec3d Origin, East, North;
        public Local(Vec3d up) { Origin = up * Geo.Radius; Geo.LocalFrame(up, out East, out North); }
        public void P(Vec3d w, out double x, out double y)
        {
            var d = w * (Geo.Radius / w.Length()) - Origin;
            x = d.Dot(East); y = d.Dot(North);
        }
        public double[] Ring(Vec3d[] ring)
        {
            var pts = new double[ring.Length * 2];
            for (int i = 0; i < ring.Length; i++) P(ring[i], out pts[2 * i], out pts[2 * i + 1]);
            return pts;
        }
    }

    /// <summary>Visite les obstacles actifs des 9 cases autour de <paramref name="p"/>.</summary>
    static IEnumerable<Shape> Near(Vec3d p)
    {
        CellOf(p, out int cla, out int clo);
        int stamp = ++_stamp;
        for (int dla = -1; dla <= 1; dla++)
        for (int dlo = -1; dlo <= 1; dlo++)
        {
            if (!Cells.TryGetValue(Key(cla + dla, clo + dlo), out var list)) continue;
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                if (s.Stamp == stamp) continue;
                s.Stamp = stamp;
                if (s.Owner is Node3D n3 && (!GodotObject.IsInstanceValid(n3) || !n3.IsVisibleInTree())) continue;
                yield return s;
            }
        }
    }

    /// <summary>
    /// Point (x, y) par rapport à une empreinte : à l'intérieur (hors cours) ou non, et point du bord le plus proche.
    /// </summary>
    static void Locate(Shape s, in Local loc, double x, double y, out bool inside, out double bx, out double by, out double dist)
    {
        if (s.Ring == null)
        {
            loc.P(s.Center, out double cx, out double cy);
            double ox = x - cx, oy = y - cy, d = Math.Sqrt(ox * ox + oy * oy);
            if (d < 1e-6) { ox = 1; oy = 0; d = 1e-6; }
            bx = cx + ox / d * s.Radius; by = cy + oy / d * s.Radius;
            inside = d < s.Radius; dist = Math.Abs(d - s.Radius);
            return;
        }
        inside = false;
        double best = double.MaxValue; bx = by = 0;
        Edges(loc.Ring(s.Ring), x, y, ref inside, ref best, ref bx, ref by);
        if (s.Holes != null) foreach (var h in s.Holes) Edges(loc.Ring(h), x, y, ref inside, ref best, ref bx, ref by);
        dist = Math.Sqrt(best);
    }

    static void Edges(double[] pts, double px, double py, ref bool inside, ref double best, ref double bx, ref double by)
    {
        int n = pts.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = pts[2 * i], yi = pts[2 * i + 1], xj = pts[2 * j], yj = pts[2 * j + 1];
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
            double ex = xi - xj, ey = yi - yj, l2 = ex * ex + ey * ey;
            double t = l2 > 0 ? Math.Clamp(((px - xj) * ex + (py - yj) * ey) / l2, 0, 1) : 0;
            double qx = xj + ex * t, qy = yj + ey * t, d2 = (px - qx) * (px - qx) + (py - qy) * (py - qy);
            if (d2 < best) { best = d2; bx = qx; by = qy; }
        }
    }

    /// <summary>Altitude du toit au point (x, y) du plan local.</summary>
    static double RoofAt(Shape s, in Local loc, double x, double y)
    {
        if (s.RidgeA is not Vec3d a || s.RidgeB is not Vec3d b || s.HalfWidth <= 0) return s.Top;
        loc.P(a, out double ax, out double ay); loc.P(b, out double bx, out double by);
        double ex = bx - ax, ey = by - ay, l2 = ex * ex + ey * ey;
        double t = l2 > 0 ? Math.Clamp(((x - ax) * ex + (y - ay) * ey) / l2, 0, 1) : 0;
        double dx = x - (ax + ex * t), dy = y - (ay + ey * t);
        double d = Math.Sqrt(dx * dx + dy * dy);
        return Math.Max(s.Eave, s.Top - d / s.HalfWidth * (s.Top - s.Eave));
    }

    // ------------------------------------------------------------------ requêtes

    /// <summary>
    /// Repousse un corps cylindrique (pieds en <paramref name="feet"/>, rayon et hauteur en m) hors des obstacles.
    /// Un corps qui est au niveau d'un toit (ou au-dessus) n'est pas repoussé : il marche dessus.
    /// Retourne la position corrigée (même altitude).
    /// </summary>
    public static Vec3d Resolve(Vec3d feet, double radius, double height) => Resolve(feet, radius, height, out _);

    /// <param name="blocked">vrai si un mur a arrêté le corps (pour l'escalade)</param>
    public static Vec3d Resolve(Vec3d feet, double radius, double height, out bool blocked)
    {
        blocked = false;
        if (Cells.Count == 0) return feet;
        double len = feet.Length();
        var loc = new Local(feet * (1 / len));
        double alt = len - Geo.Radius;
        double px = 0, py = 0;
        bool moved = false;

        for (int iter = 0; iter < 3; iter++)
        {
            bool hit = false;
            foreach (var s in Near(feet))
            {
                if (alt >= s.Top - StepUp || alt + height <= s.Base) continue;
                Locate(s, loc, px, py, out bool inside, out double bx, out double by, out double dist);
                if (!inside && dist >= radius) continue;
                // sur le toit (ou à sa hauteur, au bord) : on marche dessus
                double roof = inside ? RoofAt(s, loc, px, py) : s.Eave;
                if (alt >= roof - StepUp) continue;
                if (dist < 1e-6) continue;
                double dx = (px - bx) / dist, dy = (py - by) / dist;
                if (inside) { dx = -dx; dy = -dy; } // vers l'extérieur, par le mur le plus proche
                px = bx + dx * radius; py = by + dy * radius;
                hit = true;
            }
            if (!hit) break;
            moved = true;
        }
        if (!moved) return feet;
        blocked = true;
        var np = loc.Origin + loc.East * px + loc.North * py;
        return np.Normalized() * len;
    }

    /// <summary>
    /// Altitude du plus haut toit sous le point, atteignable en montant d'au plus <paramref name="stepUp"/> m
    /// (sinon −∞) : le personnage s'y pose comme sur le sol.
    /// </summary>
    public static double SurfaceBelow(Vec3d p, double stepUp = StepUp)
    {
        double best = double.NegativeInfinity;
        if (Cells.Count == 0) return best;
        double len = p.Length(), alt = len - Geo.Radius;
        var loc = new Local(p * (1 / len));
        foreach (var s in Near(p))
        {
            if (s.Top < best || s.Base > alt + stepUp || s.Eave > alt + stepUp) continue;
            Locate(s, loc, 0, 0, out bool inside, out _, out _, out _);
            if (!inside) continue;
            double roof = RoofAt(s, loc, 0, 0);
            if (roof <= alt + stepUp && roof > best) best = roof;
        }
        return best;
    }

    /// <summary>
    /// Le point est-il dans un décor (ou à moins de <paramref name="margin"/> m de ses murs) ?
    /// <paramref name="roof"/> : altitude du toit au-dessus, pour remonter la caméra.
    /// </summary>
    public static bool Inside(Vec3d p, double margin, out double roof)
    {
        roof = double.NegativeInfinity;
        if (Cells.Count == 0) return false;
        double len = p.Length(), alt = len - Geo.Radius;
        var loc = new Local(p * (1 / len));
        bool any = false;
        foreach (var s in Near(p))
        {
            if (alt > s.Top + margin || alt < s.Base - margin) continue;
            Locate(s, loc, 0, 0, out bool inside, out _, out _, out double dist);
            if (!inside && dist >= margin) continue;
            double r = inside ? RoofAt(s, loc, 0, 0) : s.Eave;
            if (alt > r + margin) continue;
            any = true;
            roof = Math.Max(roof, r);
        }
        return any;
    }
}
