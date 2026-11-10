using System;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>Maillage d'une tuile de routes / rails, calculé hors du thread principal.</summary>
public sealed class RoadMeshData
{
    public Vec3d Origin;            // origine des sommets (repère planète)
    public Vector3[] Vertices, Normals;
    public Color[] Colors;
    public int[] Indices;           // null pour des lignes (Vertices par paires)
    /// <summary>Tuiles de zoom 12 : un groupe de lignes par sous-tuile de zoom 14 (4×4), pour masquer
    /// celles déjà couvertes par des tuiles détaillées.</summary>
    public Vector3[][] SubLines;
    public Color[][] SubColors;
    public bool Incomplete;         // relief provisoire (téléchargement en cours) : à reconstruire
    public int DataVersion;
    public int Segments;
}

/// <summary>
/// Transforme les tronçons Overture (routes, chemins, rails) d'une tuile vectorielle en géométrie posée sur le relief :
/// - zoom 14 (près du joueur) : rubans de la vraie largeur, couleur selon le type, rails avec leurs deux files ;
///   ponts tendus entre leurs extrémités, tunnels et voies souterraines omis ;
/// - zoom 12 (au loin) : simples lignes, comme sur une carte.
/// </summary>
public static class RoadBuilder
{
    public static readonly HashSet<string> Keys = new() { "subtype", "class", "road_flags", "level_rules", "width_rules" };

    /// <summary>Largeur (m), couleur (sRGB), priorité d'affichage (les grandes routes passent dessus).</summary>
    readonly record struct Style(float Width, Color Color, int Priority, bool Rail = false);

    static readonly Color Asphalt = new(0.30f, 0.30f, 0.32f), AsphaltLight = new(0.40f, 0.40f, 0.41f);
    static readonly Color Gravel = new(0.62f, 0.56f, 0.45f), Dirt = new(0.55f, 0.45f, 0.32f);
    static readonly Color Paved = new(0.60f, 0.58f, 0.55f), Ballast = new(0.40f, 0.37f, 0.34f);

    static Style? StyleFor(string subtype, string cls)
    {
        if (subtype == "rail")
            return cls switch
            {
                "subway" => null, // presque toujours en souterrain
                "tram" => new Style(2.0f, Ballast, 8, true),
                "light_rail" or "funicular" or "monorail" => new Style(2.4f, Ballast, 7, true),
                "narrow_gauge" => new Style(2.8f, Ballast, 7, true),
                _ => new Style(3.4f, Ballast, 7, true),
            };
        if (subtype != "road") return null; // « water » = lignes de ferry
        return cls switch
        {
            "motorway" => new Style(9f, Asphalt, 6),
            "trunk" => new Style(8f, Asphalt, 6),
            "primary" => new Style(7f, Asphalt, 5),
            "secondary" => new Style(6.5f, Asphalt, 4),
            "tertiary" => new Style(6f, Asphalt, 3),
            "residential" or "unclassified" or "living_street" => new Style(5f, AsphaltLight, 2),
            "service" => new Style(3.5f, AsphaltLight, 1),
            "pedestrian" => new Style(4f, Paved, 1),
            "track" => new Style(3f, Gravel, 1),
            "steps" => new Style(1.8f, Paved, 0),
            "footway" or "sidewalk" or "crosswalk" or "cycleway" => new Style(1.8f, Paved, 0),
            "path" or "bridleway" => new Style(1.4f, Dirt, 0),
            _ => new Style(4f, AsphaltLight, 1),
        };
    }

    const double Resample = 8;  // m : les rubans suivent le relief à ce pas
    const float Lift = 0.12f;   // m au-dessus du sol

    // --- Règles Overture (« between » = portion de la longueur du tronçon complet) ---
    readonly record struct Range(double A, double B);

    static void ParseRules(Mvt.Feature f, out bool wholeUnder, List<Range> tunnels, List<Range> bridges, out double width)
    {
        wholeUnder = false; width = double.NaN;
        var flags = f.Str("road_flags");
        if (flags != null)
        {
            using var doc = JsonDocument.Parse(flags);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                var range = Between(r);
                foreach (var v in r.GetProperty("values").EnumerateArray())
                {
                    string s = v.GetString();
                    if (s == "is_tunnel") { if (range == null) wholeUnder = true; else tunnels.Add(range.Value); }
                    else if (s == "is_bridge") { if (range == null) bridges.Add(new Range(0, 1)); else bridges.Add(range.Value); }
                }
            }
        }
        var level = f.Str("level_rules");
        if (level != null)
        {
            using var doc = JsonDocument.Parse(level);
            foreach (var r in doc.RootElement.EnumerateArray())
            {
                if (!r.TryGetProperty("value", out var v) || v.GetDouble() >= 0) continue;
                var range = Between(r);
                if (range == null) wholeUnder = true; else tunnels.Add(range.Value);
            }
        }
        var w = f.Str("width_rules");
        if (w != null)
        {
            using var doc = JsonDocument.Parse(w);
            foreach (var r in doc.RootElement.EnumerateArray())
                if (Between(r) == null && r.TryGetProperty("value", out var v)) width = v.GetDouble();
        }
    }

    static Range? Between(JsonElement r)
    {
        if (!r.TryGetProperty("between", out var b) || b.GetArrayLength() != 2) return null;
        return new Range(b[0].GetDouble(), b[1].GetDouble());
    }

    static bool In(List<Range> rs, double t)
    {
        foreach (var r in rs) if (t >= r.A && t <= r.B) return true;
        return false;
    }

    /// <summary>Point posé sur le relief (et indique si le relief était provisoire).</summary>
    static double Ground(Vec3d dir, double spacing, ref bool incomplete)
    {
        Geo.ToLatLon(dir, out double lat, out double lon);
        float h = HeightProvider.Sample(lat, lon, spacing, out bool water, out bool pending);
        if (pending) incomplete = true;
        return water ? Math.Max(h, 0) : h;
    }

    static Vec3d Dir(int z, int x, int y, double fx, double fy)
    {
        PmTilesArchive.TileToLatLon(z, x, y, fx, fy, out double lat, out double lon);
        return Geo.FromLatLon(lat, lon);
    }

    /// <summary>Tuile détaillée (zoom 14) : rubans.</summary>
    public static RoadMeshData BuildDetailed(List<Mvt.Feature> segments, int z, int x, int y)
    {
        var data = new RoadMeshData { DataVersion = StreamedRaster.DataVersion };
        data.Origin = Dir(z, x, y, 0.5, 0.5) * Geo.Radius;
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var cols = new List<Color>(); var idx = new List<int>();
        var tunnels = new List<Range>(); var bridges = new List<Range>();
        bool incomplete = false;

        foreach (var f in segments)
        {
            var st = StyleFor(f.Str("subtype"), f.Str("class"));
            if (st == null || f.Parts.Count == 0) continue;
            tunnels.Clear(); bridges.Clear();
            ParseRules(f, out bool under, tunnels, bridges, out double w);
            if (under) continue;
            var style = st.Value;
            float width = double.IsNaN(w) ? style.Width : (float)Math.Clamp(w, 1, 30);
            // Les portions « between » se rapportent au tronçon entier : on ne s'y fie que s'il n'a pas été
            // coupé par le bord de la tuile.
            var p0 = f.Parts[0];
            bool whole = f.Parts.Count == 1 && InTile(p0[0], p0[1]) && InTile(p0[^2], p0[^1]);
            if (!whole)
            {
                tunnels.Clear();
                bool full = bridges.Exists(r => r.A <= 0 && r.B >= 1);
                bridges.Clear();
                if (full) bridges.Add(new Range(0, 1));
            }
            foreach (var part in f.Parts)
            {
                if (part.Length < 4) continue;
                data.Segments++;
                var line = BuildLine(part, z, x, y, tunnels, bridges, 10, ref incomplete);
                float lift = Lift + style.Priority * 0.04f;
                foreach (var run in line)
                {
                    if (style.Rail)
                    {
                        Ribbon(run, data.Origin, -width / 2, width / 2, lift, Shade(style.Color, 1), verts, norms, cols, idx);
                        var steel = new Color(0.55f, 0.55f, 0.58f);
                        float g = width > 3 ? 0.72f : 0.5f; // demi-écartement (voie normale / métrique)
                        Ribbon(run, data.Origin, -g - 0.07f, -g + 0.07f, lift + 0.16f, steel, verts, norms, cols, idx);
                        Ribbon(run, data.Origin, g - 0.07f, g + 0.07f, lift + 0.16f, steel, verts, norms, cols, idx);
                    }
                    else Ribbon(run, data.Origin, -width / 2, width / 2, lift, style.Color, verts, norms, cols, idx);
                }
            }
        }
        data.Vertices = verts.ToArray(); data.Normals = norms.ToArray(); data.Colors = cols.ToArray(); data.Indices = idx.ToArray();
        data.Incomplete = incomplete;
        return data;
    }

    static bool InTile(double fx, double fy) => fx > 0.0005 && fx < 0.9995 && fy > 0.0005 && fy < 0.9995;

    static Color Shade(Color c, float k) => new(c.R * k, c.G * k, c.B * k);

    /// <summary>Polyligne → suites de points (direction, hauteur) rééchantillonnées, coupées aux tunnels.</summary>
    static List<List<(Vec3d dir, double h)>> BuildLine(double[] part, int z, int x, int y, List<Range> tunnels, List<Range> bridges,
        double spacing, ref bool incomplete)
    {
        int n = part.Length / 2;
        var dirs = new Vec3d[n];
        var cum = new double[n];
        for (int i = 0; i < n; i++)
        {
            dirs[i] = Dir(z, x, y, part[2 * i], part[2 * i + 1]);
            if (i > 0) cum[i] = cum[i - 1] + (dirs[i] - dirs[i - 1]).Length() * Geo.Radius;
        }
        double total = Math.Max(cum[n - 1], 1e-6);
        // Points rééchantillonnés avec leur position relative (0..1) le long du tronçon
        var pts = new List<(Vec3d dir, double t)>();
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                double len = cum[i] - cum[i - 1];
                int steps = Math.Max(1, (int)Math.Ceiling(len / Resample));
                for (int s = 1; s < steps; s++)
                {
                    double a = (double)s / steps;
                    pts.Add(((dirs[i - 1] * (1 - a) + dirs[i] * a).Normalized(), (cum[i - 1] + len * a) / total));
                }
            }
            pts.Add((dirs[i], cum[i] / total));
        }
        var heights = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++) heights[i] = Ground(pts[i].dir, spacing, ref incomplete);

        // Ponts : tablier droit entre les deux culées (au-dessus de l'eau ou de la vallée)
        foreach (var br in bridges)
        {
            int i0 = -1, i1 = -1;
            for (int i = 0; i < pts.Count; i++)
                if (pts[i].t >= br.A - 1e-6 && pts[i].t <= br.B + 1e-6) { if (i0 < 0) i0 = i; i1 = i; }
            if (i0 < 0 || i1 <= i0) continue;
            // pour un pont coupé par le bord de tuile, on garde au moins le relief de part et d'autre
            double ha = heights[i0], hb = heights[i1];
            double ta = pts[i0].t, tb = pts[i1].t;
            for (int i = i0 + 1; i < i1; i++)
            {
                double a = (pts[i].t - ta) / Math.Max(tb - ta, 1e-9);
                heights[i] = Math.Max(ha * (1 - a) + hb * a, heights[i] + 0.5);
            }
        }

        var runs = new List<List<(Vec3d, double)>>();
        List<(Vec3d, double)> cur = null;
        for (int i = 0; i < pts.Count; i++)
        {
            if (tunnels.Count > 0 && In(tunnels, pts[i].t)) { cur = null; continue; }
            if (cur == null) { cur = new List<(Vec3d, double)>(); runs.Add(cur); }
            cur.Add((pts[i].dir, heights[i]));
        }
        runs.RemoveAll(r => r.Count < 2);
        return runs;
    }

    static void Ribbon(List<(Vec3d dir, double h)> run, Vec3d origin, float left, float right, float lift, Color col,
        List<Vector3> verts, List<Vector3> norms, List<Color> cols, List<int> idx)
    {
        int n = run.Count, start = verts.Count;
        for (int i = 0; i < n; i++)
        {
            var up = run[i].dir;
            var a = run[Math.Max(i - 1, 0)].dir; var b = run[Math.Min(i + 1, n - 1)].dir;
            var tangent = (b - a).Normalized();
            var side = tangent.Cross(up).Normalized(); // vers la droite
            var center = up * (Geo.Radius + run[i].h + lift);
            var nrm = up.ToVector3();
            verts.Add((center + side * left - origin).ToVector3());
            verts.Add((center + side * right - origin).ToVector3());
            norms.Add(nrm); norms.Add(nrm);
            cols.Add(col); cols.Add(col);
        }
        for (int i = 0; i < n - 1; i++)
        {
            int k = start + 2 * i;
            idx.Add(k); idx.Add(k + 2); idx.Add(k + 1);
            idx.Add(k + 1); idx.Add(k + 2); idx.Add(k + 3);
        }
    }

    /// <summary>Tuile lointaine (zoom 12) : lignes, regroupées par sous-tuile de zoom 14.</summary>
    public static RoadMeshData BuildOverview(List<Mvt.Feature> segments, int z, int x, int y)
    {
        var data = new RoadMeshData { DataVersion = StreamedRaster.DataVersion };
        data.Origin = Dir(z, x, y, 0.5, 0.5) * Geo.Radius;
        var groups = new List<Vector3>[16]; var gcols = new List<Color>[16];
        for (int i = 0; i < 16; i++) { groups[i] = new List<Vector3>(); gcols[i] = new List<Color>(); }
        var none = new List<Range>();
        bool incomplete = false;
        foreach (var f in segments)
        {
            string sub = f.Str("subtype"), cls = f.Str("class");
            Color col;
            if (sub == null) col = new Color(0.62f, 0.60f, 0.56f); // tronçons fusionnés sans attributs
            else
            {
                var st = StyleFor(sub, cls);
                if (st == null) continue;
                var tunnels = new List<Range>(); var bridges = new List<Range>();
                ParseRules(f, out bool under, tunnels, bridges, out _);
                if (under) continue;
                col = st.Value.Rail ? new Color(0.45f, 0.40f, 0.38f) : st.Value.Priority >= 3 ? new Color(0.78f, 0.74f, 0.64f) : new Color(0.62f, 0.60f, 0.56f);
            }
            foreach (var part in f.Parts)
            {
                if (part.Length < 4) continue;
                data.Segments++;
                foreach (var run in BuildLine(part, z, x, y, none, none, 120, ref incomplete))
                    for (int i = 0; i + 1 < run.Count; i++)
                    {
                        // sous-tuile selon le milieu du segment
                        var mid = (run[i].dir + run[i + 1].dir).Normalized();
                        Geo.ToLatLon(mid, out double la, out double lo);
                        PmTilesArchive.TileXY(la, lo, z + 2, out int sx, out int sy);
                        int g = Math.Clamp(sy - 4 * y, 0, 3) * 4 + Math.Clamp(sx - 4 * x, 0, 3);
                        groups[g].Add((run[i].dir * (Geo.Radius + run[i].h + 2) - data.Origin).ToVector3());
                        groups[g].Add((run[i + 1].dir * (Geo.Radius + run[i + 1].h + 2) - data.Origin).ToVector3());
                        gcols[g].Add(col); gcols[g].Add(col);
                    }
            }
        }
        data.SubLines = new Vector3[16][]; data.SubColors = new Color[16][];
        for (int i = 0; i < 16; i++) { data.SubLines[i] = groups[i].ToArray(); data.SubColors[i] = gcols[i].ToArray(); }
        data.Incomplete = incomplete;
        return data;
    }
}
