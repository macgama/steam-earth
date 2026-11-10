using System;
using System.Collections.Generic;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>Maillage des bâtiments d'une tuile, calculé hors du thread principal.</summary>
public sealed class BuildingMeshData
{
    public Vec3d Origin;                 // origine des sommets (repère planète)
    public Vector3[] Vertices, Normals;
    public Color[] Colors;               // sRGB
    public Vector2[] Uv, Uv2;            // UV : (abscisse le long du mur, hauteur au-dessus du sol) ; UV2 : (fenêtres 0/1, aléa)
    public int[] Indices;
    public int[] PerArea;                // nombre de bâtiments par zone construite
    public bool Incomplete;              // relief provisoire (téléchargement en cours) : à reconstruire
    public int DataVersion;
}

/// <summary>
/// Transforme les bâtiments Overture (contours OpenStreetMap, Microsoft, Google…) d'une tuile vectorielle de zoom 14
/// en volumes posés sur le relief : murs extrudés (avec fenêtres dessinées par le shader), toit plat, ou toit à deux
/// ou quatre pans pour les maisons à peu près rectangulaires. Hauteur : « height », sinon nombre d'étages × 3 m,
/// sinon une valeur selon le type et la surface. Les « building_part » (bâtiments 3D détaillés d'OSM) remplacent
/// le contour de leur bâtiment.
/// </summary>
public static class BuildingBuilder
{
    public static readonly HashSet<string> Keys = new()
    {
        "id", "building_id", "has_parts", "is_underground", "height", "min_height", "num_floors", "min_floor",
        "roof_shape", "roof_height", "roof_color", "roof_material", "facade_color", "facade_material", "class", "subtype",
        "roof_orientation",
    };

    /// <summary>Zone construite : cercle autour du centre d'une ville.</summary>
    public readonly record struct Area(Vec3d Dir, double Radius);

    const double R = Geo.Radius;
    const double Foundation = 6;     // m : les murs descendent sous le sol (le relief lointain est plus grossier)
    const double FloorHeight = 3;

    struct V2 { public double X, Y; public V2(double x, double y) { X = x; Y = y; } }

    sealed class Mesh
    {
        public readonly List<Vector3> V = new(), N = new();
        public readonly List<Color> C = new();
        public readonly List<Vector2> Uv = new(), Uv2 = new();
        public readonly List<int> I = new();
    }

    /// <summary>Repère local d'un bâtiment (plan tangent au centre).</summary>
    readonly struct Frame
    {
        public readonly Vec3d C, East, North, Origin;
        public Frame(Vec3d c, Vec3d origin) { C = c; Origin = origin; Geo.LocalFrame(c, out East, out North); }
        public Vector3 P(double x, double y, double h) => (C * (R + h) + East * x + North * y - Origin).ToVector3();
        public Vector3 Dir(double x, double y, double z) => (East * x + North * y + C * z).Normalized().ToVector3();
    }

    public static BuildingMeshData Build(byte[] mvt, int z, int tx, int ty, IReadOnlyList<Area> areas, double spacing)
    {
        var data = new BuildingMeshData { DataVersion = StreamedRaster.DataVersion, PerArea = new int[areas.Count] };
        PmTilesArchive.TileToLatLon(z, tx, ty, 0.5, 0.5, out double cla, out double clo);
        data.Origin = Geo.FromLatLon(cla, clo) * R;
        var m = new Mesh();
        bool incomplete = false;
        if (mvt != null && mvt.Length > 0)
        {
            var layers = Mvt.Decode(mvt, new HashSet<string> { "building", "building_part" }, Keys);
            var withParts = new HashSet<string>();
            if (layers.TryGetValue("building_part", out var parts))
                foreach (var p in parts) { var id = p.Str("building_id"); if (id != null) withParts.Add(id); }
            if (layers.TryGetValue("building", out var blds))
                foreach (var f in blds)
                {
                    if (f.Type != Mvt.GeomType.Polygon || f.Num("is_underground", 0) > 0) continue;
                    var id = f.Str("id");
                    if (id != null && withParts.Contains(id)) continue; // les parties dessinent ce bâtiment
                    Emit(f, false, z, tx, ty, areas, spacing, data, m, ref incomplete);
                }
            if (parts != null)
                foreach (var f in parts)
                {
                    if (f.Type != Mvt.GeomType.Polygon || f.Num("is_underground", 0) > 0) continue;
                    Emit(f, true, z, tx, ty, areas, spacing, data, m, ref incomplete);
                }
        }
        data.Vertices = m.V.ToArray(); data.Normals = m.N.ToArray(); data.Colors = m.C.ToArray();
        data.Uv = m.Uv.ToArray(); data.Uv2 = m.Uv2.ToArray(); data.Indices = m.I.ToArray();
        data.Incomplete = incomplete;
        return data;
    }

    static void Emit(Mvt.Feature f, bool isPart, int z, int tx, int ty, IReadOnlyList<Area> areas, double spacing,
        BuildingMeshData data, Mesh m, ref bool incomplete)
    {
        // Anneaux : extérieurs (aire > 0 dans le repère de la tuile, y vers le bas), puis leurs trous
        var polys = new List<(double[] outer, List<double[]> holes)>();
        foreach (var ring in f.Parts)
        {
            if (ring.Length < 8) continue;
            double a = 0;
            for (int i = 0; i + 3 < ring.Length; i += 2) a += ring[i] * ring[i + 3] - ring[i + 2] * ring[i + 1];
            if (a > 0) polys.Add((ring, new List<double[]>()));
            else if (polys.Count > 0) polys[^1].holes.Add(ring);
        }
        foreach (var (outer, holes) in polys)
        {
            // Centre du contour (dans la tuile : sinon il appartient à la tuile voisine)
            double cx = 0, cy = 0; int n = outer.Length / 2 - 1;
            for (int i = 0; i < n; i++) { cx += outer[2 * i]; cy += outer[2 * i + 1]; }
            cx /= n; cy /= n;
            if (cx < 0 || cx >= 1 || cy < 0 || cy >= 1) continue;
            PmTilesArchive.TileToLatLon(z, tx, ty, cx, cy, out double clat, out double clon);
            var c = Geo.FromLatLon(clat, clon);
            int area = -1;
            for (int i = 0; i < areas.Count; i++)
                if ((c - areas[i].Dir).Length() * R <= areas[i].Radius) { area = i; break; }
            if (area < 0) continue;
            var fr = new Frame(c, data.Origin);
            var ext = ToLocal(outer, z, tx, ty, fr, out var lats);
            if (ext.Count < 3) continue;
            if (SignedArea(ext) < 0) ext.Reverse();
            var hs = new List<List<V2>>();
            foreach (var h in holes)
            {
                var l = ToLocal(h, z, tx, ty, fr, out _);
                if (l.Count < 3) continue;
                if (SignedArea(l) > 0) l.Reverse();
                hs.Add(l);
            }
            double footprint = SignedArea(ext);
            foreach (var h in hs) footprint += SignedArea(h);
            if (footprint < 4) continue;

            // Sol sous le bâtiment
            double gmin = double.MaxValue, gmax = double.MinValue;
            foreach (var (la, lo) in lats)
            {
                float g = HeightProvider.Sample(la, lo, spacing, out bool water, out bool pending);
                if (pending) incomplete = true;
                if (water) g = Math.Max(g, 0);
                gmin = Math.Min(gmin, g); gmax = Math.Max(gmax, g);
            }
            if (BuildOne(f, isPart, ext, hs, footprint, gmin, gmax, fr, m)) data.PerArea[area]++;
        }
    }

    static List<V2> ToLocal(double[] ring, int z, int tx, int ty, in Frame fr, out List<(double, double)> latLon)
    {
        var pts = new List<V2>();
        latLon = new List<(double, double)>();
        for (int i = 0; i + 1 < ring.Length - 2; i += 2) // le dernier point répète le premier
        {
            PmTilesArchive.TileToLatLon(z, tx, ty, ring[i], ring[i + 1], out double la, out double lo);
            var d = Geo.FromLatLon(la, lo) - fr.C;
            var p = new V2(d.Dot(fr.East) * R, d.Dot(fr.North) * R);
            if (pts.Count > 0 && Dist(pts[^1], p) < 0.05) continue;
            pts.Add(p);
            latLon.Add((la, lo));
        }
        if (pts.Count > 1 && Dist(pts[0], pts[^1]) < 0.05) { pts.RemoveAt(pts.Count - 1); latLon.RemoveAt(latLon.Count - 1); }
        // points alignés : ils ne servent à rien et gênent le repérage des maisons rectangulaires
        for (int i = 0; pts.Count > 3 && i < pts.Count;)
        {
            var a = pts[(i + pts.Count - 1) % pts.Count]; var b = pts[i]; var c = pts[(i + 1) % pts.Count];
            double cr = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cr) < 0.02 * Math.Max(Dist(a, b) * Dist(b, c), 1e-6)) { pts.RemoveAt(i); latLon.RemoveAt(i); }
            else i++;
        }
        return pts;
    }

    static double Dist(V2 a, V2 b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    static double SignedArea(List<V2> p)
    {
        double a = 0;
        for (int i = 0; i < p.Count; i++) { var q = p[i]; var r = p[(i + 1) % p.Count]; a += q.X * r.Y - r.X * q.Y; }
        return a / 2;
    }

    // ------------------------------------------------------------------ volumes

    static bool BuildOne(Mvt.Feature f, bool isPart, List<V2> ext, List<List<V2>> holes, double footprint,
        double gmin, double gmax, in Frame fr, Mesh m)
    {
        string cls = f.Str("class") ?? f.Str("subtype");
        string shape = f.Str("roof_shape");
        double seed = Hash(ext[0].X * 7.13 + ext[0].Y * 3.71 + footprint);

        double height = f.Num("height");
        double floors = f.Num("num_floors");
        double minH = f.Num("min_height");
        if (double.IsNaN(minH)) { double mf = f.Num("min_floor"); minH = double.IsNaN(mf) ? 0 : mf * FloorHeight; }
        if (double.IsNaN(height) || height <= 0)
            height = !double.IsNaN(floors) && floors > 0 ? floors * FloorHeight + (IsPitched(shape) ? 2 : 0.5) : DefaultHeight(cls, footprint, seed);
        if (height - minH < 0.5) return false;

        // Toit en pente : forme donnée par OSM, sinon par défaut pour les petites maisons
        bool pitched = IsPitched(shape) || (shape == null && !isPart && footprint < 450 && height <= 14 && cls is not ("industrial" or "commercial" or "retail" or "warehouse" or "parking" or "garage" or "garages"));
        V2[] box = null; double boxLong = 0, boxShort = 0;
        if (pitched && holes.Count == 0)
        {
            box = OrientedBox(ext, out boxLong, out boxShort, out double fill);
            if (fill < (IsPitched(shape) ? 0.75 : 0.85) || boxShort < 3) box = null;
        }
        if (box == null) pitched = false;
        double roofH = pitched ? f.Num("roof_height") : 0;
        if (pitched && (double.IsNaN(roofH) || roofH <= 0)) roofH = Math.Min(boxShort * 0.5 * (0.7 + 0.35 * seed), 7);
        if (pitched) roofH = Math.Min(roofH, Math.Max(height - minH - 2, 0.5));

        double top = gmin + height;
        if (!isPart || minH <= 0) top = Math.Max(top, gmax + Math.Min(height, 2.5)); // sur une pente : jamais enterré côté amont
        double baseH = minH > 0 ? gmin + minH : gmin - Foundation;
        double eave = top - roofH;

        var wall = WallColor(f, cls, seed);
        var roof = RoofColor(f, shape, pitched, seed);
        bool windows = height >= 4 && footprint >= 25 && cls is not ("garage" or "garages" or "shed" or "roof" or "carport" or "hut" or "silo" or "storage_tank" or "transformer_tower");
        var uv2 = new Vector2(windows ? 1 : 0, (float)seed);

        Walls(ext, baseH, eave, gmin, wall, uv2, fr, m);
        foreach (var h in holes) Walls(h, baseH, eave, gmin, wall, uv2, fr, m);
        if (pitched) PitchedRoof(box, boxLong >= boxShort, f.Str("roof_orientation") == "across", shape, eave, roofH, roof, wall, gmin, fr, m);
        else FlatRoof(ext, holes, eave, roof, fr, m);
        return true;
    }

    static bool IsPitched(string s) => s is "gabled" or "hipped" or "half-hipped" or "side_hipped" or "gambrel" or "mansard" or "saltbox" or "pyramidal" or "skillion";

    static double DefaultHeight(string cls, double area, double seed)
    {
        if (area < 30) return 2.6 + seed;
        return cls switch
        {
            "garage" or "garages" or "shed" or "carport" or "hut" or "roof" => 3 + seed,
            "house" or "detached" or "semidetached_house" or "bungalow" or "farm" or "residential" => 6.5 + 3 * seed,
            "apartments" or "terrace" => 12 + 6 * seed,
            "church" or "cathedral" or "chapel" => 14 + 6 * seed,
            "industrial" or "warehouse" or "manufacture" or "barn" or "farm_auxiliary" or "stable" or "cowshed" => 7 + 4 * seed,
            "commercial" or "office" or "retail" or "hotel" or "civic" or "public" or "school" or "university" or "hospital" => 12 + 6 * seed,
            _ => area > 800 ? 9 + 6 * seed : 6 + 4 * seed,
        };
    }

    static Color WallColor(Mvt.Feature f, string cls, double seed)
    {
        var s = f.Str("facade_color");
        if (s != null) { var c = Color.FromString(s, new Color(-1, 0, 0)); if (c.R >= 0) return c; }
        var mat = f.Str("facade_material");
        Color b = mat switch
        {
            "brick" => new Color(0.62f, 0.36f, 0.28f),
            "wood" => new Color(0.55f, 0.40f, 0.26f),
            "stone" or "sandstone" => new Color(0.66f, 0.62f, 0.52f),
            "glass" => new Color(0.42f, 0.52f, 0.58f),
            "concrete" => new Color(0.66f, 0.66f, 0.64f),
            "metal" => new Color(0.58f, 0.60f, 0.62f),
            _ => cls switch
            {
                "industrial" or "warehouse" or "manufacture" => new Color(0.62f, 0.64f, 0.66f),
                "church" or "cathedral" or "chapel" => new Color(0.70f, 0.66f, 0.56f),
                "barn" or "farm_auxiliary" or "stable" or "cowshed" or "hut" or "shed" => new Color(0.52f, 0.38f, 0.25f),
                _ => new Color(0.86f, 0.82f, 0.74f), // crépi clair
            },
        };
        float v = (float)(seed - 0.5) * 0.12f;
        return new Color(Math.Clamp(b.R + v, 0, 1), Math.Clamp(b.G + v * 0.9f, 0, 1), Math.Clamp(b.B + v * 0.7f, 0, 1));
    }

    static Color RoofColor(Mvt.Feature f, string shape, bool pitched, double seed)
    {
        var s = f.Str("roof_color");
        if (s != null) { var c = Color.FromString(s, new Color(-1, 0, 0)); if (c.R >= 0) return c; }
        var mat = f.Str("roof_material");
        Color b = mat switch
        {
            "roof_tiles" or "tile" => new Color(0.58f, 0.30f, 0.22f),
            "metal" or "copper" => new Color(0.45f, 0.48f, 0.50f),
            "slate" or "stone" => new Color(0.34f, 0.35f, 0.38f),
            "glass" => new Color(0.40f, 0.48f, 0.55f),
            "grass" or "plants" => new Color(0.36f, 0.45f, 0.26f),
            "wood" => new Color(0.42f, 0.32f, 0.22f),
            _ => pitched
                ? (seed < 0.6 ? new Color(0.55f, 0.29f, 0.21f) : seed < 0.85 ? new Color(0.38f, 0.30f, 0.27f) : new Color(0.42f, 0.43f, 0.45f))
                : new Color(0.58f, 0.57f, 0.55f), // toit plat : gravier
        };
        float v = (float)(Hash(seed * 91.7) - 0.5) * 0.10f;
        return new Color(Math.Clamp(b.R + v, 0, 1), Math.Clamp(b.G + v, 0, 1), Math.Clamp(b.B + v, 0, 1));
    }

    static void Walls(List<V2> ring, double bottom, double top, double ground, Color col, Vector2 uv2, in Frame fr, Mesh m)
    {
        double s = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            double len = Dist(a, b);
            if (len < 0.01) continue;
            // anneau extérieur dans le sens direct : la normale sortante est (dy, −dx)
            var nrm = fr.Dir((b.Y - a.Y) / len, -(b.X - a.X) / len, 0);
            int k = m.V.Count;
            m.V.Add(fr.P(a.X, a.Y, bottom)); m.V.Add(fr.P(b.X, b.Y, bottom));
            m.V.Add(fr.P(b.X, b.Y, top)); m.V.Add(fr.P(a.X, a.Y, top));
            m.Uv.Add(new Vector2((float)s, (float)(bottom - ground))); m.Uv.Add(new Vector2((float)(s + len), (float)(bottom - ground)));
            m.Uv.Add(new Vector2((float)(s + len), (float)(top - ground))); m.Uv.Add(new Vector2((float)s, (float)(top - ground)));
            for (int j = 0; j < 4; j++) { m.N.Add(nrm); m.C.Add(col); m.Uv2.Add(uv2); }
            m.I.Add(k); m.I.Add(k + 1); m.I.Add(k + 2); m.I.Add(k); m.I.Add(k + 2); m.I.Add(k + 3);
            s += len;
        }
    }

    static void FlatRoof(List<V2> ext, List<List<V2>> holes, double h, Color col, in Frame fr, Mesh m)
    {
        var pts = new List<V2>();
        var tris = Triangulate(ext, holes, pts);
        int k = m.V.Count;
        var up = fr.C.ToVector3();
        foreach (var p in pts)
        {
            m.V.Add(fr.P(p.X, p.Y, h)); m.N.Add(up); m.C.Add(col);
            m.Uv.Add(Vector2.Zero); m.Uv2.Add(Vector2.Zero);
        }
        foreach (var t in tris) m.I.Add(k + t);
    }

    /// <summary>Toit à deux pans (pignons) ou à quatre pans sur le rectangle englobant, avec un léger débord.</summary>
    static void PitchedRoof(V2[] box, bool firstIsLong, bool across, string shape, double eave, double roofH,
        Color col, Color wall, double ground, in Frame fr, Mesh m)
    {
        // box : A, B, C, D (sens direct), AB parallèle au grand côté si firstIsLong
        var A = box[0]; var B = box[1]; var C = box[2]; var D = box[3];
        if (!firstIsLong ^ across) { (A, B, C, D) = (B, C, D, A); } // le faîte suit AB
        // débord de 0,4 m
        double ux = B.X - A.X, uy = B.Y - A.Y, L = Math.Sqrt(ux * ux + uy * uy); ux /= L; uy /= L;
        double vx = D.X - A.X, vy = D.Y - A.Y, W = Math.Sqrt(vx * vx + vy * vy); vx /= W; vy /= W;
        const double o = 0.4;
        V2 Off(V2 p, double su, double sv) => new(p.X + ux * su * o + vx * sv * o, p.Y + uy * su * o + vy * sv * o);
        A = Off(A, -1, -1); B = Off(B, 1, -1); C = Off(C, 1, 1); D = Off(D, -1, 1);
        L += 2 * o; W += 2 * o;
        double ridge = eave + roofH;
        double low = eave - o * roofH / (W / 2); // le débord descend un peu sous la gouttière
        var mid1 = new V2((A.X + D.X) / 2, (A.Y + D.Y) / 2);
        var mid2 = new V2((B.X + C.X) / 2, (B.Y + C.Y) / 2);
        bool hip = shape is "hipped" or "half-hipped" or "side_hipped" or "pyramidal" or "mansard";
        V2 r1 = mid1, r2 = mid2;
        if (hip)
        {
            double inset = shape == "pyramidal" ? L / 2 : Math.Min(W / 2, L / 2) * (shape == "half-hipped" ? 0.5 : 1);
            r1 = new V2(mid1.X + ux * inset, mid1.Y + uy * inset);
            r2 = new V2(mid2.X - ux * inset, mid2.Y - uy * inset);
        }
        if (shape == "skillion") { Skillion(A, B, C, D, low, ridge, col, wall, ground, fr, m); return; }

        var center = new V2((A.X + C.X) / 2, (A.Y + C.Y) / 2);
        Quad(A, low, B, low, r2, ridge, r1, ridge, col, center, fr, m, false);
        Quad(C, low, D, low, r1, ridge, r2, ridge, col, center, fr, m, false);
        if (hip)
        {
            Tri(B, low, C, low, r2, ridge, col, center, fr, m, false);
            Tri(D, low, A, low, r1, ridge, col, center, fr, m, false);
        }
        else
        {
            // pignons : murs triangulaires (avec fenêtres)
            Tri(B, eave, C, eave, r2, ridge, wall, center, fr, m, true, ground);
            Tri(D, eave, A, eave, r1, ridge, wall, center, fr, m, true, ground);
        }
    }

    static void Skillion(V2 A, V2 B, V2 C, V2 D, double low, double high, Color col, Color wall, double ground, in Frame fr, Mesh m)
    {
        var center = new V2((A.X + C.X) / 2, (A.Y + C.Y) / 2);
        Quad(A, low, B, low, C, high, D, high, col, center, fr, m, false);
        Quad(B, low, C, high, C, low, B, low, wall, center, fr, m, true, ground);
        Quad(D, high, A, low, A, low, D, low, wall, center, fr, m, true, ground);
        Quad(C, high, D, high, D, low, C, low, wall, center, fr, m, true, ground);
    }

    static void Tri(V2 a, double ha, V2 b, double hb, V2 c, double hc, Color col, V2 center, in Frame fr, Mesh m, bool isWall, double ground = 0)
    {
        int k = m.V.Count;
        Face(new[] { (a, ha), (b, hb), (c, hc) }, col, center, fr, m, isWall, ground);
        m.I.Add(k); m.I.Add(k + 1); m.I.Add(k + 2);
    }

    static void Quad(V2 a, double ha, V2 b, double hb, V2 c, double hc, V2 d, double hd, Color col, V2 center, in Frame fr, Mesh m, bool isWall, double ground = 0)
    {
        int k = m.V.Count;
        Face(new[] { (a, ha), (b, hb), (c, hc), (d, hd) }, col, center, fr, m, isWall, ground);
        m.I.Add(k); m.I.Add(k + 1); m.I.Add(k + 2); m.I.Add(k); m.I.Add(k + 2); m.I.Add(k + 3);
    }

    /// <summary>Ajoute les sommets d'une face plane, normale tournée vers l'extérieur du bâtiment.</summary>
    static void Face((V2 p, double h)[] pts, Color col, V2 center, in Frame fr, Mesh m, bool isWall, double ground)
    {
        var p0 = fr.P(pts[0].p.X, pts[0].p.Y, pts[0].h);
        var p1 = fr.P(pts[1].p.X, pts[1].p.Y, pts[1].h);
        var p2 = fr.P(pts[2].p.X, pts[2].p.Y, pts[2].h);
        var n = (p1 - p0).Cross(p2 - p0);
        if (n.LengthSquared() < 1e-10) n = fr.C.ToVector3(); else n = n.Normalized();
        double fx = 0, fy = 0; foreach (var (p, _) in pts) { fx += p.X; fy += p.Y; }
        var outward = fr.Dir(fx / pts.Length - center.X, fy / pts.Length - center.Y, 0.3);
        if (n.Dot(outward) < 0) n = -n;
        double s = 0;
        for (int i = 0; i < pts.Length; i++)
        {
            var (p, h) = pts[i];
            m.V.Add(fr.P(p.X, p.Y, h)); m.N.Add(n); m.C.Add(col);
            if (i > 0) s += Dist(pts[i - 1].p, p);
            m.Uv.Add(isWall ? new Vector2((float)s, (float)(h - ground)) : Vector2.Zero);
            m.Uv2.Add(isWall ? new Vector2(1, 0.5f) : Vector2.Zero);
        }
    }

    /// <summary>Plus petit rectangle englobant (orienté selon un des côtés). fill = surface du contour / surface du rectangle.</summary>
    static V2[] OrientedBox(List<V2> p, out double longSide, out double shortSide, out double fill)
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
        {
            // AB = grand côté
            box = new[] { box[1], box[2], box[3], box[0] };
        }
        return box;
    }

    static double Hash(double x)
    {
        double s = Math.Sin(x * 12.9898 + 78.233) * 43758.5453;
        return s - Math.Floor(s);
    }

    // ------------------------------------------------------------------ triangulation

    /// <summary>Triangulation par oreilles d'un polygone (sens direct) avec trous (sens indirect),
    /// les trous étant d'abord reliés au contour par des « ponts ». Remplit <paramref name="pts"/>.</summary>
    static List<int> Triangulate(List<V2> outer, List<List<V2>> holes, List<V2> pts)
    {
        var poly = new List<V2>(outer);
        if (holes.Count > 0 && outer.Count + holes.Count * 8 < 400)
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
