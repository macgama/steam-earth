using System;

namespace SteamEarth;

/// <summary>
/// Géométrie de la planète : cube-sphère et conversions latitude/longitude.
/// Repère : centre de la Terre à l'origine, Y = axe des pôles (nord),
/// X = (lat 0, lon 0), -Z = (lat 0, lon 90°E).
/// </summary>
public static class Geo
{
    public const double Radius = 6_371_000.0;

    // Faces du cube : +X, -X, +Y, -Y, +Z, -Z
    static readonly Vec3d[] Normal = { new(1, 0, 0), new(-1, 0, 0), new(0, 1, 0), new(0, -1, 0), new(0, 0, 1), new(0, 0, -1) };
    static readonly Vec3d[] AxisU = { new(0, 0, -1), new(0, 0, 1), new(1, 0, 0), new(1, 0, 0), new(1, 0, 0), new(-1, 0, 0) };
    static readonly Vec3d[] AxisV = { new(0, 1, 0), new(0, 1, 0), new(0, 0, -1), new(0, 0, 1), new(0, 1, 0), new(0, 1, 0) };

    /// <summary>Point (u, v) ∈ [-1, 1]² d'une face → direction unitaire sur la sphère.
    /// La tangente répartit les sommets plus uniformément qu'une simple normalisation.</summary>
    public static Vec3d CubeToSphere(int face, double u, double v)
    {
        double tu = Math.Tan(u * Math.PI / 4), tv = Math.Tan(v * Math.PI / 4);
        return (Normal[face] + AxisU[face] * tu + AxisV[face] * tv).Normalized();
    }

    public static void ToLatLon(Vec3d dir, out double latDeg, out double lonDeg)
    {
        latDeg = Math.Asin(Math.Clamp(dir.Y, -1, 1)) * 180 / Math.PI;
        lonDeg = Math.Atan2(-dir.Z, dir.X) * 180 / Math.PI;
    }

    public static Vec3d FromLatLon(double latDeg, double lonDeg)
    {
        double la = latDeg * Math.PI / 180, lo = lonDeg * Math.PI / 180;
        return new Vec3d(Math.Cos(la) * Math.Cos(lo), Math.Sin(la), -Math.Cos(la) * Math.Sin(lo));
    }

    /// <summary>Repère local (est, nord, haut) en un point de la sphère.</summary>
    public static void LocalFrame(Vec3d up, out Vec3d east, out Vec3d north)
    {
        var y = new Vec3d(0, 1, 0);
        east = y.Cross(up);
        if (east.Length() < 1e-9) east = new Vec3d(0, 0, -1); // aux pôles
        east = east.Normalized();
        north = up.Cross(east).Normalized();
    }
}
