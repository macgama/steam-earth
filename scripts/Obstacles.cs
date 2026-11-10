using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>
/// Collisions avec les décors (troncs d'arbres, murs des bâtiments…) pour le personnage.
/// Chaque obstacle est une empreinte au sol (cercle ou polygone) avec une altitude de base et de sommet.
/// Ils sont rangés dans une grille en latitude/longitude (cases de ~200 m) et rattachés à un nœud
/// « propriétaire » (le chunk d'arbres, la tuile de bâtiments) : quand ce nœud est caché, l'obstacle ne
/// compte pas ; quand il est supprimé, l'obstacle disparaît. Thread principal uniquement.
/// </summary>
public static class Obstacles
{
    public sealed class Shape
    {
        public Vec3d Center;    // cercle : centre au sol (repère planète, rayon quelconque)
        public double Radius;   // cercle : rayon (m) ; 0 = polygone
        public Vec3d[] Ring;    // polygone : contour extérieur (repère planète)
        public double Base, Top; // altitudes (m) du bas et du haut de l'obstacle
        internal Node Owner;
        internal int Stamp;

        public static Shape Circle(Vec3d center, double radius, double baseAlt, double topAlt) =>
            new() { Center = center, Radius = radius, Base = baseAlt, Top = topAlt };
        public static Shape Polygon(Vec3d[] ring, double baseAlt, double topAlt)
        {
            var c = new Vec3d(0, 0, 0);
            foreach (var p in ring) c += p;
            return new() { Ring = ring, Center = c * (1.0 / ring.Length), Base = baseAlt, Top = topAlt };
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

    /// <summary>Remplace les obstacles de <paramref name="owner"/>.</summary>
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

    /// <summary>
    /// Repousse un corps cylindrique (pieds en <paramref name="feet"/>, rayon et hauteur en m) hors des obstacles.
    /// Retourne la position corrigée (même altitude).
    /// </summary>
    public static Vec3d Resolve(Vec3d feet, double radius, double height)
    {
        if (Cells.Count == 0) return feet;
        double len = feet.Length();
        var up = feet * (1 / len);
        double alt = len - Geo.Radius;
        Geo.LocalFrame(up, out var east, out var north);
        var origin = up * Geo.Radius;
        CellOf(feet, out int cla, out int clo);
        double px = 0, py = 0;
        bool moved = false;

        for (int iter = 0; iter < 3; iter++)
        {
            int stamp = ++_stamp;
            bool hit = false;
            for (int dla = -1; dla <= 1; dla++)
            for (int dlo = -1; dlo <= 1; dlo++)
            {
                if (!Cells.TryGetValue(Key(cla + dla, clo + dlo), out var list)) continue;
                foreach (var s in list)
                {
                    if (s.Stamp == stamp) continue;
                    s.Stamp = stamp;
                    // on peut passer par-dessus (marche de 40 cm) ou dessous
                    if (alt >= s.Top - 0.4 || alt + height <= s.Base) continue;
                    if (s.Owner is Node3D n3 && (!GodotObject.IsInstanceValid(n3) || !n3.IsVisibleInTree())) continue;
                    var d = s.Center * (Geo.Radius / s.Center.Length()) - origin;
                    double cx = d.Dot(east), cy = d.Dot(north);
                    if (s.Ring == null)
                    {
                        double ox = px - cx, oy = py - cy, dist = Math.Sqrt(ox * ox + oy * oy), min = s.Radius + radius;
                        if (dist >= min) continue;
                        if (dist < 1e-6) { ox = 1; oy = 0; dist = 1; }
                        px = cx + ox / dist * min; py = cy + oy / dist * min;
                        hit = true;
                        continue;
                    }
                    // polygone : point le plus proche du contour
                    var pts = new double[s.Ring.Length * 2];
                    for (int i = 0; i < s.Ring.Length; i++)
                    {
                        var q = s.Ring[i] * (Geo.Radius / s.Ring[i].Length()) - origin;
                        pts[2 * i] = q.Dot(east); pts[2 * i + 1] = q.Dot(north);
                    }
                    if (PushOutOfPolygon(pts, ref px, ref py, radius)) hit = true;
                }
            }
            if (!hit) break;
            moved = true;
        }
        if (!moved) return feet;
        var np = origin + east * px + north * py;
        return np.Normalized() * len;
    }

    static bool PushOutOfPolygon(double[] pts, ref double px, ref double py, double radius)
    {
        int n = pts.Length / 2;
        bool inside = false;
        double best = double.MaxValue, bx = 0, by = 0;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = pts[2 * i], yi = pts[2 * i + 1], xj = pts[2 * j], yj = pts[2 * j + 1];
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
            double ex = xi - xj, ey = yi - yj, l2 = ex * ex + ey * ey;
            double t = l2 > 0 ? Math.Clamp(((px - xj) * ex + (py - yj) * ey) / l2, 0, 1) : 0;
            double qx = xj + ex * t, qy = yj + ey * t, d2 = (px - qx) * (px - qx) + (py - qy) * (py - qy);
            if (d2 < best) { best = d2; bx = qx; by = qy; }
        }
        double dist = Math.Sqrt(best);
        if (!inside && dist >= radius) return false;
        if (dist < 1e-6) return false; // exactement sur le mur : on laisse l'itération suivante
        double dx = (px - bx) / dist, dy = (py - by) / dist;
        if (inside) { dx = -dx; dy = -dy; } // le point le plus proche du mur, vers l'extérieur
        px = bx + dx * radius; py = by + dy * radius;
        return true;
    }
}
