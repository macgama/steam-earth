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

    /// <summary>Remplit les tableaux de végétation de <paramref name="data"/> (thread de construction).</summary>
    public static void Scatter(ChunkData data, int face, double u0, double v0, double size, int res, double spacing,
        float[] hgt, bool[] wat, int s, Vec3d center)
    {
        var conifers = new List<Transform3D>(); var conT = new List<Color>();
        var broad = new List<Transform3D>(); var broadT = new List<Color>();
        var bushes = new List<Transform3D>(); var bushT = new List<Color>();

        var quality = GraphicsQuality.Current; // lu une fois : stable pendant toute la construction
        float density = quality.VegetationDensity;
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
            double alat = Math.Abs(lat);
            float snowLine = (float)Math.Max(300, 5200 - 55 * Math.Max(0, alat - 15));
            bool high = h > snowLine - 1600;

            // Chunks de niveau 14 (plus loin) : un sous-ensemble fixe des arbres du niveau 15, un peu plus gros,
            // pour limiter le nombre de polygones (les mêmes arbres restent en place quand on s'approche).
            // Chunks encore plus lointains (Ultra, niveau 13 ≈ 40 m) : sous-ensemble encore plus réduit, arbres plus gros.
            // La densité du niveau de qualité retire des arbres partout (toujours les mêmes : tirage déterministe).
            float keep = spacing > 30 ? 0.12f : spacing > 15 ? 0.35f : 1f;
            if (Rand(ref seed) > keep * density) continue;
            List<Transform3D> list; List<Color> tints; float height; Color tint;
            if (LandCover.IsForest(cover))
            {
                if (roll > 0.8f) continue; // clairières
                bool conifer = alat > 52 || high || (alat > 40 && Rand(ref seed) < 0.35f);
                height = conifer ? 14 + Rand(ref seed) * 18 : 10 + Rand(ref seed) * 14;
                if (high) height *= 0.7f;
                if (conifer) { list = conifers; tints = conT; tint = new Color(0.12f, 0.22f, 0.12f); }
                else { list = broad; tints = broadT; tint = alat < 23 ? new Color(0.13f, 0.30f, 0.09f) : new Color(0.20f, 0.32f, 0.12f); }
            }
            else if (cover == LandCover.Shrub && roll < 0.3f)
            {
                height = 1.2f + Rand(ref seed) * 2.5f;
                list = bushes; tints = bushT; tint = new Color(0.30f, 0.33f, 0.17f);
            }
            else if (cover is LandCover.Grass or LandCover.Crop && roll < 0.004f)
            {
                // arbres isolés dans les prés et en bordure de champs
                height = 8 + Rand(ref seed) * 12;
                list = broad; tints = broadT; tint = new Color(0.20f, 0.32f, 0.12f);
            }
            else continue;

            float shade = 0.8f + Rand(ref seed) * 0.4f;
            tints.Add(new Color(tint.R * shade, tint.G * shade, tint.B * shade));
            // repère local : Y = verticale, rotation aléatoire autour, légère inclinaison
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
            list.Add(new Transform3D(basis, pos.ToVector3()));
        }
        data.VegetationCollides = spacing <= 15;
        data.Conifers = conifers.ToArray(); data.ConiferTints = conT.ToArray();
        data.Broadleaves = broad.ToArray(); data.BroadleafTints = broadT.ToArray();
        data.Bushes = bushes.ToArray(); data.BushTints = bushT.ToArray();
    }

    // ---------- Rendu (thread principal) ----------

    static Mesh _conifer, _broadleaf, _bush;

    static void InitMeshes()
    {
        if (_conifer != null) return;
        var trunkMat = new StandardMaterial3D { AlbedoColor = new Color(0.30f, 0.22f, 0.15f), Roughness = 1 };
        var crownMat = new StandardMaterial3D { VertexColorUseAsAlbedo = true, VertexColorIsSrgb = true, Roughness = 0.95f };

        // Modèles de hauteur 1, mis à l'échelle par instance. Peu de polygones : il y en a des dizaines de milliers.
        _conifer = Build(trunkMat, crownMat,
            (new CylinderMesh { TopRadius = 0.025f, BottomRadius = 0.035f, Height = 0.3f, RadialSegments = 5, Rings = 0 }, new Vector3(0, 0.15f, 0)),
            new (PrimitiveMesh, Vector3)[]
            {
                (new CylinderMesh { TopRadius = 0, BottomRadius = 0.21f, Height = 0.55f, RadialSegments = 7, Rings = 0 }, new Vector3(0, 0.40f, 0)),
                (new CylinderMesh { TopRadius = 0, BottomRadius = 0.15f, Height = 0.45f, RadialSegments = 7, Rings = 0 }, new Vector3(0, 0.775f, 0)),
            });
        _broadleaf = Build(trunkMat, crownMat,
            (new CylinderMesh { TopRadius = 0.03f, BottomRadius = 0.045f, Height = 0.5f, RadialSegments = 5, Rings = 0 }, new Vector3(0, 0.25f, 0)),
            new (PrimitiveMesh, Vector3)[]
            {
                (new SphereMesh { Radius = 0.3f, Height = 0.6f, RadialSegments = 7, Rings = 3 }, new Vector3(0, 0.68f, 0)),
            });
        _bush = Build(null, crownMat, default,
            new (PrimitiveMesh, Vector3)[]
            {
                (new SphereMesh { Radius = 0.6f, Height = 1f, RadialSegments = 6, Rings = 3 }, new Vector3(0, 0.45f, 0)),
            });
    }

    static Mesh Build(Material trunkMat, Material crownMat, (PrimitiveMesh m, Vector3 at) trunk, (PrimitiveMesh m, Vector3 at)[] crown)
    {
        var mesh = new ArrayMesh();
        if (trunk.m != null)
        {
            var st = new SurfaceTool();
            st.AppendFrom(trunk.m, 0, new Transform3D(Basis.Identity, trunk.at));
            st.Commit(mesh);
            mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, trunkMat);
        }
        var sc = new SurfaceTool();
        foreach (var (m, at) in crown) sc.AppendFrom(m, 0, new Transform3D(Basis.Identity, at));
        sc.Commit(mesh);
        mesh.SurfaceSetMaterial(mesh.GetSurfaceCount() - 1, crownMat);
        return mesh;
    }

    /// <summary>Ajoute (ou remplace) la végétation d'un chunk, en enfant de son maillage.</summary>
    public static void Attach(MeshInstance3D chunk, ChunkData data)
    {
        foreach (var child in chunk.GetChildren())
            if (child is MultiMeshInstance3D) child.QueueFree();
        if (data.Conifers == null) { Obstacles.Set(chunk, null); return; }
        InitMeshes();
        Add(chunk, _conifer, data.Conifers, data.ConiferTints);
        Add(chunk, _broadleaf, data.Broadleaves, data.BroadleafTints);
        Add(chunk, _bush, data.Bushes, data.BushTints);
        if (!data.VegetationCollides) { Obstacles.Set(chunk, null); return; }
        // Collisions : troncs (et buissons), seulement pour les chunks proches du joueur
        var shapes = new List<Obstacles.Shape>(data.Conifers.Length + data.Broadleaves.Length + data.Bushes.Length);
        AddShapes(shapes, data, data.Conifers, 0.035f);
        AddShapes(shapes, data, data.Broadleaves, 0.045f);
        AddShapes(shapes, data, data.Bushes, 0.35f);
        Obstacles.Set(chunk, shapes);
    }

    static void AddShapes(List<Obstacles.Shape> shapes, ChunkData data, Transform3D[] xf, float trunkRatio)
    {
        foreach (var t in xf)
        {
            var foot = data.Center + new Vec3d(t.Origin.X, t.Origin.Y, t.Origin.Z);
            double baseAlt = foot.Length() - Geo.Radius;
            double h = t.Basis.Column1.Length(), wide = t.Basis.Column0.Length();
            shapes.Add(Obstacles.Shape.Circle(foot, Math.Max(0.15, wide * trunkRatio), baseAlt, baseAlt + h));
        }
    }

    static void Add(MeshInstance3D chunk, Mesh mesh, Transform3D[] xf, Color[] tints)
    {
        if (xf.Length == 0) return;
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
    }
}
