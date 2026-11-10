using System;
using Godot;

namespace SteamEarth;

/// <summary>Vecteur 3D en double précision : positions planétaires (rayon 6 371 km).</summary>
public readonly struct Vec3d
{
    public readonly double X, Y, Z;
    public Vec3d(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static Vec3d operator +(Vec3d a, Vec3d b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3d operator -(Vec3d a, Vec3d b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3d operator -(Vec3d a) => new(-a.X, -a.Y, -a.Z);
    public static Vec3d operator *(Vec3d a, double s) => new(a.X * s, a.Y * s, a.Z * s);
    public static Vec3d operator *(double s, Vec3d a) => new(a.X * s, a.Y * s, a.Z * s);

    public double Dot(Vec3d b) => X * b.X + Y * b.Y + Z * b.Z;
    public Vec3d Cross(Vec3d b) => new(Y * b.Z - Z * b.Y, Z * b.X - X * b.Z, X * b.Y - Y * b.X);
    public double Length() => Math.Sqrt(X * X + Y * Y + Z * Z);
    public Vec3d Normalized() { double l = Length(); return l > 0 ? this * (1.0 / l) : this; }
    public Vector3 ToVector3() => new((float)X, (float)Y, (float)Z);
    public static Vec3d From(Vector3 v) => new(v.X, v.Y, v.Z);
    public override string ToString() => $"({X:F1}, {Y:F1}, {Z:F1})";
}
