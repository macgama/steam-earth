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
    public Vector2[] Uv, Uv2;            // UV : (abscisse le long du mur, étage × 3 + hauteur dans l'étage) ; UV2 : (motif WindowStyle, aléa)
    public int[] Indices;
    public int[] PerArea;                // nombre de bâtiments par zone construite
    public List<Obstacles.Shape> Footprints; // murs solides pour le personnage (voir Obstacles)
    public List<BuildingInfo> Infos;     // bâtiments où l'on peut entrer (intérieurs générés près du joueur)
    public bool Incomplete;              // relief provisoire (téléchargement en cours) : à reconstruire
    public int DataVersion;
    public bool Swiss;                   // construite avec les vrais bâtiments swissBUILDINGS3D
}

/// <summary>Bâtiment où l'on peut entrer : ce qu'il faut pour générer son intérieur (InteriorBuilder).</summary>
public sealed class BuildingInfo
{
    public string Id;
    public BuildingKind Kind;
    public string Name;                  // nom du bâtiment (OSM) ou de son commerce principal
    public List<(string name, string category)> Places; // commerces, cabinets… situés dedans (Overture Places)
    public LocalFrame Frame;             // repère local (origine = centre du bâtiment au niveau de la mer)
    public List<V2> Ring;                // contour (sens direct)
    public List<List<V2>> Holes;         // cours intérieures (sens indirect)
    public double Area, BoundRadius;
    public double BaseH, Floor0, Eave, Top; // altitudes : bas des fondations, sol du rez-de-chaussée, plafond du dernier étage, toit
    public int Floors;                   // nombre de niveaux
    public double LevelH;                // hauteur d'un niveau (dalle comprise)
    public double Seed;
    public WindowStyle Style;
    public Color WallColor;
    // Porte d'entrée : ouverture DoorA–DoorB dans le mur, de Floor0 à Floor0 + DoorH ; DoorOut = vers la rue
    public V2 DoorA, DoorB, DoorOut;
    public double DoorH, DoorGround;
    /// <summary>Volume plein du bâtiment pour les collisions (désactivé quand l'intérieur est construit).</summary>
    public Obstacles.Shape Solid;
    public Vec3d World => Frame.C * Geo.Radius;
}

/// <summary>
/// Transforme les bâtiments Overture (contours OpenStreetMap, Microsoft, Google…) d'une tuile vectorielle de zoom 14
/// en volumes posés sur le relief. Chaque bâtiment reçoit une fonction (église, école, magasin… voir
/// <see cref="BuildingKinds"/>) d'après sa classe OSM, les commerces Overture Places qu'il contient, son nom et la zone
/// OSM où il se trouve ; elle décide de son aspect : clocher, coupole, vitrines et auvents, gradins et pelouse d'un
/// stade, motif des fenêtres (alignées sur les étages), couleur. Hauteur : « height », sinon nombre d'étages × 3 m,
/// sinon une valeur selon la fonction et la surface. Les « building_part » (bâtiments 3D détaillés d'OSM)
/// remplacent le contour de leur bâtiment. Les bâtiments où l'on peut entrer ont une porte côté rue.
/// </summary>
public static partial class BuildingBuilder
{
    public static readonly HashSet<string> Keys = new()
    {
        "id", "building_id", "has_parts", "is_underground", "height", "min_height", "num_floors", "min_floor",
        "roof_shape", "roof_height", "roof_color", "roof_material", "facade_color", "facade_material", "class", "subtype",
        "roof_orientation", "@name",
    };
    static readonly HashSet<string> PlaceKeys = new() { "basic_category", "taxonomy", "@name", "confidence" };
    static readonly HashSet<string> LandKeys = new() { "class", "subtype" };
    static readonly HashSet<string> LandClasses = new()
    {
        "school", "education", "kindergarten", "university", "college", "hospital", "clinic", "stadium", "religious",
    };

    /// <summary>Zone construite : cercle autour du centre d'une ville.</summary>
    public readonly record struct Area(Vec3d Dir, double Radius);

    const double R = Geo.Radius;
    const double Foundation = 6;     // m : les murs descendent sous le sol (le relief lointain est plus grossier)
    const double FloorHeight = 3;
    public const double FloorAboveGround = 0.3; // le rez-de-chaussée est un peu au-dessus du point le plus haut du terrain
                                                // (marge : le relief des bâtiments est lu à 4 m, celui du sol affiché à 0,5 m)

    /// <summary>Contour d'une tuile vectorielle (coordonnées de la tuile), avant la construction.</summary>
    sealed class Cand
    {
        public Mvt.Feature F;
        public bool IsPart, Drawn;
        public double[] Outer;
        public List<double[]> Holes;
        public double X0, Y0, X1, Y1, Cx, Cy;
        public BuildingKinds.PlaceSummary Places;
        public SwissBuildings3D.Building Swiss; // vrai bâtiment swisstopo (F = bâtiment OSM qui lui correspond, ou null)
        public double Weight;                  // aire (unités de tuile²) du bâtiment OSM retenu pour un bâtiment swisstopo
    }

    sealed class Ctx
    {
        public int Z, Tx, Ty;
        public double Spacing;
        public BuildingMeshData Data;
        public MeshAcc M;
        public List<Obstacles.Shape> Obs;
        public Dictionary<long, List<Cand>> Grid;
        public Dictionary<string, Mvt.Feature> Parents;
        public List<(double[] ring, string cls)> LandUse;
        public int LandZ, LandX, LandY;
        public bool Incomplete;
        public int Detail;           // QualityProfile.BuildingDetail lu au début de la construction (balcons à partir de 1)
    }

    const int GridN = 48;
    static long Cell(int gx, int gy) => ((long)gx << 32) ^ (uint)gy;

    public static BuildingMeshData Build(byte[] mvt, byte[] places, byte[] landUse, int landZ, int landX, int landY,
        int z, int tx, int ty, IReadOnlyList<Area> areas, double spacing, IReadOnlyList<SwissBuildings3D.Building> swiss = null)
    {
        var data = new BuildingMeshData { DataVersion = StreamedRaster.DataVersion, PerArea = new int[areas.Count], Infos = new() };
        PmTilesArchive.TileToLatLon(z, tx, ty, 0.5, 0.5, out double cla, out double clo);
        data.Origin = Geo.FromLatLon(cla, clo) * R;
        var ctx = new Ctx
        {
            Z = z, Tx = tx, Ty = ty, Spacing = spacing, Data = data, M = new MeshAcc(), Obs = new(),
            Grid = new(), Parents = new(), LandUse = new(), LandZ = landZ, LandX = landX, LandY = landY,
            Detail = GraphicsQuality.Current.BuildingDetail,
        };
        ctx.M.AutoUv = true; // tours, minarets : textures en mètres
        bool hasSwiss = swiss != null && swiss.Count > 0;
        data.Swiss = hasSwiss;
        if ((mvt != null && mvt.Length > 0) || hasSwiss)
        {
            var layers = mvt != null && mvt.Length > 0
                ? Mvt.Decode(mvt, new HashSet<string> { "building", "building_part" }, Keys)
                : new Dictionary<string, List<Mvt.Feature>>();
            var withParts = new HashSet<string>();
            if (layers.TryGetValue("building_part", out var parts))
                foreach (var p in parts) { var id = p.Str("building_id"); if (id != null) withParts.Add(id); }
            var cands = new List<Cand>();
            if (layers.TryGetValue("building", out var blds))
                foreach (var f in blds)
                {
                    if (f.Type != Mvt.GeomType.Polygon || f.Num("is_underground", 0) > 0) continue;
                    var id = f.Str("id");
                    bool hasParts = id != null && withParts.Contains(id);
                    if (hasParts) ctx.Parents[id] = f;
                    AddCands(f, false, !hasParts, cands, ctx); // les parties dessinent ce bâtiment
                }
            if (parts != null)
                foreach (var f in parts)
                {
                    if (f.Type != Mvt.GeomType.Polygon || f.Num("is_underground", 0) > 0) continue;
                    AddCands(f, true, true, cands, ctx);
                }
            if (hasSwiss) AddSwiss(swiss, cands, ctx); // les vrais bâtiments remplacent les contours OSM qu'ils recouvrent
            if (places != null && places.Length > 0) AssignPlaces(places, ctx);
            if (landUse != null && landUse.Length > 0)
            {
                var land = Mvt.Decode(landUse, new HashSet<string> { "land_use" }, LandKeys);
                if (land.TryGetValue("land_use", out var lu))
                    foreach (var f in lu)
                    {
                        var c = f.Str("class");
                        if (f.Type != Mvt.GeomType.Polygon || c == null || !LandClasses.Contains(c)) continue;
                        foreach (var ring in f.Parts) if (ring.Length >= 8) ctx.LandUse.Add((ring, c));
                    }
            }
            foreach (var cd in cands)
            {
                if (!cd.Drawn) continue;
                if (cd.Swiss != null) EmitSwiss(cd, areas, ctx);
                else Emit(cd, areas, ctx);
            }
        }
        var m = ctx.M;
        FrontFaces(m);
        data.Vertices = m.V.ToArray(); data.Normals = m.N.ToArray(); data.Colors = m.C.ToArray();
        data.Uv = m.Uv.ToArray(); data.Uv2 = m.Uv2.ToArray(); data.Indices = m.I.ToArray();
        data.Footprints = ctx.Obs;
        data.Incomplete = ctx.Incomplete;
        return data;
    }

    // ------------------------------------------------------------------ contours, lieux, zones

    static void AddCands(Mvt.Feature f, bool isPart, bool drawn, List<Cand> cands, Ctx ctx)
    {
        Cand cur = null;
        foreach (var ring in f.Parts)
        {
            if (ring.Length < 8) continue;
            double a = 0;
            for (int i = 0; i + 3 < ring.Length; i += 2) a += ring[i] * ring[i + 3] - ring[i + 2] * ring[i + 1];
            if (a > 0)
            {
                cur = new Cand { F = f, IsPart = isPart, Drawn = drawn, Outer = ring, Holes = new() };
                cur.X0 = cur.Y0 = double.MaxValue; cur.X1 = cur.Y1 = double.MinValue;
                int n = ring.Length / 2 - 1;
                for (int i = 0; i < n; i++)
                {
                    double x = ring[2 * i], y = ring[2 * i + 1];
                    cur.Cx += x; cur.Cy += y;
                    cur.X0 = Math.Min(cur.X0, x); cur.X1 = Math.Max(cur.X1, x); cur.Y0 = Math.Min(cur.Y0, y); cur.Y1 = Math.Max(cur.Y1, y);
                }
                cur.Cx /= n; cur.Cy /= n;
                cands.Add(cur);
                int gx0 = Math.Clamp((int)Math.Floor(cur.X0 * GridN), -2, GridN + 1), gx1 = Math.Clamp((int)Math.Floor(cur.X1 * GridN), -2, GridN + 1);
                int gy0 = Math.Clamp((int)Math.Floor(cur.Y0 * GridN), -2, GridN + 1), gy1 = Math.Clamp((int)Math.Floor(cur.Y1 * GridN), -2, GridN + 1);
                for (int gx = gx0; gx <= gx1; gx++)
                    for (int gy = gy0; gy <= gy1; gy++)
                    {
                        if (!ctx.Grid.TryGetValue(Cell(gx, gy), out var l)) ctx.Grid[Cell(gx, gy)] = l = new List<Cand>();
                        l.Add(cur);
                    }
            }
            else cur?.Holes.Add(ring);
        }
    }

    static bool InRing(double[] r, double px, double py)
    {
        bool inside = false;
        int n = r.Length / 2;
        for (int i = 0, j = n - 1; i < n; j = i++)
        {
            double xi = r[2 * i], yi = r[2 * i + 1], xj = r[2 * j], yj = r[2 * j + 1];
            if ((yi > py) != (yj > py) && px < (xj - xi) * (py - yi) / (yj - yi) + xi) inside = !inside;
        }
        return inside;
    }

    /// <summary>Contour (dessiné ou non) qui contient ce point de la tuile, sauf <paramref name="except"/>.</summary>
    static Cand At(Ctx ctx, double x, double y, Cand except = null, bool drawnOnly = false)
    {
        int gx = (int)Math.Floor(x * GridN), gy = (int)Math.Floor(y * GridN);
        if (!ctx.Grid.TryGetValue(Cell(gx, gy), out var l)) return null;
        foreach (var c in l)
        {
            if (c == except || (drawnOnly && !c.Drawn)) continue;
            if (x < c.X0 || x > c.X1 || y < c.Y0 || y > c.Y1 || !InRing(c.Outer, x, y)) continue;
            bool inHole = false;
            foreach (var h in c.Holes) if (InRing(h, x, y)) { inHole = true; break; }
            if (!inHole) return c;
        }
        return null;
    }

    /// <summary>Overture Places : chaque commerce, école, église… est rattaché au bâtiment qui le contient.</summary>
    static void AssignPlaces(byte[] places, Ctx ctx)
    {
        var layers = Mvt.Decode(places, new HashSet<string> { "place" }, PlaceKeys);
        if (!layers.TryGetValue("place", out var pl)) return;
        foreach (var f in pl)
        {
            if (f.Type != Mvt.GeomType.Point || f.Parts.Count == 0 || f.Parts[0].Length < 2) continue;
            if (f.Num("confidence", 1) < 0.45) continue;
            var c = At(ctx, f.Parts[0][0], f.Parts[0][1], null, true);
            if (c == null) continue;
            var tax = f.Str("taxonomy");
            string primary = JsonField(tax, "primary") ?? f.Str("basic_category");
            var hier = JsonArray(tax, "hierarchy");
            var strong = BuildingKinds.FromPlace(primary, hier, out bool shop, out bool food);
            c.Places ??= new BuildingKinds.PlaceSummary();
            c.Places.Total++;
            if (shop) c.Places.Shops++;
            if (food) c.Places.Food++;
            if (strong != null && c.Places.Strong == null) c.Places.Strong = strong;
            var name = f.Str("@name");
            if (!string.IsNullOrWhiteSpace(name) && c.Places.Names.Count < 8 && (shop || food || strong != null))
                c.Places.Names.Add((name.Trim(), primary ?? ""));
        }
    }

    static string JsonField(string json, string key)
    {
        if (json == null) return null;
        int i = json.IndexOf("\"" + key + "\":\"", StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length + 4;
        int j = json.IndexOf('"', i);
        return j > i ? json.Substring(i, j - i) : null;
    }

    static string[] JsonArray(string json, string key)
    {
        if (json == null) return null;
        int i = json.IndexOf("\"" + key + "\":[", StringComparison.Ordinal);
        if (i < 0) return null;
        i += key.Length + 4;
        int j = json.IndexOf(']', i);
        if (j <= i) return null;
        var parts = json.Substring(i, j - i).Split(',');
        for (int k = 0; k < parts.Length; k++) parts[k] = parts[k].Trim('"', ' ');
        return parts;
    }

    static string LandUseAt(Ctx ctx, double lat, double lon)
    {
        if (ctx.LandUse.Count == 0) return null;
        double n = 1 << ctx.LandZ;
        double x = (lon + 180) / 360 * n - ctx.LandX;
        double latR = lat * Math.PI / 180;
        double y = (1 - Math.Log(Math.Tan(latR) + 1 / Math.Cos(latR)) / Math.PI) / 2 * n - ctx.LandY;
        string best = null;
        foreach (var (ring, cls) in ctx.LandUse)
            if (InRing(ring, x, y)) { best = cls; if (cls != "religious") break; }
        return best;
    }

    /// <summary>Point du plan local d'un bâtiment → coordonnées de la tuile (pour tester les voisins).</summary>
    static void ToTile(in LocalFrame fr, V2 p, Ctx ctx, out double x, out double y)
    {
        fr.LatLon(p, out double lat, out double lon);
        double n = 1 << ctx.Z;
        x = (lon + 180) / 360 * n - ctx.Tx;
        double latR = lat * Math.PI / 180;
        y = (1 - Math.Log(Math.Tan(latR) + 1 / Math.Cos(latR)) / Math.PI) / 2 * n - ctx.Ty;
    }

    // ------------------------------------------------------------------ bâtiment par bâtiment

    static void Emit(Cand cd, IReadOnlyList<Area> areas, Ctx ctx)
    {
        // Centre du contour dans la tuile : sinon il appartient à la tuile voisine
        if (cd.Cx < 0 || cd.Cx >= 1 || cd.Cy < 0 || cd.Cy >= 1) return;
        PmTilesArchive.TileToLatLon(ctx.Z, ctx.Tx, ctx.Ty, cd.Cx, cd.Cy, out double clat, out double clon);
        var c = Geo.FromLatLon(clat, clon);
        int area = -1;
        for (int i = 0; i < areas.Count; i++)
            if ((c - areas[i].Dir).Length() * R <= areas[i].Radius) { area = i; break; }
        if (area < 0) return;
        var fr = new LocalFrame(c, ctx.Data.Origin);
        var ext = ToLocal(cd.Outer, ctx, fr, out var lats);
        if (ext.Count < 3) return;
        if (Poly2.SignedArea(ext) < 0) ext.Reverse();
        var hs = new List<List<V2>>();
        foreach (var h in cd.Holes)
        {
            var l = ToLocal(h, ctx, fr, out _);
            if (l.Count < 3) continue;
            if (Poly2.SignedArea(l) > 0) l.Reverse();
            hs.Add(l);
        }
        double footprint = Poly2.SignedArea(ext);
        foreach (var h in hs) footprint += Poly2.SignedArea(h);
        if (footprint < 4) return;

        // Sol sous le bâtiment : aux coins, mais aussi le long des murs (tous les ~3 m) et au centre, sinon une bosse
        // ou un talus entre deux coins passe au-dessus du rez-de-chaussée (fenêtres à moitié enterrées)
        double gmin = double.MaxValue, gmax = double.MinValue;
        void Probe(double la, double lo)
        {
            float g = Ground(la, lo, ctx);
            gmin = Math.Min(gmin, g); gmax = Math.Max(gmax, g);
        }
        for (int i = 0; i < lats.Count; i++)
        {
            var (la0, lo0) = lats[i]; var (la1, lo1) = lats[(i + 1) % lats.Count];
            Probe(la0, lo0);
            double dy = (la1 - la0) * 111_320, dx = (lo1 - lo0) * 111_320 * Math.Cos(la0 * Math.PI / 180);
            int steps = Math.Min((int)(Math.Sqrt(dx * dx + dy * dy) / 3), 20);
            for (int k = 1; k <= steps; k++)
            {
                double t = k / (double)(steps + 1);
                Probe(la0 + (la1 - la0) * t, lo0 + (lo1 - lo0) * t);
            }
        }
        Probe(clat, clon);
        if (BuildOne(cd, ext, hs, footprint, gmin, gmax, fr, clat, clon, ctx)) ctx.Data.PerArea[area]++;
    }

    static float Ground(double lat, double lon, Ctx ctx)
    {
        float g = HeightProvider.Sample(lat, lon, ctx.Spacing, out bool water, out bool pending);
        if (pending) ctx.Incomplete = true;
        if (water) g = Math.Max(g, 0);
        return g;
    }

    static List<V2> ToLocal(double[] ring, Ctx ctx, in LocalFrame fr, out List<(double, double)> latLon)
    {
        var pts = new List<V2>();
        latLon = new List<(double, double)>();
        for (int i = 0; i + 1 < ring.Length - 2; i += 2) // le dernier point répète le premier
        {
            PmTilesArchive.TileToLatLon(ctx.Z, ctx.Tx, ctx.Ty, ring[i], ring[i + 1], out double la, out double lo);
            var d = Geo.FromLatLon(la, lo) - fr.C;
            var p = new V2(d.Dot(fr.East) * R, d.Dot(fr.North) * R);
            if (pts.Count > 0 && Poly2.Dist(pts[^1], p) < 0.05) continue;
            pts.Add(p);
            latLon.Add((la, lo));
        }
        if (pts.Count > 1 && Poly2.Dist(pts[0], pts[^1]) < 0.05) { pts.RemoveAt(pts.Count - 1); latLon.RemoveAt(latLon.Count - 1); }
        // points alignés : ils ne servent à rien et gênent le repérage des maisons rectangulaires
        for (int i = 0; pts.Count > 3 && i < pts.Count;)
        {
            var a = pts[(i + pts.Count - 1) % pts.Count]; var b = pts[i]; var c = pts[(i + 1) % pts.Count];
            double cr = (b.X - a.X) * (c.Y - b.Y) - (b.Y - a.Y) * (c.X - b.X);
            if (Math.Abs(cr) < 0.02 * Math.Max(Poly2.Dist(a, b) * Poly2.Dist(b, c), 1e-6)) { pts.RemoveAt(i); latLon.RemoveAt(i); }
            else i++;
        }
        return pts;
    }

    /// <summary>Correspondance hauteur → coordonnée V des murs : 3 unités par étage, 0 au sol du rez-de-chaussée,
    /// pour que les fenêtres dessinées par le shader tombent sur les vrais étages de l'intérieur.</summary>
    readonly record struct WallMap(double Floor0, double LevelH)
    {
        public float V(double h) => (float)((h - Floor0) / LevelH * 3.0);
    }

    static bool BuildOne(Cand cd, List<V2> ext, List<List<V2>> holes, double footprint,
        double gmin, double gmax, in LocalFrame fr, double clat, double clon, Ctx ctx)
    {
        var f = cd.F; bool isPart = cd.IsPart;
        var m = ctx.M;
        m.Material = 0;
        // Une partie de bâtiment hérite de la classe et du nom du bâtiment
        Mvt.Feature parent = null;
        if (isPart) { var bid = f.Str("building_id"); if (bid != null) ctx.Parents.TryGetValue(bid, out parent); }
        string cls = f.Str("class") ?? parent?.Str("class");
        string sub = f.Str("subtype") ?? parent?.Str("subtype");
        string name = f.Str("@name") ?? parent?.Str("@name");
        string shape = f.Str("roof_shape");
        if (shape == "half_hipped") shape = "half-hipped";
        double seed = Hash(ext[0].X * 7.13 + ext[0].Y * 3.71 + footprint);

        double height = f.Num("height");
        double floors = f.Num("num_floors");
        double minH = f.Num("min_height");
        if (double.IsNaN(minH)) { double mf = f.Num("min_floor"); minH = double.IsNaN(mf) ? 0 : mf * FloorHeight; }
        bool osmHeight = !double.IsNaN(height) && height > 0;

        string land = isPart && parent == null ? null : LandUseAt(ctx, clat, clon);
        double guessH = osmHeight ? height : !double.IsNaN(floors) && floors > 0 ? floors * FloorHeight : 8;
        var kind = BuildingKinds.Classify(cls, sub, name, cd.Places, land, footprint, guessH);
        bool shopfront = BuildingKinds.HasShopfront(kind, cd.Places);

        if (!osmHeight)
            height = !double.IsNaN(floors) && floors > 0 ? floors * FloorHeight + (IsPitched(shape) ? 2 : 0.5) : DefaultHeight(kind, footprint, seed);
        if (height - minH < 0.5) return false;

        bool dome = shape is "dome" or "onion" or "round";
        bool flatKind = kind is BuildingKind.Industrial or BuildingKind.Warehouse or BuildingKind.Office or BuildingKind.Supermarket
            or BuildingKind.Stadium or BuildingKind.Grandstand or BuildingKind.SportsHall or BuildingKind.Garage or BuildingKind.Hospital
            or BuildingKind.Mosque or BuildingKind.Greenhouse || cls is "commercial" or "retail" or "parking" or "garages";
        bool churchLike = kind is BuildingKind.Church or BuildingKind.Cathedral or BuildingKind.Chapel;
        // Toit en pente : forme donnée par OSM, sinon par défaut pour les petites maisons (et les églises)
        bool pitched = IsPitched(shape) || (shape == null && !isPart && !flatKind
            && ((footprint < 450 && height <= 14) || (churchLike && footprint < 4000)));
        double fill;
        var obox = Poly2.OrientedBox(ext, out double boxLong, out double boxShort, out fill);
        V2[] box = null;
        if (pitched && holes.Count == 0 && obox != null && fill >= (IsPitched(shape) || churchLike ? 0.75 : 0.85) && boxShort >= 3) box = obox;
        if (box == null) pitched = false;
        double roofH = pitched ? f.Num("roof_height") : 0;
        if (pitched && (double.IsNaN(roofH) || roofH <= 0)) roofH = Math.Min(boxShort * 0.5 * (0.7 + 0.35 * seed) * (churchLike ? 1.3 : 1), churchLike ? 14 : 7);
        if (pitched) roofH = Math.Min(roofH, Math.Max(height - minH - 2, 0.5));

        // Sur une pente, le rez-de-chaussée est au niveau du sol côté amont (porte d'entrée) et un socle de pierre
        // rattrape la pente côté aval, comme les maisons de montagne. La hauteur donnée vaut côté aval, mais l'amont
        // garde au moins un étage entier (ou 70 % des murs) : la maison ne s'enfonce plus dans le talus.
        double floor0 = minH > 0 ? gmin + minH : gmax + FloorAboveGround;
        double wallsH = height - minH - roofH;
        double eave = gmin + minH + wallsH;
        if (minH <= 0) eave = Math.Max(eave, floor0 + Math.Min(wallsH, Math.Max(FloorHeight, wallsH * 0.7)));
        double top = eave + roofH;
        double baseH = minH > 0 ? gmin + minH : gmin - Foundation;
        // Socle visible côté aval (au moins une bordure de 30 cm au pied des murs)
        bool plinth = minH <= 0 && floor0 - gmin > 0.05;

        // Étages : donnés par OSM, sinon d'après la hauteur ; les nefs et les halles sont d'un seul tenant
        double usable = eave - floor0;
        int levels;
        if (!double.IsNaN(floors) && floors >= 1 && !(BuildingKinds.SingleHall(kind) && floors <= 2 && churchLike)) levels = (int)Math.Round(floors);
        else if (BuildingKinds.SingleHall(kind)) levels = 1;
        else levels = Math.Max(1, (int)Math.Round(usable / FloorHeight));
        double levelH = usable / Math.Max(levels, 1);
        if (levelH < 2.4 && usable >= 2.4) { levels = Math.Max(1, (int)Math.Floor(usable / 2.6)); levelH = usable / levels; }
        if (usable < 0.5) levelH = FloorHeight;
        var map = new WallMap(floor0, Math.Max(levelH, 1.0));

        var wall = WallColor(f, kind, seed);
        var roof = RoofColor(f, shape, pitched, kind, seed);
        var style = height >= 3.5 && footprint >= 20 ? BuildingKinds.Windows(kind, shopfront, height, footprint) : WindowStyle.None;
        var uv2 = new Vector2((float)style, (float)seed);
        // Matières (crépi, bois de chalet, tuiles…), volets et balcons : d'après OSM, sinon la région et l'altitude
        var look = FacadeMaterials.Choose(f.Str("facade_material"), f.Str("roof_material"), f.Str("facade_color") != null,
            f.Str("roof_color") != null, kind, style, pitched, clat, clon, gmin, levels, footprint, height, seed, wall, roof);
        wall = look.WallColor; roof = look.RoofColor;

        // Porte d'entrée (bâtiments où l'on peut entrer) : sur un mur qui donne dehors, le plus près du niveau du sol
        bool enterable = BuildingKinds.Enterable(kind) && footprint >= 16 && footprint <= 40_000 && ext.Count <= 160
            && usable >= 2.3 && minH <= 0;
        int doorEdge = -1; double doorT0 = 0, doorT1 = 0, doorH = 0, doorGround = 0; V2 doorOut = default;
        if (enterable)
        {
            double doorW = kind switch
            {
                BuildingKind.Cathedral => 3.0, BuildingKind.Church or BuildingKind.Mosque or BuildingKind.Temple => 2.2,
                BuildingKind.Shop or BuildingKind.Supermarket or BuildingKind.Restaurant or BuildingKind.TrainStation or BuildingKind.Hospital
                    or BuildingKind.School or BuildingKind.University or BuildingKind.Civic or BuildingKind.Office or BuildingKind.Hotel
                    or BuildingKind.Warehouse or BuildingKind.Industrial or BuildingKind.SportsHall => 1.8,
                _ => shopfront ? 1.6 : 1.1,
            };
            doorH = Math.Min(kind switch { BuildingKind.Cathedral => 4.5, BuildingKind.Church or BuildingKind.Temple or BuildingKind.Mosque => 3.4, BuildingKind.Warehouse or BuildingKind.Industrial => 3.0, _ => 2.25 }, Math.Max(levelH - 0.45, 2.05));
            doorEdge = PickDoor(ext, cd, fr, ctx, doorW, floor0, seed, out doorT0, out doorT1, out doorGround, out doorOut);
            if (doorEdge < 0) enterable = false;
        }

        m.Material = look.WallCode;

        // Collisions : contour extérieur et cours intérieures ; toit à pans réglé par PitchedRoof
        Vec3d[][] holeRings = null;
        if (holes.Count > 0) { holeRings = new Vec3d[holes.Count][]; for (int i = 0; i < holes.Count; i++) holeRings[i] = fr.World(holes[i]); }
        var solid = Obstacles.Shape.Polygon(fr.World(ext), holeRings, baseH, top);
        ctx.Obs.Add(solid);

        bool stadiumBowl = kind is BuildingKind.Stadium && holes.Count > 0 && footprint > 2000;
        if (plinth)
        {
            double drop = floor0 - gmin;
            if (drop >= 2.3 && style != WindowStyle.None && !isPart)
            {
                // Grand socle : comme les vraies maisons en pente, c'est un étage habitable maçonné (sous-sol côté aval)
                // avec ses fenêtres, et souvent une porte de garage au niveau de la route d'en bas
                int nb = Math.Max(1, (int)Math.Floor(drop / 2.5));
                var low = new WallMap(gmin, drop / nb);
                var basementStyle = style is WindowStyle.ShopResidential or WindowStyle.Glass ? WindowStyle.Residential
                    : style is WindowStyle.ShopOffice ? WindowStyle.Office : style;
                var mat = look.Wall switch
                {
                    FacadeSurface.Stone or FacadeSurface.Brick or FacadeSurface.Concrete => look.Wall,
                    FacadeSurface.Metal => FacadeSurface.Concrete,
                    _ => FacadeSurface.Render,
                };
                var col = look.Wall is FacadeSurface.Chalet or FacadeSurface.Wood or FacadeSurface.Siding or FacadeSurface.TimberFrame or FacadeSurface.Metal
                    ? (seed < 0.5 ? new Color(0.88f, 0.86f, 0.80f) : new Color(0.80f, 0.79f, 0.75f)) : wall;
                m.Material = FacadeMaterials.Code(mat, look.Shutters);
                Walls(ext, baseH, floor0, low, col, new Vector2((float)basementStyle, (float)seed), fr, m, -1, 0, 0, 0, 0);
                if (kind is BuildingKind.House or BuildingKind.Apartments or BuildingKind.Generic or BuildingKind.Farm or BuildingKind.Hotel)
                    GarageDoor(ext, cd, fr, ctx, floor0, seed, kind == BuildingKind.Apartments, m);
            }
            else
            {
                // petit socle : pierre ou béton, sans fenêtres, jusqu'au rez-de-chaussée
                m.Material = FacadeMaterials.Code(look.Wall is FacadeSurface.Concrete or FacadeSurface.Metal ? FacadeSurface.Concrete : FacadeSurface.Stone);
                Walls(ext, baseH, floor0, map, PlinthColor(look.Wall is FacadeSurface.Chalet or FacadeSurface.Wood ? new Color(0.80f, 0.78f, 0.72f) : wall, seed), new Vector2(0, (float)seed), fr, m, -1, 0, 0, 0, 0);
            }
            m.Material = look.WallCode;
            Walls(ext, floor0, eave, map, wall, uv2, fr, m, doorEdge, doorT0, doorT1, floor0, doorH);
        }
        else Walls(ext, baseH, eave, map, wall, uv2, fr, m, doorEdge, doorT0, doorT1, floor0, doorH);
        if (stadiumBowl)
        {
            double inner = Math.Min(floor0 + 2.5, eave);
            foreach (var h in holes) Walls(h, baseH, inner, map, MeshAcc.Shade(wall, 0.85f), new Vector2(0, (float)seed), fr, m, -1, 0, 0, 0, 0);
            StadiumBowl(ext, holes, eave, inner, floor0, seed, fr, m, ctx);
        }
        else
        {
            foreach (var h in holes) Walls(h, baseH, eave, map, wall, uv2, fr, m, -1, 0, 0, 0, 0);
            if (pitched) PitchedRoof(box, boxLong >= boxShort, f.Str("roof_orientation") == "across", shape, eave, roofH, roof, wall, map, uv2, fr, m, solid, look);
            else FlatRoof(ext, holes, eave, roof, fr, m, look.RoofCode, seed);
        }

        m.Material = 0;
        // Signes distinctifs selon la fonction (seulement pour les contours simples, sans parties 3D détaillées)
        if (!isPart && obox != null && fill > 0.55)
        {
            if (churchLike && footprint >= 40) Steeple(kind, obox, boxLong, boxShort, eave, roofH, top, floor0, seed, fr, m, ctx);
            if (kind is BuildingKind.Mosque && footprint >= 60) MosqueDome(obox, boxLong, boxShort, eave, floor0, seed, fr, m, ctx);
        }
        if (dome && obox != null && !pitched)
        {
            double r = Math.Min(boxShort, boxLong) * 0.45;
            var ctr = (obox[0] + obox[2]) * 0.5;
            m.Material = 0;
            if (r > 1) m.Dome(fr, ctr, r, eave, r * (shape == "onion" ? 1.3 : 0.8), roof);
        }
        // Balcons (Moyen et plus) : bois sur les chalets, garde-corps métalliques sur les immeubles
        if (ctx.Detail >= 1 && look.Balconies != BalconyKind.None && !isPart && levels >= 2 && minH <= 0)
            Balconies(ext, cd, fr, ctx, floor0, levelH, levels, seed, look, m);
        m.Material = 0;
        if (shopfront) Awnings(ext, cd, fr, ctx, floor0, levelH, seed, doorEdge, m);

        // Porte (battant fermé, effacé par le shader quand le joueur s'approche : l'intérieur prend le relais)
        if (enterable)
        {
            var a = ext[doorEdge]; var b = ext[(doorEdge + 1) % ext.Count];
            var da = a + (b - a) * doorT0; var db = a + (b - a) * doorT1;
            var doorCol = DoorColor(kind, shopfront, seed);
            m.Side(fr, da, db, floor0, floor0 + doorH, doorCol, new Vector2((float)WindowStyle.Door, (float)seed),
                new Vector2(0, 0), new Vector2((float)Poly2.Dist(da, db), (float)doorH));

            var info = new BuildingInfo
            {
                Id = f.Str("id") ?? $"{clat:F6},{clon:F6}",
                Kind = kind, Name = name ?? (cd.Places?.Names.Count > 0 ? cd.Places.Names[0].name : null),
                Places = cd.Places?.Names ?? new(),
                Frame = new LocalFrame(fr.C, fr.C * R),
                Ring = ext, Holes = holes, Area = footprint, Seed = seed, Style = style, WallColor = wall,
                BaseH = baseH, Floor0 = floor0, Eave = eave, Top = top, Floors = Math.Max(levels, 1), LevelH = levelH,
                DoorA = da, DoorB = db, DoorOut = doorOut, DoorH = doorH, DoorGround = doorGround, Solid = solid,
            };
            foreach (var p in ext) info.BoundRadius = Math.Max(info.BoundRadius, p.Length);
            ctx.Data.Infos.Add(info);
        }
        return true;
    }

    /// <summary>Mur de la porte : un côté qui ne touche pas un autre bâtiment (rangées de maisons), le plus près
    /// du niveau du rez-de-chaussée dehors (sur une pente, la porte est côté amont), plutôt long. -1 : aucune.</summary>
    static int PickDoor(List<V2> ext, Cand cd, in LocalFrame fr, Ctx ctx, double doorW, double floor0, double seed,
        out double t0, out double t1, out double ground, out V2 outward)
    {
        int best = -1; double bestScore = double.MaxValue;
        t0 = t1 = ground = 0; outward = default;
        for (int i = 0; i < ext.Count; i++)
        {
            var a = ext[i]; var b = ext[(i + 1) % ext.Count];
            double len = Poly2.Dist(a, b);
            if (len < doorW + 0.8) continue;
            var dir = (b - a) * (1 / len);
            var n = dir.Right;
            // position de la porte : au milieu, ou décalée sur les longues façades
            double c = len > 12 ? len * (0.3 + 0.4 * Hash(seed * 31.1 + i)) : len / 2;
            c = Math.Clamp(c, doorW / 2 + 0.4, len - doorW / 2 - 0.4);
            var mid = a + dir * c;
            // un autre bâtiment juste devant : mur mitoyen
            bool shared = false;
            foreach (double off in new[] { 0.8, 2.0 })
            {
                ToTile(fr, mid + n * off, ctx, out double x, out double y);
                if (At(ctx, x, y, cd) != null) { shared = true; break; }
            }
            if (shared) continue;
            fr.LatLon(mid + n * 1.5, out double la, out double lo);
            double g = Ground(la, lo, ctx);
            double drop = floor0 - g;
            double score = Math.Max(drop, 0) * 3 + Math.Max(-drop, 0) * 6 - Math.Min(len, 30) * 0.08 + Hash(seed + i * 7.7) * 0.3;
            if (drop > 4.5) continue; // trop haut au-dessus de la rue
            if (score >= bestScore) continue;
            bestScore = score; best = i;
            t0 = (c - doorW / 2) / len; t1 = (c + doorW / 2) / len; ground = g; outward = n;
        }
        return best;
    }

    static bool IsPitched(string s) => s is "gabled" or "hipped" or "half-hipped" or "side_hipped" or "gambrel" or "mansard" or "saltbox" or "pyramidal" or "skillion";

    static double DefaultHeight(BuildingKind k, double area, double seed)
    {
        if (area < 30) return 2.6 + seed;
        return k switch
        {
            BuildingKind.Garage or BuildingKind.Shed or BuildingKind.Roof => 3 + seed,
            BuildingKind.House => 6.5 + 3 * seed,
            BuildingKind.Farm => 7 + 4 * seed,
            BuildingKind.Apartments => 12 + 6 * seed,
            BuildingKind.Church => 14 + 6 * seed,
            BuildingKind.Cathedral => 24 + 8 * seed,
            BuildingKind.Chapel => 7 + 3 * seed,
            BuildingKind.Mosque or BuildingKind.Temple => 10 + 4 * seed,
            BuildingKind.Industrial or BuildingKind.Warehouse => 7 + 4 * seed,
            BuildingKind.Supermarket => 6 + 2 * seed,
            BuildingKind.Shop or BuildingKind.Restaurant => area > 300 ? 10 + 6 * seed : 6.5 + 3 * seed,
            BuildingKind.School or BuildingKind.Kindergarten => area > 400 ? 10 + 3 * seed : 4 + 3 * seed,
            BuildingKind.Stadium => 18 + 10 * seed,
            BuildingKind.Grandstand => 8 + 4 * seed,
            BuildingKind.SportsHall => 8 + 3 * seed,
            BuildingKind.Greenhouse => 3 + seed,
            BuildingKind.TrainStation => 9 + 4 * seed,
            BuildingKind.Office or BuildingKind.Hotel or BuildingKind.Civic or BuildingKind.University or BuildingKind.Hospital
                or BuildingKind.Library or BuildingKind.Museum => 12 + 6 * seed,
            _ => area > 800 ? 9 + 6 * seed : 6 + 4 * seed,
        };
    }

    /// <summary>Porte de garage (sectionnelle, à lames horizontales) dans le socle, sur le mur le plus bas : là où une
    /// route passe en contrebas. Deux portes côte à côte sur les longues façades d'immeubles.</summary>
    static void GarageDoor(List<V2> ext, Cand cd, in LocalFrame fr, Ctx ctx, double floor0, double seed, bool block, MeshAcc m)
    {
        int best = -1; double bestG = double.MaxValue;
        for (int i = 0; i < ext.Count; i++)
        {
            var a = ext[i]; var b = ext[(i + 1) % ext.Count];
            double len = Poly2.Dist(a, b);
            if (len < 3.4) continue;
            var n = ((b - a) * (1 / len)).Right;
            var mid = (a + b) * 0.5;
            ToTile(fr, mid + n * 1.5, ctx, out double x, out double y);
            if (At(ctx, x, y, cd) != null) continue; // mur mitoyen
            fr.LatLon(mid + n * 1.5, out double la, out double lo);
            double g = Ground(la, lo, ctx);
            if (g < bestG) { bestG = g; best = i; }
        }
        if (best < 0 || floor0 - bestG < 2.5) return;
        var A = ext[best]; var B = ext[(best + 1) % ext.Count];
        double L = Poly2.Dist(A, B);
        var d = (B - A) * (1 / L); var nn = d.Right;
        int count = block && L > 8 ? 2 : 1;
        double w = 2.5, gap = 0.6, total = count * w + (count - 1) * gap;
        double start = (L - total) * (0.35 + 0.3 * Hash(seed * 17.3));
        var cols = new[] { new Color(0.62f, 0.63f, 0.64f), new Color(0.40f, 0.27f, 0.17f), new Color(0.86f, 0.86f, 0.84f), new Color(0.32f, 0.34f, 0.36f) };
        var col = cols[(int)(Hash(seed * 7.7) * cols.Length) % cols.Length];
        m.Material = FacadeMaterials.Code(FacadeSurface.Siding);
        for (int k = 0; k < count; k++)
        {
            double t0 = start + k * (w + gap);
            var p0 = A + d * t0 + nn * 0.03; var p1 = A + d * (t0 + w) + nn * 0.03;
            m.Side(fr, p0, p1, bestG - 0.3, bestG + 2.15, col, new Vector2(0, (float)seed), new Vector2(0, 0), new Vector2((float)w, 2.45f));
        }
    }

    /// <summary>Socle sous le rez-de-chaussée : pierre grise (ou béton) un peu plus sombre que la façade.</summary>
    static Color PlinthColor(Color wall, double seed)
    {
        var stone = seed < 0.5 ? new Color(0.50f, 0.49f, 0.46f) : new Color(0.58f, 0.57f, 0.55f);
        return MeshAcc.Shade(stone.Lerp(wall, 0.25f), 0.85f);
    }

    static Color WallColor(Mvt.Feature f, BuildingKind kind, double seed)
    {
        var s = f?.Str("facade_color");
        if (s != null) { var c = Color.FromString(s, new Color(-1, 0, 0)); if (c.R >= 0) return c; }
        var mat = f?.Str("facade_material");
        Color b = mat switch
        {
            "brick" => new Color(0.62f, 0.36f, 0.28f),
            "wood" => new Color(0.55f, 0.40f, 0.26f),
            "stone" or "sandstone" => new Color(0.66f, 0.62f, 0.52f),
            "glass" => new Color(0.42f, 0.52f, 0.58f),
            "concrete" => new Color(0.66f, 0.66f, 0.64f),
            "metal" => new Color(0.58f, 0.60f, 0.62f),
            _ => BuildingKinds.Wall(kind, seed),
        };
        float v = (float)(seed - 0.5) * 0.12f;
        return new Color(Math.Clamp(b.R + v, 0, 1), Math.Clamp(b.G + v * 0.9f, 0, 1), Math.Clamp(b.B + v * 0.7f, 0, 1));
    }

    static Color RoofColor(Mvt.Feature f, string shape, bool pitched, BuildingKind kind, double seed)
    {
        var s = f?.Str("roof_color");
        if (s != null) { var c = Color.FromString(s, new Color(-1, 0, 0)); if (c.R >= 0) return c; }
        var mat = f?.Str("roof_material");
        Color b = mat switch
        {
            "roof_tiles" or "tile" => new Color(0.58f, 0.30f, 0.22f),
            "metal" or "copper" => mat == "copper" ? new Color(0.36f, 0.55f, 0.47f) : new Color(0.45f, 0.48f, 0.50f),
            "slate" or "stone" => new Color(0.34f, 0.35f, 0.38f),
            "glass" => new Color(0.40f, 0.48f, 0.55f),
            "grass" or "plants" => new Color(0.36f, 0.45f, 0.26f),
            "wood" => new Color(0.42f, 0.32f, 0.22f),
            _ => kind switch
            {
                BuildingKind.Church or BuildingKind.Cathedral or BuildingKind.Chapel => seed < 0.5 ? new Color(0.32f, 0.33f, 0.36f) : new Color(0.55f, 0.29f, 0.21f),
                BuildingKind.Greenhouse => new Color(0.62f, 0.74f, 0.78f),
                _ => pitched
                    ? (seed < 0.6 ? new Color(0.55f, 0.29f, 0.21f) : seed < 0.85 ? new Color(0.38f, 0.30f, 0.27f) : new Color(0.42f, 0.43f, 0.45f))
                    : new Color(0.58f, 0.57f, 0.55f), // toit plat : gravier
            },
        };
        float v = (float)(Hash(seed * 91.7) - 0.5) * 0.10f;
        return new Color(Math.Clamp(b.R + v, 0, 1), Math.Clamp(b.G + v, 0, 1), Math.Clamp(b.B + v, 0, 1));
    }

    /// <summary>Remet chaque triangle dans le sens de Godot (face avant = sommets dans le sens horaire vue de l'extérieur,
    /// c.-à-d. produit vectoriel opposé à la normale). Sans cela, le matériau (cull_disabled) voyait l'arrière des murs
    /// et retournait leur normale vers l'intérieur : façades noires côté soleil, éclairées côté ombre.</summary>
    static void FrontFaces(MeshAcc m)
    {
        for (int t = 0; t + 2 < m.I.Count; t += 3)
        {
            int a = m.I[t], b = m.I[t + 1], c = m.I[t + 2];
            var cross = (m.V[b] - m.V[a]).Cross(m.V[c] - m.V[a]);
            if (cross.Dot(m.N[a] + m.N[b] + m.N[c]) > 0) { m.I[t + 1] = c; m.I[t + 2] = b; }
        }
    }

    static Color DoorColor(BuildingKind k, bool shop, double seed)
    {
        if (shop || k is BuildingKind.Office or BuildingKind.Supermarket or BuildingKind.Hospital or BuildingKind.TrainStation or BuildingKind.University)
            return new Color(0.20f, 0.24f, 0.27f); // porte vitrée
        if (k is BuildingKind.Industrial or BuildingKind.Warehouse or BuildingKind.FireStation) return new Color(0.50f, 0.52f, 0.54f);
        return seed < 0.5 ? new Color(0.36f, 0.22f, 0.13f) : seed < 0.8 ? new Color(0.45f, 0.30f, 0.18f) : new Color(0.25f, 0.30f, 0.38f);
    }

    /// <summary>Murs extrudés d'un contour ; sur le côté <paramref name="doorEdge"/>, une ouverture de porte.</summary>
    static void Walls(List<V2> ring, double bottom, double top, WallMap map, Color col, Vector2 uv2, in LocalFrame fr, MeshAcc m,
        int doorEdge, double doorT0, double doorT1, double floor0, double doorH)
    {
        double s = 0;
        for (int i = 0; i < ring.Count; i++)
        {
            var a = ring[i]; var b = ring[(i + 1) % ring.Count];
            double len = Poly2.Dist(a, b);
            if (len < 0.01) continue;
            if (i == doorEdge)
            {
                var da = a + (b - a) * doorT0; var db = a + (b - a) * doorT1;
                double sa = s + len * doorT0, sb = s + len * doorT1;
                WallQuad(a, da, s, sa, bottom, top, map, col, uv2, fr, m);
                WallQuad(db, b, sb, s + len, bottom, top, map, col, uv2, fr, m);
                WallQuad(da, db, sa, sb, bottom, floor0, map, col, uv2, fr, m);
                if (floor0 + doorH < top) WallQuad(da, db, sa, sb, floor0 + doorH, top, map, col, uv2, fr, m);
            }
            else WallQuad(a, b, s, s + len, bottom, top, map, col, uv2, fr, m);
            s += len;
        }
    }

    static void WallQuad(V2 a, V2 b, double s0, double s1, double h0, double h1, WallMap map, Color col, Vector2 uv2, in LocalFrame fr, MeshAcc m)
    {
        if (h1 - h0 < 0.01) return;
        m.Side(fr, a, b, h0, h1, col, uv2, new Vector2((float)s0, map.V(h0)), new Vector2((float)s1, map.V(h1)));
    }

    static void FlatRoof(List<V2> ext, List<List<V2>> holes, double h, Color col, in LocalFrame fr, MeshAcc m, byte material = 0, double seed = 0)
    {
        var pts = new List<V2>();
        var tris = Poly2.Triangulate(ext, holes, pts);
        int k = m.Count;
        var up = fr.Up;
        byte keep = m.Material;
        m.Material = material;
        var uv2 = new Vector2(0, (float)seed);
        foreach (var p in pts) m.Vert(fr.P(p, h), up, col, new Vector2((float)p.X, (float)-p.Y), uv2);
        foreach (var t in tris) m.I.Add(k + t);
        m.Material = keep;
    }

    /// <summary>Toit à deux pans (pignons) ou à quatre pans sur le rectangle englobant, avec un léger débord.</summary>
    static void PitchedRoof(V2[] box, bool firstIsLong, bool across, string shape, double eave, double roofH,
        Color col, Color wall, WallMap map, Vector2 wallUv2, in LocalFrame fr, MeshAcc m, Obstacles.Shape solid, BuildingLook look)
    {
        var roofUv2 = new Vector2(0, wallUv2.Y);
        // box : A, B, C, D (sens direct), AB parallèle au grand côté si firstIsLong
        var A = box[0]; var B = box[1]; var C = box[2]; var D = box[3];
        if (!firstIsLong ^ across) { (A, B, C, D) = (B, C, D, A); } // le faîte suit AB
        // débord de 0,4 m
        double ux = B.X - A.X, uy = B.Y - A.Y, L = Math.Sqrt(ux * ux + uy * uy); ux /= L; uy /= L;
        double vx = D.X - A.X, vy = D.Y - A.Y, W = Math.Sqrt(vx * vx + vy * vy); vx /= W; vy /= W;
        const double o = 0.4;
        V2 Off(V2 p, double su, double sv) => new(p.X + ux * su * o + vx * sv * o, p.Y + uy * su * o + vy * sv * o);
        var gA = A; var gB = B; var gC = C; var gD = D; // pignons : dans le plan des murs
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
        if (shape == "skillion") { Skillion(A, B, C, D, low, ridge, col, wall, fr, m, look, roofUv2); return; }
        // Collisions : on peut marcher sur ce toit (même pente que le maillage)
        solid.SetPitched(fr.World(r1), fr.World(r2), ridge, low, W / 2);

        var center = new V2((A.X + C.X) / 2, (A.Y + C.Y) / 2);
        m.Material = look.RoofCode;
        Face(new[] { (A, low), (B, low), (r2, ridge), (r1, ridge) }, col, center, fr, m, roofUv2);
        Face(new[] { (C, low), (D, low), (r1, ridge), (r2, ridge) }, col, center, fr, m, roofUv2);
        if (hip)
        {
            Face(new[] { (B, low), (C, low), (r2, ridge) }, col, center, fr, m, roofUv2);
            Face(new[] { (D, low), (A, low), (r1, ridge) }, col, center, fr, m, roofUv2);
            m.Material = look.WallCode;
        }
        else
        {
            m.Material = look.WallCode;
            // pignons : murs triangulaires (avec fenêtres)
            var g2 = new V2((gB.X + gC.X) / 2, (gB.Y + gC.Y) / 2); var g1 = new V2((gD.X + gA.X) / 2, (gD.Y + gA.Y) / 2);
            Gable(gB, gC, g2, eave, ridge, wall, map, wallUv2, fr, m);
            Gable(gD, gA, g1, eave, ridge, wall, map, wallUv2, fr, m);
        }
    }

    static void Gable(V2 a, V2 b, V2 top, double eave, double ridge, Color wall, WallMap map, Vector2 uv2, in LocalFrame fr, MeshAcc m)
    {
        var d = (b - a).Normalized.Right;
        var n = fr.Dir(d.X, d.Y, 0);
        float len = (float)Poly2.Dist(a, b);
        int k = m.Count;
        m.Vert(fr.P(a, eave), n, wall, new Vector2(0, map.V(eave)), uv2);
        m.Vert(fr.P(b, eave), n, wall, new Vector2(len, map.V(eave)), uv2);
        m.Vert(fr.P(top, ridge), n, wall, new Vector2(len / 2, map.V(ridge)), uv2);
        m.I.Add(k); m.I.Add(k + 1); m.I.Add(k + 2);
    }

    static void Skillion(V2 A, V2 B, V2 C, V2 D, double low, double high, Color col, Color wall, in LocalFrame fr, MeshAcc m,
        BuildingLook look, Vector2 uv2)
    {
        var center = new V2((A.X + C.X) / 2, (A.Y + C.Y) / 2);
        m.Material = look.RoofCode;
        Face(new[] { (A, low), (B, low), (C, high), (D, high) }, col, center, fr, m, uv2);
        // murs sous le toit en appentis : matière du mur, mais sans le rez-de-chaussée maçonné d'un chalet (UV en altitude)
        m.Material = FacadeMaterials.Code(look.Wall == FacadeSurface.Chalet ? FacadeSurface.Wood : look.Wall);
        Face(new[] { (B, low), (C, high), (C, low) }, wall, center, fr, m, uv2);
        Face(new[] { (D, high), (A, low), (D, low) }, wall, center, fr, m, uv2);
        Face(new[] { (C, high), (D, high), (D, low), (C, low) }, wall, center, fr, m, uv2);
        m.Material = look.WallCode;
    }

    /// <summary>Ajoute une face plane (3 ou 4 points), normale tournée vers l'extérieur du bâtiment. UV en mètres :
    /// horizontale le long de la face, distance vers le bas de la pente (rangées de tuiles parallèles à la gouttière).</summary>
    static void Face((V2 p, double h)[] pts, Color col, V2 center, in LocalFrame fr, MeshAcc m, Vector2 uv2 = default)
    {
        var p0 = fr.P(pts[0].p, pts[0].h);
        var p1 = fr.P(pts[1].p, pts[1].h);
        var p2 = fr.P(pts[2].p, pts[2].h);
        var n = (p1 - p0).Cross(p2 - p0);
        if (n.LengthSquared() < 1e-10) n = fr.Up; else n = n.Normalized();
        double fx = 0, fy = 0; foreach (var (p, _) in pts) { fx += p.X; fy += p.Y; }
        var outward = fr.Dir(fx / pts.Length - center.X, fy / pts.Length - center.Y, 0.3);
        if (n.Dot(outward) < 0) n = -n;
        // normale dans le repère local (est, nord, haut) pour les UV de la pente
        double lx = n.Dot(fr.East.ToVector3()), ly = n.Dot(fr.North.ToVector3()), lz = n.Dot(fr.Up);
        int k = m.Count;
        foreach (var (p, h) in pts) m.Vert(fr.P(p, h), n, col, FacadeMaterials.SlopeUv(p.X, p.Y, h, lx, ly, lz), uv2);
        m.I.Add(k); m.I.Add(k + 1); m.I.Add(k + 2);
        if (pts.Length == 4) { m.I.Add(k); m.I.Add(k + 2); m.I.Add(k + 3); }
    }

    // ------------------------------------------------------------------ signes distinctifs

    /// <summary>Clocher d'église : tour carrée à un bout de la nef, flèche pyramidale ; clocheton pour une chapelle.</summary>
    static void Steeple(BuildingKind kind, V2[] box, double L, double W, double eave, double roofH, double top, double floor0,
        double seed, in LocalFrame fr, MeshAcc m, Ctx ctx)
    {
        var A = box[0]; var B = box[1]; var D = box[3];
        var u = (B - A).Normalized; var v = (D - A).Normalized;
        // bout le plus à l'ouest (orientation traditionnelle : chœur à l'est)
        bool atA = (A + D).X <= (B + box[2]).X;
        var endMid = atA ? (A + D) * 0.5 : (B + box[2]) * 0.5;
        var inward = atA ? u : u * -1;
        bool chapel = kind == BuildingKind.Chapel;
        double s = chapel ? Math.Clamp(W * 0.25, 1.4, 2.6) : Math.Clamp(W * 0.42, 3, kind == BuildingKind.Cathedral ? 14 : 8);
        if (s > L * 0.45) return;
        var c = endMid + inward * (s / 2 + (chapel ? s * 0.3 : 0));
        var stone = MeshAcc.Shade(BuildingKinds.Wall(kind, seed), 0.95f);
        var spireCol = seed < 0.45 ? new Color(0.30f, 0.31f, 0.34f) : seed < 0.75 ? new Color(0.36f, 0.55f, 0.47f) : new Color(0.50f, 0.28f, 0.20f);
        double baseH = chapel ? top - 0.2 : floor0 - 1.0;
        double towerTop = chapel ? top + s * 1.2 : Math.Max(top + Math.Max(4, s * 0.8), eave + s * 2.4) * (kind == BuildingKind.Cathedral ? 1.15 : 1);
        double spire = s * (chapel ? 1.8 : 1.9);
        m.Material = FacadeMaterials.Code(FacadeSurface.Stone);
        m.Box(fr, c, u, s / 2, s / 2, baseH, towerTop, stone);
        m.Material = 0;
        // ouvertures du beffroi (sombres) sur chaque face, sous la flèche
        var dark = new Color(0.10f, 0.10f, 0.11f);
        double bh0 = towerTop - Math.Min(s * 0.9, 4.0), bh1 = towerTop - Math.Min(s * 0.25, 1.0);
        for (int k = 0; k < 4; k++)
        {
            var nrm = k switch { 0 => u, 1 => v, 2 => u * -1, _ => v * -1 };
            var side = new V2(-nrm.Y, nrm.X);
            var fc = c + nrm * (s / 2 + 0.03);
            m.Side(fr, fc + side * (-s * 0.18), fc + side * (s * 0.18), bh0, bh1, dark);
        }
        // flèche pyramidale
        var p = new[] { c - u * (s / 2) - v * (s / 2), c + u * (s / 2) - v * (s / 2), c + u * (s / 2) + v * (s / 2), c - u * (s / 2) + v * (s / 2) };
        m.Material = FacadeMaterials.Code(spireCol.G > spireCol.R + 0.08f ? FacadeSurface.RoofCopper : spireCol.R > spireCol.B + 0.12f ? FacadeSurface.RoofTiles : FacadeSurface.RoofSlate);
        for (int k = 0; k < 4; k++) Face(new[] { (p[k], towerTop), (p[(k + 1) % 4], towerTop), (c, towerTop + spire) }, spireCol, c, fr, m, new Vector2(0, (float)seed));
        m.Material = 0;
        // collisions : la tour (on ne grimpe pas sur la flèche)
        var ring = new Vec3d[4]; for (int k = 0; k < 4; k++) ring[k] = fr.World(p[k]);
        var sh = Obstacles.Shape.Polygon(ring, null, baseH, towerTop);
        ctx.Obs.Add(sh);
    }

    /// <summary>Mosquée : grande coupole au centre et minaret à un angle.</summary>
    static void MosqueDome(V2[] box, double L, double W, double eave, double floor0, double seed, in LocalFrame fr, MeshAcc m, Ctx ctx)
    {
        var ctr = (box[0] + box[2]) * 0.5;
        double r = Math.Clamp(Math.Min(L, W) * 0.36, 2.5, 18);
        var domeCol = seed < 0.5 ? new Color(0.88f, 0.88f, 0.86f) : seed < 0.8 ? new Color(0.30f, 0.55f, 0.48f) : new Color(0.78f, 0.66f, 0.36f);
        m.Cylinder(fr, ctr, r * 0.95, eave, eave + r * 0.25, 20, BuildingKinds.Wall(BuildingKind.Mosque, seed), 0, domeCol);
        m.Dome(fr, ctr, r, eave + r * 0.25, r * 0.9, domeCol);
        var u = (box[1] - box[0]).Normalized; var v = (box[3] - box[0]).Normalized;
        var corner = box[1] - u * 1.6 + v * 1.6;
        double mh = Math.Max(eave + 12, floor0 + Math.Max(L, W) * 0.9);
        m.Cylinder(fr, corner, 1.1, floor0 - 1, mh, 12, BuildingKinds.Wall(BuildingKind.Mosque, seed), 3.2, domeCol);
        m.Cylinder(fr, corner, 1.5, mh - 4.5, mh - 4.0, 12, MeshAcc.Shade(domeCol, 0.9f), 0, MeshAcc.Shade(domeCol, 0.9f));
        var ring = new Vec3d[8];
        for (int k = 0; k < 8; k++) ring[k] = fr.World(corner + new V2(Math.Cos(k * Math.Tau / 8), Math.Sin(k * Math.Tau / 8)) * 1.1);
        ctx.Obs.Add(Obstacles.Shape.Polygon(ring, null, floor0 - 1, mh));
    }

    /// <summary>Stade : gradins en pente du bord intérieur (bas) au bord extérieur (haut), pelouse tracée au milieu.</summary>
    static void StadiumBowl(List<V2> ext, List<List<V2>> holes, double outerH, double innerH, double floor0, double seed,
        in LocalFrame fr, MeshAcc m, Ctx ctx)
    {
        m.Material = 0;
        var pts = new List<V2>();
        var tris = Poly2.Triangulate(ext, holes, pts);
        var inner = new HashSet<(long, long)>();
        foreach (var h in holes) foreach (var p in h) inner.Add(((long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100)));
        var seat = seed < 0.33 ? new Color(0.22f, 0.32f, 0.62f) : seed < 0.66 ? new Color(0.68f, 0.18f, 0.16f) : new Color(0.85f, 0.70f, 0.20f);
        var seatUv2 = new Vector2((float)WindowStyle.Seats, (float)seed);
        int k = m.Count;
        foreach (var p in pts)
        {
            bool isIn = inner.Contains(((long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100)));
            double h = isIn ? innerH : outerH;
            m.Vert(fr.P(p, h), fr.Up, seat, new Vector2(0, (float)(h - innerH)), seatUv2);
        }
        foreach (var t in tris) m.I.Add(k + t);

        // pelouse (sur la cour intérieure), praticable
        var grass = new Color(0.25f, 0.50f, 0.20f);
        var pitchUv2 = new Vector2((float)WindowStyle.Pitch, (float)seed);
        double pitchH = floor0;
        foreach (var h in holes)
        {
            var ring = new List<V2>(h); ring.Reverse();
            var hp = new List<V2>();
            var ht = Poly2.Triangulate(ring, null, hp);
            int q = m.Count;
            foreach (var p in hp) m.Vert(fr.P(p, pitchH), fr.Up, grass, new Vector2((float)p.X, (float)p.Y), pitchUv2);
            foreach (var t in ht) m.I.Add(q + t);
            var floor = Obstacles.Shape.Polygon(fr.World(ring), null, pitchH - 0.3, pitchH);
            floor.Floor = true; floor.Clears = true;
            ctx.Obs.Add(floor);
            // lignes du terrain sur le rectangle englobant de la pelouse
            var box = Poly2.OrientedBox(ring, out double L, out double W, out double fill);
            if (box == null || fill < 0.6 || L < 30 || W < 20) continue;
            var c = (box[0] + box[2]) * 0.5; var u = (box[1] - box[0]).Normalized; var v = new V2(-u.Y, u.X);
            double hl = Math.Min(L / 2 - 3, 52.5), hw = Math.Min(W / 2 - 3, 34);
            var white = new Color(0.92f, 0.93f, 0.92f);
            double lh = pitchH + 0.03;
            var lf = fr;
            void Line(V2 a, V2 b)
            {
                var d = (b - a).Normalized; var n = new V2(-d.Y, d.X) * 0.06;
                m.Quad(lf.P(a - n, lh), lf.P(b - n, lh), lf.P(b + n, lh), lf.P(a + n, lh), lf.Up, white, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero);
            }
            V2 P(double x, double y) => c + u * x + v * y;
            Line(P(-hl, -hw), P(hl, -hw)); Line(P(hl, -hw), P(hl, hw)); Line(P(hl, hw), P(-hl, hw)); Line(P(-hl, hw), P(-hl, -hw));
            Line(P(0, -hw), P(0, hw));
            double cr = Math.Min(9.15, hw * 0.5);
            for (int i = 0; i < 24; i++)
                Line(P(Math.Cos(i * Math.Tau / 24) * cr, Math.Sin(i * Math.Tau / 24) * cr), P(Math.Cos((i + 1) * Math.Tau / 24) * cr, Math.Sin((i + 1) * Math.Tau / 24) * cr));
            if (hl > 30)
                foreach (int sgn in new[] { -1, 1 })
                {
                    double pw = Math.Min(20.15, hw * 0.6), pd = 16.5;
                    Line(P(sgn * hl, -pw), P(sgn * (hl - pd), -pw)); Line(P(sgn * (hl - pd), -pw), P(sgn * (hl - pd), pw)); Line(P(sgn * (hl - pd), pw), P(sgn * hl, pw));
                }
        }
    }

    /// <summary>
    /// Balcons sur les façades libres les mieux exposées (au sud de préférence), à chaque étage au-dessus du
    /// rez-de-chaussée : dalle ou plancher, balustrade en planches découpées (chalets) ou garde-corps à barreaux.
    /// Sur les immeubles, ils sont alignés sur les fenêtres dessinées par le shader (cases de 3,2 m le long du contour).
    /// </summary>
    static void Balconies(List<V2> ext, Cand cd, in LocalFrame fr, Ctx ctx, double floor0, double levelH, int levels,
        double seed, BuildingLook look, MeshAcc m)
    {
        bool wood = look.Balconies == BalconyKind.Wood;
        const double Cell = 3.2; // largeur des cases de fenêtres du shader (WindowStyle.Residential)
        var faces = new List<(int i, double score, double s0)>();
        double s = 0;
        for (int i = 0; i < ext.Count; i++)
        {
            var a = ext[i]; var b = ext[(i + 1) % ext.Count];
            double len = Poly2.Dist(a, b);
            double s0 = s; s += len;
            if (len < 4.0) continue;
            var n = ((b - a) * (1 / len)).Right;
            bool shared = false;
            foreach (double off in new[] { 1.0, 2.2 })
            {
                ToTile(fr, (a + b) * 0.5 + n * off, ctx, out double x, out double y);
                if (At(ctx, x, y, cd) != null) { shared = true; break; }
            }
            if (shared) continue;
            faces.Add((i, -n.Y * 2 + Math.Min(len, 20) * 0.05 + Hash(seed * 5.3 + i) * 0.3, s0));
        }
        if (faces.Count == 0) return;
        faces.Sort((p, q) => q.score.CompareTo(p.score));
        int nFaces = !wood && levels >= 3 ? Math.Min(2, faces.Count) : 1;
        int topLevel = wood ? Math.Min(levels - 1, 2) : Math.Min(levels - 1, 12);
        double depth = wood ? 1.2 : levels >= 3 ? 1.4 : 1.1, thick = wood ? 0.16 : 0.2;
        var woodCol = look.Wall is FacadeSurface.Chalet or FacadeSurface.Wood ? MeshAcc.Shade(look.WallColor, 0.95f) : new Color(0.42f, 0.27f, 0.16f);
        var slabCol = wood ? woodCol : new Color(0.72f, 0.72f, 0.70f);
        var railCol = wood ? woodCol : Hash(seed * 3.1) < 0.6 ? new Color(0.22f, 0.23f, 0.24f) : new Color(0.85f, 0.85f, 0.83f);
        byte slabMat = FacadeMaterials.Code(wood ? FacadeSurface.DeckWood : FacadeSurface.Concrete);
        byte railMat = FacadeMaterials.Code(wood ? FacadeSurface.RailWood : FacadeSurface.RailMetal);
        var uv2 = new Vector2(0, (float)seed);
        var up = fr.Up;

        for (int fi = 0; fi < nFaces; fi++)
        {
            var (i, _, s0) = faces[fi];
            var a = ext[i]; var b = ext[(i + 1) % ext.Count];
            double len = Poly2.Dist(a, b);
            var d = (b - a) * (1 / len); var n = d.Right;
            // portions de façade (distances depuis a)
            var spans = new List<(double t0, double t1)>();
            if (wood) { double w = Math.Min(len - 0.6, Math.Max(len * 0.8, 3)); spans.Add(((len - w) / 2, (len + w) / 2)); }
            else
            {
                int k0 = (int)Math.Ceiling((s0 + 0.3) / Cell), k1 = (int)Math.Floor((s0 + len - 0.3) / Cell); // cases entières
                if (k1 - k0 == 1) spans.Add((k0 * Cell - s0 + 0.15, (k0 + 1) * Cell - s0 - 0.15));
                for (int k = k0; k + 2 <= k1; k += 3) spans.Add((k * Cell - s0 + 0.15, (k + 2) * Cell - s0 - 0.15));
            }
            foreach (var (t0, t1) in spans)
            {
                var p0 = a + d * t0; var p1 = a + d * t1;
                var q0 = p0 + n * depth; var q1 = p1 + n * depth;
                double w = t1 - t0;
                for (int lvl = 1; lvl <= topLevel; lvl++)
                {
                    double hb = floor0 + lvl * levelH;
                    m.Material = slabMat;
                    m.Quad(fr.P(p0, hb), fr.P(p1, hb), fr.P(q1, hb), fr.P(q0, hb), up, slabCol,
                        new Vector2(0, 0), new Vector2((float)w, 0), new Vector2((float)w, (float)depth), new Vector2(0, (float)depth), uv2);
                    m.Quad(fr.P(q0, hb - thick), fr.P(q1, hb - thick), fr.P(p1, hb - thick), fr.P(p0, hb - thick), -up, MeshAcc.Shade(slabCol, 0.8f),
                        new Vector2(0, 0), new Vector2((float)w, 0), new Vector2((float)w, (float)depth), new Vector2(0, (float)depth), uv2);
                    m.Side(fr, q0, q1, hb - thick, hb, MeshAcc.Shade(slabCol, 0.9f), uv2, new Vector2(0, 0), new Vector2((float)w, (float)thick));
                    m.Side(fr, p0, q0, hb - thick, hb, MeshAcc.Shade(slabCol, 0.9f), uv2, new Vector2(0, 0), new Vector2((float)depth, (float)thick));
                    m.Side(fr, q1, p1, hb - thick, hb, MeshAcc.Shade(slabCol, 0.9f), uv2, new Vector2(0, 0), new Vector2((float)depth, (float)thick));
                    if (wood)
                    {
                        // consoles sous le plancher
                        m.Material = FacadeMaterials.Code(FacadeSurface.Wood);
                        int nb = w > 4.5 ? 3 : 2;
                        for (int c = 0; c < nb; c++)
                        {
                            var at = p0 + d * (0.15 + (w - 0.3) * c / (nb - 1)) + n * (depth * 0.45);
                            m.Box(fr, at, n, depth * 0.45, 0.06, hb - thick - 0.32, hb - thick, MeshAcc.Shade(woodCol, 0.85f));
                        }
                    }
                    // balustrade (1 m), en retrait de 3 cm du bord
                    m.Material = railMat;
                    var r0 = q0 - n * 0.03 + d * 0.03; var r1 = q1 - n * 0.03 - d * 0.03;
                    var e0 = p0 + d * 0.03; var e1 = p1 - d * 0.03;
                    m.Side(fr, r0, r1, hb, hb + 1.0, railCol, uv2, new Vector2(0, 0), new Vector2((float)Poly2.Dist(r0, r1), 1));
                    m.Side(fr, e0, r0, hb, hb + 1.0, railCol, uv2, new Vector2(0, 0), new Vector2((float)Poly2.Dist(e0, r0), 1));
                    m.Side(fr, r1, e1, hb, hb + 1.0, railCol, uv2, new Vector2(0, 0), new Vector2((float)Poly2.Dist(r1, e1), 1));
                }
            }
        }
        m.Material = 0;
    }

    /// <summary>Auvents de toile au-dessus des vitrines, sur les façades qui donnent sur la rue.</summary>
    static void Awnings(List<V2> ext, Cand cd, in LocalFrame fr, Ctx ctx, double floor0, double levelH, double seed, int doorEdge, MeshAcc m)
    {
        var cols = new[] { new Color(0.62f, 0.12f, 0.12f), new Color(0.12f, 0.36f, 0.20f), new Color(0.15f, 0.25f, 0.50f), new Color(0.80f, 0.45f, 0.10f), new Color(0.35f, 0.12f, 0.22f) };
        var col = cols[(int)(seed * 977) % cols.Length];
        double h = floor0 + Math.Min(levelH, 3.4) - 0.35;
        var uv2 = new Vector2((float)WindowStyle.Awning, (float)seed);
        int done = 0;
        for (int i = 0; i < ext.Count && done < 3; i++)
        {
            var a = ext[i]; var b = ext[(i + 1) % ext.Count];
            double len = Poly2.Dist(a, b);
            if (len < 3.5) continue;
            var d = (b - a) * (1 / len); var n = d.Right;
            ToTile(fr, (a + b) * 0.5 + n * 1.5, ctx, out double x, out double y);
            if (At(ctx, x, y, cd) != null) continue; // mur mitoyen
            if (i != doorEdge && Hash(seed * 13 + i) > 0.7) continue;
            var a0 = a + d * 0.4; var b0 = b - d * 0.4;
            var a1 = a0 + n * 1.1; var b1 = b0 + n * 1.1;
            m.Quad(fr.P(a0, h + 0.45), fr.P(b0, h + 0.45), fr.P(b1, h), fr.P(a1, h), fr.Dir(n.X * 0.4, n.Y * 0.4, 1), col,
                new Vector2(0, 0), new Vector2((float)len, 0), new Vector2((float)len, 1), new Vector2(0, 1), uv2);
            m.Side(fr, a1, b1, h - 0.25, h, MeshAcc.Shade(col, 0.85f), uv2, new Vector2(0, 0), new Vector2((float)len, 1));
            done++;
        }
    }

    static double Hash(double x)
    {
        double s = Math.Sin(x * 12.9898 + 78.233) * 43758.5453;
        return s - Math.Floor(s);
    }
}
