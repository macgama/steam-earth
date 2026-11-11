using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace SteamEarth;

/// <summary>
/// Génère les maillages d'un personnage habillé à partir de la peau du corps CC0 :
/// <list type="bullet">
/// <item>silhouette : le corps « super-héros » d'origine est affiné selon la corpulence choisie ;</item>
/// <item>chaque vêtement est une « coque » : la peau couverte par le vêtement, découpée proprement le long de
/// l'ourlet, lissée (plus de muscles ni d'orteils sous le tissu) et poussée vers l'extérieur de son épaisseur,
/// avec les mêmes os et poids (il suit donc toutes les animations) ;</item>
/// <item>un ourlet ferme le bord (bande entre la coque et la peau) ;</item>
/// <item>la peau cachée sous les vêtements est retirée, découpée le long du même bord (pas de jour, rien ne transperce) ;</item>
/// <item>la tête est séparée du corps : en vue à la première personne, elle ne projette plus que son ombre.</item>
/// </list>
/// Les maillages sont mis en cache : les personnages qui portent la même tenue partagent les mêmes.
/// </summary>
public static class CharacterMeshes
{
    enum Region : byte { Torso, Arm, Leg, Head }

    /// <summary>Peau du corps de base, analysée une fois par type de corps et corpulence.</summary>
    sealed class BodyData
    {
        public Vector3[] V, N;
        public float[] T;         // tangentes (4 par sommet), peut être null
        public Vector2[] UV;
        public int[] Bones;
        public float[] Weights;
        public int Bpv;           // os par sommet (4 ou 8)
        public int[] Idx;
        public Region[] Reg;
        public float[] P;         // coordonnée normalisée dans sa région (voir Coverage)
        public Vector3[] SmoothN; // normale moyenne des sommets confondus (coutures UV) : coques sans fentes
        public float AnkleY;
    }

    public sealed class Built
    {
        public ArrayMesh Body, Head;            // peau visible (corps, tête)
        public ArrayMesh Clothes, Headwear;     // une surface par vêtement
        public GarmentDef[] ClothesDefs = Array.Empty<GarmentDef>(), HeadwearDefs = Array.Empty<GarmentDef>();
    }

    static readonly Dictionary<string, BodyData> _bodies = new();
    static readonly Dictionary<string, Built> _cache = new();

    /// <summary>Corps habillé. <paramref name="build"/> : corpulence (0,85 = mince, 1 = athlétique d'origine).</summary>
    public static Built Build(BodyType body, float build, Mesh skin, Skeleton3D skel, IEnumerable<GarmentDef> garments, bool hems)
    {
        build = Mathf.Snapped(Math.Clamp(build, 0.8f, 1.05f), 0.01f);
        var list = garments.Where(g => g != null && g.Thickness > 0).OrderByDescending(g => g.Layer).ToList();
        string key = $"{body}|{build}|{hems}|{string.Join(",", list.Select(g => g.Id))}";
        if (_cache.TryGetValue(key, out var built)) return built;

        string bodyKey = $"{body}|{build}";
        if (!_bodies.TryGetValue(bodyKey, out var d)) _bodies[bodyKey] = d = Analyse(skin, skel, build);
        var pool = new Pool(d, list);
        int tris = d.Idx.Length / 3;

        var clothes = new List<(GarmentDef def, Godot.Collections.Array arrays)>();
        var headwear = new List<(GarmentDef def, Godot.Collections.Array arrays)>();
        for (int gi = 0; gi < list.Count; gi++)
        {
            var g = list[gi];
            var triangles = new List<int>();
            var hemEdges = new List<(int, int)>();
            for (int t = 0; t < tris; t++)
            {
                int a = d.Idx[t * 3], b = d.Idx[t * 3 + 1], c = d.Idx[t * 3 + 2];
                if (pool.F(a, gi) < 0 && pool.F(b, gi) < 0 && pool.F(c, gi) < 0) continue;
                var poly = pool.Clip(new List<int> { a, b, c }, gi, keepInside: true, hemEdges);
                // les vêtements du dessus cachent ce morceau ? on le retire (moins de triangles, pas de scintillement)
                for (int o = 0; o < gi && poly.Count >= 3; o++) poly = pool.Clip(poly, o, keepInside: false, null);
                Fan(poly, triangles);
            }
            if (triangles.Count == 0) continue;
            var arrays = pool.Shell(g, gi, triangles, hems ? hemEdges : null);
            (g.Slot == GarmentSlot.Head ? headwear : clothes).Add((g, arrays));
        }

        // Peau visible (découpée le long des vêtements), séparée en corps et tête
        var bodyTris = new List<int>();
        var headTris = new List<int>();
        for (int t = 0; t < tris; t++)
        {
            var poly = new List<int> { d.Idx[t * 3], d.Idx[t * 3 + 1], d.Idx[t * 3 + 2] };
            for (int gi = 0; gi < list.Count && poly.Count >= 3; gi++) poly = pool.Clip(poly, gi, keepInside: false, null);
            int heads = 0;
            for (int k = 0; k < 3; k++) { int v = d.Idx[t * 3 + k]; if (d.Reg[v] == Region.Head && d.P[v] > 0.08f) heads++; }
            Fan(poly, heads >= 2 ? headTris : bodyTris);
        }

        built = new Built
        {
            Body = Mesh(new[] { pool.Skin(bodyTris) }, d),
            Head = Mesh(new[] { pool.Skin(headTris) }, d),
            Clothes = clothes.Count > 0 ? Mesh(clothes.Select(c => c.arrays), d) : null,
            Headwear = headwear.Count > 0 ? Mesh(headwear.Select(c => c.arrays), d) : null,
            ClothesDefs = clothes.Select(c => c.def).ToArray(),
            HeadwearDefs = headwear.Select(c => c.def).ToArray(),
        };
        _cache[key] = built;
        return built;
    }

    static void Fan(List<int> poly, List<int> dst)
    {
        for (int i = 1; i + 1 < poly.Count; i++) { dst.Add(poly[0]); dst.Add(poly[i]); dst.Add(poly[i + 1]); }
    }

    // ---------------------------------------------------------------- analyse du corps

    static BodyData Analyse(Mesh skin, Skeleton3D skel, float build)
    {
        var a = skin.SurfaceGetArrays(0);
        var d = new BodyData
        {
            V = a[(int)Godot.Mesh.ArrayType.Vertex].AsVector3Array(),
            N = a[(int)Godot.Mesh.ArrayType.Normal].AsVector3Array(),
            UV = a[(int)Godot.Mesh.ArrayType.TexUV].AsVector2Array(),
            Bones = a[(int)Godot.Mesh.ArrayType.Bones].AsInt32Array(),
            Weights = a[(int)Godot.Mesh.ArrayType.Weights].AsFloat32Array(),
            Idx = a[(int)Godot.Mesh.ArrayType.Index].AsInt32Array(),
        };
        var tan = a[(int)Godot.Mesh.ArrayType.Tangent];
        d.T = tan.VariantType == Variant.Type.Nil ? null : tan.AsFloat32Array();
        if (d.T != null && d.T.Length != d.V.Length * 4) d.T = null;
        int nv = d.V.Length;
        d.Bpv = d.Weights.Length / Math.Max(1, nv);

        Vector3 Bone(string name) => skel.GetBoneGlobalRest(skel.FindBone(name)).Origin;
        float shoulder = Bone("upperarm_l").X, wrist = Bone("hand_l").X;
        float hip = Bone("thigh_l").Y, ankle = Bone("foot_l").Y;
        float neck = Bone("neck_01").Y;
        float top = d.V.Max(v => v.Y);
        d.AnkleY = ankle;

        // Région de chaque os (bras, jambe, tête, tronc) et facteur d'affinement de la silhouette
        int nb = skel.GetBoneCount();
        var boneRegion = new Region[nb];
        var slim = new float[nb];
        var segA = new Vector3[nb]; var segB = new Vector3[nb];
        for (int b = 0; b < nb; b++)
        {
            string n = skel.GetBoneName(b);
            bool arm = n.StartsWith("upperarm") || n.StartsWith("lowerarm");
            bool hand = n.StartsWith("hand") || n.StartsWith("thumb") || n.StartsWith("index") || n.StartsWith("middle") || n.StartsWith("ring") || n.StartsWith("pinky");
            bool leg = n.StartsWith("thigh") || n.StartsWith("calf");
            bool foot = n.StartsWith("foot") || n.StartsWith("ball");
            bool head = n.StartsWith("Head") || n.StartsWith("neck");
            boneRegion[b] = arm || hand ? Region.Arm : leg || foot ? Region.Leg : head ? Region.Head : Region.Torso;
            slim[b] = arm ? build : leg ? 1 - (1 - build) * 0.6f : n.StartsWith("spine") || n.StartsWith("clavicle") ? build : 1;
            segA[b] = skel.GetBoneGlobalRest(b).Origin;
            var children = skel.GetBoneChildren(b);
            segB[b] = children.Length > 0 ? skel.GetBoneGlobalRest(children[0]).Origin : segA[b];
        }

        d.Reg = new Region[nv];
        d.P = new float[nv];
        for (int v = 0; v < nv; v++)
        {
            // os dominant (plus grand poids)
            int best = 0; float bw = -1;
            for (int k = 0; k < d.Bpv; k++)
            {
                float w = d.Weights[v * d.Bpv + k];
                if (w > bw) { bw = w; best = d.Bones[v * d.Bpv + k]; }
            }
            var r = best >= 0 && best < nb ? boneRegion[best] : Region.Torso;
            var p = d.V[v];
            d.Reg[v] = r;
            d.P[v] = r switch
            {
                Region.Arm => (Math.Abs(p.X) - shoulder) / (wrist - shoulder),
                Region.Leg => (hip - p.Y) / (hip - ankle),
                Region.Head => (p.Y - neck) / (top - neck),
                _ => (p.Y - hip) / (neck - hip),
            };
        }

        // Silhouette : chaque sommet se rapproche de l'axe de ses os (moyenne pondérée par les poids)
        if (build < 0.999f)
        {
            var moved = new Vector3[nv];
            for (int v = 0; v < nv; v++)
            {
                var p = d.V[v];
                var sum = Vector3.Zero; float ws = 0;
                for (int k = 0; k < d.Bpv; k++)
                {
                    float w = d.Weights[v * d.Bpv + k];
                    int b = d.Bones[v * d.Bpv + k];
                    if (w <= 0 || b < 0 || b >= nb) continue;
                    var c = Closest(segA[b], segB[b], p);
                    sum += (c + (p - c) * slim[b]) * w;
                    ws += w;
                }
                moved[v] = ws > 0 ? sum / ws : p;
            }
            d.V = moved;
        }

        // Sommets confondus (coutures UV) : normale moyenne
        var weld = new Dictionary<(int, int, int), int>();
        var ids = new int[nv];
        var acc = new List<Vector3>();
        for (int v = 0; v < nv; v++)
        {
            var key = Key(d.V[v]);
            if (!weld.TryGetValue(key, out int id)) { id = acc.Count; weld[key] = id; acc.Add(Vector3.Zero); }
            ids[v] = id;
            acc[id] += d.N[v];
        }
        d.SmoothN = new Vector3[nv];
        for (int v = 0; v < nv; v++) d.SmoothN[v] = acc[ids[v]].Normalized();
        return d;
    }

    static (int, int, int) Key(Vector3 p) => ((int)Math.Round(p.X * 5000), (int)Math.Round(p.Y * 5000), (int)Math.Round(p.Z * 5000));

    static Vector3 Closest(Vector3 a, Vector3 b, Vector3 p)
    {
        var ab = b - a;
        float l2 = ab.LengthSquared();
        if (l2 < 1e-8f) return a;
        return a + ab * Math.Clamp((p - a).Dot(ab) / l2, 0, 1);
    }

    // ---------------------------------------------------------------- sommets (corps + points de découpe)

    /// <summary>Tous les sommets : ceux du corps puis ceux créés en découpant le long des ourlets (attributs interpolés).</summary>
    sealed class Pool
    {
        readonly BodyData _d;
        readonly List<GarmentDef> _g;
        readonly int _G;
        public readonly List<Vector3> P = new(), N = new(), SN = new();
        public readonly List<Vector2> UV = new();
        public readonly List<float> T, W = new(), Fields = new();
        public readonly List<int> B = new();
        public readonly List<Region> Reg = new();
        readonly Dictionary<(int, int, int), int> _cuts = new();

        public Pool(BodyData d, List<GarmentDef> garments)
        {
            _d = d; _g = garments; _G = garments.Count;
            T = d.T != null ? new List<float>(d.T) : null;
            P.AddRange(d.V); N.AddRange(d.N); SN.AddRange(d.SmoothN); UV.AddRange(d.UV);
            B.AddRange(d.Bones); W.AddRange(d.Weights); Reg.AddRange(d.Reg);
            for (int v = 0; v < d.V.Length; v++)
                foreach (var g in garments) Fields.Add(Field(g.Cover, d.Reg[v], d.P[v]));
        }

        /// <summary>&gt; 0 : couvert par le vêtement (distance à l'ourlet, en coordonnée normalisée).</summary>
        static float Field(in Coverage c, Region r, float p)
        {
            (float lo, float hi) = r switch
            {
                Region.Torso => (c.TorsoMin, c.TorsoMax),
                Region.Arm => (c.ArmMin, c.ArmMax),
                Region.Leg => (c.LegMin, c.LegMax),
                _ => (c.HeadMin, c.HeadMax),
            };
            if (lo > hi) return -1;
            return Math.Min(p - lo, hi - p);
        }

        public float F(int v, int g) => Fields[v * _G + g];

        /// <summary>Point de découpe sur l'arête a–b (là où le champ du vêtement g s'annule), partagé entre triangles voisins.</summary>
        int Cut(int a, int b, int g)
        {
            var key = a < b ? (a, b, g) : (b, a, g);
            if (_cuts.TryGetValue(key, out int id)) return id;
            int lo = key.Item1, hi = key.Item2;
            float fa = F(lo, g), fb = F(hi, g);
            float s = Math.Clamp(fa / (fa - fb), 0, 1);
            id = P.Count;
            P.Add(P[lo].Lerp(P[hi], s));
            N.Add(N[lo].Lerp(N[hi], s).Normalized());
            SN.Add(SN[lo].Lerp(SN[hi], s).Normalized());
            UV.Add(UV[lo].Lerp(UV[hi], s));
            if (T != null) for (int k = 0; k < 4; k++) T.Add(Mathf.Lerp(T[lo * 4 + k], T[hi * 4 + k], s));
            Reg.Add(s < 0.5f ? Reg[lo] : Reg[hi]);
            // poids des os : mélange des deux extrémités, on garde les plus forts
            int bpv = _d.Bpv;
            var mix = new Dictionary<int, float>();
            for (int k = 0; k < bpv; k++)
            {
                void AddW(int v, float f) { float w = W[v * bpv + k] * f; if (w <= 0) return; int bone = B[v * bpv + k]; mix[bone] = mix.GetValueOrDefault(bone) + w; }
                AddW(lo, 1 - s); AddW(hi, s);
            }
            var top = mix.OrderByDescending(kv => kv.Value).Take(bpv).ToList();
            float total = top.Sum(kv => kv.Value);
            for (int k = 0; k < bpv; k++)
            {
                B.Add(k < top.Count ? top[k].Key : 0);
                W.Add(k < top.Count && total > 0 ? top[k].Value / total : 0);
            }
            for (int k = 0; k < _G; k++) Fields.Add(Mathf.Lerp(F(lo, k), F(hi, k), s));
            Fields[id * _G + g] = 0;
            _cuts[key] = id;
            return id;
        }

        /// <summary>Garde la partie du polygone dans (ou hors de) la zone du vêtement g.
        /// <paramref name="hems"/> reçoit le bord créé (ourlet), dans le sens du polygone.</summary>
        public List<int> Clip(List<int> poly, int g, bool keepInside, List<(int, int)> hems)
        {
            float S(int v) => keepInside ? F(v, g) : -F(v, g);
            bool allIn = true, allOut = true;
            foreach (int v in poly) { if (S(v) >= 0) allOut = false; else allIn = false; }
            if (allIn) return poly;
            if (allOut) return new List<int>();
            var res = new List<int>(poly.Count + 2);
            int exit = -1, entry = -1;
            for (int i = 0; i < poly.Count; i++)
            {
                int a = poly[i], b = poly[(i + 1) % poly.Count];
                float sa = S(a), sb = S(b);
                if (sa >= 0) res.Add(a);
                if ((sa >= 0) != (sb >= 0))
                {
                    int c = (sa == 0 || sb == 0) ? (sa == 0 ? a : b) : Cut(a, b, g);
                    if (c != a) res.Add(c);
                    if (sa >= 0) exit = c; else entry = c;
                }
            }
            if (hems != null && exit >= 0 && entry >= 0 && exit != entry) hems.Add((exit, entry));
            return res;
        }

        /// <summary>Coque d'un vêtement : sommets poussés vers l'extérieur, lissés, plus les ourlets.</summary>
        public Godot.Collections.Array Shell(GarmentDef g, int gi, List<int> tris, List<(int, int)> hems)
        {
            var map = new Dictionary<int, int>();
            var order = new List<int>();
            foreach (int v in tris) if (!map.ContainsKey(v)) { map[v] = order.Count; order.Add(v); }
            int n = order.Count;
            var pos = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                int v = order[i];
                var p = P[v];
                float th = g.Thickness;
                if (g.Quilt > 0) th *= 1 - g.Quilt + g.Quilt * Mathf.Abs(Mathf.Sin(p.Y * Mathf.Pi / 0.085f));
                var o = p + SN[v] * th;
                if (g.SoleThickness > 0 && p.Y < _d.AnkleY * 0.45f)
                {
                    // semelle : plus large et plus épaisse sous le pied
                    float k = 1 - p.Y / (_d.AnkleY * 0.45f);
                    o += SN[v] * (g.SoleThickness * 0.5f * k);
                    if (p.Y < 0.02f) o.Y -= g.SoleThickness * k;
                }
                pos[i] = o;
            }

            // Lissage (Taubin : sans rétrécir) : le tissu ne colle pas aux muscles, les chaussures n'ont plus d'orteils
            int iterations = g.Slot switch { GarmentSlot.Feet => 60, GarmentSlot.Hands => 3, GarmentSlot.Outer => 6, GarmentSlot.Head => 4, _ => 4 };
            if (iterations > 0)
            {
                var weld = new Dictionary<(int, int, int), int>();
                var wid = new int[n];
                for (int i = 0; i < n; i++)
                {
                    var key = Key(P[order[i]]);
                    if (!weld.TryGetValue(key, out int id)) { id = weld.Count; weld[key] = id; }
                    wid[i] = id;
                }
                int nw = weld.Count;
                var nb = new HashSet<int>[nw];
                for (int i = 0; i < nw; i++) nb[i] = new HashSet<int>();
                for (int t = 0; t < tris.Count; t += 3)
                    for (int k = 0; k < 3; k++)
                    {
                        int a = wid[map[tris[t + k]]], b = wid[map[tris[t + (k + 1) % 3]]];
                        if (a != b) { nb[a].Add(b); nb[b].Add(a); }
                    }
                // bords (ourlets) fixes : la coque reste collée à la découpe de la peau
                var fixedW = new bool[nw];
                if (hems != null) foreach (var (a, b) in hems) { if (map.TryGetValue(a, out int ia)) fixedW[wid[ia]] = true; if (map.TryGetValue(b, out int ib)) fixedW[wid[ib]] = true; }
                var wp = new Vector3[nw];
                for (int i = 0; i < n; i++) wp[wid[i]] = pos[i];
                var tmp = new Vector3[nw];
                for (int it = 0; it < iterations * 2; it++)
                {
                    float f = it % 2 == 0 ? 0.5f : -0.53f;
                    for (int i = 0; i < nw; i++)
                    {
                        if (fixedW[i] || nb[i].Count == 0) { tmp[i] = wp[i]; continue; }
                        var avg = Vector3.Zero;
                        foreach (int j in nb[i]) avg += wp[j];
                        avg /= nb[i].Count;
                        tmp[i] = wp[i] + (avg - wp[i]) * f;
                    }
                    (wp, tmp) = (tmp, wp);
                }
                for (int i = 0; i < n; i++) pos[i] = wp[wid[i]];
            }

            // Normales recalculées sur la coque lissée (sinon l'ombrage montrerait encore les muscles et les orteils)
            var nrm = new Vector3[n];
            {
                var acc = new Dictionary<(int, int, int), Vector3>();
                var keys = new (int, int, int)[n];
                for (int i = 0; i < n; i++) keys[i] = Key(P[order[i]]);
                for (int t = 0; t < tris.Count; t += 3)
                {
                    int a = map[tris[t]], b = map[tris[t + 1]], c = map[tris[t + 2]];
                    var fn = (pos[b] - pos[a]).Cross(pos[c] - pos[a]);
                    foreach (int i in new[] { a, b, c }) acc[keys[i]] = acc.GetValueOrDefault(keys[i]) + fn;
                }
                for (int i = 0; i < n; i++)
                {
                    var v = acc.GetValueOrDefault(keys[i]);
                    // même sens que la normale de la peau (le sens des triangles de Godot peut être inversé)
                    nrm[i] = v.LengthSquared() > 1e-12f ? v.Normalized() : SN[order[i]];
                }
                int agree = 0;
                for (int i = 0; i < n; i++) if (nrm[i].Dot(SN[order[i]]) > 0) agree++;
                if (agree < n / 2) for (int i = 0; i < n; i++) nrm[i] = -nrm[i];
            }

            // Couleur par sommet : bandes plus sombres au bord (poignets, col, taille, bas), fermeture éclair des vestes
            float band = g.Slot switch { GarmentSlot.Outer => 0.05f, GarmentSlot.Feet => 0.05f, GarmentSlot.Head => 0.08f, _ => 0.035f };
            Color Shade(int v)
            {
                float f = F(v, gi);
                float k = f < band ? 0.72f : 1f;
                if (g.Slot == GarmentSlot.Feet && P[v].Y < 0.03f) k = 1.6f; // semelle claire
                if (g.Slot == GarmentSlot.Outer && Reg[v] == Region.Torso && P[v].Z > 0 && Math.Abs(P[v].X) < 0.006f) k = 0.5f;
                return new Color(k, k, k);
            }

            var V = new List<Vector3>(pos); var Nn = new List<Vector3>(); var U = new List<Vector2>();
            var Cc = new List<Color>();
            var Tt = T != null ? new List<float>() : null;
            var Bb = new List<int>(); var Ww = new List<float>();
            int bpv = _d.Bpv;
            void Attr(int v, Vector3 normal)
            {
                Nn.Add(normal); U.Add(UV[v]); Cc.Add(Shade(v));
                if (Tt != null) for (int k = 0; k < 4; k++) Tt.Add(T[v * 4 + k]);
                for (int k = 0; k < bpv; k++) { Bb.Add(B[v * bpv + k]); Ww.Add(W[v * bpv + k]); }
            }
            for (int i = 0; i < n; i++) Attr(order[i], nrm[i]);
            var I = new List<int>(tris.Count);
            foreach (int v in tris) I.Add(map[v]);

            if (hems != null)
            {
                foreach (var (a, b) in hems)
                {
                    if (!map.TryGetValue(a, out int a1) || !map.TryGetValue(b, out int b1)) continue;
                    var edge = P[b] - P[a];
                    var nOut = SN[a].Cross(edge).Normalized();
                    if (nOut.LengthSquared() < 0.5f) nOut = N[a];
                    int a0 = V.Count; V.Add(P[a] - SN[a] * 0.002f); Attr(a, nOut);
                    int b0 = V.Count; V.Add(P[b] - SN[b] * 0.002f); Attr(b, nOut);
                    I.Add(a1); I.Add(b0); I.Add(b1);
                    I.Add(a1); I.Add(a0); I.Add(b0);
                }
            }
            var arr = Arrays(V.ToArray(), Nn.ToArray(), Tt?.ToArray(), U.ToArray(), Bb.ToArray(), Ww.ToArray(), I.ToArray());
            arr[(int)Godot.Mesh.ArrayType.Color] = Cc.ToArray();
            return arr;
        }

        /// <summary>Peau : triangles gardés (sommets renumérotés).</summary>
        public Godot.Collections.Array Skin(List<int> tris)
        {
            var map = new Dictionary<int, int>();
            var order = new List<int>();
            var I = new int[tris.Count];
            for (int i = 0; i < tris.Count; i++)
            {
                if (!map.TryGetValue(tris[i], out int n)) { n = order.Count; map[tris[i]] = n; order.Add(tris[i]); }
                I[i] = n;
            }
            int nv = order.Count, bpv = _d.Bpv;
            var V = new Vector3[nv]; var Nn = new Vector3[nv]; var U = new Vector2[nv];
            var Tt = T != null ? new float[nv * 4] : null;
            var Bb = new int[nv * bpv]; var Ww = new float[nv * bpv];
            for (int i = 0; i < nv; i++)
            {
                int v = order[i];
                V[i] = P[v]; Nn[i] = N[v]; U[i] = UV[v];
                if (Tt != null) for (int k = 0; k < 4; k++) Tt[i * 4 + k] = T[v * 4 + k];
                for (int k = 0; k < bpv; k++) { Bb[i * bpv + k] = B[v * bpv + k]; Ww[i * bpv + k] = W[v * bpv + k]; }
            }
            return Arrays(V, Nn, Tt, U, Bb, Ww, I);
        }
    }

    static Godot.Collections.Array Arrays(Vector3[] v, Vector3[] n, float[] t, Vector2[] uv, int[] b, float[] w, int[] idx)
    {
        var a = new Godot.Collections.Array();
        a.Resize((int)Godot.Mesh.ArrayType.Max);
        a[(int)Godot.Mesh.ArrayType.Vertex] = v;
        a[(int)Godot.Mesh.ArrayType.Normal] = n;
        if (t != null) a[(int)Godot.Mesh.ArrayType.Tangent] = t;
        a[(int)Godot.Mesh.ArrayType.TexUV] = uv;
        a[(int)Godot.Mesh.ArrayType.Bones] = b;
        a[(int)Godot.Mesh.ArrayType.Weights] = w;
        a[(int)Godot.Mesh.ArrayType.Index] = idx;
        return a;
    }

    static ArrayMesh Mesh(IEnumerable<Godot.Collections.Array> surfaces, BodyData d)
    {
        var m = new ArrayMesh();
        var flags = d.Bpv == 8 ? Godot.Mesh.ArrayFormat.FlagUse8BoneWeights : 0;
        foreach (var s in surfaces)
        {
            if (s[(int)Godot.Mesh.ArrayType.Index].AsInt32Array().Length == 0) continue;
            m.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, s, null, null, flags);
        }
        return m.GetSurfaceCount() > 0 ? m : null;
    }
}
