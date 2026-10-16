using System;

// Minimal 3D vector so the core has no RhinoCommon dependency.
public struct Vec3
{
    public double X, Y, Z;
    public Vec3(double x, double y, double z) { X = x; Y = y; Z = z; }

    public static readonly Vec3 Zero = new Vec3(0, 0, 0);

    public static Vec3 operator +(Vec3 a, Vec3 b) { return new Vec3(a.X + b.X, a.Y + b.Y, a.Z + b.Z); }
    public static Vec3 operator -(Vec3 a, Vec3 b) { return new Vec3(a.X - b.X, a.Y - b.Y, a.Z - b.Z); }
    public static Vec3 operator -(Vec3 a) { return new Vec3(-a.X, -a.Y, -a.Z); }
    public static Vec3 operator *(Vec3 a, double s) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
    public static Vec3 operator *(double s, Vec3 a) { return new Vec3(a.X * s, a.Y * s, a.Z * s); }
    public static Vec3 operator /(Vec3 a, double s) { return new Vec3(a.X / s, a.Y / s, a.Z / s); }

    public static double Dot(Vec3 a, Vec3 b) { return a.X * b.X + a.Y * b.Y + a.Z * b.Z; }
    public static Vec3 Cross(Vec3 a, Vec3 b)
    {
        return new Vec3(a.Y * b.Z - a.Z * b.Y, a.Z * b.X - a.X * b.Z, a.X * b.Y - a.Y * b.X);
    }

    public double Length { get { return Math.Sqrt(X * X + Y * Y + Z * Z); } }
    public double LengthSq { get { return X * X + Y * Y + Z * Z; } }

    public Vec3 Normalized()
    {
        double l = Length;
        return l > 1e-300 ? this / l : Zero;
    }

    public static Vec3 Lerp(Vec3 a, Vec3 b, double t) { return a + (b - a) * t; }

    public override string ToString() { return "(" + X.ToString("G5") + ", " + Y.ToString("G5") + ", " + Z.ToString("G5") + ")"; }
}
