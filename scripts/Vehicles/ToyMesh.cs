using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>
/// Formes « jouet » pour les véhicules stylisés (même esprit que les modèles Kenney) : boîtes aux arêtes arrondies,
/// volumes profilés (fuselages, coques). Les maillages sont mis en cache par paramètres.
/// Convention Godot : avant = −Z, haut = +Y.
/// </summary>
public static class ToyMesh
{
    static readonly Dictionary<string, Mesh> Cache = new();

    /// <summary>Boîte de taille <paramref name="size"/> aux arêtes arrondies (rayon <paramref name="radius"/>).
    /// <paramref name="taper"/> rétrécit le dessus (0 = droit ; 0,25 = dessus 25 % plus étroit) : pavillon, capot.
    /// <paramref name="slant"/> recule le haut vers l'arrière (m) : pare-brise incliné.</summary>
    public static Mesh RoundedBox(Vector3 size, float radius, float taper = 0, float slant = 0, int seg = 3)
    {
        string key = $"rb{size}{radius}{taper}{slant}{seg}";
        if (Cache.TryGetValue(key, out var cached)) return cached;
        radius = Mathf.Clamp(radius, 0.002f, Mathf.Min(size.X, Mathf.Min(size.Y, size.Z)) * 0.5f - 0.001f);
        var half = size / 2;
        var inner = half - Vector3.One * radius;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        int n = seg * 2 + 2;
        Vector3[] faces = { Vector3.Right, Vector3.Left, Vector3.Up, Vector3.Down, Vector3.Back, Vector3.Forward };
        foreach (var fn in faces)
        {
            var u = Mathf.Abs(fn.Y) > 0.5f ? Vector3.Right : Vector3.Up;
            var v = fn.Cross(u);
            float ru = Ratio(u, inner, half), rv = Ratio(v, inner, half);
            var pts = new Vector3[n, n];
            var nrm = new Vector3[n, n];
            for (int i = 0; i < n; i++)
            for (int j = 0; j < n; j++)
            {
                var q = fn + u * Coord(i, seg, ru) + v * Coord(j, seg, rv); // point du cube unité
                var p = q * half;
                var c = new Vector3(Mathf.Clamp(p.X, -inner.X, inner.X), Mathf.Clamp(p.Y, -inner.Y, inner.Y), Mathf.Clamp(p.Z, -inner.Z, inner.Z));
                var d = p - c;
                var dir = d.Length() > 1e-6f ? d.Normalized() : fn;
                var pos = c + dir * radius;
                float h = (pos.Y + half.Y) / size.Y; // 0 en bas, 1 en haut
                pos.X *= 1 - taper * h;
                pos.Z = pos.Z * (1 - taper * h) + slant * h;
                pts[i, j] = pos;
                nrm[i, j] = dir;
            }
            for (int i = 0; i < n - 1; i++)
            for (int j = 0; j < n - 1; j++)
            {
                Tri(st, pts[i, j], pts[i + 1, j], pts[i + 1, j + 1], nrm[i, j], nrm[i + 1, j], nrm[i + 1, j + 1], fn);
                Tri(st, pts[i, j], pts[i + 1, j + 1], pts[i, j + 1], nrm[i, j], nrm[i + 1, j + 1], nrm[i, j + 1], fn);
            }
        }
        if (taper != 0 || slant != 0) { st.GenerateNormals(); }
        var mesh = st.Commit();
        Cache[key] = mesh;
        return mesh;
    }

    static float Ratio(Vector3 axis, Vector3 inner, Vector3 half)
    {
        var a = axis.Abs();
        return (a.X * inner.X + a.Y * inner.Y + a.Z * inner.Z) / (a.X * half.X + a.Y * half.Y + a.Z * half.Z);
    }

    /// <summary>Coordonnée [-1, 1] de la rangée <paramref name="i"/> : rangées 0..seg dans l'arrondi d'un côté,
    /// puis le plat, puis l'arrondi de l'autre côté. <paramref name="r"/> = début du plat (demi-boîte intérieure / demi-boîte).</summary>
    static float Coord(int i, int seg, float r)
    {
        int n = seg * 2 + 2;
        if (i <= seg) return -1 + (1 - r) * i / seg;
        int k = n - 1 - i;
        return 1 - (1 - r) * k / seg;
    }

    static void Tri(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 na, Vector3 nb, Vector3 nc, Vector3 face)
    {
        // Godot : faces avant dans le sens horaire vu de l'extérieur
        if ((b - a).Cross(c - a).Dot(face) > 0) { (b, c) = (c, b); (nb, nc) = (nc, nb); }
        st.SetNormal(na); st.AddVertex(a);
        st.SetNormal(nb); st.AddVertex(b);
        st.SetNormal(nc); st.AddVertex(c);
    }

    /// <summary>
    /// Volume profilé le long de Z, de <paramref name="z0"/> (avant) à <paramref name="z1"/> (arrière) :
    /// <paramref name="section"/>(t) donne le contour (X, Y) de la section à t ∈ [0, 1], toujours le même nombre de points,
    /// tournant dans le sens trigonométrique vu de l'arrière. Les extrémités sont fermées.
    /// </summary>
    public static Mesh Loft(string key, float z0, float z1, Func<float, Vector2[]> section, int rings = 18)
    {
        if (Cache.TryGetValue(key, out var cached)) return cached;
        var st = new SurfaceTool();
        st.Begin(Mesh.PrimitiveType.Triangles);
        st.SetSmoothGroup(0);
        var grid = new List<Vector3[]>();
        for (int r = 0; r <= rings; r++)
        {
            float t = (float)r / rings;
            var s = section(t);
            float z = Mathf.Lerp(z0, z1, t);
            var ring = new Vector3[s.Length];
            for (int k = 0; k < s.Length; k++) ring[k] = new Vector3(s[k].X, s[k].Y, z);
            grid.Add(ring);
        }
        int sides = grid[0].Length;
        for (int r = 0; r < rings; r++)
        for (int s = 0; s < sides; s++)
        {
            int s1 = (s + 1) % sides;
            Quad(st, grid[r][s], grid[r][s1], grid[r + 1][s1], grid[r + 1][s]);
        }
        Cap(st, grid[0], true);
        Cap(st, grid[rings], false);
        st.GenerateNormals();
        var mesh = st.Commit();
        Cache[key] = mesh;
        return mesh;
    }

    static void Quad(SurfaceTool st, Vector3 a, Vector3 b, Vector3 c, Vector3 d)
    {
        st.AddVertex(a); st.AddVertex(b); st.AddVertex(c);
        st.AddVertex(a); st.AddVertex(c); st.AddVertex(d);
    }

    static void Cap(SurfaceTool st, Vector3[] ring, bool front)
    {
        var c = Vector3.Zero;
        foreach (var p in ring) c += p;
        c /= ring.Length;
        if ((ring[0] - c).Length() < 0.01f) return;
        for (int s = 0; s < ring.Length; s++)
        {
            int s1 = (s + 1) % ring.Length;
            if (front) { st.AddVertex(c); st.AddVertex(ring[s1]); st.AddVertex(ring[s]); }
            else { st.AddVertex(c); st.AddVertex(ring[s]); st.AddVertex(ring[s1]); }
        }
    }

    /// <summary>Contour elliptique (demi-largeur <paramref name="w"/>, demi-hauteur <paramref name="h"/>, centre <paramref name="cy"/>).</summary>
    public static Vector2[] Ellipse(float w, float h, float cy = 0, int sides = 16)
    {
        var p = new Vector2[sides];
        for (int i = 0; i < sides; i++)
        {
            float a = i * Mathf.Tau / sides;
            p[i] = new Vector2(Mathf.Cos(a) * Mathf.Max(w, 0.001f), Mathf.Sin(a) * Mathf.Max(h, 0.001f) + cy);
        }
        return p;
    }

    /// <summary>Section de coque : pont plat à <paramref name="deck"/>, bouchain, fond en V jusqu'à la quille à −<paramref name="keel"/>.</summary>
    public static Vector2[] HullSection(float halfWidth, float deck, float keel)
    {
        float w = Mathf.Max(halfWidth, 0.002f);
        return new[]
        {
            new Vector2(w, deck), new Vector2(w * 0.98f, deck * 0.3f), new Vector2(w * 0.8f, -keel * 0.55f),
            new Vector2(0, -keel),
            new Vector2(-w * 0.8f, -keel * 0.55f), new Vector2(-w * 0.98f, deck * 0.3f), new Vector2(-w, deck),
            new Vector2(-w * 0.5f, deck), new Vector2(0, deck), new Vector2(w * 0.5f, deck),
        };
    }
}
