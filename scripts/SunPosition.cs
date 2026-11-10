using System;

namespace SteamEarth;

/// <summary>Position du Soleil (formule simplifiée de l'Almanach astronomique, précision ~0,01°).</summary>
public static class SunPosition
{
    /// <summary>Point de la Terre où le Soleil est au zénith à l'instant donné (UTC).</summary>
    public static void Subsolar(DateTime utc, out double latDeg, out double lonDeg)
    {
        double d = (utc - new DateTime(2000, 1, 1, 12, 0, 0, DateTimeKind.Utc)).TotalDays;
        double g = Rad(357.529 + 0.98560028 * d);
        double q = 280.459 + 0.98564736 * d;
        double L = Rad(q + 1.915 * Math.Sin(g) + 0.020 * Math.Sin(2 * g));
        double e = Rad(23.439 - 0.00000036 * d);
        double ra = Math.Atan2(Math.Cos(e) * Math.Sin(L), Math.Cos(L)) * 180 / Math.PI;
        double dec = Math.Asin(Math.Sin(e) * Math.Sin(L)) * 180 / Math.PI;
        double eqTimeH = Wrap180(q - ra) / 15.0;
        double utcH = utc.TimeOfDay.TotalHours;
        latDeg = dec;
        lonDeg = Wrap180(-15.0 * (utcH - 12.0 + eqTimeH));
    }

    public static Vec3d Direction(DateTime utc)
    {
        Subsolar(utc, out double lat, out double lon);
        return Geo.FromLatLon(lat, lon);
    }

    static double Rad(double deg) => deg * Math.PI / 180;
    static double Wrap180(double a) => ((a + 180) % 360 + 360) % 360 - 180;
}
