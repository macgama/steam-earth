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
    public Vector2[] UVs, UV2s;     // UV = (fraction de largeur, distance le long en m) ; UV2.x = largeur (m)
    public int[] Indices;           // null pour des lignes (Vertices par paires)
    /// <summary>Tuiles de zoom 12 : un groupe de lignes par sous-tuile de zoom 14 (4×4), pour masquer
    /// celles déjà couvertes par des tuiles détaillées.</summary>
    public Vector3[][] SubLines;
    public Color[][] SubColors;
    public bool Incomplete;         // relief provisoire (téléchargement en cours) : à reconstruire
    public int DataVersion;
    public int Segments;
    /// <summary>Zoom 14 : couloirs le long des voies (non bloquants) qui retirent les arbres tombant sur la chaussée.</summary>
    public List<Obstacles.Shape> Clearings;
    /// <summary>Zoom 14 : trottoirs et chemins où marchent les passants (<see cref="Population.Walkways"/>).</summary>
    public List<Population.Walkway> Walks;
}

/// <summary>
/// Transforme les tronçons Overture (routes, chemins, rails) d'une tuile vectorielle en géométrie posée sur le relief :
/// - zoom 14 (près du joueur) : rubans de la vraie largeur, couleur selon le type, rails avec leurs deux files ;
///   ponts tendus entre leurs extrémités, tunnels et voies souterraines omis ;
/// - zoom 12 (au loin) : simples lignes, comme sur une carte.
/// </summary>
public static class RoadBuilder
{
    public static readonly HashSet<string> Keys = new() { "id", "subtype", "class", "road_flags", "level_rules", "width_rules", "subclass" };

    /// <summary>Largeur (m), couleur (sRGB), priorité d'affichage (les grandes routes passent dessus).</summary>
    readonly record struct Style(float Width, Color Color, int Priority, Surface Kind, bool Rail = false);

    /// <summary>Revêtement dessiné par le shader (stocké dans l'alpha de la couleur de sommet).</summary>
    public enum Surface { MarkedAsphalt = 0, Asphalt = 1, Paved = 2, Gravel = 3, Dirt = 4, Ballast = 5, Steel = 6,
        /// <summary>Accotement en gravier : bord extérieur à gauche (UV.x = 0) ou à droite (UV.x = 1), fondu dans l'herbe.</summary>
        ShoulderLeft = 7, ShoulderRight = 8,
        /// <summary>Muret de pierre (talus ou remblai) : UV.x = hauteur (m), UV.y = longueur (m).</summary>
        StoneWall = 9,
        /// <summary>Béton des ponts (bords du tablier, sous-face, piles) : UV.x = hauteur (m), UV.y = longueur (m).</summary>
        Concrete = 10 }

    static readonly Color Asphalt = new(0.40f, 0.40f, 0.42f), AsphaltLight = new(0.48f, 0.48f, 0.49f);
    static readonly Color Gravel = new(0.62f, 0.56f, 0.45f), Dirt = new(0.55f, 0.45f, 0.32f);
    static readonly Color Paved = new(0.60f, 0.58f, 0.55f), Ballast = new(0.64f, 0.60f, 0.54f);
    static readonly Color Shoulder = new(0.52f, 0.49f, 0.43f), Stone = new(0.55f, 0.53f, 0.49f);
    static readonly Color Concrete = new(0.66f, 0.65f, 0.62f);

    static Style? StyleFor(string subtype, string cls)
    {
        if (subtype == "rail")
            return cls switch
            {
                "subway" => null, // presque toujours en souterrain
                "tram" => new Style(2.2f, AsphaltLight, 8, Surface.Asphalt, true), // rails noyés dans la chaussée
                "light_rail" or "funicular" or "monorail" => new Style(2.6f, Ballast, 7, Surface.Ballast, true),
                "narrow_gauge" => new Style(3.0f, Ballast, 7, Surface.Ballast, true),
                _ => new Style(3.6f, Ballast, 7, Surface.Ballast, true),
            };
        if (subtype != "road") return null; // « water » = lignes de ferry
        return cls switch
        {
            "motorway" => new Style(9f, Asphalt, 6, Surface.MarkedAsphalt),
            "trunk" => new Style(8f, Asphalt, 6, Surface.MarkedAsphalt),
            "primary" => new Style(7f, Asphalt, 5, Surface.MarkedAsphalt),
            "secondary" => new Style(6.5f, Asphalt, 4, Surface.MarkedAsphalt),
            "tertiary" => new Style(6f, Asphalt, 3, Surface.MarkedAsphalt),
            "residential" or "unclassified" or "living_street" => new Style(5f, AsphaltLight, 2, Surface.Asphalt),
            "service" => new Style(3.5f, AsphaltLight, 1, Surface.Asphalt),
            "pedestrian" => new Style(4f, Paved, 1, Surface.Paved),
            "track" => new Style(3f, Gravel, 1, Surface.Gravel),
            "steps" => new Style(1.8f, Paved, 0, Surface.Paved),
            "footway" or "sidewalk" or "crosswalk" or "cycleway" => new Style(1.8f, Paved, 0, Surface.Paved),
            "path" or "bridleway" => new Style(1.4f, Dirt, 0, Surface.Dirt),
            _ => new Style(4f, AsphaltLight, 1, Surface.Asphalt),
        };
    }

    // m au-dessus du sol (+ 2 cm par rang de priorité pour l'ordre des croisements). Garder bas : les voitures et le
    // personnage roulent sur le relief, une chaussée trop haute leur couperait les roues (le shader rapproche déjà
    // les rubans de la caméra pour qu'ils restent visibles au-dessus d'un relief plus grossier).
    const float Lift = 0.05f, LiftPerPriority = 0.02f;
    /// <summary>Passants : distance (m) entre le bord de la chaussée et la ligne où ils marchent.</summary>
    const double SidewalkOffset = 0.9;
    const double MinBridgeClearance = 2; // m : en dessous, un pont est posé au sol (le relief ne montre pas le creux)

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
                if (!r.TryGetProperty("value", out var v) || v.GetDouble() == 0) continue;
                var range = Between(r);
                if (v.GetDouble() < 0) { if (range == null) wholeUnder = true; else tunnels.Add(range.Value); }
                else bridges.Add(range ?? new Range(0, 1)); // niveau surélevé = pont / viaduc
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
    /// <param name="spacing">précision du relief (m) : 1 près du joueur dans les zones haute définition (Suisse 0,5 m),
    /// plus grossier au loin, 10 ailleurs (Copernicus 30 m). Les rubans sont rééchantillonnés en conséquence.</param>
    public static RoadMeshData BuildDetailed(List<Mvt.Feature> segments, int z, int x, int y, double spacing,
        Func<int, int, List<Mvt.Feature>> neighbours = null)
    {
        var data = new RoadMeshData { DataVersion = StreamedRaster.DataVersion, Clearings = new List<Obstacles.Shape>(), Walks = new List<Population.Walkway>() };
        data.Origin = Dir(z, x, y, 0.5, 0.5) * Geo.Radius;
        var q = GraphicsQuality.Current;
        var verts = new List<Vector3>(); var norms = new List<Vector3>(); var cols = new List<Color>(); var idx = new List<int>();
        var uvs = new List<Vector2>(); var uv2s = new List<Vector2>();
        var g = new Geom(verts, norms, cols, uvs, uv2s, idx);
        var tunnels = new List<Range>(); var bridges = new List<Range>();
        bool incomplete = false;
        var junctions = Junctions.Of(segments, z, x, y);
        var corridors = Corridors.Of(segments, z, x, y);

        foreach (var f in segments)
        {
            var st = StyleFor(f.Str("subtype"), f.Str("class"));
            if (st == null || f.Parts.Count == 0) continue;
            // bretelles (« link ») : pas de ligne médiane, souvent de simples voies de raccordement
            var kind = st.Value.Kind == Surface.MarkedAsphalt && f.Str("subclass") == "link" ? Surface.Asphalt : st.Value.Kind;
            tunnels.Clear(); bridges.Clear();
            ParseRules(f, out bool under, tunnels, bridges, out double w);
            if (under) continue;
            var style = st.Value;
            float width = double.IsNaN(w) ? style.Width : (float)Math.Clamp(w, 1, 30);
            // Les portions « between » (ponts, tunnels) se rapportent au tronçon entier. S'il a été coupé par le bord
            // de la tuile, on le recompose avec les morceaux des tuiles voisines pour savoir où tombe notre morceau.
            var p0 = f.Parts[0];
            bool whole = f.Parts.Count == 1 && InTile(p0[0], p0[1]) && InTile(p0[^2], p0[^1]);
            List<Piece> chain = null;
            if (tunnels.Count > 0 || bridges.Count > 0)
            {
                chain = whole ? new List<Piece> { MakePiece(z, x, y, p0, true) } : Stitch(f, z, x, y, neighbours);
                if (chain != null) Order(chain);
                else
                {
                    // tronçon impossible à recomposer : on ne garde que les règles valables sur toute sa longueur
                    tunnels.Clear();
                    bool full = bridges.Exists(r => r.A <= 0 && r.B >= 1);
                    bridges.Clear();
                    if (full) bridges.Add(new Range(0, 1));
                }
            }
            var decks = chain == null ? null : DeckHeights(chain, bridges, spacing, ref incomplete);
            double total = chain == null ? 0 : chain[^1].Offset + chain[^1].Len;
            var mine = chain == null ? f.Parts.ConvertAll(pp => (pp, 0.0)) : chain.FindAll(c => c.Mine).ConvertAll(c => (c.P, c.Offset));
            foreach (var (part, offset) in mine)
            {
                if (part.Length < 4) continue;
                data.Segments++;
                var line = BuildLine(part, z, x, y, offset, total, tunnels, bridges, decks, spacing, Math.Clamp(spacing * 2, 2, 8), ref incomplete);
                float lift = Lift + style.Priority * LiftPerPriority;
                foreach (var full in line)
                    foreach (var (run, onBridge) in Sections(full))
                    {
                        // marquages estompés aux carrefours (ils se chevaucheraient d'une voie à l'autre)
                        float[] mark = null;
                        if (kind == Surface.MarkedAsphalt)
                        {
                            mark = new float[run.Count];
                            for (int i = 0; i < run.Count; i++)
                                mark[i] = (float)Math.Clamp((junctions.Nearest(run[i].dir, 14) - 6) / 6, 0, 1);
                        }
                        Ribbon(run, data.Origin, -width / 2, width / 2, lift, style.Color, kind, g, mark);
                        // pas d'arbre sur la chaussée ni juste au bord (un sentier garde les arbres tout proches)
                        Clear(run, width / 2 + (width >= 2.5f ? 1.5f : 0.4f), data.Clearings);
                        // passants : bord des rues (pas sur les autoroutes ni les voies ferrées), milieu des chemins
                        if (!style.Rail && style.Priority < 6 && run.Count >= 2)
                        {
                            string cls = f.Str("class");
                            bool foot = style.Priority == 0 || cls is "pedestrian" or "living_street";
                            var pts = new Vec3d[run.Count];
                            for (int k = 0; k < run.Count; k++) pts[k] = run[k].dir * (Geo.Radius + run[k].h);
                            data.Walks.Add(new Population.Walkway(pts, foot ? width * 0.22 : width / 2 + SidewalkOffset, foot));
                        }
                        if (onBridge)
                            BridgeStructure(run, data.Origin, width / 2, lift, g, corridors);
                        else
                        {
                            // En ville (carrefours rapprochés) : ni accotements en gravier ni murets, la rue est bordée de trottoirs
                            bool urban = junctions.Count(run[run.Count / 2].dir, 80) >= 10;
                            // Accotements en gravier des routes goudronnées (bord extérieur fondu dans l'herbe)
                            float shoulder = 0;
                            if (!urban && !style.Rail && style.Kind is Surface.MarkedAsphalt or Surface.Asphalt && q.RoadShoulder > 0)
                            {
                                shoulder = q.RoadShoulder * (style.Priority >= 3 ? 1f : 0.6f);
                                Ribbon(run, data.Origin, -width / 2 - shoulder, -width / 2, Lift, Shoulder, Surface.ShoulderLeft, g);
                                Ribbon(run, data.Origin, width / 2, width / 2 + shoulder, Lift, Shoulder, Surface.ShoulderRight, g);
                            }
                            // Murets de pierre là où la route entaille la pente (amont) ou passe sur un remblai (aval)
                            if (!urban && q.RoadWalls && !style.Rail && style.Priority >= 1)
                                SideWalls(run, data.Origin, width / 2 + shoulder + 0.1, spacing, lift, g, ref incomplete);
                        }
                        if (style.Rail)
                        {
                            // deux rails en acier, un peu surélevés (sur les traverses)
                            var steel = new Color(0.62f, 0.62f, 0.65f);
                            float gauge = width > 3.3f ? 0.72f : 0.5f; // demi-écartement (voie normale / métrique)
                            float top = lift + (style.Kind == Surface.Ballast ? 0.18f : 0.03f);
                            Ribbon(run, data.Origin, -gauge - 0.08f, -gauge + 0.08f, top, steel, Surface.Steel, g);
                            Ribbon(run, data.Origin, gauge - 0.08f, gauge + 0.08f, top, steel, Surface.Steel, g);
                        }
                    }
            }
        }
        data.Vertices = verts.ToArray(); data.Normals = norms.ToArray(); data.Colors = cols.ToArray(); data.Indices = idx.ToArray();
        data.UVs = uvs.ToArray(); data.UV2s = uv2s.ToArray();
        data.Incomplete = incomplete;
        return data;
    }

    readonly record struct Geom(List<Vector3> Verts, List<Vector3> Norms, List<Color> Cols, List<Vector2> Uvs, List<Vector2> Uv2s, List<int> Idx);

    /// <summary>Découpe une suite de points en portions au sol / sur un pont (le point de jonction appartient aux deux).</summary>
    static IEnumerable<(List<Pt> run, bool bridge)> Sections(List<Pt> run)
    {
        int start = 0;
        for (int i = 2; i <= run.Count; i++) // segment (i-1, i) : même portion que le précédent ?
        {
            if (i < run.Count && SegBridge(i) == SegBridge(start + 1)) continue;
            yield return (run.GetRange(start, i - start), SegBridge(start + 1));
            start = i - 1;
        }
        // un segment est « sur le pont » si ses deux extrémités le sont
        bool SegBridge(int k) => k < run.Count && run[k - 1].bridge && run[k].bridge;
    }

    /// <summary>Extrémités des voies carrossables d'une tuile (carrefours, raccords), indexées par cases de 20 m.</summary>
    sealed class Junctions
    {
        const double Cell = 20;
        readonly Dictionary<(long, long, long), List<Vec3d>> _cells = new();

        public static Junctions Of(List<Mvt.Feature> segments, int z, int x, int y)
        {
            var j = new Junctions();
            foreach (var f in segments)
            {
                var st = StyleFor(f.Str("subtype"), f.Str("class"));
                if (st == null || st.Value.Rail || st.Value.Priority < 1) continue;
                foreach (var p in f.Parts)
                {
                    if (p.Length < 4) continue;
                    if (InTile(p[0], p[1])) j.Add(Dir(z, x, y, p[0], p[1]) * Geo.Radius);
                    if (InTile(p[^2], p[^1])) j.Add(Dir(z, x, y, p[^2], p[^1]) * Geo.Radius);
                }
            }
            return j;
        }

        static (long, long, long) Key(Vec3d p) => ((long)Math.Floor(p.X / Cell), (long)Math.Floor(p.Y / Cell), (long)Math.Floor(p.Z / Cell));

        void Add(Vec3d p)
        {
            var k = Key(p);
            if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<Vec3d>();
            l.Add(p);
        }

        IEnumerable<Vec3d> Around(Vec3d p, double r)
        {
            var (kx, ky, kz) = Key(p);
            int c = (int)Math.Ceiling(r / Cell);
            for (long a = -c; a <= c; a++) for (long b = -c; b <= c; b++) for (long d = -c; d <= c; d++)
                if (_cells.TryGetValue((kx + a, ky + b, kz + d), out var l))
                    foreach (var q in l) if ((q - p).Length() <= r) yield return q;
        }

        /// <summary>Distance (m) à l'extrémité de voie la plus proche, plafonnée à <paramref name="max"/>.</summary>
        public double Nearest(Vec3d dir, double max)
        {
            var p = dir * Geo.Radius;
            double best = max;
            foreach (var q in Around(p, max)) best = Math.Min(best, (q - p).Length());
            return best;
        }

        public int Count(Vec3d dir, double r)
        {
            int n = 0;
            foreach (var _ in Around(dir * Geo.Radius, r)) n++;
            return n;
        }
    }

    /// <summary>Emprise au sol des routes et voies ferrées d'une tuile (hors ponts), pour placer les piles des ponts.</summary>
    sealed class Corridors
    {
        const double Cell = 10;
        readonly Dictionary<(long, long, long), List<(Vec3d p, double half)>> _cells = new();

        public static Corridors Of(List<Mvt.Feature> segments, int z, int x, int y)
        {
            var c = new Corridors();
            var tunnels = new List<Range>(); var bridges = new List<Range>();
            foreach (var f in segments)
            {
                var st = StyleFor(f.Str("subtype"), f.Str("class"));
                if (st == null || !st.Value.Rail && st.Value.Priority < 1) continue;
                tunnels.Clear(); bridges.Clear();
                ParseRules(f, out bool under, tunnels, bridges, out double w);
                if (under || bridges.Count > 0) continue;
                double half = (double.IsNaN(w) ? st.Value.Width : Math.Clamp(w, 1, 30)) / 2;
                foreach (var part in f.Parts)
                    for (int i = 0; i + 3 < part.Length; i += 2)
                    {
                        var a = Dir(z, x, y, part[i], part[i + 1]) * Geo.Radius;
                        var b = Dir(z, x, y, part[i + 2], part[i + 3]) * Geo.Radius;
                        int steps = Math.Max(1, (int)Math.Ceiling((b - a).Length() / 2));
                        for (int s = 0; s <= steps; s++) c.Add(a + (b - a) * ((double)s / steps), half);
                    }
            }
            return c;
        }

        static (long, long, long) Key(Vec3d p) => ((long)Math.Floor(p.X / Cell), (long)Math.Floor(p.Y / Cell), (long)Math.Floor(p.Z / Cell));

        void Add(Vec3d p, double half)
        {
            var k = Key(p);
            if (!_cells.TryGetValue(k, out var l)) _cells[k] = l = new List<(Vec3d, double)>();
            l.Add((p, half));
        }

        /// <summary>Vrai si aucune voie ne passe à moins de <paramref name="margin"/> m (plus sa demi-largeur) de p.</summary>
        public bool Free(Vec3d p, double margin)
        {
            var (kx, ky, kz) = Key(p);
            for (long a = -2; a <= 2; a++) for (long b = -2; b <= 2; b++) for (long d = -2; d <= 2; d++)
                if (_cells.TryGetValue((kx + a, ky + b, kz + d), out var l))
                    foreach (var (q, half) in l) if ((q - p).Length() < half + margin) return false;
            return true;
        }
    }

    /// <summary>Point d'une voie posée : direction, hauteur de la chaussée, hauteur du sol, sur un tablier de pont.</summary>
    readonly record struct Pt(Vec3d dir, double h, double ground, bool bridge);

    static bool InTile(double fx, double fy) => fx > 0.0005 && fx < 0.9995 && fy > 0.0005 && fy < 0.9995;

    static Color Shade(Color c, float k) => new(c.R * k, c.G * k, c.B * k);

    /// <summary>Polyligne → suites de points (direction, hauteur) rééchantillonnées, coupées aux tunnels.</summary>
    /// <param name="offset">distance (m) entre le début du tronçon complet et le début de ce morceau</param>
    /// <param name="total">longueur du tronçon complet (m) ; 0 = ce morceau seul</param>
    /// <param name="decks">hauteur des culées de chaque pont (même ordre que <paramref name="bridges"/>)</param>
    static List<List<Pt>> BuildLine(double[] part, int z, int x, int y, double offset, double total,
        List<Range> tunnels, List<Range> bridges, (double ha, double hb, double ta, double tb)[] decks, double spacing, double step, ref bool incomplete)
    {
        int n = part.Length / 2;
        var dirs = new Vec3d[n];
        var cum = new double[n];
        for (int i = 0; i < n; i++)
        {
            dirs[i] = Dir(z, x, y, part[2 * i], part[2 * i + 1]);
            if (i > 0) cum[i] = cum[i - 1] + (dirs[i] - dirs[i - 1]).Length() * Geo.Radius;
        }
        if (total <= 0) { offset = 0; total = cum[n - 1]; }
        total = Math.Max(total, 1e-6);
        // Points rééchantillonnés avec leur position relative (0..1) le long du tronçon
        var pts = new List<(Vec3d dir, double t)>();
        for (int i = 0; i < n; i++)
        {
            if (i > 0)
            {
                double len = cum[i] - cum[i - 1];
                int steps = Math.Max(1, (int)Math.Ceiling(len / step));
                for (int s = 1; s < steps; s++)
                {
                    double a = (double)s / steps;
                    pts.Add(((dirs[i - 1] * (1 - a) + dirs[i] * a).Normalized(), (offset + cum[i - 1] + len * a) / total));
                }
            }
            pts.Add((dirs[i], (offset + cum[i]) / total));
        }
        var heights = new double[pts.Count];
        for (int i = 0; i < pts.Count; i++) heights[i] = Ground(pts[i].dir, spacing, ref incomplete);
        var ground = (double[])heights.Clone();
        var onDeck = new bool[pts.Count]; // sous un tablier de pont (une seule portion continue par pont)

        // Ponts : tablier droit entre les deux culées (au-dessus de l'eau ou de la vallée). Les hauteurs des culées
        // sont calculées une fois pour le tronçon entier : les morceaux voisins se raccordent.
        for (int k = 0; k < bridges.Count; k++)
        {
            var br = bridges[k];
            double ha, hb, ta = br.A, tb = br.B;
            if (decks != null) { (ha, hb, ta, tb) = decks[k]; if (double.IsNaN(ha)) continue; } // pont sans creux : posé au sol
            else
            {
                // tronçon non recomposé (pont sur toute sa longueur) : culées = extrémités de ce morceau
                int i0 = -1, i1 = -1;
                for (int i = 0; i < pts.Count; i++)
                    if (pts[i].t >= br.A - 1e-6 && pts[i].t <= br.B + 1e-6) { if (i0 < 0) i0 = i; i1 = i; }
                if (i0 < 0 || i1 <= i0) continue;
                ha = heights[i0]; hb = heights[i1]; ta = pts[i0].t; tb = pts[i1].t;
            }
            if (decks == null && Clearance(pts, heights, ha, hb, ta, tb) < MinBridgeClearance) continue; // voir DeckHeights
            for (int i = 0; i < pts.Count; i++)
            {
                double t = pts[i].t;
                if (t < ta || t > tb) continue;
                double a = (t - ta) / Math.Max(tb - ta, 1e-9);
                heights[i] = Math.Max(ha * (1 - a) + hb * a, heights[i] + 0.3);
                onDeck[i] = true;
            }
        }

        var runs = new List<List<Pt>>();
        List<Pt> cur = null;
        for (int i = 0; i < pts.Count; i++)
        {
            if (tunnels.Count > 0 && In(tunnels, pts[i].t)) { cur = null; continue; }
            if (cur == null) { cur = new List<Pt>(); runs.Add(cur); }
            cur.Add(new Pt(pts[i].dir, heights[i], ground[i], onDeck[i]));
        }
        runs.RemoveAll(r => r.Count < 2);
        return runs;
    }

    // --- Recomposition d'un tronçon coupé par les bords de tuiles ---

    /// <summary>Morceau d'un tronçon, découpé au carré de sa tuile (sans la marge), en coordonnées de tuile.</summary>
    sealed class Piece
    {
        public double[] P;      // (x, y) en fraction de la tuile X, Y
        public Vec3d[] D;       // mêmes points sur la sphère
        public double Len, Offset;
        public bool Mine;       // morceau de la tuile en cours de construction
    }

    const int MaxStitchTiles = 25;

    static Piece MakePiece(int z, int x, int y, double[] p, bool mine)
    {
        int n = p.Length / 2;
        var d = new Vec3d[n];
        double len = 0;
        for (int i = 0; i < n; i++)
        {
            d[i] = Dir(z, x, y, p[2 * i], p[2 * i + 1]);
            if (i > 0) len += (d[i] - d[i - 1]).Length() * Geo.Radius;
        }
        return new Piece { P = p, D = d, Len = len, Mine = mine };
    }

    /// <summary>Rassemble les morceaux du même tronçon (même identifiant) dans les tuiles voisines, de proche en proche.
    /// Null si une tuile manque ou si le tronçon est trop long.</summary>
    static List<Piece> Stitch(Mvt.Feature f, int z, int x, int y, Func<int, int, List<Mvt.Feature>> neighbours)
    {
        string id = f.Str("id");
        if (id == null || neighbours == null) return null;
        int n = 1 << z;
        var pieces = new List<Piece>();
        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int, int)>();
        queue.Enqueue((x, y));
        while (queue.Count > 0)
        {
            var (tx, ty) = queue.Dequeue();
            if (!seen.Add((tx, ty))) continue;
            if (seen.Count > MaxStitchTiles) return null;
            bool own = tx == x && ty == y;
            var feats = own ? new List<Mvt.Feature> { f } : neighbours(tx, ty);
            if (feats == null) return null;
            foreach (var g in feats)
            {
                if (!own && g.Str("id") != id) continue;
                foreach (var part in g.Parts)
                {
                    if (part.Length < 4) continue;
                    foreach (var c in ClipUnit(part)) pieces.Add(MakePiece(z, tx, ty, c, own));
                    // une extrémité dans la marge : le tronçon continue dans la tuile voisine
                    foreach (int e in new[] { 0, part.Length - 2 })
                    {
                        double px = part[e], py = part[e + 1];
                        int dx = px < -1e-4 ? -1 : px > 1 + 1e-4 ? 1 : 0, dy = py < -1e-4 ? -1 : py > 1 + 1e-4 ? 1 : 0;
                        if ((dx != 0 || dy != 0) && ty + dy >= 0 && ty + dy < n) queue.Enqueue(((tx + dx + n) % n, ty + dy));
                    }
                }
            }
        }
        return pieces.Count > 0 ? pieces : null;
    }

    /// <summary>Met les morceaux bout à bout et calcule la distance de chacun depuis le début du tronçon.</summary>
    static void Order(List<Piece> pieces)
    {
        double tol = 4.0 / Geo.Radius; // les morceaux de deux tuiles se touchent à ~1 m près (arrondi des tuiles)
        var ordered = new List<Piece>();
        var left = new List<Piece>(pieces);
        // tête : le morceau dont le début ne prolonge aucun autre
        Piece cur = left.Find(p => !left.Exists(q => q != p && (q.D[^1] - p.D[0]).Length() < tol)) ?? left[0];
        while (cur != null)
        {
            left.Remove(cur);
            ordered.Add(cur);
            var end = cur.D[^1];
            cur = left.Find(p => (p.D[0] - end).Length() < tol);
        }
        ordered.AddRange(left); // morceaux non raccordés (rare) : à la suite
        double off = 0;
        foreach (var p in ordered) { p.Offset = off; off += p.Len; }
        pieces.Clear();
        pieces.AddRange(ordered);
    }

    /// <summary>Découpe une polyligne (fractions de tuile) au carré [0, 1]² (Liang-Barsky).</summary>
    static List<double[]> ClipUnit(double[] p)
    {
        var res = new List<double[]>();
        List<double> cur = null;
        for (int i = 0; i + 3 < p.Length; i += 2)
        {
            double x0 = p[i], y0 = p[i + 1], dx = p[i + 2] - x0, dy = p[i + 3] - y0;
            double t0 = 0, t1 = 1;
            bool ok = Clip(-dx, x0, ref t0, ref t1) && Clip(dx, 1 - x0, ref t0, ref t1)
                   && Clip(-dy, y0, ref t0, ref t1) && Clip(dy, 1 - y0, ref t0, ref t1);
            if (!ok) { if (cur != null) { res.Add(cur.ToArray()); cur = null; } continue; }
            if (cur != null && t0 > 0) { res.Add(cur.ToArray()); cur = null; }
            if (cur == null) { cur = new List<double> { x0 + t0 * dx, y0 + t0 * dy }; }
            cur.Add(x0 + t1 * dx); cur.Add(y0 + t1 * dy);
            if (t1 < 1) { res.Add(cur.ToArray()); cur = null; }
        }
        if (cur != null) res.Add(cur.ToArray());
        return res;

        static bool Clip(double q, double r, ref double t0, ref double t1)
        {
            if (Math.Abs(q) < 1e-15) return r >= 0;
            double t = r / q;
            if (q < 0) { if (t > t1) return false; if (t > t0) t0 = t; }
            else { if (t < t0) return false; if (t < t1) t1 = t; }
            return true;
        }
    }

    /// <summary>Point du tronçon recomposé à <paramref name="m"/> mètres de son début.</summary>
    static Vec3d PointAt(List<Piece> chain, double m)
    {
        foreach (var p in chain)
        {
            if (m > p.Offset + p.Len && p != chain[^1]) continue;
            double r = Math.Clamp(m - p.Offset, 0, p.Len);
            for (int i = 1; i < p.D.Length; i++)
            {
                double s = (p.D[i] - p.D[i - 1]).Length() * Geo.Radius;
                if (r <= s || i == p.D.Length - 1)
                {
                    double a = s > 0 ? Math.Clamp(r / s, 0, 1) : 0;
                    return (p.D[i - 1] * (1 - a) + p.D[i] * a).Normalized();
                }
                r -= s;
            }
        }
        return chain[^1].D[^1];
    }

    /// <summary>Plus grand creux (m) entre le tablier droit (culées <paramref name="ha"/>, <paramref name="hb"/>) et le relief.</summary>
    static double Clearance(List<(Vec3d dir, double t)> pts, double[] heights, double ha, double hb, double ta, double tb)
    {
        double clearance = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            double t = pts[i].t;
            if (t < ta || t > tb) continue;
            double a = (t - ta) / Math.Max(tb - ta, 1e-9);
            clearance = Math.Max(clearance, ha * (1 - a) + hb * a - heights[i]);
        }
        return clearance;
    }

    /// <summary>Hauteur des deux culées de chaque pont. Les limites OSM tombent souvent un peu avant le haut du talus :
    /// on remonte jusqu'à 30 m vers l'extérieur tant que le terrain monte.</summary>
    static (double, double, double, double)[] DeckHeights(List<Piece> chain, List<Range> bridges, double spacing, ref bool incomplete)
    {
        double total = chain[^1].Offset + chain[^1].Len;
        var res = new (double, double, double, double)[bridges.Count];
        for (int k = 0; k < bridges.Count; k++)
        {
            double ma = bridges[k].A * total, mb = bridges[k].B * total;
            // Les culées vont jusqu'en haut du talus : la portion entre la limite OSM et le haut du talus fait partie
            // du tablier (sinon la route plonge dans le creux avant de remonter sur le pont).
            var (ha, ma2) = Abutment(ma, -1, ref incomplete);
            var (hb, mb2) = Abutment(mb, +1, ref incomplete);
            // Le relief (30 m, lissé) ne montre souvent pas le creux franchi : passage supérieur au-dessus d'une autre rue,
            // petit ruisseau… Le tablier ne serait alors qu'une feuille flottant à 30 cm du sol, qui traverse les voitures
            // et le personnage. Sans vrai creux (moins de 2 m), la voie reste posée au sol. Mesuré sur tout le pont :
            // tous les morceaux du tronçon décident pareil (pas de marche en bord de tuile).
            double clearance = 0;
            int n = Math.Max(2, (int)Math.Ceiling((mb2 - ma2) / Math.Max(spacing, 5)));
            for (int i = 1; i < n; i++)
            {
                double a = (double)i / n;
                clearance = Math.Max(clearance, ha * (1 - a) + hb * a - Ground(PointAt(chain, ma2 + (mb2 - ma2) * a), spacing, ref incomplete));
            }
            res[k] = clearance < MinBridgeClearance ? (double.NaN, double.NaN, 0, 0) : (ha, hb, ma2 / total, mb2 / total);
        }
        return res;

        // Haut du talus : point le plus haut à moins de 40 m vers l'extérieur, en suivant le terrain tant qu'il ne
        // redescend pas nettement (un petit creux du relief lissé n'arrête pas la recherche).
        (double h, double m) Abutment(double m, int dir, ref bool inc)
        {
            double best = Ground(PointAt(chain, m), spacing, ref inc), bestM = m;
            for (double d = 2; d <= 40; d += 2)
            {
                double mm = m + dir * d;
                if (mm < 0 || mm > total) break;
                double h = Ground(PointAt(chain, mm), spacing, ref inc);
                if (h > best + 0.05) { best = h; bestM = mm; }
                else if (h < best - 0.5) break;
            }
            return (best, bestM);
        }
    }

    /// <summary>Découpe le couloir d'une voie en quadrilatères d'une quinzaine de mètres (non bloquants, « Clears »).</summary>
    static void Clear(List<Pt> run, double half, List<Obstacles.Shape> outList)
    {
        int n = run.Count;
        if (n < 2) return;
        int i = 0;
        while (i < n - 1)
        {
            int j = i + 1;
            while (j < n - 1 && (run[j].dir - run[i].dir).Length() * Geo.Radius < 15) j++;
            var (da, ha, _, _) = run[i]; var (db, hb, _, _) = run[j];
            var t = db - da;
            if (t.Length() > 1e-12)
            {
                var side = t.Normalized().Cross(da).Normalized() * half;
                var along = t.Normalized() * 0.5; // léger recouvrement entre morceaux
                var a = da * (Geo.Radius + ha) - along; var b = db * (Geo.Radius + hb) + along;
                var ring = new[] { a - side, b - side, b + side, a + side };
                var sh = Obstacles.Shape.Polygon(ring, null, Math.Min(ha, hb) - 2, Math.Max(ha, hb) + 3);
                sh.Solid = false;
                outList.Add(sh);
            }
            i = j;
        }
    }

    /// <summary>
    /// Murets le long d'une route : côté amont quand le terrain dépasse la chaussée de 0,7 à 6 m (mur de soutènement
    /// face à la route, de la chaussée jusqu'au-dessus du talus), côté aval quand la route est 1 à 3,2 m au-dessus du
    /// terrain (remblai : mur tourné vers l'extérieur, du sol jusqu'au bord de la chaussée). Au-delà, c'est un pont
    /// ou une falaise : rien.
    /// </summary>
    static void SideWalls(List<Pt> run, Vec3d origin, double off, double spacing, float lift, Geom g, ref bool incomplete)
    {
        int n = run.Count;
        if (n < 2) return;
        var gh = new double[n];
        var sides = new Vec3d[n];
        var stone = Stone; stone.A = (float)Surface.StoneWall / 10f;
        double h0 = run[0].h;
        foreach (int s in new[] { -1, 1 })
        {
            for (int i = 0; i < n; i++)
            {
                var up = run[i].dir;
                var tangent = (run[Math.Min(i + 1, n - 1)].dir - run[Math.Max(i - 1, 0)].dir).Normalized();
                sides[i] = tangent.Cross(up).Normalized(); // vers la droite
                var p = (up * Geo.Radius + sides[i] * (off * s)).Normalized();
                Geo.ToLatLon(p, out double la, out double lo);
                float hv = HeightProvider.Sample(la, lo, spacing, out bool water, out bool pending);
                if (pending) incomplete = true;
                gh[i] = water ? double.NaN : hv;
            }
            double along = 0;
            for (int i = 0; i + 1 < n; i++)
            {
                double seg = (run[i + 1].dir - run[i].dir).Length() * Geo.Radius;
                double c0 = gh[i] - run[i].h, c1 = gh[i + 1] - run[i + 1].h;
                bool cut = c0 > 0.7 && c1 > 0.7 && c0 < 6 && c1 < 6;
                bool fill = c0 < -1.0 && c1 < -1.0 && c0 > -3.2 && c1 > -3.2;
                if (cut || fill)
                {
                    double b0 = cut ? run[i].h + lift - 0.3 : gh[i] - 0.3, b1 = cut ? run[i + 1].h + lift - 0.3 : gh[i + 1] - 0.3;
                    double t0 = cut ? Math.Min(gh[i], run[i].h + 3.5) + 0.15 : run[i].h + lift + 0.08;
                    double t1 = cut ? Math.Min(gh[i + 1], run[i + 1].h + 3.5) + 0.15 : run[i + 1].h + lift + 0.08;
                    Vec3d P(int k, double h) => run[k].dir * (Geo.Radius + h) + sides[k] * (off * s);
                    // face visible : vers la route (talus) ou vers l'extérieur (remblai) ; sens des triangles de Godot
                    // (horaire vu de face) : B0, B1, T0 regarde vers la gauche de la route
                    var facing = sides[i] * (cut ? -s : s);
                    bool leftFacing = cut ? s > 0 : s < 0;
                    var nrm = facing.ToVector3().Normalized();
                    int k0 = g.Verts.Count;
                    void V(Vec3d w, double hh, double a)
                    {
                        g.Verts.Add((w - origin).ToVector3()); g.Norms.Add(nrm); g.Cols.Add(stone);
                        g.Uvs.Add(new Vector2((float)(hh - h0), (float)a)); g.Uv2s.Add(new Vector2(1, 0));
                    }
                    V(P(i, b0), b0, along); V(P(i + 1, b1), b1, along + seg); V(P(i, t0), t0, along); V(P(i + 1, t1), t1, along + seg);
                    // k0 = B0, k0+1 = B1, k0+2 = T0, k0+3 = T1
                    if (leftFacing) { g.Idx.Add(k0); g.Idx.Add(k0 + 1); g.Idx.Add(k0 + 2); g.Idx.Add(k0 + 2); g.Idx.Add(k0 + 1); g.Idx.Add(k0 + 3); }
                    else { g.Idx.Add(k0 + 2); g.Idx.Add(k0 + 3); g.Idx.Add(k0); g.Idx.Add(k0); g.Idx.Add(k0 + 3); g.Idx.Add(k0 + 1); }
                }
                along += seg;
            }
        }
    }

    /// <param name="mark">intensité des marquages par point (0 aux carrefours), dans UV2.y ; null = 1</param>
    static void Ribbon(List<Pt> run, Vec3d origin, float left, float right, float lift, Color col, Surface kind, Geom g, float[] mark = null)
    {
        var (verts, norms, cols, uvs, uv2s, idx) = (g.Verts, g.Norms, g.Cols, g.Uvs, g.Uv2s, g.Idx);
        int n = run.Count, start = verts.Count;
        col.A = (float)kind / 10f;
        double along = 0;
        for (int i = 0; i < n; i++)
        {
            var up = run[i].dir;
            var (side, miter) = Side(run, i);
            var center = up * (Geo.Radius + run[i].h + lift);
            var nrm = up.ToVector3();
            verts.Add((center + side * (left * miter) - origin).ToVector3());
            verts.Add((center + side * (right * miter) - origin).ToVector3());
            norms.Add(nrm); norms.Add(nrm);
            cols.Add(col); cols.Add(col);
            if (i > 0) along += (run[i].dir - run[i - 1].dir).Length() * Geo.Radius;
            uvs.Add(new Vector2(0, (float)along)); uvs.Add(new Vector2(1, (float)along));
            var uv2 = new Vector2(Math.Abs(right - left), mark == null ? 1 : mark[i]);
            uv2s.Add(uv2); uv2s.Add(uv2);
        }
        for (int i = 0; i < n - 1; i++)
        {
            int k = start + 2 * i;
            idx.Add(k); idx.Add(k + 2); idx.Add(k + 1);
            idx.Add(k + 1); idx.Add(k + 2); idx.Add(k + 3);
        }
    }

    /// <summary>Direction vers la droite de la voie au point i (bissectrice) et facteur d'onglet : dans un virage, le
    /// ruban garde sa largeur au lieu de s'étrangler (limité à ×2 dans les épingles).</summary>
    static (Vec3d side, double miter) Side(List<Pt> run, int i)
    {
        int n = run.Count;
        var up = run[i].dir;
        var t1 = i > 0 ? (run[i].dir - run[i - 1].dir).Normalized() : (run[1].dir - run[0].dir).Normalized();
        var t2 = i < n - 1 ? (run[i + 1].dir - run[i].dir).Normalized() : t1;
        var tangent = (t1 + t2);
        if (tangent.Length() < 1e-9) tangent = t2;
        tangent = tangent.Normalized();
        double cos = Math.Max(tangent.Dot(t1), 0.5);
        return (tangent.Cross(up).Normalized(), 1 / cos);
    }

    /// <summary>Quadrilatère a-b-c-d (dans l'ordre du pourtour) dont la face avant regarde vers <paramref name="normal"/>
    /// (Godot : sommets dans le sens horaire vu de face).</summary>
    static void Quad(Geom g, Vec3d origin, Vec3d a, Vec3d b, Vec3d c, Vec3d d, Vec3d normal, Color col,
        Vector2 ua, Vector2 ub, Vector2 uc, Vector2 ud)
    {
        int k = g.Verts.Count;
        var nrm = normal.ToVector3().Normalized();
        foreach (var (p, uv) in new[] { (a, ua), (b, ub), (c, uc), (d, ud) })
        {
            g.Verts.Add((p - origin).ToVector3()); g.Norms.Add(nrm); g.Cols.Add(col);
            g.Uvs.Add(uv); g.Uv2s.Add(new Vector2(1, 1));
        }
        bool flip = (b - a).Cross(c - a).Dot(normal) > 0;
        if (!flip) { g.Idx.Add(k); g.Idx.Add(k + 1); g.Idx.Add(k + 2); g.Idx.Add(k); g.Idx.Add(k + 2); g.Idx.Add(k + 3); }
        else { g.Idx.Add(k); g.Idx.Add(k + 2); g.Idx.Add(k + 1); g.Idx.Add(k); g.Idx.Add(k + 3); g.Idx.Add(k + 2); }
    }

    /// <summary>
    /// Structure d'un pont sous le tablier (une portion de voie marquée « bridge ») : bords en béton du dessous du
    /// tablier jusqu'en haut du parapet, sous-face, et piles jusqu'au sol tous les ~25 m là où le vide dépasse 3 m.
    /// </summary>
    static void BridgeStructure(List<Pt> run, Vec3d origin, float half, float lift, Geom g, Corridors below)
    {
        int n = run.Count;
        if (n < 2) return;
        var concrete = Concrete; concrete.A = (float)Surface.Concrete / 10f;
        const double Thick = 0.9;            // épaisseur du tablier (m)
        double top = lift + 1.0; // parapet
        // sous-face
        // sous-face : face avant vers le bas (bords inversés = triangles dans l'autre sens), visible depuis la rue
        // (normale vers le haut : éclairée comme le dessus, assombrie, sinon presque noire sous la seule lumière ambiante)
        Ribbon(run, origin, half, -half, lift - (float)Thick, Shade(Concrete, 0.7f), Surface.Concrete, g);
        var sides = new Vec3d[n]; var miters = new double[n];
        for (int i = 0; i < n; i++) (sides[i], miters[i]) = Side(run, i);
        double along = 0;
        for (int i = 0; i + 1 < n; i++)
        {
            double seg = (run[i + 1].dir - run[i].dir).Length() * Geo.Radius;
            foreach (int s in new[] { -1, 1 })
            {
                Vec3d P(int k, double dh) => run[k].dir * (Geo.Radius + run[k].h + dh) + sides[k] * (half * miters[k] * s);
                float b = (float)-Thick, t = (float)(top - lift);
                // UV : (hauteur, longueur) comme les murets
                Quad(g, origin, P(i, lift - Thick), P(i + 1, lift - Thick), P(i + 1, top), P(i, top), sides[i] * s, concrete,
                    new Vector2(b, (float)along), new Vector2(b, (float)(along + seg)), new Vector2(t, (float)(along + seg)), new Vector2(t, (float)along));
            }
            along += seg;
        }
        // Piles : murs-piles de béton, perpendiculaires à la voie, tous les ~25 m. Une pile ne se pose jamais sur
        // une route ou une voie ferrée en dessous : on la décale de quelques mètres, ou on s'en passe.
        var cum = new double[n];
        for (int i = 1; i < n; i++) cum[i] = cum[i - 1] + (run[i].dir - run[i - 1].dir).Length() * Geo.Radius;
        double total = cum[n - 1];
        double pw = Math.Max(half * 0.7, 0.8);
        for (double target = Math.Min(12, total / 2); target < total - 6; target += 25)
            for (int tryK = 0; tryK <= 10; tryK++)
            {
                double m = target + (tryK % 2 == 0 ? 1 : -1) * 2 * ((tryK + 1) / 2);
                if (m < 4 || m > total - 4) continue;
                int i = 0;
                while (i < n - 2 && cum[i + 1] < m) i++;
                double a = (m - cum[i]) / Math.Max(cum[i + 1] - cum[i], 1e-9);
                var dir = (run[i].dir * (1 - a) + run[i + 1].dir * a).Normalized();
                double h = run[i].h * (1 - a) + run[i + 1].h * a;
                double gnd = run[i].ground * (1 - a) + run[i + 1].ground * a;
                if (h - gnd <= 3) continue;
                var tangent = (run[i + 1].dir - run[i].dir).Normalized();
                var side = tangent.Cross(dir).Normalized();
                bool free = true;
                for (double e = -pw; e <= pw + 1e-9 && free; e += Math.Max(pw / 3, 0.5))
                    free = below == null || below.Free(dir * Geo.Radius + side * e, 1.2);
                if (!free) continue;
                Pier(dir, tangent, h + lift - Thick, gnd - 0.5);
                break;
            }

        void Pier(Vec3d up, Vec3d tangent, double hTop, double hBot)
        {
            var side = tangent.Cross(up).Normalized();
            tangent = up.Cross(side).Normalized();
            double w = Math.Max(half * 0.7, 0.8), d = 0.6;
            float hb = (float)(hBot - hTop), ht = 0;
            var faces = new[] { (side, w, d, tangent), (-side, w, d, -tangent), (tangent, d, w, -side), (-tangent, d, w, side) };
            foreach (var (nrm, off, ext, dirAlong) in faces)
            {
                // face à la distance off dans la direction nrm, large de 2·ext dans la direction dirAlong
                Vec3d Q(double e, double hh) => up * (Geo.Radius + hh) + nrm * off + dirAlong * e;
                float L = (float)(2 * ext);
                Quad(g, origin, Q(-ext, hBot), Q(ext, hBot), Q(ext, hTop), Q(-ext, hTop), nrm, concrete,
                    new Vector2(hb, 0), new Vector2(hb, L), new Vector2(ht, L), new Vector2(ht, 0));
            }
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
                col = st.Value.Rail ? new Color(0.50f, 0.30f, 0.24f) : st.Value.Priority >= 3 ? new Color(0.78f, 0.74f, 0.64f) : new Color(0.62f, 0.60f, 0.56f);
            }
            foreach (var part in f.Parts)
            {
                if (part.Length < 4) continue;
                data.Segments++;
                foreach (var run in BuildLine(part, z, x, y, 0, 0, none, none, null, 120, 16, ref incomplete))
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
