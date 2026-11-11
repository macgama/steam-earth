using System;
using System.Collections.Generic;
using Godot;

namespace SteamEarth;

/// <summary>
/// Arbres et buissons en 3D sur les chunks les plus fins (≤ 20 m entre sommets, soit ~1,5 km autour du joueur),
/// placés là où ESA WorldCover indique une forêt, une mangrove ou des broussailles.
/// Positions déterministes (même forêt à chaque passage), calculées dans le thread de construction du chunk,
/// puis rendues par MultiMesh (un seul appel de dessin par type d'arbre et par chunk).
/// </summary>
public static class Vegetation
{
    /// <summary>Chunks qui reçoivent des arbres : 21 = niveaux 14 (≈ 20 m) et 15 (≈ 10 m) ; dépend du niveau de qualité.</summary>
    public static double MaxSpacing => GraphicsQuality.Current.VegetationMaxSpacing;
    /// <summary>Nombre d'arbres et buissons actuellement affichés (ou prêts à l'être).</summary>
    public static int Instances { get; private set; }
    const double TreeStep = 7.5;          // un emplacement d'arbre tous les ~7,5 m (≈ 180 arbres/ha au plus)

    static uint Hash(int x, int y, int z)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u + (uint)z * 2246822519u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }
    static float Rand(ref uint s) { s = s * 1664525u + 1013904223u; return (s >> 8) / 16777216f; }

    /// <summary>Essences d'arbres (un modèle 3D chacune).</summary>
    public enum Species { Spruce, Fir, Pine, Larch, Broadleaf, Birch, Poplar, Palm, Acacia, Bush }
    public const int SpeciesCount = 10;

    /// <summary>Saison de la végétation (hémisphère nord ; inversée au sud) : 0 = été … 1 = automne, 2 = hiver, 3 = printemps.</summary>
    internal static int Season(double lat, DateTime utc)
    {
        int doy = utc.DayOfYear;
        if (lat < 0) doy = (doy + 182) % 365 + 1;
        if (Math.Abs(lat) < 23) return 0; // tropiques : toujours vert
        if (doy >= 274 && doy < 320) return 1;  // octobre – mi-novembre
        if (doy >= 320 || doy < 95) return 2;   // mi-novembre – début avril
        if (doy < 135) return 3;                // printemps
        return 0;
    }

    static Color Leaf(Species sp, int season, double lat, float r)
    {
        bool tropic = Math.Abs(lat) < 23;
        switch (sp)
        {
            case Species.Spruce: return new Color(0.09f, 0.18f, 0.11f);
            case Species.Fir: return new Color(0.11f, 0.21f, 0.11f);
            case Species.Pine: return new Color(0.16f, 0.24f, 0.11f);
            case Species.Larch:
                return season switch
                {
                    1 => new Color(0.42f, 0.29f, 0.07f),     // mélèzes dorés
                    2 => new Color(0.30f, 0.25f, 0.20f),     // sans aiguilles
                    3 => new Color(0.30f, 0.42f, 0.16f),
                    _ => new Color(0.20f, 0.32f, 0.13f),
                };
            case Species.Palm: return new Color(0.20f, 0.33f, 0.10f);
            case Species.Acacia: return new Color(0.27f, 0.32f, 0.12f);
            case Species.Bush: return season == 2 && !tropic ? new Color(0.28f, 0.25f, 0.18f) : new Color(0.28f, 0.32f, 0.15f);
            default: // feuillus
                if (tropic) return new Color(0.12f, 0.29f, 0.08f);
                return season switch
                {
                    1 => r < 0.35f ? new Color(0.36f, 0.16f, 0.05f) : r < 0.7f ? new Color(0.38f, 0.26f, 0.07f) : new Color(0.25f, 0.23f, 0.09f),
                    2 => new Color(0.30f, 0.26f, 0.21f),     // branches nues
                    3 => new Color(0.30f, 0.45f, 0.15f),
                    _ => sp == Species.Birch ? new Color(0.24f, 0.36f, 0.12f) : new Color(0.19f, 0.31f, 0.11f),
                };
        }
    }

    /// <summary>Essence d'un arbre de forêt selon latitude, altitude et hasard (forêts mélangées réalistes).</summary>
    static Species ForestSpecies(double alat, double h, float snowLine, float r)
    {
        if (alat < 23) return r < 0.2f ? Species.Palm : Species.Broadleaf;
        if (alat < 40) return r < 0.45f ? Species.Pine : r < 0.55f ? Species.Fir : Species.Broadleaf;
        if (alat > 58) return r < 0.5f ? Species.Spruce : r < 0.8f ? Species.Pine : Species.Birch;
        double sub = snowLine - 1300; // étage subalpin
        if (h > sub) return r < 0.55f ? Species.Spruce : r < 0.8f ? Species.Larch : r < 0.9f ? Species.Fir : Species.Pine;
        if (h > sub - 700) return r < 0.4f ? Species.Spruce : r < 0.62f ? Species.Fir : r < 0.92f ? Species.Broadleaf : Species.Larch;
        return r < 0.45f ? Species.Broadleaf : r < 0.62f ? Species.Spruce : r < 0.74f ? Species.Pine : r < 0.86f ? Species.Fir : r < 0.95f ? Species.Birch : Species.Poplar;
    }

    static (float min, float range) HeightOf(Species sp) => sp switch
    {
        Species.Spruce => (18, 18), Species.Fir => (16, 16), Species.Pine => (14, 14), Species.Larch => (15, 15),
        Species.Birch => (10, 10), Species.Poplar => (18, 12), Species.Palm => (8, 12), Species.Acacia => (5, 6),
        Species.Bush => (1.2f, 2.5f), _ => (12, 14),
    };

    /// <summary>Remplit les tableaux de végétation de <paramref name="data"/> (thread de construction).</summary>
    public static void Scatter(ChunkData data, int face, double u0, double v0, double size, int res, double spacing,
        float[] hgt, bool[] wat, int s, Vec3d center)
    {
        var lists = new List<Transform3D>[SpeciesCount];
        var tints = new List<Color>[SpeciesCount];
        for (int i = 0; i < SpeciesCount; i++) { lists[i] = new List<Transform3D>(); tints[i] = new List<Color>(); }

        var quality = GraphicsQuality.Current; // lu une fois : stable pendant toute la construction
        float density = quality.VegetationDensity;
        var now = DateTime.UtcNow;
        double step = size / res;
        // Grille d'emplacements alignée sur une grille mondiale fixe : un chunk et ses enfants
        // ont exactement les mêmes arbres.
        double cellUv = TreeStep / (Math.PI / 4 * Geo.Radius);
        int c0 = (int)Math.Floor(u0 / cellUv), c1 = (int)Math.Ceiling((u0 + size) / cellUv);
        int r0 = (int)Math.Floor(v0 / cellUv), r1 = (int)Math.Ceiling((v0 + size) / cellUv);

        for (int r = r0; r < r1; r++)
        for (int c = c0; c < c1; c++)
        {
            uint seed = Hash(c, r, face);
            double u = (c + Rand(ref seed)) * cellUv, v = (r + Rand(ref seed)) * cellUv;
            if (u < u0 || u >= u0 + size || v < v0 || v >= v0 + size) continue;

            // altitude : interpolation dans la grille du chunk (bordure d'un échantillon)
            double fi = (u - u0) / step + 1, fj = (v - v0) / step + 1;
            int i0 = Math.Clamp((int)fi, 0, s - 2), j0 = Math.Clamp((int)fj, 0, s - 2);
            double tx = fi - i0, ty = fj - j0;
            int k = j0 * s + i0;
            if (wat[k] || wat[k + 1] || wat[k + s] || wat[k + s + 1]) continue;
            float h00 = hgt[k], h10 = hgt[k + 1], h01 = hgt[k + s], h11 = hgt[k + s + 1];
            double h = (h00 * (1 - tx) + h10 * tx) * (1 - ty) + (h01 * (1 - tx) + h11 * tx) * ty;
            // pas d'arbres sur les falaises (> ~40°)
            double slope = Math.Max(Math.Abs(h10 - h00), Math.Abs(h01 - h00)) / (step * Math.PI / 4 * Geo.Radius);
            if (slope > 0.85) continue;

            var dir = Geo.CubeToSphere(face, u, v);
            Geo.ToLatLon(dir, out double lat, out double lon);
            byte cover = LandCover.Sample(lat, lon, spacing, false, out _);
            float roll = Rand(ref seed);
            float pick = Rand(ref seed); // essence : tirée avant le filtre de densité, donc stable entre niveaux
            double alat = Math.Abs(lat);
            float snowLine = (float)Math.Max(300, 5200 - 55 * Math.Max(0, alat - 15));
            bool high = h > snowLine - 1600;

            // Chunks de niveau 14 (plus loin) : un sous-ensemble fixe des arbres du niveau 15, un peu plus gros,
            // pour limiter le nombre de polygones (les mêmes arbres restent en place quand on s'approche).
            // Chunks encore plus lointains (Ultra, niveau 13 ≈ 40 m) : sous-ensemble encore plus réduit, arbres plus gros.
            // La densité du niveau de qualité retire des arbres partout (toujours les mêmes : tirage déterministe).
            float keep = spacing > 30 ? 0.12f : spacing > 15 ? 0.35f : 1f;
            if (Rand(ref seed) > keep * density) continue;
            Species sp;
            if (LandCover.IsForest(cover))
            {
                if (roll > 0.8f) continue; // clairières
                sp = cover == LandCover.Mangrove ? Species.Broadleaf : ForestSpecies(alat, h, snowLine, pick);
            }
            else if (cover == LandCover.Shrub && roll < 0.3f)
                sp = alat < 30 && pick < 0.12f ? Species.Acacia : Species.Bush;
            else if (cover is LandCover.Grass or LandCover.Crop && roll < 0.004f)
            {
                // arbres isolés dans les prés et en bordure de champs
                sp = alat < 23 ? (pick < 0.4f ? Species.Palm : Species.Broadleaf)
                    : alat < 35 ? (pick < 0.3f ? Species.Acacia : Species.Broadleaf)
                    : pick < 0.6f ? Species.Broadleaf : pick < 0.8f ? Species.Poplar : Species.Birch;
            }
            else continue;

            var (hmin, hrange) = HeightOf(sp);
            float height = hmin + Rand(ref seed) * hrange;
            if (high && sp != Species.Bush) height *= 0.7f;
            var leaf = Leaf(sp, Season(lat, now), lat, Rand(ref seed));
            float shade = 0.8f + Rand(ref seed) * 0.4f;
            tints[(int)sp].Add(new Color(leaf.R * shade, leaf.G * shade, leaf.B * shade));
            // repère local : Y = verticale, rotation aléatoire autour
            var up = dir.ToVector3().Normalized();
            var any = Math.Abs(up.Y) < 0.9f ? Vector3.Up : Vector3.Right;
            var x = any.Cross(up).Normalized();
            var z = x.Cross(up);
            float a = Rand(ref seed) * Mathf.Tau;
            var xr = x * Mathf.Cos(a) + z * Mathf.Sin(a);
            var zr = xr.Cross(up);
            float wide = height * (0.85f + Rand(ref seed) * 0.3f) * (keep < 0.2f ? 1.6f : keep < 1 ? 1.3f : 1f);
            var basis = new Basis(xr * wide, up * height, zr * wide);
            var pos = dir * (Geo.Radius + h - 0.3) - center; // enfoncé de 30 cm : pas de pied flottant en pente
            lists[(int)sp].Add(new Transform3D(basis, pos.ToVector3()));
        }
        data.VegetationCollides = spacing <= 15;
        data.Trees = new Transform3D[SpeciesCount][];
        data.TreeTints = new Color[SpeciesCount][];
        for (int i = 0; i < SpeciesCount; i++) { data.Trees[i] = lists[i].ToArray(); data.TreeTints[i] = tints[i].ToArray(); }
    }

    // ---------- Rendu (thread principal) ----------

    static Mesh[] _meshes;
    static readonly float[] TrunkRatio = { 0.03f, 0.035f, 0.03f, 0.03f, 0.045f, 0.03f, 0.03f, 0.03f, 0.04f, 0.35f };

    static ShaderMaterial Mat(bool crown, float stiffness = 1, Color? trunk = null)
    {
        var m = new ShaderMaterial { Shader = GD.Load<Shader>("res://shaders/tree.gdshader") };
        m.SetShaderParameter("use_instance_color", crown);
        m.SetShaderParameter("stiffness", stiffness);
        if (trunk is Color t) m.SetShaderParameter("trunk_color", t);
        return m;
    }

    static Transform3D At(float x, float y, float z, float sx = 1, float sy = 1, float sz = 1, float rotX = 0, float rotY = 0) =>
        new(Basis.FromEuler(new Vector3(rotX, rotY, 0)).Scaled(new Vector3(sx, sy, sz)), new Vector3(x, y, z));

    static CylinderMesh Cone(float r, float hgt, int seg = 7) => new() { TopRadius = 0, BottomRadius = r, Height = hgt, RadialSegments = seg, Rings = 0 };
    static CylinderMesh Trunk(float r0, float r1, float hgt) => new() { TopRadius = r1, BottomRadius = r0, Height = hgt, RadialSegments = 5, Rings = 0 };
    static SphereMesh Ball(float r, int seg = 7, int rings = 3) => new() { Radius = r, Height = 2 * r, RadialSegments = seg, Rings = rings };

    static void InitMeshes()
    {
        if (_meshes != null) return;
        var bark = Mat(false);
        var birchBark = Mat(false, 1, new Color(0.82f, 0.80f, 0.74f));
        var palmBark = Mat(false, 0.6f, new Color(0.45f, 0.38f, 0.28f));
        var crown = Mat(true);
        var soft = Mat(true, 0.6f);
        _meshes = new Mesh[SpeciesCount];
        // Modèles de hauteur 1, mis à l'échelle par instance. Peu de polygones : il y en a des dizaines de milliers.
        _meshes[(int)Species.Spruce] = Build(bark, crown, new (PrimitiveMesh, Transform3D)[] { (Trunk(0.03f, 0.02f, 0.25f), At(0, 0.125f, 0)) },
            new (PrimitiveMesh, Transform3D)[] { (Cone(0.17f, 0.45f), At(0, 0.36f, 0)), (Cone(0.13f, 0.4f), At(0, 0.6f, 0)), (Cone(0.085f, 0.35f), At(0, 0.825f, 0)) });
        _meshes[(int)Species.Fir] = Build(bark, crown, new (PrimitiveMesh, Transform3D)[] { (Trunk(0.035f, 0.025f, 0.25f), At(0, 0.125f, 0)) },
            new (PrimitiveMesh, Transform3D)[] { (Cone(0.24f, 0.4f), At(0, 0.33f, 0)), (Cone(0.19f, 0.35f), At(0, 0.53f, 0)), (Cone(0.13f, 0.32f), At(0, 0.71f, 0)), (Cone(0.07f, 0.25f), At(0, 0.875f, 0)) });
        _meshes[(int)Species.Pine] = Build(bark, crown, new (PrimitiveMesh, Transform3D)[] { (Trunk(0.035f, 0.022f, 0.75f), At(0, 0.375f, 0)) },
            new (PrimitiveMesh, Transform3D)[] { (Ball(0.2f), At(0, 0.82f, 0, 1.2f, 0.55f, 1.1f)), (Ball(0.14f), At(0.09f, 0.72f, 0.05f, 1, 0.6f, 1)), (Ball(0.12f), At(-0.08f, 0.9f, -0.04f, 1, 0.6f, 1)) });
        _meshes[(int)Species.Larch] = Build(bark, crown, new (PrimitiveMesh, Transform3D)[] { (Trunk(0.03f, 0.02f, 0.3f), At(0, 0.15f, 0)) },
            new (PrimitiveMesh, Transform3D)[] { (Cone(0.18f, 0.75f, 6), At(0, 0.6f, 0)) });
        _meshes[(int)Species.Broadleaf] = Build(bark, crown, new (PrimitiveMesh, Transform3D)[] { (Trunk(0.045f, 0.03f, 0.5f), At(0, 0.25f, 0)) },
            new (PrimitiveMesh, Transform3D)[] { (Ball(0.28f), At(0, 0.68f, 0, 1, 0.85f, 1)), (Ball(0.2f), At(0.15f, 0.6f, 0.06f)), (Ball(0.19f), At(-0.13f, 0.62f, -0.08f)), (Ball(0.17f), At(0.02f, 0.86f, 0.03f)) });
        _meshes[(int)Species.Birch] = Build(birchBark, crown, new (PrimitiveMesh, Transform3D)[] { (Trunk(0.025f, 0.015f, 0.6f), At(0, 0.3f, 0)) },
            new (PrimitiveMesh, Transform3D)[] { (Ball(0.17f), At(0, 0.68f, 0, 0.9f, 1.8f, 0.9f)) });
        _meshes[(int)Species.Poplar] = Build(bark, soft, new (PrimitiveMesh, Transform3D)[] { (Trunk(0.03f, 0.02f, 0.25f), At(0, 0.125f, 0)) },
            new (PrimitiveMesh, Transform3D)[] { (Ball(0.1f), At(0, 0.6f, 0, 1, 4.2f, 1)) });
        // Palmier : stipe un peu penché + palmes retombantes (boîtes aplaties)
        var fronds = new List<(PrimitiveMesh, Transform3D)>();
        for (int i = 0; i < 8; i++)
        {
            float a = i * Mathf.Tau / 8;
            var leaf = new BoxMesh { Size = new Vector3(0.09f, 0.008f, 0.42f) };
            var b = Basis.FromEuler(new Vector3(0.55f, a, 0));
            fronds.Add((leaf, new Transform3D(b, new Vector3(0.06f + Mathf.Sin(a) * 0.16f, 0.93f, Mathf.Cos(a) * 0.16f))));
        }
        _meshes[(int)Species.Palm] = Build(palmBark, soft, new (PrimitiveMesh, Transform3D)[] { (Trunk(0.035f, 0.025f, 0.95f), At(0.03f, 0.47f, 0, 1, 1, 1, 0, 0) with { Basis = Basis.FromEuler(new Vector3(0, 0, -0.07f)) }) },
            fronds.ToArray());
        // Acacia : tronc court, couronne plate en parasol
        _meshes[(int)Species.Acacia] = Build(bark, crown, new (PrimitiveMesh, Transform3D)[] { (Trunk(0.05f, 0.035f, 0.6f), At(0, 0.3f, 0)) },
            new (PrimitiveMesh, Transform3D)[] { (Ball(0.4f, 8, 3), At(0, 0.78f, 0, 1.3f, 0.3f, 1.3f)) });
        _meshes[(int)Species.Bush] = Build(null, crown, null,
            new (PrimitiveMesh, Transform3D)[] { (Ball(0.6f, 6, 3), At(0, 0.45f, 0, 1, 0.83f, 1)) });
    }

    static Mesh Build(Material trunkMat, Material crownMat, (PrimitiveMesh m, Transform3D at)[] trunk, (PrimitiveMesh m, Transform3D at)[] crown)
    {
        var mesh = new ArrayMesh();
        if (trunk != null)
        {
            var st = new SurfaceTool();
            foreach (var (m, at) in trunk) st.AppendFrom(m, 0, at);
            st.Commit(mesh);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, trunkMat);
        }
        var sc = new SurfaceTool();
        foreach (var (m, at) in crown) sc.AppendFrom(m, 0, at);
        sc.Commit(mesh);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, crownMat);
        return mesh;
    }

    /// <summary>Vent réel (GFS) pour les arbres et l'herbe : paramètre global des shaders.</summary>
    public static void SetWind(WeatherSample w)
    {
        float sp = Math.Max(w.Wind10Speed, 0.01f);
        RenderingServer.GlobalShaderParameterSet("wind", new Vector4(Math.Clamp(w.Wind10Speed / 15f, 0, 1),
            w.Wind10East / sp, -w.Wind10North / sp, Math.Clamp((w.Gust - w.Wind10Speed) / 10f, 0, 1)));
    }

    /// <summary>Ajoute (ou remplace) la végétation d'un chunk, en enfant de son maillage.</summary>
    public static void Attach(MeshInstance3D chunk, ChunkData data)
    {
        foreach (var child in chunk.GetChildren())
            if (child is MultiMeshInstance3D) child.QueueFree();
        if (data.Trees == null) { Obstacles.Set(chunk, null); return; }
        InitMeshes();
        // Chaque arbre est aussi une empreinte : solide près du joueur (troncs), et cachée s'il tombe
        // dans un bâtiment ou sur une route (voir Obstacles.Clears).
        int total = 0;
        foreach (var a in data.Trees) total += a.Length;
        var shapes = new List<Obstacles.Shape>(total);
        for (int i = 0; i < SpeciesCount; i++)
        {
            var mm = Add(chunk, _meshes[i], data.Trees[i], data.TreeTints[i]);
            if (mm == null) continue;
            for (int k = 0; k < data.Trees[i].Length; k++)
            {
                var t = data.Trees[i][k];
                var foot = data.Center + new Vec3d(t.Origin.X, t.Origin.Y, t.Origin.Z);
                double baseAlt = foot.Length() - Geo.Radius;
                double h = t.Basis.Column1.Length(), wide = t.Basis.Column0.Length();
                var sh = Obstacles.Shape.Circle(foot, Math.Max(0.15, wide * TrunkRatio[i]), baseAlt, baseAlt + h);
                sh.Solid = data.VegetationCollides;
                sh.Instances = mm; sh.InstanceIndex = k;
                shapes.Add(sh);
            }
        }
        Obstacles.Set(chunk, shapes);
    }

    static MultiMesh Add(MeshInstance3D chunk, Mesh mesh, Transform3D[] xf, Color[] tints)
    {
        if (xf.Length == 0) return null;
        var mm = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform3D,
            UseColors = true,
            Mesh = mesh,
            InstanceCount = xf.Length,
        };
        for (int i = 0; i < xf.Length; i++)
        {
            mm.SetInstanceTransform(i, xf[i]);
            mm.SetInstanceColor(i, tints[i]);
        }
        var mmi = new MultiMeshInstance3D { Multimesh = mm, CastShadow = GraphicsQuality.ShadowFor(ShadowCaster.Trees) };
        int n = xf.Length;
        Instances += n;
        mmi.TreeExiting += () => Instances -= n;
        chunk.AddChild(mmi);
        return mm;
    }
}
