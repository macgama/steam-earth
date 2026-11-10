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
    public int[] Indices;
    public Vec3d Center; // centre du chunk dans le repère planète (double), au niveau de la mer
    public double MeanHeight; // altitude moyenne du chunk (m), pour le calcul du niveau de détail
    public bool Incomplete;   // des données fines étaient encore en téléchargement : à reconstruire
    public int DataVersion;   // version des données au moment de la construction
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
        double step = size / res;

        for (int j = 0; j < s; j++)
        for (int i = 0; i < s; i++)
        {
            double u = u0 + (i - 1) * step, v = v0 + (j - 1) * step;
            var d = Geo.CubeToSphere(face, u, v);
            Geo.ToLatLon(d, out double lat, out double lon);
            float h = HeightProvider.Sample(lat, lon, spacing, out bool w, out bool pending);
            if (pending) incomplete = true;
            if (w) h = Math.Max(h, 0f); // océans : surface au niveau de la mer
            int k = j * s + i;
            dirs[k] = d; wat[k] = w; hgt[k] = h;
            pos[k] = d * (Geo.Radius + h);
        }

        var centerDir = Geo.CubeToSphere(face, u0 + size / 2, v0 + size / 2);
        var center = centerDir * Geo.Radius;
        int n = res + 1;
        int border = 4 * res;
        var verts = new Vector3[n * n + border];
        var norms = new Vector3[n * n + border];
        var cols = new Color[n * n + border];

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
            Geo.ToLatLon(dirs[k], out double lat, out _);
            cols[o] = TerrainColor(hgt[k], wat[k], (float)nn.Dot(dirs[k]), lat);
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
        double skirt = size * Geo.Radius * 0.02 + 20.0;
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
        }
        for (int q = 0; q < border; q++)
        {
            int p0 = ring[q], p1 = ring[(q + 1) % border], s0 = sb + q, s1 = sb + (q + 1) % border;
            // les deux sens, pour être visibles quel que soit l'angle
            idx[t++] = p0; idx[t++] = p1; idx[t++] = s0; idx[t++] = p1; idx[t++] = s1; idx[t++] = s0;
            idx[t++] = p0; idx[t++] = s0; idx[t++] = p1; idx[t++] = p1; idx[t++] = s0; idx[t++] = s1;
        }

        double sum = 0;
        for (int j = 1; j <= n; j++) for (int i = 1; i <= n; i++) sum += hgt[j * s + i];
        return new ChunkData { Vertices = verts, Normals = norms, Colors = cols, Indices = idx, Center = center, MeanHeight = sum / (n * n), Incomplete = incomplete, DataVersion = version };
    }

    static readonly Color Ocean = new(0.06f, 0.17f, 0.32f);
    static readonly Color Lake = new(0.10f, 0.25f, 0.40f);
    static readonly Color Lowland = new(0.24f, 0.36f, 0.15f);
    static readonly Color Forest = new(0.15f, 0.25f, 0.11f);
    static readonly Color Alpine = new(0.40f, 0.42f, 0.26f);
    static readonly Color Rock = new(0.42f, 0.39f, 0.35f);
    static readonly Color Snow = new(0.92f, 0.94f, 0.97f);
    static readonly Color Sand = new(0.70f, 0.62f, 0.45f);

    /// <summary>Couleur provisoire selon altitude, pente et latitude (sera remplacée par des textures).</summary>
    static Color TerrainColor(float h, bool water, float flatness, double lat)
    {
        if (water) return h <= 0.5f ? Ocean : Lake;
        double alat = Math.Abs(lat);
        float snowLine = (float)Math.Max(300, 5200 - 55 * Math.Max(0, alat - 15));
        float treeLine = snowLine - 1000;
        Color c;
        if (h < 3 && alat < 60) c = Sand;
        else if (h < treeLine * 0.5f) c = Lowland.Lerp(Forest, Math.Clamp(h / (treeLine * 0.5f), 0, 1));
        else if (h < treeLine) c = Forest.Lerp(Alpine, Math.Clamp((h - treeLine * 0.5f) / (treeLine * 0.5f), 0, 1));
        else c = Alpine.Lerp(Rock, Math.Clamp((h - treeLine) / 800f, 0, 1));
        // Pentes raides : roche
        float steep = Math.Clamp((0.88f - flatness) / 0.15f, 0, 1);
        c = c.Lerp(Rock, steep);
        // Neige au-dessus de la limite, sauf sur les falaises
        float snow = Math.Clamp((h - snowLine) / 300f, 0, 1) * Math.Clamp((flatness - 0.7f) / 0.15f, 0, 1);
        return c.Lerp(Snow, snow);
    }
}
