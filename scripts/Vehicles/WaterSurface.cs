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
    /// <summary>Hauteur des vagues (m, + = au-dessus du niveau moyen) en un point du repère planète, au temps <c>t</c> (s).</summary>
    public static Func<Vec3d, double, double> Waves = (p, t) => 0;

    public static double Time => Godot.Time.GetTicksMsec() / 1000.0;

    /// <summary>Y a-t-il de l'eau en <paramref name="p"/> ? <paramref name="level"/> = altitude de la surface (vagues comprises).</summary>
    public static bool At(Vec3d p, out double level)
    {
        level = Vehicle.Ground(p, out bool water);
        if (water) level += Waves(p, Time);
        return water;
    }
}
