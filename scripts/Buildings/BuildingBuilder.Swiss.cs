using System;
using System.Collections.Generic;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Vrais bâtiments en Suisse (swissBUILDINGS3D, swisstopo) : le maillage des toits et des murs vient tel quel du modèle 3D
/// (pans, croupes, débords, lucarnes, clochers, chalets…), posé sur le relief du jeu. Le reste suit les bâtiments
/// génériques : fonction (classe OSM du contour qui lui correspond, lieux Overture, nom, zone), fenêtres du shader
/// calées sur les étages, porte découpée dans le vrai mur, intérieur visitable, collisions.
/// Les contours OSM recouverts par un bâtiment swisstopo ne sont plus dessinés ; ceux qui ne recouvrent rien
/// (constructions récentes) restent.
/// </summary>
public static partial class BuildingBuilder
{
    // ------------------------------------------------------------------ contours

    static void AddToGrid(Cand cur, Dictionary<long, List<Cand>> grid)
    {
        int gx0 = Math.Clamp((int)Math.Floor(cur.X0 * GridN), -2, GridN + 1), gx1 = Math.Clamp((int)Math.Floor(cur.X1 * GridN), -2, GridN + 1);
        int gy0 = Math.Clamp((int)Math.Floor(cur.Y0 * GridN), -2, GridN + 1), gy1 = Math.Clamp((int)Math.Floor(cur.Y1 * GridN), -2, GridN + 1);
        for (int gx = gx0; gx <= gx1; gx++)
            for (int gy = gy0; gy <= gy1; gy++)
            {
                if (!grid.TryGetValue(Cell(gx, gy), out var l)) grid[Cell(gx, gy)] = l = new List<Cand>();
                l.Add(cur);
            }
    }

    static double[] TileRing(List<V2> ring, in LocalFrame fr, Ctx ctx)
    {
        var r = new double[(ring.Count + 1) * 2];
        for (int i = 0; i <= ring.Count; i++)
        {
            ToTile(fr, ring[i % ring.Count], ctx, out double x, out double y);
            r[2 * i] = x; r[2 * i + 1] = y;
        }
        return r;
    }

    static double TileArea(double[] r)
    {
        double a = 0;
        for (int i = 0; i + 3 < r.Length; i += 2) a += r[i] * r[i + 3] - r[i + 2] * r[i + 1];
        return Math.Abs(a) / 2;
    }

    static bool InCand(Cand c, double x, double y)
    {
        if (x < c.X0 || x > c.X1 || y < c.Y0 || y > c.Y1 || !InRing(c.Outer, x, y)) return false;
        foreach (var h in c.Holes) if (InRing(h, x, y)) return false;
        return true;
    }

    static Cand InGrid(Dictionary<long, List<Cand>> grid, double x, double y)
    {
        if (!grid.TryGetValue(Cell((int)Math.Floor(x * GridN), (int)Math.Floor(y * GridN)), out var l)) return null;
        foreach (var c in l) if (InCand(c, x, y)) return c;
        return null;
    }

    /// <summary>Ajoute les bâtiments swisstopo aux contours de la tuile ; les contours OSM qu'ils recouvrent ne sont plus
    /// dessinés mais donnent leur classe et leur nom au bâtiment swisstopo qui les contient.</summary>
    static void AddSwiss(IReadOnlyList<SwissBuildings3D.Building> swiss, List<Cand> cands, Ctx ctx)
    {
        var sc = new List<Cand>(swiss.Count);
        var grid = new Dictionary<long, List<Cand>>();
        foreach (var b in swiss)
        {
            var fr = new LocalFrame(Geo.FromLatLon(b.Lat, b.Lon), ctx.Data.Origin);
            var cd = new Cand { Swiss = b, Drawn = true, Holes = new() };
            cd.Outer = TileRing(b.Rings[0], fr, ctx);
            for (int r = 1; r < b.Rings.Count && Poly2.SignedArea(b.Rings[r]) < 0; r++) cd.Holes.Add(TileRing(b.Rings[r], fr, ctx));
            cd.X0 = cd.Y0 = double.MaxValue; cd.X1 = cd.Y1 = double.MinValue;
            for (int i = 0; i + 1 < cd.Outer.Length; i += 2)
            {
                cd.X0 = Math.Min(cd.X0, cd.Outer[i]); cd.X1 = Math.Max(cd.X1, cd.Outer[i]);
                cd.Y0 = Math.Min(cd.Y0, cd.Outer[i + 1]); cd.Y1 = Math.Max(cd.Y1, cd.Outer[i + 1]);
            }
            ToTile(fr, new V2(0, 0), ctx, out cd.Cx, out cd.Cy);
            sc.Add(cd);
            if (!b.RoofOnly) AddToGrid(cd, grid);
        }

        // Contours OSM : retirés s'ils tombent dans un bâtiment swisstopo (centre ou points à mi-chemin du centre et des coins)
        foreach (var o in cands)
        {
            Cand hit = InGrid(grid, o.Cx, o.Cy);
            int n = o.Outer.Length / 2 - 1, step = Math.Max(1, n / 8);
            for (int i = 0; hit == null && i < n; i += step)
                hit = InGrid(grid, (o.Outer[2 * i] + o.Cx) / 2, (o.Outer[2 * i + 1] + o.Cy) / 2);
            if (hit == null) continue;
            o.Drawn = false;
            // classe et nom : le bâtiment OSM entier plutôt qu'une de ses parties
            var f = o.F;
            if (o.IsPart) { var bid = f.Str("building_id"); if (bid != null && ctx.Parents.TryGetValue(bid, out var parent)) f = parent; }
            double w = TileArea(o.Outer);
            var owner = InGrid(grid, o.Cx, o.Cy) ?? hit;
            if (owner.F == null || w > owner.Weight) { owner.F = f; owner.Weight = w; }
        }
        // Bâtiment swisstopo sans contour OSM au centre (grand contour OSM qui en couvre plusieurs) : celui qui le contient
        foreach (var s in sc)
            if (s.F == null && !s.Swiss.RoofOnly)
                foreach (var o in cands)
                {
                    if (s.Cx < o.X0 || s.Cx > o.X1 || s.Cy < o.Y0 || s.Cy > o.Y1 || !InCand(o, s.Cx, s.Cy)) continue;
                    var f = o.F;
                    if (o.IsPart) { var bid = f.Str("building_id"); if (bid != null && ctx.Parents.TryGetValue(bid, out var parent)) f = parent; }
                    s.F = f;
                    break;
                }

        // La grille ne garde que ce qui est dessiné (portes et vitrines : un voisin OSM retiré n'est pas un mur mitoyen)
        ctx.Grid.Clear();
        foreach (var o in cands) if (o.Drawn) AddToGrid(o, ctx.Grid);
        foreach (var s in sc) if (!s.Swiss.RoofOnly) AddToGrid(s, ctx.Grid);
        cands.AddRange(sc);
    }

    // ------------------------------------------------------------------ maillage

    struct P3
    {
        public double X, Y, H;
        public P3(double x, double y, double h) { X = x; Y = y; H = h; }
        public static P3 Lerp(P3 a, P3 b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.H + (b.H - a.H) * t);
    }

    static void EmitSwiss(Cand cd, IReadOnlyList<Area> areas, Ctx ctx)
    {
        var b = cd.Swiss;
        var c = Geo.FromLatLon(b.Lat, b.Lon);
        int area = -1;
        for (int i = 0; i < areas.Count; i++)
            if ((c - areas[i].Dir).Length() * R <= areas[i].Radius) { area = i; break; }
        if (area < 0) return;
        var fr = new LocalFrame(c, ctx.Data.Origin);
        var m = ctx.M;

        // Emprise : premier contour (le plus grand) et ses cours ; les autres morceaux servent aux collisions
        var ext = new List<V2>(b.Rings[0]);
        if (Poly2.SignedArea(ext) < 0) ext.Reverse();
        var holes = new List<List<V2>>();
        int ri = 1;
        for (; ri < b.Rings.Count && Poly2.SignedArea(b.Rings[ri]) < 0; ri++) holes.Add(new List<V2>(b.Rings[ri]));
        var others = new List<(List<V2> outer, List<List<V2>> holes)>();
        for (; ri < b.Rings.Count; ri++)
        {
            if (Poly2.SignedArea(b.Rings[ri]) > 0) others.Add((new List<V2>(b.Rings[ri]), new()));
            else if (others.Count > 0) others[^1].holes.Add(new List<V2>(b.Rings[ri]));
        }
        double footprint = Poly2.SignedArea(ext);
        foreach (var h in holes) footprint += Poly2.SignedArea(h);

        // Sol du jeu sous le bâtiment (coins, le long des murs tous les ~3 m, centre) : le pied des murs swisstopo
        // (point le plus bas du terrain) est posé sur le point le plus bas du relief du jeu
        double gmin = double.MaxValue, gmax = double.MinValue;
        void Probe(V2 p)
        {
            fr.LatLon(p, out double la, out double lo);
            float g = Ground(la, lo, ctx);
            gmin = Math.Min(gmin, g); gmax = Math.Max(gmax, g);
        }
        for (int i = 0; i < ext.Count; i++)
        {
            var a = ext[i]; var e = ext[(i + 1) % ext.Count];
            Probe(a);
            int steps = Math.Min((int)(Poly2.Dist(a, e) / 3), 20);
            for (int k = 1; k <= steps; k++) Probe(a + (e - a) * (k / (double)(steps + 1)));
        }
        Probe(Poly2.Centroid(ext));
        if (gmin == double.MaxValue) return;

        // Hauteurs : bas des fondations, gouttière (hauteur des murs aux coins, médiane), faîte
        var V = b.Verts;
        int nWallIdx = b.WallTris * 3;
        double eaveRel;
        {
            var corner = new List<double>();
            foreach (var p in ext)
            {
                double best = double.NaN;
                for (int k = 0; k < nWallIdx; k++)
                {
                    var v = V[b.Idx[k]];
                    if (Math.Abs(v.X - p.X) < 0.3 && Math.Abs(v.Y - p.Y) < 0.3 && !(v.Z <= best)) best = v.Z;
                }
                if (!double.IsNaN(best)) corner.Add(best);
            }
            corner.Sort();
            eaveRel = corner.Count > 0 ? corner[corner.Count / 2] : b.RoofMin;
            if (eaveRel < 1.5) eaveRel = Math.Max(b.RoofMin, 1.5);
        }
        double top = gmin + Math.Max(b.RoofMax, eaveRel);
        double eave = gmin + eaveRel;
        double baseH = gmin - Foundation;
        double Hgt(float z) => z < 0.05f ? baseH : gmin + z;

        // Fonction : classe swisstopo si elle est parlante (église, serre, abri…), sinon celle d'OSM
        var f = cd.F;
        string sCls = SwissBuildings3D.OsmClass(b), oCls = f?.Str("class");
        string cls = sCls is "church" or "chapel" or "greenhouse" or "roof" or "storage_tank" or "stadium" or "parking" ? sCls : oCls ?? sCls;
        string sub = f?.Str("subtype");
        string name = b.Name ?? f?.Str("@name");
        string land = LandUseAt(ctx, b.Lat, b.Lon);
        double height = top - gmin;
        var kind = b.RoofOnly ? BuildingKind.Roof : BuildingKinds.Classify(cls, sub, name, cd.Places, land, footprint, height);
        bool shopfront = !b.RoofOnly && BuildingKinds.HasShopfront(kind, cd.Places);
        double seed = Hash((b.Id % 1_000_003) * 0.731 + footprint * 0.013);

        // Rez-de-chaussée au niveau du sol côté amont, sans dépasser la gouttière (au moins un étage)
        double floor0 = Math.Min(gmax + FloorAboveGround, eave - Math.Min(2.6, Math.Max(eave - gmin - 0.3, 0)));
        floor0 = Math.Max(floor0, gmin + FloorAboveGround);
        double usable = eave - floor0;
        int levels = BuildingKinds.SingleHall(kind) ? 1 : Math.Max(1, (int)Math.Round(usable / FloorHeight));
        double levelH = usable / levels;
        if (levelH < 2.4 && usable >= 2.4) { levels = Math.Max(1, (int)Math.Floor(usable / 2.6)); levelH = usable / levels; }
        if (usable < 0.5) levelH = FloorHeight;
        var map = new WallMap(floor0, Math.Max(levelH, 1.0));

        bool pitched = false;
        for (int k = nWallIdx; k + 2 < b.Idx.Length && !pitched; k += 3)
        {
            var n = (V[b.Idx[k + 1]] - V[b.Idx[k]]).Cross(V[b.Idx[k + 2]] - V[b.Idx[k]]);
            if (n.LengthSquared() > 1e-6f && Math.Abs(n.Normalized().Z) < 0.96f) pitched = true;
        }
        var wall = WallColor(f, kind, seed);
        var roof = RoofColor(f, null, pitched, kind, seed);
        var style = height >= 3.5 && footprint >= 20 && !b.RoofOnly ? BuildingKinds.Windows(kind, shopfront, height, footprint) : WindowStyle.None;
        var uv2 = new Vector2((float)style, (float)seed);

        // Porte : sur un vrai mur qui donne dehors, le plus près du niveau du sol
        bool enterable = !SwissBuildings3D.Closed(b) && BuildingKinds.Enterable(kind) && footprint >= 16 && footprint <= 40_000
            && ext.Count <= 160 && usable >= 2.3 && b.WallTris > 0;
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
            if (doorEdge >= 0 && !HasWallAlong(b, ext[doorEdge], ext[(doorEdge + 1) % ext.Count])) doorEdge = -1;
            if (doorEdge < 0) enterable = false;
        }

        // Murs (fenêtres du shader sur les étages, porte découpée), puis toit
        V2 dA = default, dDir = default, dN = default; double dS0 = 0, dS1 = 0, dH0 = 0, dH1 = 0;
        if (enterable)
        {
            dA = ext[doorEdge]; var dB = ext[(doorEdge + 1) % ext.Count];
            double len = Poly2.Dist(dA, dB);
            dDir = (dB - dA) * (1 / len); dN = dDir.Right;
            dS0 = doorT0 * len; dS1 = doorT1 * len; dH0 = floor0; dH1 = floor0 + doorH;
        }
        for (int k = 0; k < nWallIdx; k += 3)
        {
            var va = V[b.Idx[k]]; var vb = V[b.Idx[k + 1]]; var vc = V[b.Idx[k + 2]];
            var n = (vb - va).Cross(vc - va);
            double nl = Math.Sqrt(n.X * n.X + n.Y * n.Y);
            if (nl < 1e-6) continue;
            // swisstopo tourne les triangles des murs dans l'autre sens (normale vers l'intérieur pour 99,7 % d'entre eux)
            var nh = new V2(-n.X / nl, -n.Y / nl);
            // vers l'extérieur : un pas devant le mur doit tomber hors de l'emprise (murs au-dessus d'un décrochement :
            // les deux côtés sont dehors, on garde le sens swisstopo)
            var mid = new V2((va.X + vb.X + vc.X) / 3, (va.Y + vb.Y + vc.Y) / 3);
            if (InsideAny(ext, holes, others, mid + nh * 0.4) && !InsideAny(ext, holes, others, mid - nh * 0.4)) nh = nh * -1;
            var t = new P3[] { new(va.X, va.Y, Hgt(va.Z)), new(vb.X, vb.Y, Hgt(vb.Z)), new(vc.X, vc.Y, Hgt(vc.Z)) };
            var nrm = fr.Dir(nh.X, nh.Y, 0);
            var tan = new V2(-nh.Y, nh.X);
            if (enterable && OnDoor(t, dA, dDir, dN, dS0, dS1, dH0, dH1))
            {
                // découpe : à gauche, à droite, au-dessus et au-dessous de l'ouverture
                foreach (var region in new[] { (double.MinValue, dS0, double.MinValue, double.MaxValue), (dS1, double.MaxValue, double.MinValue, double.MaxValue),
                             (dS0, dS1, dH1, double.MaxValue), (dS0, dS1, double.MinValue, dH0) })
                {
                    var poly = ClipRegion(t, dA, dDir, region.Item1, region.Item2, region.Item3, region.Item4);
                    for (int q = 1; q + 1 < poly.Count; q++) WallVert3(poly[0], poly[q], poly[q + 1], nrm, tan, wall, uv2, map, fr, m);
                }
            }
            else WallVert3(t[0], t[1], t[2], nrm, tan, wall, uv2, map, fr, m);
        }
        for (int k = nWallIdx; k + 2 < b.Idx.Length; k += 3)
        {
            var va = V[b.Idx[k]]; var vb = V[b.Idx[k + 1]]; var vc = V[b.Idx[k + 2]];
            var n = (vb - va).Cross(vc - va);
            if (n.LengthSquared() < 1e-8f) continue;
            n = n.Normalized();
            if (n.Z < 0) n = -n;
            var nrm = fr.Dir(n.X, n.Y, n.Z);
            int i0 = m.Count;
            m.Vert(fr.P(va.X, va.Y, gmin + va.Z), nrm, roof, Vector2.Zero, Vector2.Zero);
            m.Vert(fr.P(vb.X, vb.Y, gmin + vb.Z), nrm, roof, Vector2.Zero, Vector2.Zero);
            m.Vert(fr.P(vc.X, vc.Y, gmin + vc.Z), nrm, roof, Vector2.Zero, Vector2.Zero);
            m.I.Add(i0); m.I.Add(i0 + 1); m.I.Add(i0 + 2);
        }

        // Collisions : murs pleins (un abri sans murs laisse passer dessous)
        Vec3d[][] holeRings = null;
        if (holes.Count > 0) { holeRings = new Vec3d[holes.Count][]; for (int i = 0; i < holes.Count; i++) holeRings[i] = fr.World(holes[i]); }
        double solidBase = b.RoofOnly ? gmin + b.RoofMin - 0.1 : baseH;
        var solid = Obstacles.Shape.Polygon(fr.World(ext), holeRings, solidBase, top);
        ctx.Obs.Add(solid);
        foreach (var (o, oh) in others)
        {
            Vec3d[][] ohr = oh.Count > 0 ? oh.ConvertAll(h => fr.World(h)).ToArray() : null;
            ctx.Obs.Add(Obstacles.Shape.Polygon(fr.World(o), ohr, solidBase, top));
        }

        if (shopfront) Awnings(ext, cd, fr, ctx, floor0, levelH, seed, doorEdge, m);

        if (enterable)
        {
            var a = ext[doorEdge]; var e = ext[(doorEdge + 1) % ext.Count];
            var da = a + (e - a) * doorT0; var db = a + (e - a) * doorT1;
            var doorCol = DoorColor(kind, shopfront, seed);
            m.Side(fr, da, db, floor0, floor0 + doorH, doorCol, new Vector2((float)WindowStyle.Door, (float)seed),
                new Vector2(0, 0), new Vector2((float)Poly2.Dist(da, db), (float)doorH));
            var info = new BuildingInfo
            {
                Id = $"sb3d:{b.Id:x16}",
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
        ctx.Data.PerArea[area]++;
    }

    static bool InsideAny(List<V2> ext, List<List<V2>> holes, List<(List<V2> outer, List<List<V2>> holes)> others, V2 p)
    {
        if (Poly2.Inside(ext, holes, p)) return true;
        foreach (var (o, h) in others) if (Poly2.Inside(o, h, p)) return true;
        return false;
    }

    /// <summary>Un mur du modèle longe ce côté de l'emprise (sinon pas de porte : le côté serait dans le vide).</summary>
    static bool HasWallAlong(SwissBuildings3D.Building b, V2 a, V2 e)
    {
        double len = Poly2.Dist(a, e);
        if (len < 0.5) return false;
        var u = (e - a) * (1 / len); var n = u.Right;
        double covered = 0;
        for (int k = 0; k < b.WallTris * 3; k += 3)
        {
            double s0 = double.MaxValue, s1 = double.MinValue; bool near = true;
            for (int j = 0; j < 3; j++)
            {
                var v = b.Verts[b.Idx[k + j]];
                var d = new V2(v.X, v.Y) - a;
                if (Math.Abs(d.Dot(n)) > 0.35) { near = false; break; }
                double s = d.Dot(u); s0 = Math.Min(s0, s); s1 = Math.Max(s1, s);
            }
            if (near) covered = Math.Max(covered, Math.Min(s1, len) - Math.Max(s0, 0));
        }
        return covered > Math.Min(len * 0.5, 2.0);
    }

    static bool OnDoor(P3[] t, V2 a, V2 u, V2 n, double s0, double s1, double h0, double h1)
    {
        double smin = double.MaxValue, smax = double.MinValue, hmin = double.MaxValue, hmax = double.MinValue;
        foreach (var p in t)
        {
            var d = new V2(p.X - a.X, p.Y - a.Y);
            if (Math.Abs(d.Dot(n)) > 0.35) return false;
            double s = d.Dot(u);
            smin = Math.Min(smin, s); smax = Math.Max(smax, s); hmin = Math.Min(hmin, p.H); hmax = Math.Max(hmax, p.H);
        }
        return smax > s0 && smin < s1 && hmax > h0 && hmin < h1;
    }

    /// <summary>Partie du triangle (dans le plan du mur) comprise dans s0 ≤ s ≤ s1, h0 ≤ h ≤ h1 (Sutherland–Hodgman).</summary>
    static List<P3> ClipRegion(P3[] t, V2 a, V2 u, double s0, double s1, double h0, double h1)
    {
        var poly = new List<P3>(t);
        double S(P3 p) => (p.X - a.X) * u.X + (p.Y - a.Y) * u.Y;
        void Clip(Func<P3, double> f) // garde f ≥ 0
        {
            if (poly.Count == 0) return;
            var res = new List<P3>(poly.Count + 2);
            for (int i = 0; i < poly.Count; i++)
            {
                var p = poly[i]; var q = poly[(i + 1) % poly.Count];
                double fp = f(p), fq = f(q);
                if (fp >= 0) res.Add(p);
                if ((fp >= 0) != (fq >= 0)) res.Add(P3.Lerp(p, q, fp / (fp - fq)));
            }
            poly = res;
        }
        if (s0 > double.MinValue) Clip(p => S(p) - s0);
        if (s1 < double.MaxValue) Clip(p => s1 - S(p));
        if (h0 > double.MinValue) Clip(p => p.H - h0);
        if (h1 < double.MaxValue) Clip(p => h1 - p.H);
        return poly;
    }

    static void WallVert3(P3 a, P3 b, P3 c, Vector3 nrm, V2 tan, Color col, Vector2 uv2, WallMap map, in LocalFrame fr, MeshAcc m)
    {
        // aire nulle (morceau de découpe dégénéré) : rien
        double cx = (b.Y - a.Y) * (c.H - a.H) - (b.H - a.H) * (c.Y - a.Y), cy = (b.H - a.H) * (c.X - a.X) - (b.X - a.X) * (c.H - a.H),
            cz = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (cx * cx + cy * cy + cz * cz < 1e-8) return;
        int i0 = m.Count;
        foreach (var p in new[] { a, b, c })
            m.Vert(fr.P(p.X, p.Y, p.H), nrm, col, new Vector2((float)(p.X * tan.X + p.Y * tan.Y), map.V(p.H)), uv2);
        m.I.Add(i0); m.I.Add(i0 + 1); m.I.Add(i0 + 2);
    }
}
