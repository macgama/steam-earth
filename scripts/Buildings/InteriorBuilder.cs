using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>Intérieur d'un bâtiment, calculé hors du thread principal.</summary>
public sealed class InteriorData
{
    public BuildingInfo Info;
    public Vector3[] Vertices, Normals;
    public Color[] Colors;
    public Vector2[] Uv, Uv2;      // UV2.x : 0 = mat, 1 = lampe (émissive), 2 = parquet, 3 = carrelage, 4 = tableau noir
    public int[] Indices;
    public List<Obstacles.Shape> Shapes;
    public int FocusFloor;         // étage meublé autour duquel l'intérieur a été construit
    public bool HasStairs;
    public int AccessibleFloors;
    /// <summary>Palier de l'escalier à chaque étage (repère planète, pieds) : pour l'ascenseur.</summary>
    public Vec3d[] Landings;
    /// <summary>Pied de la première volée (repère planète) et direction de la montée : pour les tests.</summary>
    public Vec3d StairFoot, StairDir;
    public int Rooms, Furniture;
}

/// <summary>
/// Génère l'intérieur d'un bâtiment (procédural mais stable : même bâtiment, même intérieur) :
/// - une dalle par étage (plancher et plafond), le nombre d'étages venant d'OSM (building:levels) ou de la hauteur ;
/// - une cage d'escalier à deux volées alternées (une volée par étage, garde-corps au bord de la trémie) ;
/// - des pièces obtenues en découpant le plan (partition binaire), séparées par des cloisons percées de portes ;
/// - du mobilier selon la fonction : bancs et autel d'église, pupitres et tableau d'école, rayons et caisse de
///   magasin, lits d'hôpital, bureaux, tables de restaurant, chambres et cuisines d'appartement, rayonnages…
/// - la porte d'entrée ouverte et, si le rez-de-chaussée est au-dessus de la rue, quelques marches devant.
/// Toutes les surfaces ont leurs collisions (planchers, plafonds, escaliers, murs, meubles).
/// </summary>
public static class InteriorBuilder
{
    const double Slab = 0.2, WallT = 0.12, ExtWallT = 0.25, DoorW = 0.9, DoorTop = 2.1;

    sealed class Ctx
    {
        public BuildingInfo B;
        public LocalFrame Fr;
        public MeshAcc M = new();
        public List<Obstacles.Shape> S = new();
        public int Detail, Focus, Span;
        public Random Rng;
        // repère du rectangle englobant (axes des pièces)
        public V2 C0, U, V; public double L, W;
        public Core Core;
        public double[] FloorH;  // dessus de la dalle de chaque étage
        public int Floors;
        public Color FloorCol, WallCol, CeilCol; public float FloorPattern;
        public int Rooms, Items;
    }

    /// <summary>Cage d'escalier : rectangle orienté (centre, axe), deux couloirs (volées) séparés par un mur.</summary>
    sealed class Core
    {
        public V2 C, A;          // centre, axe (unitaire) le long des volées
        public double Lc, Wc, Flight, Landing, Lane, Run;
        public int Risers;
        public V2 P => new(-A.Y, A.X);
        public V2 At(double x, double y) => C + A * x + P * y;
        public double LaneY(int lane) => (lane == 0 ? -1 : 1) * (WallT / 2 + Lane / 2);
    }

    public static InteriorData Build(BuildingInfo b, int detail, int focusFloor, int span)
    {
        var x = new Ctx
        {
            B = b, Fr = b.Frame, Detail = detail, Focus = focusFloor, Span = span,
            Rng = new Random((int)(b.Seed * 1_000_000) ^ StableHash(b.Id)),
        };
        Palette(x);
        var box = Poly2.OrientedBox(b.Ring, out _, out _, out _) ?? new[] { new V2(-1, -1), new V2(1, -1), new V2(1, 1), new V2(-1, 1) };
        x.C0 = (box[0] + box[2]) * 0.5;
        x.U = (box[1] - box[0]).Normalized; x.V = new V2(-x.U.Y, x.U.X);
        x.L = Poly2.Dist(box[0], box[1]); x.W = Poly2.Dist(box[0], box[3]);

        int floors = Math.Max(1, b.Floors);
        if (floors > 1) x.Core = PlaceCore(x, b.LevelH);
        x.Floors = x.Core != null ? floors : 1;
        x.FloorH = new double[floors + 1];
        for (int k = 0; k <= floors; k++) x.FloorH[k] = b.Floor0 + k * b.LevelH;
        double ceilingTop = x.Floors == floors ? b.Eave : x.FloorH[1];

        Slabs(x, ceilingTop);
        ExteriorWalls(x);
        if (x.Core != null) Stairs(x, ceilingTop);
        for (int k = 0; k < x.Floors; k++)
        {
            bool furnished = Math.Abs(k - focusFloor) <= span;
            if (!furnished && x.Floors > 6) continue; // immeubles : seuls les étages autour du joueur ont des pièces
            double top = k + 1 < x.Floors ? x.FloorH[k + 1] - Slab : ceilingTop;
            Rooms(x, k, top, furnished && detail > 0);
        }
        Entrance(x);
        if (detail >= 3) Lamps(x, ceilingTop);

        var d = new InteriorData
        {
            Info = b, Shapes = x.S, FocusFloor = focusFloor, HasStairs = x.Core != null, AccessibleFloors = x.Floors,
            Vertices = x.M.V.ToArray(), Normals = x.M.N.ToArray(), Colors = x.M.C.ToArray(),
            Uv = x.M.Uv.ToArray(), Uv2 = x.M.Uv2.ToArray(), Indices = x.M.I.ToArray(), Rooms = x.Rooms, Furniture = x.Items,
        };
        if (x.Core != null)
        {
            d.Landings = new Vec3d[x.Floors];
            var lp = x.Core.At(-(x.Core.Flight / 2 + x.Core.Landing / 2), 0);
            for (int k = 0; k < x.Floors; k++) d.Landings[k] = x.Fr.C * (Geo.Radius + x.FloorH[k] + 0.05) + x.Fr.East * lp.X + x.Fr.North * lp.Y;
            var foot = x.Core.At(-x.Core.Flight / 2 - 0.6, x.Core.LaneY(0));
            d.StairFoot = x.Fr.C * (Geo.Radius + x.FloorH[0] + 0.05) + x.Fr.East * foot.X + x.Fr.North * foot.Y;
            d.StairDir = (x.Fr.East * x.Core.A.X + x.Fr.North * x.Core.A.Y).Normalized();
        }
        return d;
    }

    /// <summary>Hachage FNV-1a (string.GetHashCode change à chaque lancement).</summary>
    static int StableHash(string s)
    {
        uint h = 2166136261;
        foreach (char c in s) { h ^= c; h *= 16777619; }
        return (int)h;
    }

    static void Palette(Ctx x)
    {
        var k = x.B.Kind;
        x.WallCol = new Color(0.92f, 0.90f, 0.86f);
        x.CeilCol = new Color(0.95f, 0.95f, 0.93f);
        (x.FloorCol, x.FloorPattern) = k switch
        {
            BuildingKind.House or BuildingKind.Apartments or BuildingKind.Hotel or BuildingKind.Generic => (new Color(0.56f, 0.39f, 0.25f), 2f),
            BuildingKind.Office or BuildingKind.Civic or BuildingKind.Library or BuildingKind.University => (new Color(0.42f, 0.44f, 0.48f), 0f),
            BuildingKind.School or BuildingKind.Kindergarten => (new Color(0.62f, 0.56f, 0.44f), 0f),
            BuildingKind.Hospital => (new Color(0.80f, 0.83f, 0.82f), 3f),
            BuildingKind.Shop or BuildingKind.Supermarket or BuildingKind.Restaurant => (new Color(0.78f, 0.77f, 0.74f), 3f),
            BuildingKind.Church or BuildingKind.Cathedral or BuildingKind.Chapel or BuildingKind.Temple or BuildingKind.Museum or BuildingKind.TrainStation => (new Color(0.62f, 0.60f, 0.56f), 3f),
            BuildingKind.Mosque => (new Color(0.55f, 0.18f, 0.16f), 0f),
            BuildingKind.SportsHall => (new Color(0.78f, 0.60f, 0.37f), 2f),
            _ => (new Color(0.55f, 0.55f, 0.53f), 0f), // béton
        };
        if (k is BuildingKind.Church or BuildingKind.Cathedral or BuildingKind.Chapel) x.WallCol = new Color(0.80f, 0.76f, 0.68f);
        if (k is BuildingKind.Industrial or BuildingKind.Warehouse or BuildingKind.Farm or BuildingKind.FireStation) { x.WallCol = new Color(0.70f, 0.70f, 0.69f); x.CeilCol = new Color(0.60f, 0.61f, 0.62f); }
        if (k is BuildingKind.Kindergarten) x.WallCol = new Color(0.95f, 0.88f, 0.70f);
    }

    static Vector2 Mat(float kind) => new(kind, 0);

    // ------------------------------------------------------------------ cage d'escalier

    static Core PlaceCore(Ctx x, double levelH)
    {
        // escalier confortable, sinon raide (petites maisons)
        foreach (var (run, lane, landing, rise) in new[] { (0.27, 1.1, 1.2, 0.185), (0.23, 0.9, 1.0, 0.21) })
        {
            int risers = Math.Max(2, (int)Math.Ceiling(levelH / rise));
            double flight = risers * run;
            var c = new Core { Lane = lane, Run = run, Risers = risers, Flight = flight, Landing = landing, Lc = flight + 2 * landing, Wc = 2 * lane + WallT };
            V2 door = (x.B.DoorA + x.B.DoorB) * 0.5;
            double best = double.MaxValue; V2 bestC = default, bestA = default;
            double step = Math.Max(0.5, Math.Max(x.L, x.W) / 40);
            for (int o = 0; o < 2; o++)
            {
                var axis = o == 0 ? x.U : x.V;
                for (double s = -x.L / 2; s <= x.L / 2; s += step)
                    for (double t = -x.W / 2; t <= x.W / 2; t += step)
                    {
                        var ctr = x.C0 + x.U * s + x.V * t;
                        double score = (ctr - x.C0).Length;
                        if (score >= best) continue;
                        if (!Poly2.RectInside(x.B.Ring, x.B.Holes, ctr, axis, c.Lc / 2 + 0.9, c.Wc / 2 + 0.15, 0.02)) continue;
                        var dd = door - ctr;
                        if (Math.Abs(dd.Dot(axis)) < c.Lc / 2 + 2.0 && Math.Abs(dd.Dot(new V2(-axis.Y, axis.X))) < c.Wc / 2 + 1.5) continue;
                        if (Overlap(ctr, axis, c.Lc / 2, c.Wc / 2, door - x.B.DoorOut * 2.0, x.B.DoorOut, 2.0, 0.9)) continue; // couloir d'entrée
                        best = score; bestC = ctr; bestA = axis;
                    }
            }
            if (best < double.MaxValue) { c.C = bestC; c.A = bestA; return c; }
        }
        return null;
    }

    /// <summary>Rectangle d'une volée (couloir <paramref name="lane"/>) dans le sens direct, ou indirect pour un trou.</summary>
    static List<V2> LaneRect(Core c, int lane, bool hole)
    {
        double y = c.LaneY(lane), h = c.Lane / 2 + 0.02, f = c.Flight / 2;
        var r = new List<V2> { c.At(-f, y - h), c.At(f, y - h), c.At(f, y + h), c.At(-f, y + h) };
        if (hole) r.Reverse();
        return r;
    }

    static void Stairs(Ctx x, double ceilingTop)
    {
        var c = x.Core; var fr = x.Fr; var m = x.M;
        double bottom = x.FloorH[0] - 0.05;
        var wcol = x.WallCol;
        // murs latéraux de la cage (toute la hauteur) et mur entre les volées
        foreach (int side in new[] { -1, 1 })
            WallBox(x, c.At(-c.Lc / 2, side * (c.Wc / 2 + WallT / 2)), c.At(c.Lc / 2, side * (c.Wc / 2 + WallT / 2)), WallT, bottom, ceilingTop, wcol, true);
        WallBox(x, c.At(-c.Flight / 2, 0), c.At(c.Flight / 2, 0), WallT, bottom, ceilingTop, wcol, true);

        var stepCol = x.B.Kind is BuildingKind.House or BuildingKind.Apartments ? new Color(0.50f, 0.35f, 0.22f) : new Color(0.66f, 0.65f, 0.62f);
        var rail = new Color(0.30f, 0.30f, 0.32f);
        for (int j = 0; j + 1 < x.Floors; j++)
        {
            int lane = j % 2;
            double dir = j % 2 == 0 ? 1 : -1;
            double e0 = -dir * c.Flight / 2; // bas de la volée
            double y = c.LaneY(lane);
            double h0 = x.FloorH[j], h1 = x.FloorH[j + 1];
            double rise = (h1 - h0) / c.Risers;
            for (int i = 0; i < c.Risers; i++)
            {
                var sc = c.At(e0 + dir * (i + 0.5) * c.Run, y);
                m.Box(fr, sc, c.A, c.Run / 2, c.Lane / 2, h0 - 0.02, h0 + (i + 1) * rise, stepCol, false, 1f, Mat(0));
            }
            // collisions : rampe qui suit le nez des marches, palier haut à plat
            var ring = new[] { c.At(e0, y - c.Lane / 2), c.At(-e0, y - c.Lane / 2), c.At(-e0, y + c.Lane / 2), c.At(e0, y + c.Lane / 2) };
            x.S.Add(Obstacles.Shape.Ramp(fr.World(ring), fr.World(c.At(e0, y)), fr.World(c.At(-e0 - dir * c.Run, y)), h0 + rise, h1));
            // garde-corps au bord de la trémie (étage du dessus, côté bas de la volée)
            WallBox(x, c.At(e0, y - c.Lane / 2), c.At(e0, y + c.Lane / 2), 0.06, h1, h1 + 1.0, rail, true);
            // main courante le long du mur central
            var hr0 = c.At(e0, y - Math.Sign(y) * (c.Lane / 2 - 0.05)); var hr1 = c.At(-e0, y - Math.Sign(y) * (c.Lane / 2 - 0.05));
            _ = hr0; _ = hr1;
        }
    }

    // ------------------------------------------------------------------ dalles, murs

    static void Slabs(Ctx x, double ceilingTop)
    {
        var b = x.B; var fr = x.Fr;
        for (int k = 0; k < x.Floors; k++)
        {
            double h = x.FloorH[k];
            var holes = new List<List<V2>>(b.Holes);
            if (k >= 1 && x.Core != null) holes.Add(LaneRect(x.Core, (k - 1) % 2, true));
            Surface(x, b.Ring, holes, h, x.FloorCol, true, Mat(x.FloorPattern));
            if (k >= 1) Surface(x, b.Ring, holes, h - Slab, x.CeilCol, false, Mat(0));
            // bord de la trémie
            if (k >= 1 && x.Core != null)
            {
                var hr = LaneRect(x.Core, (k - 1) % 2, false);
                for (int i = 0; i < 4; i++) x.M.Side(fr, hr[(i + 1) % 4], hr[i], h - Slab, h, x.CeilCol);
            }
            var floor = Obstacles.Shape.Polygon(fr.World(b.Ring), WorldRings(fr, holes), k == 0 ? h - 0.6 : h - Slab, h);
            floor.Floor = true; floor.Clears = false;
            x.S.Add(floor);
        }
        // plafond du dernier étage accessible (sous le toit)
        Surface(x, b.Ring, b.Holes, ceilingTop - 0.06, x.CeilCol, false, Mat(0));
        // le toit reste praticable (le volume plein du bâtiment est désactivé quand l'intérieur existe)
        var s = b.Solid;
        var roof = Obstacles.Shape.Polygon(fr.World(b.Ring), WorldRings(fr, b.Holes), ceilingTop - 0.06, s.Top);
        roof.Floor = true; roof.Clears = false;
        if (s.RidgeA is Vec3d ra && s.RidgeB is Vec3d rb) roof.SetPitched(ra, rb, s.Top, s.Eave, s.HalfWidth);
        else roof.Eave = s.Top;
        x.S.Add(roof);
    }

    static Vec3d[][] WorldRings(in LocalFrame fr, List<List<V2>> rings)
    {
        if (rings == null || rings.Count == 0) return null;
        var r = new Vec3d[rings.Count][];
        for (int i = 0; i < rings.Count; i++) r[i] = fr.World(rings[i]);
        return r;
    }

    static void Surface(Ctx x, List<V2> ring, List<List<V2>> holes, double h, Color col, bool up, Vector2 uv2)
    {
        var pts = new List<V2>();
        var tris = Poly2.Triangulate(ring, holes, pts);
        var n = up ? x.Fr.Up : -x.Fr.Up;
        int k = x.M.Count;
        foreach (var p in pts) x.M.Vert(x.Fr.P(p, h), n, col, new Vector2((float)p.Dot(x.U), (float)p.Dot(x.V)), uv2);
        for (int i = 0; i < tris.Count; i += 3)
        {
            if (up) { x.M.I.Add(k + tris[i]); x.M.I.Add(k + tris[i + 1]); x.M.I.Add(k + tris[i + 2]); }
            else { x.M.I.Add(k + tris[i]); x.M.I.Add(k + tris[i + 2]); x.M.I.Add(k + tris[i + 1]); }
        }
    }

    /// <summary>Mur droit d'épaisseur <paramref name="t"/> centré sur a–b, avec collisions.</summary>
    static void WallBox(Ctx x, V2 a, V2 b, double t, double h0, double h1, Color col, bool collide, bool draw = true)
    {
        double len = Poly2.Dist(a, b);
        if (len < 0.02 || h1 - h0 < 0.02) return;
        var u = (b - a) * (1 / len);
        if (draw) x.M.Box(x.Fr, (a + b) * 0.5, u, len / 2, t / 2, h0, h1, col, true, 1f, Mat(0));
        if (collide) x.S.Add(Obstacles.Shape.Solid3D(x.Fr.World(RectRing((a + b) * 0.5, u, len / 2, t / 2)), h0, h1));
    }

    static V2[] RectRing(V2 c, V2 u, double hu, double hv)
    {
        var v = new V2(-u.Y, u.X);
        return new[] { c - u * hu - v * hv, c + u * hu - v * hv, c + u * hu + v * hv, c - u * hu + v * hv };
    }

    /// <summary>Murs extérieurs (collisions seulement : la façade dessinée par la tuile se voit des deux côtés),
    /// avec l'ouverture de la porte d'entrée.</summary>
    static void ExteriorWalls(Ctx x)
    {
        var b = x.B;
        void Ring(List<V2> r)
        {
            for (int i = 0; i < r.Count; i++)
            {
                var a = r[i]; var c = r[(i + 1) % r.Count];
                double len = Poly2.Dist(a, c);
                if (len < 0.02) continue;
                var u = (c - a) * (1 / len); var inward = u.Right * -1;
                bool door = r == b.Ring && Poly2.SegDist(a, c, b.DoorA) < 0.01 && Poly2.SegDist(a, c, b.DoorB) < 0.01;
                if (door)
                {
                    double ta = (b.DoorA - a).Dot(u), tb = (b.DoorB - a).Dot(u);
                    Piece(a, u, inward, 0, ta, b.BaseH, b.Eave);
                    Piece(a, u, inward, tb, len, b.BaseH, b.Eave);
                    Piece(a, u, inward, ta, tb, b.Floor0 + b.DoorH, b.Eave);
                    Piece(a, u, inward, ta, tb, b.BaseH, b.Floor0);
                }
                else Piece(a, u, inward, 0, len, b.BaseH, b.Eave);
            }
        }
        void Piece(V2 a, V2 u, V2 inward, double s0, double s1, double h0, double h1)
        {
            if (s1 - s0 < 0.02 || h1 - h0 < 0.02) return;
            var p0 = a + u * s0; var p1 = a + u * s1;
            var ring = new[] { p0, p1, p1 + inward * ExtWallT, p0 + inward * ExtWallT };
            x.S.Add(Obstacles.Shape.Solid3D(x.Fr.World(ring), h0, h1));
        }
        Ring(b.Ring);
        foreach (var h in b.Holes) Ring(h);
    }

    // ------------------------------------------------------------------ pièces

    sealed class Room { public double S0, S1, T0, T1; public int Floor; public string Use; }

    static double MinRoom(BuildingKind k, int floor) => k switch
    {
        BuildingKind.House or BuildingKind.Apartments or BuildingKind.Hotel or BuildingKind.Generic => 3.4,
        BuildingKind.Office or BuildingKind.Civic or BuildingKind.Library or BuildingKind.Hospital => 4.5,
        BuildingKind.University => floor == 0 ? 9 : 6.5,
        BuildingKind.School or BuildingKind.Kindergarten => 6.5,
        BuildingKind.Museum => 8,
        BuildingKind.Shop or BuildingKind.Restaurant => floor == 0 ? 0 : 3.4,
        _ => 0, // grand volume d'un seul tenant
    };

    static string UseOf(BuildingKind k, int floor, bool shopfront) => k switch
    {
        BuildingKind.Shop => floor == 0 ? "shop" : "flat",
        BuildingKind.Restaurant => floor == 0 ? "restaurant" : "flat",
        BuildingKind.Supermarket => floor == 0 ? "market" : "office",
        _ when shopfront && floor == 0 => "shop",
        BuildingKind.House or BuildingKind.Apartments or BuildingKind.Generic => "flat",
        BuildingKind.Hotel => floor == 0 ? "lobby" : "hotel",
        BuildingKind.Office or BuildingKind.Civic => "office",
        BuildingKind.School or BuildingKind.Kindergarten => "class",
        BuildingKind.University => floor == 0 ? "lecture" : "class",
        BuildingKind.Hospital => floor == 0 ? "lobby" : "ward",
        BuildingKind.Library => "library",
        BuildingKind.Museum => "museum",
        BuildingKind.Church or BuildingKind.Cathedral or BuildingKind.Chapel or BuildingKind.Temple => "nave",
        BuildingKind.Mosque => "prayer",
        BuildingKind.TrainStation => "station",
        BuildingKind.SportsHall => "sports",
        BuildingKind.Industrial or BuildingKind.Warehouse or BuildingKind.Farm or BuildingKind.FireStation => "storage",
        _ => "flat",
    };

    static void Rooms(Ctx x, int floor, double top, bool furnish)
    {
        var b = x.B;
        // tirages propres à chaque étage : un étage a toujours le même plan, quel que soit l'étage du joueur
        x.Rng = new Random(((int)(b.Seed * 1_000_000) ^ StableHash(b.Id)) + floor * 7919);
        bool shopfront = b.Style is WindowStyle.ShopResidential or WindowStyle.ShopOffice;
        double min = MinRoom(b.Kind, floor);
        if (shopfront && floor == 0) min = 0;
        string use = UseOf(b.Kind, floor, shopfront);
        var rooms = new List<Room>();
        var root = new Room { S0 = -x.L / 2, S1 = x.L / 2, T0 = -x.W / 2, T1 = x.W / 2, Floor = floor, Use = use };
        var doors = new List<V2>();
        var clear = new List<(V2, V2, double, double)>();
        if (floor == 0)
        {
            var mid = (b.DoorA + b.DoorB) * 0.5;
            doors.Add(mid);
            // couloir libre de 1,8 m de large sur 4 m depuis la porte d'entrée (sinon un rayon peut barrer l'accès)
            clear.Add((mid - b.DoorOut * 2.0, b.DoorOut, 2.0, 0.9));
        }
        Split(x, root, min, floor, x.FloorH[floor], top, rooms, doors, 0);
        x.Rooms += rooms.Count;
        if (!furnish) return;
        // pièces d'un appartement : la plus grande est le séjour, puis chambres, cuisine…
        if (use is "flat" or "hotel")
        {
            rooms.Sort((p, q) => ((q.S1 - q.S0) * (q.T1 - q.T0)).CompareTo((p.S1 - p.S0) * (p.T1 - p.T0)));
            for (int i = 0; i < rooms.Count; i++)
                rooms[i].Use = use == "hotel" ? "bedroom" : i == 0 ? "living" : i % 3 == 1 ? "bedroom" : i % 3 == 2 ? "kitchen" : "bedroom";
        }
        foreach (var r in rooms) Furnish(x, r, x.FloorH[floor], top, doors, clear);
    }

    static void Split(Ctx x, Room r, double min, int floor, double h0, double h1, List<Room> leaves, List<V2> doors, int depth)
    {
        double ls = r.S1 - r.S0, lt = r.T1 - r.T0;
        if (min <= 0 || depth > 7 || (ls < 2 * min && lt < 2 * min)) { leaves.Add(r); return; }
        for (int attempt = 0; attempt < 6; attempt++)
        {
            bool alongS = (attempt % 2 == 0) == (ls >= lt); // découpe perpendiculaire au plus grand côté d'abord
            double span = alongS ? ls : lt;
            if (span < 2 * min) continue;
            double lo = alongS ? r.S0 : r.T0;
            double cut = lo + span * (0.5 + (x.Rng.NextDouble() - 0.5) * 0.3);
            cut = Math.Clamp(cut, lo + min, lo + span - min);
            V2 a, c;
            if (alongS) { a = x.C0 + x.U * cut + x.V * r.T0; c = x.C0 + x.U * cut + x.V * r.T1; }
            else { a = x.C0 + x.U * r.S0 + x.V * cut; c = x.C0 + x.U * r.S1 + x.V * cut; }
            if (Blocked(x, a, c, doors)) continue;
            // cloison découpée par le contour du bâtiment, une porte par morceau
            var parts = Poly2.ClipSegment(x.B.Ring, x.B.Holes, a, c);
            if (parts.Count == 0) continue;
            double len = Poly2.Dist(a, c);
            foreach (var (t0, t1) in parts)
            {
                var p0 = a + (c - a) * t0; var p1 = a + (c - a) * t1;
                double plen = (t1 - t0) * len;
                if (plen < 1.6) { WallBox(x, p0, p1, WallT, h0, h1, x.WallCol, true); continue; }
                double dc = 0.5 + (x.Rng.NextDouble() - 0.5) * Math.Max(0, plen - 2.4) / plen;
                var d0 = p0 + (p1 - p0) * (dc - DoorW / 2 / plen); var d1 = p0 + (p1 - p0) * (dc + DoorW / 2 / plen);
                WallBox(x, p0, d0, WallT, h0, h1, x.WallCol, true);
                WallBox(x, d1, p1, WallT, h0, h1, x.WallCol, true);
                if (h0 + DoorTop < h1) WallBox(x, d0, d1, WallT, h0 + DoorTop, h1, x.WallCol, true);
                doors.Add((d0 + d1) * 0.5);
            }
            Room r1, r2;
            if (alongS) { r1 = new Room { S0 = r.S0, S1 = cut, T0 = r.T0, T1 = r.T1, Floor = floor, Use = r.Use }; r2 = new Room { S0 = cut, S1 = r.S1, T0 = r.T0, T1 = r.T1, Floor = floor, Use = r.Use }; }
            else { r1 = new Room { S0 = r.S0, S1 = r.S1, T0 = r.T0, T1 = cut, Floor = floor, Use = r.Use }; r2 = new Room { S0 = r.S0, S1 = r.S1, T0 = cut, T1 = r.T1, Floor = floor, Use = r.Use }; }
            Split(x, r1, min, floor, h0, h1, leaves, doors, depth + 1);
            Split(x, r2, min, floor, h0, h1, leaves, doors, depth + 1);
            return;
        }
        leaves.Add(r);
    }

    /// <summary>Une cloison a–c traverserait-elle la cage d'escalier (et ses paliers) ou passerait-elle devant une porte ?</summary>
    static bool Blocked(Ctx x, V2 a, V2 c, List<V2> doors)
    {
        foreach (var d in doors) if (Poly2.SegDist(a, c, d) < 1.1) return true;
        var k = x.Core;
        if (k == null) return false;
        var ring = RectRing(k.C, k.A, k.Lc / 2 + 1.0, k.Wc / 2 + WallT + 0.15);
        // segment contre rectangle : extrémités dedans, ou croisement d'un côté
        bool In(V2 p) { var d = p - k.C; return Math.Abs(d.Dot(k.A)) < k.Lc / 2 + 1.0 && Math.Abs(d.Dot(k.P)) < k.Wc / 2 + WallT + 0.15; }
        if (In(a) || In(c) || In((a + c) * 0.5)) return true;
        for (int i = 0; i < 4; i++) if (SegCross(a, c, ring[i], ring[(i + 1) % 4])) return true;
        return false;
    }

    static bool SegCross(V2 a, V2 b, V2 c, V2 d)
    {
        double O(V2 p, V2 q, V2 r) => (q.X - p.X) * (r.Y - p.Y) - (q.Y - p.Y) * (r.X - p.X);
        return O(a, b, c) * O(a, b, d) < 0 && O(c, d, a) * O(c, d, b) < 0;
    }

    // ------------------------------------------------------------------ mobilier

    /// <summary>Emplacement libre pour un meuble (rectangle aligné sur les axes des pièces).</summary>
    sealed class Placer
    {
        readonly Ctx _x; readonly Room _r; readonly List<V2> _doors;
        public readonly List<(V2 c, V2 u, double hu, double hv)> Taken = new();
        readonly List<(V2 c, V2 u, double hu, double hv)> _clear;
        public Placer(Ctx x, Room r, List<V2> doors, List<(V2, V2, double, double)> clear) { _x = x; _r = r; _doors = doors; _clear = clear; }

        public bool Free(V2 c, V2 u, double hu, double hv)
        {
            if (!Poly2.RectInside(_x.B.Ring, _x.B.Holes, c, u, hu, hv, 0.05)) return false;
            // passages à laisser libres (couloir d'entrée depuis la porte de la rue)
            foreach (var (zc, zu, zhu, zhv) in _clear) if (Overlap(c, u, hu, hv, zc, zu, zhu, zhv)) return false;
            foreach (var d in _doors)
            {
                var dd = d - c;
                if (Math.Abs(dd.Dot(u)) < hu + 0.9 && Math.Abs(dd.Dot(new V2(-u.Y, u.X))) < hv + 0.9) return false;
            }
            var k = _x.Core;
            if (k != null)
            {
                var dd = c - k.C;
                if (Math.Abs(dd.Dot(k.A)) < k.Lc / 2 + 1.1 + Math.Max(hu, hv) && Math.Abs(dd.Dot(k.P)) < k.Wc / 2 + 0.3 + Math.Max(hu, hv)) return false;
            }
            foreach (var (tc, tu, thu, thv) in Taken)
            {
                // boîtes alignées sur les mêmes axes (u ou v) : test sur l'axe U du bâtiment
                var d = c - tc;
                double ext1u = Math.Abs(u.Dot(_x.U)) > 0.5 ? hu : hv, ext1v = Math.Abs(u.Dot(_x.U)) > 0.5 ? hv : hu;
                double ext2u = Math.Abs(tu.Dot(_x.U)) > 0.5 ? thu : thv, ext2v = Math.Abs(tu.Dot(_x.U)) > 0.5 ? thv : thu;
                if (Math.Abs(d.Dot(_x.U)) < ext1u + ext2u + 0.05 && Math.Abs(d.Dot(_x.V)) < ext1v + ext2v + 0.05) return false;
            }
            return true;
        }

        /// <summary>Contre un mur de la pièce : <paramref name="w"/> le long du mur, <paramref name="d"/> en profondeur.
        /// Retourne le centre et la direction « vers la pièce » (face avant du meuble).</summary>
        public bool AgainstWall(double w, double d, out V2 c, out V2 front, int tries = 14)
        {
            var x = _x; var r = _r;
            c = default; front = default;
            for (int i = 0; i < tries; i++)
            {
                int side = x.Rng.Next(4);
                double s, t; V2 f;
                double gap = WallT / 2 + 0.06 + d / 2;
                switch (side)
                {
                    case 0: s = Lerp(r.S0 + w / 2 + 0.2, r.S1 - w / 2 - 0.2); t = r.T0 + gap; f = x.V; break;
                    case 1: s = Lerp(r.S0 + w / 2 + 0.2, r.S1 - w / 2 - 0.2); t = r.T1 - gap; f = x.V * -1; break;
                    case 2: s = r.S0 + gap; t = Lerp(r.T0 + w / 2 + 0.2, r.T1 - w / 2 - 0.2); f = x.U; break;
                    default: s = r.S1 - gap; t = Lerp(r.T0 + w / 2 + 0.2, r.T1 - w / 2 - 0.2); f = x.U * -1; break;
                }
                if (double.IsNaN(s) || double.IsNaN(t)) continue;
                var ctr = x.C0 + x.U * s + x.V * t;
                var along = new V2(f.Y, -f.X);
                // mur extérieur (contour irrégulier) : on recule le meuble jusqu'à ce qu'il tienne
                if (!Free(ctr, along, w / 2, d / 2)) continue;
                c = ctr; front = f;
                Taken.Add((ctr, along, w / 2, d / 2));
                return true;
            }
            return false;
        }

        public bool At(V2 c, V2 along, double hu, double hv)
        {
            if (!Free(c, along, hu, hv)) return false;
            Taken.Add((c, along, hu, hv));
            return true;
        }

        double Lerp(double a, double b) => a > b ? double.NaN : a + (b - a) * _x.Rng.NextDouble();
    }

    /// <summary>Meuble : boîte en coordonnées du meuble (x le long, y vers l'avant, z hauteur au-dessus du sol).</summary>
    static void Part(Ctx x, V2 c, V2 front, double x0, double x1, double y0, double y1, double z0, double z1, Color col, double floorH, float mat = 0)
    {
        var along = new V2(front.Y, -front.X);
        var pc = c + along * ((x0 + x1) / 2) + front * ((y0 + y1) / 2);
        x.M.Box(x.Fr, pc, along, (x1 - x0) / 2, (y1 - y0) / 2, floorH + z0, floorH + z1, col, false, 1f, Mat(mat));
    }

    static void Collide(Ctx x, V2 c, V2 along, double hu, double hv, double h0, double h1)
    {
        x.S.Add(Obstacles.Shape.Solid3D(x.Fr.World(RectRing(c, along, hu, hv)), h0, h1));
    }

    static readonly Color Wood = new(0.48f, 0.32f, 0.20f), LightWood = new(0.70f, 0.55f, 0.36f), White = new(0.92f, 0.92f, 0.90f),
        Fabric = new(0.30f, 0.38f, 0.52f), Dark = new(0.18f, 0.18f, 0.20f), Metal = new(0.60f, 0.62f, 0.64f), Green = new(0.25f, 0.45f, 0.22f);

    /// <summary>Deux rectangles orientés (centre, axe, demi-longueurs) se chevauchent-ils ? (axes séparateurs)</summary>
    static bool Overlap(V2 c1, V2 u1, double hu1, double hv1, V2 c2, V2 u2, double hu2, double hv2)
    {
        var v1 = new V2(-u1.Y, u1.X); var v2 = new V2(-u2.Y, u2.X); var d = c2 - c1;
        foreach (var a in new[] { u1, v1, u2, v2 })
        {
            double r1 = hu1 * Math.Abs(u1.Dot(a)) + hv1 * Math.Abs(v1.Dot(a));
            double r2 = hu2 * Math.Abs(u2.Dot(a)) + hv2 * Math.Abs(v2.Dot(a));
            if (Math.Abs(d.Dot(a)) > r1 + r2) return false;
        }
        return true;
    }

    static void Furnish(Ctx x, Room r, double h, double top, List<V2> doors, List<(V2, V2, double, double)> clear)
    {
        var p = new Placer(x, r, doors, clear);
        int detail = x.Detail;
        double rs = r.S1 - r.S0, rt = r.T1 - r.T0;
        var center = x.C0 + x.U * ((r.S0 + r.S1) / 2) + x.V * ((r.T0 + r.T1) / 2);
        // grand côté de la pièce
        var longAx = rs >= rt ? x.U : x.V; var shortAx = rs >= rt ? x.V : x.U;
        double lLen = Math.Max(rs, rt), sLen = Math.Min(rs, rt);
        switch (r.Use)
        {
            case "bedroom":
                if (p.AgainstWall(1.6, 2.1, out var bc, out var bf)) Bed(x, bc, bf, h, 1.6);
                if (p.AgainstWall(1.2, 0.6, out var wc, out var wf)) { Part(x, wc, wf, -0.6, 0.6, -0.3, 0.3, 0, 2.0, Wood, h); Collide(x, wc, new V2(wf.Y, -wf.X), 0.6, 0.3, h, h + 2.0); x.Items++; }
                if (detail >= 2 && p.AgainstWall(1.0, 0.5, out var dc, out var df)) Desk(x, dc, df, h, 1.0, false);
                break;
            case "living":
                if (p.AgainstWall(2.1, 0.9, out var sc, out var sf)) Sofa(x, sc, sf, h);
                if (detail >= 2 && p.At(center, longAx, 0.7, 0.45)) { Table(x, center, longAx, h, 1.4, 0.9, detail >= 3); }
                if (p.AgainstWall(1.6, 0.45, out var tc, out var tf)) { Part(x, tc, tf, -0.8, 0.8, -0.22, 0.22, 0, 0.5, Wood, h); Part(x, tc, tf, -0.55, 0.55, -0.05, 0.03, 0.55, 1.15, Dark, h); x.Items++; }
                if (detail >= 3 && p.AgainstWall(0.5, 0.5, out var pc, out var pf)) Plant(x, pc, h);
                break;
            case "kitchen":
                if (p.AgainstWall(Math.Min(3.0, lLen - 0.6), 0.62, out var kc, out var kf)) Kitchen(x, kc, kf, h, Math.Min(3.0, lLen - 0.6), detail);
                if (detail >= 2 && p.At(center, longAx, 0.55, 0.4)) Table(x, center, longAx, h, 1.1, 0.8, true);
                break;
            case "office":
                for (int i = 0; i < 12 && x.Items < 4000; i++)
                    if (p.AgainstWall(1.4, 0.75, out var oc, out var of)) Desk(x, oc, of, h, 1.4, detail >= 2); else break;
                if (detail >= 2 && lLen > 6) for (double s = -lLen / 2 + 2; s < lLen / 2 - 2; s += 2.6)
                    { var c = center + longAx * s; if (p.At(c, longAx, 0.75, 0.75)) { Desk(x, c - shortAx * 0.4, shortAx * -1, h, 1.4, detail >= 3); Desk(x, c + shortAx * 0.4, shortAx, h, 1.4, detail >= 3); } }
                if (detail >= 3 && p.AgainstWall(0.5, 0.5, out var opc, out _)) Plant(x, opc, h);
                break;
            case "class":
            case "lecture":
            {
                // tableau noir sur un petit côté, pupitres en rangées face à lui
                var front = longAx;
                var board = center - longAx * (lLen / 2 - WallT / 2 - 0.03);
                var bAlong = new V2(front.Y, -front.X);
                double bw = Math.Min(4, sLen - 1.5);
                if (bw > 1) { x.M.Quad(x.Fr.P(board - bAlong * (bw / 2), h + 0.9), x.Fr.P(board + bAlong * (bw / 2), h + 0.9), x.Fr.P(board + bAlong * (bw / 2), h + 2.1), x.Fr.P(board - bAlong * (bw / 2), h + 2.1), new Color(0.12f, 0.22f, 0.16f), Mat(4)); }
                if (p.At(board + front * 1.3, bAlong, 0.7, 0.35)) Desk(x, board + front * 1.3, front * -1, h, 1.4, false);
                for (double a = -lLen / 2 + 3.0; a < lLen / 2 - 1.0; a += 1.3)
                    for (double s = -sLen / 2 + 1.2; s < sLen / 2 - 0.8; s += 1.6)
                    {
                        var c = center + front * a + shortAx * s;
                        if (!p.At(c, bAlong, 0.6, 0.3)) continue;
                        Part(x, c, front * -1, -0.6, 0.6, -0.3, 0.3, 0.70, 0.75, LightWood, h);
                        Part(x, c, front * -1, -0.55, -0.5, -0.25, 0.25, 0, 0.70, Metal, h); Part(x, c, front * -1, 0.5, 0.55, -0.25, 0.25, 0, 0.70, Metal, h);
                        Collide(x, c, bAlong, 0.6, 0.3, h, h + 0.75);
                        if (detail >= 2) { Chair(x, c - front * 0.5 - bAlong * 0.3, front, h); Chair(x, c - front * 0.5 + bAlong * 0.3, front, h); }
                        x.Items++;
                    }
                break;
            }
            case "ward":
                for (int i = 0; i < 4; i++) if (p.AgainstWall(1.0, 2.1, out var hc, out var hf)) HospitalBed(x, hc, hf, h); else break;
                break;
            case "lobby":
                if (p.AgainstWall(Math.Min(4, lLen - 1), 0.8, out var lc, out var lf)) { Counter(x, lc, lf, h, Math.Min(4, lLen - 1), new Color(0.40f, 0.30f, 0.22f)); }
                if (detail >= 2) for (int i = 0; i < 3; i++) if (p.AgainstWall(2.0, 0.8, out var sc2, out var sf2)) Sofa(x, sc2, sf2, h);
                if (detail >= 3) for (int i = 0; i < 3; i++) if (p.AgainstWall(0.5, 0.5, out var pc2, out _)) Plant(x, pc2, h);
                break;
            case "shop":
            case "market":
            {
                // rayons parallèles au grand côté, caisse près de l'entrée
                if (p.AgainstWall(2.0, 0.8, out var cc, out var cf)) Counter(x, cc, cf, h, 2.0, new Color(0.30f, 0.32f, 0.36f));
                double shelfH = r.Use == "market" ? 1.8 : 1.6;
                for (double s = -sLen / 2 + 1.6; s < sLen / 2 - 1.2; s += 2.4)
                {
                    double len = lLen - 4.0;
                    if (len < 1.5) break;
                    var c = center + shortAx * s;
                    for (double l = len; l >= 1.5; l -= 1.5)
                        if (p.At(c, longAx, l / 2, 0.45)) { Shelf(x, c, longAx, l, h, shelfH, detail); break; }
                }
                for (int i = 0; i < 6; i++) if (p.AgainstWall(Math.Min(4, lLen / 2), 0.5, out var wc2, out var wf2)) Shelf(x, wc2 + wf2 * 0.0, new V2(wf2.Y, -wf2.X), Math.Min(4, lLen / 2), h, 2.0, detail, 0.25); else break;
                break;
            }
            case "restaurant":
                if (p.AgainstWall(Math.Min(4, lLen - 1), 0.7, out var rc, out var rf)) Counter(x, rc, rf, h, Math.Min(4, lLen - 1), new Color(0.35f, 0.22f, 0.14f));
                for (double a = -lLen / 2 + 1.6; a < lLen / 2 - 1.2; a += 2.2)
                    for (double s = -sLen / 2 + 1.5; s < sLen / 2 - 1.0; s += 2.2)
                    {
                        var c = center + longAx * a + shortAx * s;
                        if (p.At(c, longAx, 0.85, 0.85)) Table(x, c, longAx, h, 0.9, 0.9, detail >= 2);
                    }
                break;
            case "nave":
            {
                // allée centrale, bancs de part et d'autre, autel au fond (loin de la porte)
                var door = (x.B.DoorA + x.B.DoorB) * 0.5;
                var toAltar = (center - door).Dot(longAx) > 0 ? longAx : longAx * -1;
                var altar = center + toAltar * (lLen / 2 - 2.0);
                var aAlong = new V2(toAltar.Y, -toAltar.X);
                if (p.At(altar, aAlong, 1.2, 0.5))
                {
                    Part(x, altar, toAltar * -1, -1.2, 1.2, -0.5, 0.5, 0, 1.0, White, h);
                    Collide(x, altar, aAlong, 1.2, 0.5, h, h + 1.0);
                    if (detail >= 2) Part(x, altar + toAltar * 0.6, toAltar * -1, -0.05, 0.05, -0.05, 0.05, 1.0, 2.6, new Color(0.75f, 0.62f, 0.30f), h); // croix
                    if (detail >= 2) Part(x, altar + toAltar * 0.6, toAltar * -1, -0.5, 0.5, -0.05, 0.05, 2.0, 2.1, new Color(0.75f, 0.62f, 0.30f), h);
                    x.Items++;
                }
                double bench = Math.Min(4.5, (sLen - 2.0) / 2 - 0.6);
                if (detail >= 1 && bench > 0.8)
                    for (double a = lLen / 2 - 5.0; a > -lLen / 2 + 2.5; a -= 1.0)
                        foreach (int side in new[] { -1, 1 })
                        {
                            var c = center + toAltar * a + aAlong * (side * (1.0 + bench / 2));
                            if (!p.At(c, aAlong, bench / 2, 0.25)) continue;
                            Part(x, c, toAltar, -bench / 2, bench / 2, -0.22, 0.22, 0.42, 0.47, Wood, h);
                            Part(x, c, toAltar, -bench / 2, bench / 2, -0.25, -0.20, 0.0, 0.95, Wood, h);
                            Collide(x, c, aAlong, bench / 2, 0.25, h, h + 0.5);
                            x.Items++;
                        }
                break;
            }
            case "prayer":
                // tapis (couleur du sol), minbar contre le mur du fond
                if (p.AgainstWall(1.2, 1.0, out var mc, out var mf)) { Part(x, mc, mf, -0.6, 0.6, -0.5, 0.5, 0, 2.2, new Color(0.62f, 0.50f, 0.32f), h); Collide(x, mc, new V2(mf.Y, -mf.X), 0.6, 0.5, h, h + 2.2); x.Items++; }
                break;
            case "station":
                if (p.AgainstWall(Math.Min(5, lLen - 1), 0.8, out var gc, out var gf)) Counter(x, gc, gf, h, Math.Min(5, lLen - 1), new Color(0.45f, 0.46f, 0.50f));
                for (double a = -lLen / 2 + 3; a < lLen / 2 - 3; a += 3.5)
                {
                    var c = center + longAx * a;
                    if (p.At(c, longAx, 1.0, 0.3)) { Part(x, c, shortAx, -1.0, 1.0, -0.25, 0.25, 0.42, 0.47, Wood, h); Part(x, c, shortAx, -0.9, -0.8, -0.2, 0.2, 0, 0.42, Metal, h); Part(x, c, shortAx, 0.8, 0.9, -0.2, 0.2, 0, 0.42, Metal, h); Collide(x, c, longAx, 1.0, 0.3, h, h + 0.47); x.Items++; }
                }
                break;
            case "sports":
                // lignes du terrain au sol et paniers aux deux bouts
                SportsLines(x, center, longAx, lLen, sLen, h);
                foreach (int sgn in new[] { -1, 1 })
                {
                    var c = center + longAx * (sgn * (lLen / 2 - 1.0));
                    if (p.At(c, shortAx, 0.3, 0.3)) { Part(x, c, longAx * -sgn, -0.08, 0.08, -0.08, 0.08, 0, 3.05, Metal, h); Part(x, c + longAx * (-sgn * 0.3), longAx * -sgn, -0.9, 0.9, -0.03, 0.03, 2.9, 3.95, White, h); x.Items++; }
                }
                break;
            case "library":
                for (double s = -sLen / 2 + 1.5; s < sLen / 2 - 1.0; s += 2.0)
                {
                    var c = center + shortAx * s; double len = lLen - 3.5;
                    if (len > 1.5 && p.At(c, longAx, len / 2, 0.3)) Bookcase(x, c, longAx, len, h);
                }
                break;
            case "museum":
                for (int i = 0; i < 4; i++)
                {
                    var c = center + longAx * ((i - 1.5) * lLen / 5) + shortAx * ((i % 2 - 0.5) * sLen * 0.3);
                    if (!p.At(c, longAx, 0.45, 0.45)) continue;
                    Part(x, c, longAx, -0.45, 0.45, -0.45, 0.45, 0, 1.0, White, h);
                    if (detail >= 2) Part(x, c, longAx, -0.2, 0.2, -0.2, 0.2, 1.0, 1.8, new Color(0.55f, 0.45f, 0.30f), h);
                    Collide(x, c, longAx, 0.45, 0.45, h, h + 1.0); x.Items++;
                }
                if (detail >= 2) Paintings(x, r, h, p);
                break;
            case "storage":
                for (double s = -sLen / 2 + 2.0; s < sLen / 2 - 1.5; s += 3.5)
                {
                    var c = center + shortAx * s; double len = Math.Min(lLen - 4.0, 18);
                    if (len > 2 && p.At(c, longAx, len / 2, 0.55)) Rack(x, c, longAx, len, h, Math.Min(top - h - 0.5, 4.5), detail);
                }
                break;
        }
    }

    static void Bed(Ctx x, V2 c, V2 f, double h, double w)
    {
        // tête contre le mur : le meuble « regarde » vers la pièce, longueur selon f
        Part(x, c, f, -w / 2, w / 2, -1.05, 1.05, 0, 0.35, Wood, h);
        Part(x, c, f, -w / 2 + 0.05, w / 2 - 0.05, -1.0, 1.0, 0.35, 0.55, White, h);
        Part(x, c, f, -w / 2 + 0.05, w / 2 - 0.05, -0.2, 1.0, 0.55, 0.60, new Color(0.55f, 0.22f + (float)x.Rng.NextDouble() * 0.3f, 0.25f), h);
        Part(x, c, f, -w / 2 + 0.15, w / 2 - 0.15, -0.95, -0.6, 0.55, 0.68, White, h);
        Part(x, c, f, -w / 2, w / 2, -1.08, -1.0, 0, 1.0, Wood, h);
        Collide(x, c, new V2(f.Y, -f.X), w / 2, 1.05, h, h + 0.6);
        x.Items++;
    }

    static void HospitalBed(Ctx x, V2 c, V2 f, double h)
    {
        Part(x, c, f, -0.45, 0.45, -1.0, 1.0, 0.45, 0.62, White, h);
        Part(x, c, f, -0.48, 0.48, -1.05, -0.98, 0, 1.1, Metal, h);
        Part(x, c, f, -0.48, 0.48, 0.98, 1.05, 0, 0.8, Metal, h);
        foreach (var (a, b) in new[] { (-0.42, -0.9), (0.42, -0.9), (-0.42, 0.9), (0.42, 0.9) }) Part(x, c, f, a - 0.03, a + 0.03, b - 0.03, b + 0.03, 0, 0.45, Metal, h);
        Collide(x, c, new V2(f.Y, -f.X), 0.48, 1.05, h, h + 0.62);
        x.Items++;
    }

    static void Sofa(Ctx x, V2 c, V2 f, double h)
    {
        var col = new Color(0.30f + (float)x.Rng.NextDouble() * 0.3f, 0.30f, 0.34f + (float)x.Rng.NextDouble() * 0.2f);
        Part(x, c, f, -1.0, 1.0, -0.45, 0.45, 0, 0.45, col, h);
        Part(x, c, f, -1.0, 1.0, -0.45, -0.25, 0.45, 0.85, col, h);
        Part(x, c, f, -1.0, -0.85, -0.45, 0.45, 0.45, 0.62, col, h);
        Part(x, c, f, 0.85, 1.0, -0.45, 0.45, 0.45, 0.62, col, h);
        Collide(x, c, new V2(f.Y, -f.X), 1.0, 0.45, h, h + 0.45);
        x.Items++;
    }

    static void Table(Ctx x, V2 c, V2 along, double h, double w, double d, bool chairs)
    {
        var f = new V2(-along.Y, along.X);
        Part(x, c, f, -w / 2, w / 2, -d / 2, d / 2, 0.72, 0.76, LightWood, h);
        foreach (var (a, b) in new[] { (-1, -1), (1, -1), (-1, 1), (1, 1) }) Part(x, c, f, a * (w / 2 - 0.06) - 0.03, a * (w / 2 - 0.06) + 0.03, b * (d / 2 - 0.06) - 0.03, b * (d / 2 - 0.06) + 0.03, 0, 0.72, Wood, h);
        Collide(x, c, along, w / 2, d / 2, h, h + 0.76);
        if (chairs)
            foreach (int s in new[] { -1, 1 })
            {
                Chair(x, c + f * (s * (d / 2 + 0.3)), f * -s, h);
                if (w > 1.2) { Chair(x, c + f * (s * (d / 2 + 0.3)) + along * 0.45, f * -s, h); Chair(x, c + f * (s * (d / 2 + 0.3)) - along * 0.45, f * -s, h); }
            }
        x.Items++;
    }

    static void Chair(Ctx x, V2 c, V2 f, double h)
    {
        Part(x, c, f, -0.2, 0.2, -0.2, 0.2, 0.42, 0.46, Wood, h);
        Part(x, c, f, -0.2, 0.2, -0.22, -0.18, 0.46, 0.9, Wood, h);
        Part(x, c, f, -0.03, 0.03, -0.03, 0.03, 0, 0.42, Dark, h);
    }

    static void Desk(Ctx x, V2 c, V2 f, double h, double w, bool chair)
    {
        Part(x, c, f, -w / 2, w / 2, -0.35, 0.35, 0.72, 0.75, new Color(0.80f, 0.78f, 0.74f), h);
        Part(x, c, f, -w / 2, -w / 2 + 0.05, -0.33, 0.33, 0, 0.72, Metal, h);
        Part(x, c, f, w / 2 - 0.05, w / 2, -0.33, 0.33, 0, 0.72, Metal, h);
        if (x.Detail >= 3) Part(x, c, f, -0.28, 0.28, -0.25, -0.22, 0.80, 1.15, Dark, h); // écran
        Collide(x, c, new V2(f.Y, -f.X), w / 2, 0.35, h, h + 0.75);
        if (chair) Chair(x, c + f * 0.6, f * -1, h);
        x.Items++;
    }

    static void Kitchen(Ctx x, V2 c, V2 f, double h, double w, int detail)
    {
        Part(x, c, f, -w / 2, w / 2, -0.31, 0.29, 0, 0.88, White, h);
        Part(x, c, f, -w / 2, w / 2, -0.31, 0.31, 0.88, 0.92, new Color(0.30f, 0.30f, 0.32f), h);
        if (detail >= 3) Part(x, c, f, -w / 2, w / 2, -0.31, 0.04, 1.5, 2.2, White, h);
        Part(x, c, f, w / 2 - 0.65, w / 2 - 0.05, -0.30, 0.30, 0, 1.9, Metal, h); // réfrigérateur
        Collide(x, c, new V2(f.Y, -f.X), w / 2, 0.31, h, h + 0.92);
        x.Items++;
    }

    static void Counter(Ctx x, V2 c, V2 f, double h, double w, Color col)
    {
        Part(x, c, f, -w / 2, w / 2, -0.35, 0.35, 0, 1.0, col, h);
        Part(x, c, f, -w / 2 - 0.02, w / 2 + 0.02, -0.37, 0.37, 1.0, 1.05, new Color(0.85f, 0.84f, 0.80f), h);
        if (x.Detail >= 3) Part(x, c, f, -0.2, 0.2, -0.15, 0.15, 1.05, 1.35, Dark, h); // caisse
        Collide(x, c, new V2(f.Y, -f.X), w / 2, 0.37, h, h + 1.05);
        x.Items++;
    }

    static void Shelf(Ctx x, V2 c, V2 along, double len, double h, double height, int detail, double depth = 0.45)
    {
        var f = new V2(-along.Y, along.X);
        Part(x, c, f, -len / 2, len / 2, -depth, depth, 0, 0.12, Metal, h);
        Part(x, c, f, -len / 2, len / 2, -0.04, 0.04, 0.12, height, Metal, h);
        int shelves = 4;
        for (int i = 0; i < shelves; i++)
        {
            double z = 0.15 + i * (height - 0.25) / shelves;
            Part(x, c, f, -len / 2, len / 2, -depth, depth, z, z + 0.03, Metal, h);
            if (detail >= 2)
            {
                // marchandises : bandes de couleurs
                var col = Color.FromHsv((float)x.Rng.NextDouble(), 0.55f, 0.75f);
                var col2 = Color.FromHsv((float)x.Rng.NextDouble(), 0.55f, 0.75f);
                Part(x, c, f, -len / 2 + 0.05, len / 2 - 0.05, 0.05, depth - 0.05, z + 0.03, z + 0.03 + (height - 0.25) / shelves * 0.7, col, h);
                if (depth > 0.3) Part(x, c, f, -len / 2 + 0.05, len / 2 - 0.05, -depth + 0.05, -0.05, z + 0.03, z + 0.03 + (height - 0.25) / shelves * 0.7, col2, h);
            }
        }
        Collide(x, c, along, len / 2, depth, h, h + height);
        x.Items++;
    }

    static void Bookcase(Ctx x, V2 c, V2 along, double len, double h)
    {
        var f = new V2(-along.Y, along.X);
        Part(x, c, f, -len / 2, len / 2, -0.3, 0.3, 0, 2.2, Wood, h);
        if (x.Detail >= 2)
            for (int i = 0; i < 5; i++)
            {
                double z = 0.1 + i * 0.42;
                foreach (int s in new[] { -1, 1 })
                    Part(x, c, f, -len / 2 + 0.05, len / 2 - 0.05, s * 0.3 - 0.01, s * 0.3 + 0.01, z, z + 0.30, Color.FromHsv((float)x.Rng.NextDouble(), 0.5f, 0.55f), h);
            }
        Collide(x, c, along, len / 2, 0.3, h, h + 2.2);
        x.Items++;
    }

    static void Rack(Ctx x, V2 c, V2 along, double len, double h, double height, int detail)
    {
        var f = new V2(-along.Y, along.X);
        var orange = new Color(0.85f, 0.45f, 0.12f); var blue = new Color(0.15f, 0.30f, 0.60f);
        for (double s = -len / 2; s <= len / 2 + 0.01; s += 2.7) Part(x, c, f, s - 0.05, s + 0.05, -0.55, 0.55, 0, height, blue, h);
        for (double z = 0.1; z < height; z += 1.5)
        {
            Part(x, c, f, -len / 2, len / 2, -0.55, -0.48, z, z + 0.12, orange, h);
            Part(x, c, f, -len / 2, len / 2, 0.48, 0.55, z, z + 0.12, orange, h);
            if (detail >= 2) for (double s = -len / 2 + 0.7; s < len / 2 - 0.5; s += 1.35)
                Part(x, c, f, s - 0.55, s + 0.55, -0.5, 0.5, z + 0.12, z + 0.12 + 0.9, x.Rng.NextDouble() < 0.5 ? new Color(0.62f, 0.48f, 0.30f) : new Color(0.75f, 0.70f, 0.60f), h);
        }
        Collide(x, c, along, len / 2, 0.55, h, h + height);
        x.Items++;
    }

    static void Plant(Ctx x, V2 c, double h)
    {
        x.M.Cylinder(x.Fr, c, 0.18, h, h + 0.35, 8, new Color(0.55f, 0.32f, 0.22f), 0, new Color(0.30f, 0.22f, 0.15f));
        x.M.Box(x.Fr, c, x.U, 0.25, 0.25, h + 0.35, h + 1.0, Green, false);
        x.Items++;
    }

    static void Paintings(Ctx x, Room r, double h, Placer p)
    {
        // tableaux accrochés aux murs (sans collision)
        for (int i = 0; i < 4; i++)
        {
            if (!p.AgainstWall(1.2, 0.05, out var c, out var f)) continue;
            var along = new V2(f.Y, -f.X);
            var q = c - f * 0.02;
            x.M.Quad(x.Fr.P(q - along * 0.6, h + 1.3), x.Fr.P(q + along * 0.6, h + 1.3), x.Fr.P(q + along * 0.6, h + 2.1), x.Fr.P(q - along * 0.6, h + 2.1),
                Color.FromHsv((float)x.Rng.NextDouble(), 0.5f, 0.6f), Mat(0));
        }
    }

    static void SportsLines(Ctx x, V2 c, V2 u, double L, double W, double h)
    {
        var v = new V2(-u.Y, u.X);
        var col = new Color(0.90f, 0.90f, 0.88f);
        double hl = L / 2 - 1.0, hw = W / 2 - 1.0;
        if (hl < 3 || hw < 2) return;
        void Line(V2 a, V2 b)
        {
            var d = (b - a).Normalized; var n = new V2(-d.Y, d.X) * 0.03;
            x.M.Quad(x.Fr.P(a - n, h + 0.01), x.Fr.P(b - n, h + 0.01), x.Fr.P(b + n, h + 0.01), x.Fr.P(a + n, h + 0.01), x.Fr.Up, col, Vector2.Zero, Vector2.Zero, Vector2.Zero, Vector2.Zero, Mat(0));
        }
        V2 P(double a, double b) => c + u * a + v * b;
        Line(P(-hl, -hw), P(hl, -hw)); Line(P(hl, -hw), P(hl, hw)); Line(P(hl, hw), P(-hl, hw)); Line(P(-hl, hw), P(-hl, -hw)); Line(P(0, -hw), P(0, hw));
    }

    // ------------------------------------------------------------------ entrée, lumières

    static void Entrance(Ctx x)
    {
        var b = x.B; var fr = x.Fr;
        double w = Poly2.Dist(b.DoorA, b.DoorB);
        var along = (b.DoorB - b.DoorA).Normalized; var inward = b.DoorOut * -1;
        // battant ouvert vers l'intérieur (un seul battant pour une porte simple, deux pour une grande)
        var doorCol = b.Kind is BuildingKind.Shop or BuildingKind.Supermarket or BuildingKind.Office or BuildingKind.Restaurant ? new Color(0.20f, 0.24f, 0.27f) : new Color(0.40f, 0.26f, 0.15f);
        if (w <= 1.3)
            x.M.Box(fr, b.DoorA + inward * (w / 2) + along * 0.03, inward, w / 2, 0.025, b.Floor0, b.Floor0 + b.DoorH - 0.02, doorCol, false);
        else
        {
            x.M.Box(fr, b.DoorA + inward * (w / 4) + along * 0.03, inward, w / 4, 0.025, b.Floor0, b.Floor0 + b.DoorH - 0.02, doorCol, false);
            x.M.Box(fr, b.DoorB + inward * (w / 4) - along * 0.03, inward, w / 4, 0.025, b.Floor0, b.Floor0 + b.DoorH - 0.02, doorCol, false);
        }
        // paillasson
        var mid = (b.DoorA + b.DoorB) * 0.5;
        x.M.Box(fr, mid + inward * 0.6, along, Math.Min(w, 1.2) / 2, 0.35, b.Floor0, b.Floor0 + 0.012, new Color(0.25f, 0.22f, 0.20f), false);

        // marches devant la porte quand le rez-de-chaussée est au-dessus de la rue
        double drop = b.Floor0 - b.DoorGround;
        if (drop > 0.22)
        {
            int n = (int)Math.Ceiling(drop / 0.17);
            double rise = drop / n, run = 0.30, sw = w / 2 + 0.3;
            var stone = new Color(0.62f, 0.61f, 0.58f);
            for (int i = 0; i < n; i++)
            {
                // marche i : la plus haute contre la porte
                double d0 = i * run, d1 = (i + 1) * run;
                var c = mid + b.DoorOut * ((d0 + d1) / 2);
                x.M.Box(fr, c, along, sw, run / 2, b.DoorGround - 0.4, b.Floor0 - i * rise, stone, false);
            }
            var outer = mid + b.DoorOut * (n * run);
            var ring = new[] { mid - along * sw, mid + along * sw, outer + along * sw, outer - along * sw };
            x.S.Add(Obstacles.Shape.Ramp(fr.World(ring), fr.World(outer - b.DoorOut * run), fr.World(mid), b.DoorGround + rise, b.Floor0));
        }
    }

    /// <summary>Plafonniers (Ultra) : petites dalles lumineuses au plafond de chaque étage meublé.</summary>
    static void Lamps(Ctx x, double ceilingTop)
    {
        var lamp = new Color(1.0f, 0.95f, 0.85f);
        for (int k = 0; k < x.Floors; k++)
        {
            if (Math.Abs(k - x.Focus) > x.Span) continue;
            double top = k + 1 < x.Floors ? x.FloorH[k + 1] - Slab : ceilingTop - 0.06;
            for (double s = -x.L / 2 + 2; s < x.L / 2 - 1; s += 4)
                for (double t = -x.W / 2 + 2; t < x.W / 2 - 1; t += 4)
                {
                    var c = x.C0 + x.U * s + x.V * t;
                    if (!Poly2.Inside(x.B.Ring, x.B.Holes, c) || Poly2.EdgeDistance(x.B.Ring, x.B.Holes, c) < 0.8) continue;
                    x.M.Box(x.Fr, c, x.U, 0.3, 0.3, top - 0.04, top, lamp, true, 1f, Mat(1));
                }
        }
    }
}
