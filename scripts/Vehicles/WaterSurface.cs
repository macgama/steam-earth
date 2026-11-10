using System;
using Godot;

namespace SteamEarth;

/// <summary>
/// Surface de l'eau pour la flottaison (bateau, amerrissage) : niveau des lacs, rivières et de la mer d'après
/// le relief (l'eau y est plate), plus le déplacement vertical des vagues.
/// <see cref="Waves"/> est un point d'accroche : le rendu de l'océan peut y brancher sa propre fonction de houle
/// pour que les bateaux montent et descendent exactement avec les vagues affichées.
/// </summary>
public static class WaterSurface
{
    /// <summary>Hauteur des vagues en mer (m, + = au-dessus du niveau de la mer) en un point du repère planète, au temps <c>t</c> (s).
    /// Branchée par <see cref="Ocean"/> sur <c>Ocean.HeightAt</c>.</summary>
    public static Func<Vec3d, double, double> Waves = (p, t) => 0;

    public static double Time => Godot.Time.GetTicksMsec() / 1000.0;

    /// <summary>Y a-t-il de l'eau en <paramref name="p"/> ? <paramref name="level"/> = altitude de la surface (vagues comprises).</summary>
    public static bool At(Vec3d p, out double level)
    {
        Geo.ToLatLon(p.Normalized(), out double lat, out double lon);
        float h = HeightProvider.Sample(lat, lon, 0.5, out bool water, out _, out _, out bool sea);
        // En mer : niveau 0 + vagues (le relief y est le fond marin) ; lacs et rivières : leur niveau mesuré, plat
        level = sea ? Waves(p, Time) : water ? Math.Max(h, 0) : h;
        return water;
    }
}
