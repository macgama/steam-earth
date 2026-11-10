using System;
using Godot;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>Données géométriques d'un chunk, calculées hors du thread principal.</summary>
public sealed class ChunkData
{
    public Vector3[] Vertices;
    public Vector3[] Normals;
    public Color[] Colors;
    // Textures de sol (voir terrain.gdshader) : poids herbe, sous-bois, champ, ville (le reste = terre) ;
    // roche et neige ; décalage du chunk pour des coordonnées de texture continues d'un chunk à l'autre.
    public float[] Layers;   // 4 par sommet
    public Vector2[] RockSnow;
    public float[] TexOffset; // 3 par sommet (constant dans le chunk)
    public float[] Geo3;      // 3 par sommet : latitude, longitude (degrés), altitude (m) — neige réelle dans le shader
    public int[] Indices;
    public Vec3d Center; // centre du chunk dans le repère planète (double), au niveau de la mer
    public double MeanHeight; // altitude moyenne du chunk (m), pour le calcul du niveau de détail
    public bool Incomplete;   // des données fines étaient encore en téléchargement : à reconstruire
    public int DataVersion;   // version des données au moment de la construction
    // Végétation (chunks les plus fins seulement) : transformations relatives au centre du chunk
    public Transform3D[][] Trees;   // par essence (Vegetation.Species)
    public Color[][] TreeTints;
    public bool VegetationCollides; // chunks proches (≤ 15 m) : troncs solides pour le personnage
    // Surface de l'eau (mers et lacs), rendue par Ocean : null s'il n'y a pas d'eau dans le chunk
    public Vector3[] WaterVertices, WaterNormals;
    public Color[] WaterColors;
    public Vector2[] WaterUV, WaterUV2;
    public int[] WaterIndices;
    public bool HasSea; // le chunk touche la mer : le quadtree peut le détailler davantage (vagues)
}

/// <summary>
/// Construit le maillage d'une tuile de cube-sphère : grille (Res+1)² sommets,
/// déplacés selon l'altitude, + « jupes » sur les bords pour masquer les fissures entre LOD.
/// Les sommets sont exprimés relativement au centre du chunk pour garder la précision en float.
/// </summary>
public static class ChunkBuilder
{
    public static ChunkData Build(int face, double u0, double v0, double size, int res)
    {
        int version = StreamedRaster.DataVersion;
        bool incomplete = false;
        // Espacement au sol entre deux sommets (m) : choisit la résolution des données à lire.
        double spacing = size / res * Math.PI / 4 * Geo.Radius;
        int s = res + 3; // grille avec une bordure d'un échantillon (pour les normales)
        var pos = new Vec3d[s * s];
        var dirs = new Vec3d[s * s];
        var wat = new bool[s * s];
        var hgt = new float[s * s];
        var cov = new byte[s * s];
        var sea = new bool[s * s];
        var surf = new float[s * s]; // altitude de la surface : eau (0 en mer, niveau du lac) ou sol
        double step = size / res;

        for (int j = 0; j < s; j++)
        for (int i = 0; i < s; i++)
        {
            double u = u0 + (i - 1) * step, v = v0 + (j - 1) * step;
            var d = Geo.CubeToSphere(face, u, v);
            Geo.ToLatLon(d, out double lat, out double lon);
            float h = HeightProvider.Sample(lat, lon, spacing, out bool w, out bool pending, out byte cover, out bool sw);
            if (pending) incomplete = true;
            int k = j * s + i;
            float bed = h;
            if (sw)
            {
                // mer : surface au niveau de la mer, fond GEBCO
                bed = Bathymetry.SeabedHeight(lat, lon, spacing, out bool bp);
                if (bp) incomplete = true;
                surf[k] = 0;
            }
            else if (w)
            {
                // lac ou rivière : surface mesurée par le relief ; fond inconnu, on le creuse un peu sous la surface
                surf[k] = h;
                bed = h - LakeDepth;
            }
            else surf[k] = h;
            dirs[k] = d; wat[k] = w; sea[k] = sw; hgt[k] = bed; cov[k] = cover;
            pos[k] = d * (Geo.Radius + bed);
        }

        var centerDir = Geo.CubeToSphere(face, u0 + size / 2, v0 + size / 2);
        var center = centerDir * Geo.Radius;
        int n = res + 1;
        int border = 4 * res;
        var verts = new Vector3[n * n + border];
        var norms = new Vector3[n * n + border];
        var cols = new Color[n * n + border];
        var layers = new float[(n * n + border) * 4];
        var rockSnow = new Vector2[n * n + border];
        var geo3 = new float[(n * n + border) * 3];

        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int k = (j + 1) * s + (i + 1);
            int o = j * n + i;
            verts[o] = (pos[k] - center).ToVector3();
            var dx = pos[k + 1] - pos[k - 1];
            var dy = pos[k + s] - pos[k - s];
            var nn = dx.Cross(dy).Normalized();
            if (nn.Dot(dirs[k]) < 0) nn = -nn;
            norms[o] = nn.ToVector3();
            Geo.ToLatLon(dirs[k], out double lat, out double lon);
            Color lw = default;
            if (wat[k]) { cols[o] = BedColor(surf[k] - hgt[k], sea[k]); rockSnow[o] = new Vector2(-1, 0); } // fond : pas de texture de sol
            else cols[o] = TerrainColor(hgt[k], false, (float)nn.Dot(dirs[k]), lat, lon, cov[k], spacing, out lw, out rockSnow[o]);
            geo3[3 * o] = (float)lat; geo3[3 * o + 1] = (float)lon; geo3[3 * o + 2] = hgt[k];
            layers[4 * o] = lw.R; layers[4 * o + 1] = lw.G; layers[4 * o + 2] = lw.B; layers[4 * o + 3] = lw.A;
        }

        // Indices : on choisit l'ordre des sommets pour que la face avant regarde vers l'extérieur
        // (Godot : sens horaire = face avant).
        var a0 = verts[0]; var b0 = verts[1]; var c0 = verts[n];
        bool flip = ((b0 - a0).Cross(c0 - a0)).Dot(centerDir.ToVector3()) > 0;
        int quadIdx = res * res * 6;
        var idx = new int[quadIdx + border * 12];
        int t = 0;
        for (int j = 0; j < res; j++)
        for (int i = 0; i < res; i++)
        {
            int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
            if (!flip) { idx[t++] = a; idx[t++] = b; idx[t++] = c; idx[t++] = b; idx[t++] = d; idx[t++] = c; }
            else { idx[t++] = a; idx[t++] = c; idx[t++] = b; idx[t++] = b; idx[t++] = c; idx[t++] = d; }
        }

        // Jupes : contour du chunk, dupliqué et abaissé vers le centre de la Terre.
        // Leur hauteur suit le dénivelé du chunk : près d'une falaise, le voisin moins détaillé peut passer
        // des dizaines de mètres plus bas (ex. parois de Lauterbrunnen en 0,5 m à côté du 30 m).
        float hMin = float.MaxValue, hMax = float.MinValue;
        for (int j = 1; j <= n; j++) for (int i = 1; i <= n; i++) { float hv = hgt[j * s + i]; if (hv < hMin) hMin = hv; if (hv > hMax) hMax = hv; }
        double skirt = size * Geo.Radius * 0.02 + 20.0 + (hMax - hMin);
        var ring = new int[border];
        int r = 0;
        for (int i = 0; i < res; i++) ring[r++] = i;                       // bas
        for (int j = 0; j < res; j++) ring[r++] = j * n + res;             // droite
        for (int i = res; i > 0; i--) ring[r++] = res * n + i;             // haut
        for (int j = res; j > 0; j--) ring[r++] = j * n;                   // gauche
        int sb = n * n;
        var down = (-centerDir * skirt).ToVector3();
        for (int q = 0; q < border; q++)
        {
            verts[sb + q] = verts[ring[q]] + down;
            norms[sb + q] = norms[ring[q]];
            cols[sb + q] = cols[ring[q]];
            rockSnow[sb + q] = rockSnow[ring[q]];
            for (int c = 0; c < 3; c++) geo3[3 * (sb + q) + c] = geo3[3 * ring[q] + c];
            for (int c = 0; c < 4; c++) layers[4 * (sb + q) + c] = layers[4 * ring[q] + c];
        }
        for (int q = 0; q < border; q++)
        {
            int p0 = ring[q], p1 = ring[(q + 1) % border], s0 = sb + q, s1 = sb + (q + 1) % border;
            // les deux sens, pour être visibles quel que soit l'angle
            idx[t++] = p0; idx[t++] = p1; idx[t++] = s0; idx[t++] = p1; idx[t++] = s1; idx[t++] = s0;
            idx[t++] = p0; idx[t++] = s0; idx[t++] = p1; idx[t++] = p1; idx[t++] = s0; idx[t++] = s1;
        }

        // Altitude moyenne de la surface (eau comprise) : le niveau de détail suit la surface de la mer, pas le fond
        double sum = 0;
        for (int j = 1; j <= n; j++) for (int i = 1; i <= n; i++) sum += surf[j * s + i];
        // Décalage de texture : centre du chunk modulo la période des textures (calculé en double)
        const double period = 1024;
        float ox = (float)(center.X - Math.Floor(center.X / period) * period);
        float oy = (float)(center.Y - Math.Floor(center.Y / period) * period);
        float oz = (float)(center.Z - Math.Floor(center.Z / period) * period);
        var texOff = new float[verts.Length * 3];
        for (int q = 0; q < verts.Length; q++) { texOff[3 * q] = ox; texOff[3 * q + 1] = oy; texOff[3 * q + 2] = oz; }
        var data = new ChunkData { Geo3 = geo3, Layers = layers, RockSnow = rockSnow, TexOffset = texOff, Vertices = verts, Normals = norms, Colors = cols, Indices = idx, Center = center, MeanHeight = sum / (n * n), Incomplete = incomplete, DataVersion = version };
        BuildWater(data, res, s, spacing, size, dirs, wat, sea, surf, hgt, center, centerDir, flip);
        if (spacing <= Vegetation.MaxSpacing) Vegetation.Scatter(data, face, u0, v0, size, res, spacing, hgt, wat, s, center);
        return data;
    }

    /// <summary>Profondeur supposée des lacs et rivières (m) : leur fond n'est pas connu.</summary>
    const float LakeDepth = 3f;

    /// <summary>
    /// Surface de l'eau du chunk : même grille que le relief, au niveau de la mer (ou du lac), sur toutes les cases
    /// qui touchent de l'eau. Elle déborde un peu sous la plage : l'intersection avec le relief dessine le rivage.
    /// </summary>
    static void BuildWater(ChunkData data, int res, int s, double spacing, double size, Vec3d[] dirs, bool[] wat, bool[] sea,
        float[] surf, float[] hgt, Vec3d center, Vec3d centerDir, bool flip)
    {
        int n = res + 1;
        bool any = false;
        for (int j = 1; j <= n && !any; j++) for (int i = 1; i <= n; i++) if (wat[j * s + i]) { any = true; break; }
        if (!any) return;

        // Niveau de l'eau de chaque sommet ; un sommet à terre prend celui de l'eau voisine (NaN : pas d'eau autour)
        var level = new float[n * n];
        var isSea = new bool[n * n];
        bool hasSea = false;
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int k = (j + 1) * s + (i + 1);
            float lv = float.NaN; bool sv = false;
            if (wat[k]) { lv = surf[k]; sv = sea[k]; }
            else
            {
                for (int dj = -1; dj <= 1; dj++)
                for (int di = -1; di <= 1; di++)
                {
                    int kk = k + dj * s + di;
                    if (!wat[kk]) continue;
                    if (sea[kk]) { lv = 0; sv = true; }
                    else if (!sv && (float.IsNaN(lv) || surf[kk] > lv)) lv = surf[kk];
                }
            }
            level[j * n + i] = lv; isSea[j * n + i] = sv;
            if (sv) hasSea = true;
        }

        // Centre du chunk modulo la période des vagues (en double), voir shaders/ocean.gdshader
        double P = Ocean.Period;
        double Mod(double x) => x - Math.Floor(x / P) * P;
        var cm = new Vector3((float)Mod(center.X), (float)Mod(center.Y), (float)Mod(center.Z));
        float spacingCode = (float)Math.Clamp((Math.Log2(Math.Max(spacing, 0.07)) + 4) / 20, 0, 1);

        int border = 4 * res;
        var verts = new Vector3[n * n + border];
        var norms = new Vector3[n * n + border];
        var cols = new Color[n * n + border];
        var uv = new Vector2[n * n + border];
        var uv2 = new Vector2[n * n + border];
        for (int j = 0; j < n; j++)
        for (int i = 0; i < n; i++)
        {
            int o = j * n + i, k = (j + 1) * s + (i + 1);
            float lv = float.IsNaN(level[o]) ? surf[k] : level[o];
            verts[o] = (dirs[k] * (Geo.Radius + lv) - center).ToVector3();
            norms[o] = dirs[k].ToVector3();
            cols[o] = new Color(isSea[o] ? 1 : 0, spacingCode, 0, 1);
            uv[o] = new Vector2(cm.X, cm.Y);
            uv2[o] = new Vector2(cm.Z, lv - hgt[k]); // profondeur (négative à terre)
        }

        var idx = new System.Collections.Generic.List<int>(res * res * 6);
        for (int j = 0; j < res; j++)
        for (int i = 0; i < res; i++)
        {
            int a = j * n + i, b = a + 1, c = a + n, d = c + 1;
            bool w = wat[(j + 1) * s + i + 1] || wat[(j + 1) * s + i + 2] || wat[(j + 2) * s + i + 1] || wat[(j + 2) * s + i + 2];
            if (!w) continue;
            if (!flip) { idx.Add(a); idx.Add(b); idx.Add(c); idx.Add(b); idx.Add(d); idx.Add(c); }
            else { idx.Add(a); idx.Add(c); idx.Add(b); idx.Add(b); idx.Add(c); idx.Add(d); }
        }
        if (idx.Count == 0) return;

        // Jupes : bord de la surface abaissé (masque les fentes entre chunks de niveaux différents, vagues comprises)
        var ring = new int[border];
        int r = 0;
        for (int i = 0; i < res; i++) ring[r++] = i;
        for (int j = 0; j < res; j++) ring[r++] = j * n + res;
        for (int i = res; i > 0; i--) ring[r++] = res * n + i;
        for (int j = res; j > 0; j--) ring[r++] = j * n;
        int sb = n * n;
        var down = (-centerDir * (12.0 + size * Geo.Radius * 0.002)).ToVector3();
        for (int q = 0; q < border; q++)
        {
            int p = ring[q];
            verts[sb + q] = verts[p] + down;
            norms[sb + q] = norms[p]; cols[sb + q] = cols[p]; uv[sb + q] = uv[p]; uv2[sb + q] = uv2[p];
        }
        for (int q = 0; q < border; q++)
        {
            int p0 = ring[q], p1 = ring[(q + 1) % border], s0 = sb + q, s1 = sb + (q + 1) % border;
            if (float.IsNaN(level[p0]) || float.IsNaN(level[p1])) continue;
            idx.Add(p0); idx.Add(p1); idx.Add(s0); idx.Add(p1); idx.Add(s1); idx.Add(s0);
        }

        data.WaterVertices = verts; data.WaterNormals = norms; data.WaterColors = cols;
        data.WaterUV = uv; data.WaterUV2 = uv2; data.WaterIndices = idx.ToArray();
        data.HasSea = hasSea;
    }

    /// <summary>Couleur du fond sous l'eau : sable près des côtes, vase sombre au large.</summary>
    static Color BedColor(float depth, bool sea)
    {
        if (!sea) return new Color(0.30f, 0.28f, 0.20f);
        float t = Math.Clamp(depth / 60f, 0, 1);
        return new Color(0.66f, 0.60f, 0.45f).Lerp(new Color(0.24f, 0.24f, 0.22f), t);
    }

    static readonly Color OceanColor = new(0.06f, 0.17f, 0.32f);
    static readonly Color Lake = new(0.10f, 0.25f, 0.40f);
    static readonly Color Lowland = new(0.24f, 0.36f, 0.15f);
    static readonly Color Forest = new(0.15f, 0.25f, 0.11f);
    static readonly Color Alpine = new(0.40f, 0.42f, 0.26f);
    static readonly Color Rock = new(0.42f, 0.39f, 0.35f);
    static readonly Color Snow = new(0.92f, 0.94f, 0.97f);
    static readonly Color Sand = new(0.70f, 0.62f, 0.45f);

    // Couleurs par classe ESA WorldCover
    static readonly Color Broadleaf = new(0.16f, 0.27f, 0.10f);
    static readonly Color Conifer = new(0.10f, 0.19f, 0.10f);
    static readonly Color Tropical = new(0.11f, 0.26f, 0.08f);
    static readonly Color Shrub = new(0.36f, 0.37f, 0.21f);
    static readonly Color Grass = new(0.33f, 0.45f, 0.18f);
    static readonly Color DryGrass = new(0.55f, 0.52f, 0.30f);
    static readonly Color Urban = new(0.50f, 0.46f, 0.42f);
    static readonly Color Desert = new(0.76f, 0.64f, 0.45f);
    static readonly Color Ice = new(0.86f, 0.91f, 0.97f);
    static readonly Color Wetland = new(0.26f, 0.34f, 0.22f);
    static readonly Color Mangrove = new(0.09f, 0.21f, 0.11f);
    static readonly Color Moss = new(0.45f, 0.44f, 0.33f);
    // Champs : une couleur par parcelle (blé, colza, labour, prairie temporaire, maïs…)
    static readonly Color[] Fields =
    {
        new(0.62f, 0.56f, 0.30f), new(0.70f, 0.66f, 0.28f), new(0.42f, 0.33f, 0.22f),
        new(0.36f, 0.48f, 0.19f), new(0.47f, 0.52f, 0.22f), new(0.55f, 0.47f, 0.30f),
    };

    static uint Hash(int x, int y)
    {
        uint h = (uint)x * 374761393u + (uint)y * 668265263u;
        h = (h ^ (h >> 13)) * 1274126177u;
        return h ^ (h >> 16);
    }

    /// <summary>Couleur du sol : classe WorldCover si connue, sinon estimation selon altitude, pente et latitude.</summary>
    static Color TerrainColor(float h, bool water, float flatness, double lat, double lon, byte cover, double spacing,
        out Color layers, out Vector2 rockSnow)
    {
        layers = new Color(0, 0, 0, 0);
        rockSnow = Vector2.Zero;
        if (water) { rockSnow = new Vector2(-1, 0); return h <= 0.5f ? OceanColor : Lake; } // eau : pas de texture de sol
        double alat = Math.Abs(lat);
        float snowLine = (float)Math.Max(300, 5200 - 55 * Math.Max(0, alat - 15));
        float treeLine = snowLine - 1000;
        float steep = Math.Clamp((0.88f - flatness) / 0.15f, 0, 1);
        Color c;
        if (cover != LandCover.Unknown)
        {
            // Détail visible seulement quand les sommets sont assez serrés (sinon : moiré)
            float detail = (float)Math.Clamp((300 - spacing) / 200, 0, 1);
            switch (cover)
            {
                case LandCover.Tree:
                    c = alat > 50 || h > treeLine * 0.6f ? Conifer : alat < 23 ? Tropical : Broadleaf;
                    break;
                case LandCover.Shrub: c = alat < 40 ? Shrub.Lerp(DryGrass, 0.3f) : Shrub; break;
                case LandCover.Grass:
                    // alpages et steppes plus jaunes en altitude
                    c = Grass.Lerp(DryGrass, Math.Clamp((h - treeLine * 0.8f) / 800f, 0, 0.7f) + (alat < 35 ? 0.4f : 0));
                    break;
                case LandCover.Crop:
                {
                    // parcelles d'environ 150 × 200 m
                    int fx = (int)Math.Floor(lon * 600 * Math.Cos(lat * Math.PI / 180)), fy = (int)Math.Floor(lat * 800);
                    var field = Fields[Hash(fx, fy) % (uint)Fields.Length];
                    c = Fields[0].Lerp(field, detail);
                    break;
                }
                case LandCover.BuiltUp: c = Urban; break;
                case LandCover.Bare: c = h > treeLine ? Rock : Desert.Lerp(Rock, Math.Clamp(h / 2000f, 0, 0.6f)); break;
                case LandCover.SnowIce: rockSnow = new Vector2(steep * 0.5f, 1 - steep * 0.5f); return Ice.Lerp(Rock, steep * 0.5f); // glaciers, inlandsis
                case LandCover.Wetland: c = Wetland; break;
                case LandCover.Mangrove: c = Mangrove; break;
                case LandCover.Moss: c = Moss; break;
                default: c = Lowland; break;
            }
            // légère variation pour casser l'aplat (forêts, prairies)
            if (cover is LandCover.Tree or LandCover.Grass or LandCover.Shrub)
            {
                float v = ((Hash((int)Math.Floor(lon * 3000), (int)Math.Floor(lat * 3000)) & 255) / 255f - 0.5f) * 0.12f * detail;
                c = new Color(c.R * (1 + v), c.G * (1 + v), c.B * (1 + v));
            }
            // forêts accrochées aux pentes : elles restent vertes ; le reste laisse voir la roche
            float rock = steep * (LandCover.IsForest(cover) ? 0.25f : cover == LandCover.BuiltUp ? 0.3f : 0.8f);
            layers = cover switch
            {
                LandCover.Tree or LandCover.Mangrove => new Color(0, 1, 0, 0),
                LandCover.Grass or LandCover.Wetland => new Color(1, 0, 0, 0),
                LandCover.Shrub or LandCover.Moss => new Color(0.5f, 0, 0, 0),
                LandCover.Crop => new Color(0, 0, 1, 0),
                LandCover.BuiltUp => new Color(0, 0, 0, 1),
                _ => new Color(0, 0, 0, 0), // terre, sable, désert
            };
            if (cover == LandCover.Bare && h > treeLine) rock = 1;
            rockSnow = new Vector2(rock, 0);
            return c.Lerp(Rock, rock);
        }

        if (h < 3 && alat < 60) c = Sand;
        else if (h < treeLine * 0.5f) c = Lowland.Lerp(Forest, Math.Clamp(h / (treeLine * 0.5f), 0, 1));
        else if (h < treeLine) c = Forest.Lerp(Alpine, Math.Clamp((h - treeLine * 0.5f) / (treeLine * 0.5f), 0, 1));
        else c = Alpine.Lerp(Rock, Math.Clamp((h - treeLine) / 800f, 0, 1));
        // Pentes raides : roche
        c = c.Lerp(Rock, steep);
        // Neige au-dessus de la limite, sauf sur les falaises
        float snow = Math.Clamp((h - snowLine) / 300f, 0, 1) * Math.Clamp((flatness - 0.7f) / 0.15f, 0, 1);
        float alpineRock = h >= treeLine ? Math.Clamp((h - treeLine) / 800f, 0, 1) : 0;
        layers = h < 3 && alat < 60 ? new Color(0, 0, 0, 0) : new Color(1, 0, 0, 0);
        rockSnow = new Vector2(Math.Max(steep, alpineRock), snow);
        return c.Lerp(Snow, snow);
    }
}
