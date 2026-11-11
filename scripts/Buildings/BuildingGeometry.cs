using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>Point du plan local d'un bâtiment (m) : X vers l'est, Y vers le nord.</summary>
public struct V2
{
    public double X, Y;
    public V2(double x, double y) { X = x; Y = y; }
    public static V2 operator +(V2 a, V2 b) => new(a.X + b.X, a.Y + b.Y);
    public static V2 operator -(V2 a, V2 b) => new(a.X - b.X, a.Y - b.Y);
    public static V2 operator *(V2 a, double k) => new(a.X * k, a.Y * k);
    public double Length => Math.Sqrt(X * X + Y * Y);
    public V2 Normalized { get { double l = Length; return l > 1e-12 ? new V2(X / l, Y / l) : new V2(1, 0); } }
    /// <summary>Perpendiculaire (rotation de −90°) : pour un contour dans le sens direct, c'est la normale sortante.</summary>
    public V2 Right => new(Y, -X);
    public double Dot(V2 o) => X * o.X + Y * o.Y;
    public override string ToString() => $"({X:F2}, {Y:F2})";
}

/// <summary>Repère local d'un bâtiment (plan tangent en son centre) et conversion vers le repère planète.</summary>
public readonly struct LocalFrame
{
    const double R = Geo.Radius;
    public readonly Vec3d C, East, North, Origin;
    public LocalFrame(Vec3d c, Vec3d origin) { C = c; Origin = origin; Geo.LocalFrame(c, out East, out North); }
    /// <summary>Sommet (relatif à <see cref="Origin"/>) à l'altitude <paramref name="h"/>.</summary>
    public Vector3 P(double x, double y, double h) => (C * (R + h) + East * x + North * y - Origin).ToVector3();
    public Vector3 P(V2 p, double h) => P(p.X, p.Y, h);
    public Vector3 Dir(double x, double y, double z) => (East * x + North * y + C * z).Normalized().ToVector3();
    public Vector3 Up => C.ToVector3();
    /// <summary>Point au sol (repère planète, rayon quelconque) pour les collisions.</summary>
    public Vec3d World(V2 p) => C * R + East * p.X + North * p.Y;
    public Vec3d[] World(IReadOnlyList<V2> pts)
    {
        var w = new Vec3d[pts.Count];
        for (int i = 0; i < pts.Count; i++) w[i] = World(pts[i]);
        return w;
    }
    /// <summary>Latitude / longitude d'un point du plan local.</summary>
    public void LatLon(V2 p, out double lat, out double lon) => Geo.ToLatLon((C * R + East * p.X + North * p.Y).Normalized(), out lat, out lon);
}

/// <summary>Accumulateur de maillage (sommets, normales, couleurs sRGB, UV, UV2, indices).</summary>
public sealed class MeshAcc
{
    public readonly List<Vector3> V = new(), N = new();
    public readonly List<Color> C = new();
    public readonly List<Vector2> Uv = new(), Uv2 = new();
    public readonly List<int> I = new();

    public int Count => V.Count;

    /// <summary>Matière des sommets ajoutés à partir de maintenant (code <see cref="FacadeMaterials"/>, rangé dans l'alpha
    /// de la couleur ; 0 = uni, alpha 1).</summary>
    public byte Material;
    /// <summary>Faces verticales sans UV données (<see cref="Side"/>) : UV en mètres (longueur, altitude) au lieu de zéro,
    /// pour que les textures des bâtiments s'y dessinent.</summary>
    public bool AutoUv;

    public void Vert(Vector3 p, Vector3 n, Color c, Vector2 uv, Vector2 uv2)
    {
        V.Add(p); N.Add(n); C.Add(Material == 0 ? c : FacadeMaterials.Encode(c, Material));
        Uv.Add(uv); Uv2.Add(uv2);
    }

    /// <summary>Quadrilatère plan (a, b, c, d dans l'ordre) de normale <paramref name="n"/>.</summary>
    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Vector3 n, Color col,
        Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud, Vector2 uv2)
    {
        int k = V.Count;
        Vert(a, n, col, ua, uv2); Vert(b, n, col, ub, uv2); Vert(c, n, col, uc, uv2); Vert(d, n, col, ud, uv2);
        I.Add(k); I.Add(k + 1); I.Add(k + 2); I.Add(k); I.Add(k + 2); I.Add(k + 3);
    }

    public void Quad(Vector3 a, Vector3 b, Vector3 c, Vector3 d, Color col, Vector2 uv2 = default)
    {
        var n = (b - a).Cross(c - a);
        n = n.LengthSquared() > 1e-12 ? n.Normalized() : Vector3.Up;
        Quad(a, b, c, d, n, col, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, uv2);
    }

    public void Tri(Vector3 a, Vector3 b, Vector3 c, Color col, Vector2 uv2 = default)
    {
        var n = (b - a).Cross(c - a);
        n = n.LengthSquared() > 1e-12 ? n.Normalized() : Vector3.Up;
        int k = V.Count;
        Vert(a, n, col, Vector2.Zero, uv2); Vert(b, n, col, Vector2.Zero, uv2); Vert(c, n, col, Vector2.Zero, uv2);
        I.Add(k); I.Add(k + 1); I.Add(k + 2);
    }

    /// <summary>
    /// Pavé orienté dans le plan local : centre <paramref name="c"/>, axe <paramref name="u"/> (unitaire), demi-longueurs
    /// <paramref name="hu"/> le long de u et <paramref name="hv"/> en travers, de l'altitude <paramref name="h0"/> à
    /// <paramref name="h1"/>. <paramref name="shade"/> : faces latérales un peu plus sombres (éclairage figé des intérieurs).
    /// </summary>
    public void Box(in LocalFrame fr, V2 c, V2 u, double hu, double hv, double h0, double h1, Color col,
        bool bottom = false, float shade = 1f, Vector2 uv2 = default)
    {
        var v = new V2(-u.Y, u.X);
        V2 p00 = c - u * hu - v * hv, p10 = c + u * hu - v * hv, p11 = c + u * hu + v * hv, p01 = c - u * hu + v * hv;
        var up = fr.Up;
        Quad(fr.P(p00, h1), fr.P(p10, h1), fr.P(p11, h1), fr.P(p01, h1), up, col, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, uv2);
        if (bottom) Quad(fr.P(p01, h0), fr.P(p11, h0), fr.P(p10, h0), fr.P(p00, h0), -up, Shade(col, 0.85f * shade), Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, uv2);
        Side(fr, p00, p10, h0, h1, Shade(col, 0.80f * shade), uv2);
        Side(fr, p10, p11, h0, h1, Shade(col, 0.90f * shade), uv2);
        Side(fr, p11, p01, h0, h1, Shade(col, 0.84f * shade), uv2);
        Side(fr, p01, p00, h0, h1, Shade(col, 0.94f * shade), uv2);
    }

    /// <summary>Face verticale de a à b (sens direct vu de l'extérieur : normale à droite de a→b).</summary>
    public void Side(in LocalFrame fr, V2 a, V2 b, double h0, double h1, Color col, Vector2 uv2 = default,
        Vector2? uvA0 = null, Vector2? uvB1 = null)
    {
        var d = (b - a).Normalized.Right;
        var n = fr.Dir(d.X, d.Y, 0);
        var ua = uvA0 ?? (AutoUv ? new Vector2(0, (float)h0) : Vector2.Zero);
        var ub = uvB1 ?? (AutoUv ? new Vector2((float)Poly2.Dist(a, b), (float)h1) : Vector2.Zero);
        Quad(fr.P(a, h0), fr.P(b, h0), fr.P(b, h1), fr.P(a, h1), n, col,
            new Vector2(ua.X, ua.Y), new Vector2(ub.X, ua.Y), new Vector2(ub.X, ub.Y), new Vector2(ua.X, ub.Y), uv2);
    }

    /// <summary>Cylindre vertical (tour, minaret) de rayon <paramref name="r"/>, avec un chapeau conique ou plat.</summary>
    public void Cylinder(in LocalFrame fr, V2 c, double r, double h0, double h1, int seg, Color col, double cone, Color capCol, Vector2 uv2 = default)
    {
        var up = fr.Up;
        for (int i = 0; i < seg; i++)
        {
            double a0 = i * Math.Tau / seg, a1 = (i + 1) * Math.Tau / seg;
            var p0 = c + new V2(Math.Cos(a0), Math.Sin(a0)) * r; var p1 = c + new V2(Math.Cos(a1), Math.Sin(a1)) * r;
            Side(fr, p0, p1, h0, h1, col, uv2);
            if (cone > 0) Tri(fr.P(p0, h1), fr.P(p1, h1), fr.P(c, h1 + cone), capCol, uv2);
            else
            {
                int k = V.Count;
                Vert(fr.P(c, h1), up, capCol, Vector2.Zero, uv2); Vert(fr.P(p0, h1), up, capCol, Vector2.Zero, uv2); Vert(fr.P(p1, h1), up, capCol, Vector2.Zero, uv2);
                I.Add(k); I.Add(k + 1); I.Add(k + 2);
            }
        }
    }

    /// <summary>Coupole (demi-sphère aplatie) posée à l'altitude <paramref name="h0"/>.</summary>
    public void Dome(in LocalFrame fr, V2 c, double r, double h0, double height, Color col, Vector2 uv2 = default)
    {
        const int seg = 20, rings = 7;
        for (int j = 0; j < rings; j++)
        {
            double t0 = j * Math.PI / 2 / rings, t1 = (j + 1) * Math.PI / 2 / rings;
            double r0 = Math.Cos(t0) * r, r1 = Math.Cos(t1) * r, z0 = h0 + Math.Sin(t0) * height, z1 = h0 + Math.Sin(t1) * height;
            for (int i = 0; i < seg; i++)
            {
                double a0 = i * Math.Tau / seg, a1 = (i + 1) * Math.Tau / seg;
                V2 d0 = new(Math.Cos(a0), Math.Sin(a0)), d1 = new(Math.Cos(a1), Math.Sin(a1));
                var q00 = fr.P(c + d0 * r0, z0); var q10 = fr.P(c + d1 * r0, z0);
                var q11 = fr.P(c + d1 * r1, z1); var q01 = fr.P(c + d0 * r1, z1);
                var n = fr.Dir((d0.X + d1.X) * 0.5 * Math.Cos(t0), (d0.Y + d1.Y) * 0.5 * Math.Cos(t0), Math.Sin((t0 + t1) / 2) * r / Math.Max(height, 0.1));
                Quad(q00, q10, q11, q01, n, col, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, uv2);
            }
        }
    }

    public static Color Shade(Color c, float k) => new(c.R * k, c.G * k, c.B * k, c.A);
}

/// <summary>Géométrie plane des contours de bâtiments.</summary>
public static class Poly2
{
    public static double Dist(V2 a, V2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    public static double SignedArea(IReadOnlyList<V2> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++) { var q = p[i]; var r = p[(i + 1) % p.Count]; a += q.X * r.Y - r.X * q.Y; }
        return a / 2;
    }

    public static V2 Centroid(IReadOnlyList<V2> p)
    {
        double a = 0, cx = 0, cy = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var q = p[i]; var r = p[(i + 1) % p.Count];
            double cr = q.X * r.Y - r.X * q.Y;
            a += cr; cx += (q.X + r.X) * cr; cy += (q.Y + r.Y) * cr;
        }
        if (Math.Abs(a) < 1e-9) { foreach (var q in p) { cx += q.X; cy += q.Y; } return new V2(cx / p.Count, cy / p.Count); }
        return new V2(cx / (3 * a), cy / (3 * a));
    }

    static bool InRing(IReadOnlyList<V2> r, double px, double py)
    {
        bool inside = false;
        for (int i = 0, j = r.Count - 1; i < r.Count; j = i++)
        {
            double xi = r[i].X, yi = r[i].Y, xj = r[j].X, yj = r[j].Y;
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Le point est-il dans le contour (hors des cours intérieures) ?</summary>
    public static bool Inside(IReadOnlyList<V2> outer, IReadOnlyList<List<V2>> holes, V2 p)
    {
        if (!InRing(outer, p.X, p.Y)) return false;
        if (holes != null) foreach (var h in holes) if (InRing(h, p.X, p.Y)) return false;
        return true;
    }

    /// <summary>Distance du point au bord le plus proche (contour et cours).</summary>
    public static double EdgeDistance(IReadOnlyList<V2> outer, IReadOnlyList<List<V2>> holes, V2 p)
    {
        double best = double.MaxValue;
        void Ring(IReadOnlyList<V2> r)
        {
            for (int i = 0; i < r.Count; i++) best = Math.Min(best, SegDist(r[i], r[(i + 1) % r.Count], p));
        }
        Ring(outer);
        if (holes != null) foreach (var h in holes) Ring(h);
        return best;
    }

    public static double SegDist(V2 a, V2 b, V2 p)
    {
        var e = b - a; double l2 = e.Dot(e);
        double t = l2 > 0 ? Math.Clamp((p - a).Dot(e) / l2, 0, 1) : 0;
        return Dist(a + e * t, p);
    }

    /// <summary>Rectangle orienté entièrement dans le contour, à <paramref name="margin"/> m au moins des murs ?</summary>
    public static bool RectInside(IReadOnlyList<V2> outer, IReadOnlyList<List<V2>> holes, V2 c, V2 u, double hu, double hv, double margin)
    {
        var v = new V2(-u.Y, u.X);
        // coins, milieux des côtés et centre
        for (int i = -1; i <= 1; i++)
            for (int j = -1; j <= 1; j++)
            {
                var p = c + u * (hu * i) + v * (hv * j);
                if (!Inside(outer, holes, p)) return false;
                if (margin > 0 && (i != 0 || j != 0) && EdgeDistance(outer, holes, p) < margin) return false;
            }
        // aucun sommet du contour dans le rectangle (contours en L, en U…)
        bool Any(IReadOnlyList<V2> r)
        {
            foreach (var q in r)
            {
                var d = q - c;
                if (Math.Abs(d.Dot(u)) < hu + margin && Math.Abs(d.Dot(v)) < hv + margin) return true;
            }
            return false;
        }
        if (Any(outer)) return false;
        if (holes != null) foreach (var h in holes) if (Any(h)) return false;
        return true;
    }

    /// <summary>
    /// Parties d'un segment a→b situées dans le contour : intervalles de paramètre [t0, t1] (0 = a, 1 = b).
    /// </summary>
    public static List<(double t0, double t1)> ClipSegment(IReadOnlyList<V2> outer, IReadOnlyList<List<V2>> holes, V2 a, V2 b)
    {
        var ts = new List<double> { 0, 1 };
        void Ring(IReadOnlyList<V2> r)
        {
            for (int i = 0; i < r.Count; i++)
            {
                var c = r[i]; var d = r[(i + 1) % r.Count];
                double den = (b.X - a.X) * (d.Y - c.Y) - (b.Y - a.Y) * (d.X - c.X);
                if (Math.Abs(den) < 1e-12) continue;
                double t = ((c.X - a.X) * (d.Y - c.Y) - (c.Y - a.Y) * (d.X - c.X)) / den;
                double s = ((c.X - a.X) * (b.Y - a.Y) - (c.Y - a.Y) * (b.X - a.X)) / den;
                if (t > 0 && t < 1 && s >= 0 && s <= 1) ts.Add(t);
            }
        }
        Ring(outer);
        if (holes != null) foreach (var h in holes) Ring(h);
        ts.Sort();
        var res = new List<(double, double)>();
        for (int i = 0; i + 1 < ts.Count; i++)
        {
            double t0 = ts[i], t1 = ts[i + 1];
            if (t1 - t0 < 1e-6) continue;
            var m = a + (b - a) * ((t0 + t1) / 2);
            if (!Inside(outer, holes, m)) continue;
            if (res.Count > 0 && Math.Abs(res[^1].Item2 - t0) < 1e-6) res[^1] = (res[^1].Item1, t1);
            else res.Add((t0, t1));
        }
        return res;
    }

    /// <summary>Plus petit rectangle englobant (orienté selon un des côtés). fill = surface du contour / surface du rectangle.
    /// Le résultat A, B, C, D est dans le sens direct, AB = grand côté.</summary>
    public static V2[] OrientedBox(IReadOnlyList<V2> p, out double longSide, out double shortSide, out double fill)
    {
        double best = double.MaxValue; V2[] box = null; longSide = shortSide = 0;
        for (int i = 0; i < p.Count; i++)
        {
            var a = p[i]; var b = p[(i + 1) % p.Count];
            double ux = b.X - a.X, uy = b.Y - a.Y, l = Math.Sqrt(ux * ux + uy * uy);
            if (l < 0.5) continue;
            ux /= l; uy /= l;
            double u0 = double.MaxValue, u1 = double.MinValue, v0 = double.MaxValue, v1 = double.MinValue;
            foreach (var q in p)
            {
                double u = q.X * ux + q.Y * uy, v = -q.X * uy + q.Y * ux;
                u0 = Math.Min(u0, u); u1 = Math.Max(u1, u); v0 = Math.Min(v0, v); v1 = Math.Max(v1, v);
            }
            double area = (u1 - u0) * (v1 - v0);
            if (area >= best) continue;
            best = area;
            V2 P(double u, double v) => new(u * ux - v * uy, u * uy + v * ux);
            box = new[] { P(u0, v0), P(u1, v0), P(u1, v1), P(u0, v1) };
            longSide = u1 - u0; shortSide = v1 - v0;
        }
        fill = box == null ? 0 : SignedArea(p) / best;
        if (box != null && shortSide > longSide) (longSide, shortSide) = (shortSide, longSide);
        if (box != null && Dist(box[0], box[1]) < Dist(box[1], box[2]))
            box = new[] { box[1], box[2], box[3], box[0] }; // AB = grand côté
        return box;
    }

    // ------------------------------------------------------------------ triangulation

    /// <summary>Triangulation par oreilles d'un polygone (sens direct) avec trous (sens indirect),
    /// les trous étant d'abord reliés au contour par des « ponts ». Remplit <paramref name="pts"/>.</summary>
    public static List<int> Triangulate(List<V2> outer, List<List<V2>> holes, List<V2> pts)
    {
        var poly = new List<V2>(outer);
        if (holes != null && holes.Count > 0 && outer.Count + holes.Count * 8 < 400)
        {
            var hs = new List<List<V2>>(holes);
            hs.Sort((a, b) => MaxX(b).CompareTo(MaxX(a)));
            foreach (var h in hs)
            {
                int im = 0;
                for (int i = 1; i < h.Count; i++) if (h[i].X > h[im].X) im = i;
                var M = h[im];
                int best = -1; double bestD = double.MaxValue;
                for (int i = 0; i < poly.Count; i++)
                {
                    double d = (poly[i].X - M.X) * (poly[i].X - M.X) + (poly[i].Y - M.Y) * (poly[i].Y - M.Y);
                    if (d >= bestD || !Visible(poly, hs, M, poly[i])) continue;
                    bestD = d; best = i;
                }
                if (best < 0) continue; // trou ignoré
                var merged = new List<V2>(poly.Count + h.Count + 2);
                for (int i = 0; i <= best; i++) merged.Add(poly[i]);
                for (int i = 0; i <= h.Count; i++) merged.Add(h[(im + i) % h.Count]);
                merged.Add(poly[best]);
                for (int i = best + 1; i < poly.Count; i++) merged.Add(poly[i]);
                poly = merged;
            }
        }
        pts.AddRange(poly);
        var tris = EarClip(poly);
        if (tris == null)
        {
            // secours : contour seul, par Godot
            pts.Clear(); pts.AddRange(outer);
            var arr = new Vector2[outer.Count];
            for (int i = 0; i < outer.Count; i++) arr[i] = new Vector2((float)outer[i].X, (float)outer[i].Y);
            tris = new List<int>(Geometry2D.TriangulatePolygon(arr));
        }
        return tris;
    }

    static double MaxX(List<V2> r) { double m = double.MinValue; foreach (var p in r) m = Math.Max(m, p.X); return m; }

    static bool Visible(List<V2> poly, List<List<V2>> holes, V2 a, V2 b)
    {
        if (Crosses(poly, a, b)) return false;
        foreach (var h in holes) if (Crosses(h, a, b)) return false;
        return true;
    }

    static bool Crosses(List<V2> ring, V2 a, V2 b)
    {
        for (int i = 0; i < ring.Count; i++)
        {
            var c = ring[i]; var d = ring[(i + 1) % ring.Count];
            if (Same(c, a) || Same(c, b) || Same(d, a) || Same(d, b)) continue;
            if (Orient(a, b, c) * Orient(a, b, d) < 0 && Orient(c, d, a) * Orient(c, d, b) < 0) return true;
        }
        return false;
    }

    static bool Same(V2 a, V2 b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;
    static double Orient(V2 a, V2 b, V2 c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);

    static List<int> EarClip(List<V2> p)
    {
        int n = p.Count;
        if (n < 3) return new List<int>();
        if (n > 600) return null;
        var idx = new List<int>(n);
        for (int i = 0; i < n; i++) idx.Add(i);
        var tris = new List<int>((n - 2) * 3);
        int guard = 0;
        while (idx.Count > 3 && guard++ < n * n)
        {
            bool clipped = false;
            for (int k = 0; k < idx.Count; k++)
            {
                int ia = idx[(k + idx.Count - 1) % idx.Count], ib = idx[k], ic = idx[(k + 1) % idx.Count];
                var a = p[ia]; var b = p[ib]; var c = p[ic];
                double cr = Orient(a, b, c);
                if (cr <= 1e-9) continue; // angle rentrant ou plat
                bool inside = false;
                foreach (int j in idx)
                {
                    if (j == ia || j == ib || j == ic) continue;
                    var q = p[j];
                    if (Same(q, a) || Same(q, b) || Same(q, c)) continue;
                    if (Orient(a, b, q) >= 0 && Orient(b, c, q) >= 0 && Orient(c, a, q) >= 0) { inside = true; break; }
                }
                if (inside) continue;
                tris.Add(ia); tris.Add(ib); tris.Add(ic);
                idx.RemoveAt(k);
                clipped = true;
                break;
            }
            if (!clipped)
            {
                // polygone dégénéré : on retire un sommet plat ou rentrant
                bool removed = false;
                for (int k = 0; k < idx.Count && !removed; k++)
                {
                    int ia = idx[(k + idx.Count - 1) % idx.Count], ib = idx[k], ic = idx[(k + 1) % idx.Count];
                    if (Math.Abs(Orient(p[ia], p[ib], p[ic])) <= 1e-9) { idx.RemoveAt(k); removed = true; }
                }
                if (!removed) return tris.Count > 0 ? tris : null;
            }
        }
        if (idx.Count == 3) { tris.Add(idx[0]); tris.Add(idx[1]); tris.Add(idx[2]); }
        return tris;
    }
}
