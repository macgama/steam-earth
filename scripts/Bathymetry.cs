using System;
using SteamEarth.Data;

namespace SteamEarth;

/// <summary>
/// Fonds marins : grille GEBCO (15″ ≈ 450 m, domaine public), découpée en COG de 30° publiés dans la release
/// GitHub « gebco » (workflow .github/workflows/gebco.yml) et lue à la demande comme le relief Copernicus.
/// Tant qu'une tuile n'est pas arrivée (ou hors ligne), on prend la bathymétrie de la carte mondiale de base (~5 km).
/// </summary>
public static class Bathymetry
{
    public const string Release = "https://github.com/macgama/steam-earth/releases/download/gebco";
    public const string Attribution = "GEBCO Compilation Group, GEBCO Grid (domaine public)";

    /// <summary>Plus faible profondeur gardée sous une mer (m) : la côte reste ainsi bien dessinée.</summary>
    public const float MinDepth = 1.0f;

    static string Hemi(int v, char pos, char neg, string fmt) => (v >= 0 ? pos : neg) + Math.Abs(v).ToString(fmt);

    public static readonly StreamedRaster Gebco = new("gebco", 30, (lat, lon) =>
        $"{Release}/gebco_{Hemi(lat, 'N', 'S', "00")}{Hemi(lon, 'E', 'W', "000")}.tif", 128);

    /// <summary>
    /// Altitude du fond (m, négative) sous une mer, pour un maillage aux sommets espacés de <paramref name="spacing"/> m.
    /// <paramref name="pending"/> = true si une donnée plus fine est en cours de téléchargement.
    /// </summary>
    public static float SeabedHeight(double lat, double lon, double spacing, out bool pending)
    {
        pending = false;
        float hb = HeightProvider.BaseHeight(lat, lon);
        float bed = hb;
        if (spacing < HeightProvider.BaseOnlySpacing)
        {
            // 0 : 450 m, 1 : 900 m, 2 : 1,8 km, 3 : 3,6 km
            int level = spacing < 600 ? 0 : spacing < 1200 ? 1 : spacing < 2400 ? 2 : 3;
            var r = Gebco.Sample(lat, lon, level, true, true, out float v);
            if (r == StreamedRaster.Result.Pending)
            {
                pending = true;
                for (int l = level + 1; l <= 4 && r != StreamedRaster.Result.Ok; l++)
                    r = Gebco.Sample(lat, lon, l, true, false, out v);
            }
            if (r == StreamedRaster.Result.Ok && !float.IsNaN(v)) bed = v;
        }
        return Math.Min(bed, -MinDepth);
    }
}
