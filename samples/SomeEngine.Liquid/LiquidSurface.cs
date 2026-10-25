using System.Numerics;

namespace SomeEngine.Liquid;

/// <summary>Curved contact geometry, copied through extraction to the liquid shader.</summary>
public readonly record struct LiquidSurface(Vector4 Profile, Vector4 Ridges, Vector4 Ripple)
{
    public float Distance(Vector3 position, out Vector3 normal) => Distance(position, out normal, out _);

    public float Distance(Vector3 position, out Vector3 normal, out LiquidContactSurface surface)
    {
        float height = Height(position.X, position.Z, out normal);
        float separation = (position.Y - height) * normal.Y + Ripple.W * .5f;
        if (separation < 0) normal = -normal;
        float distance = Math.Abs(separation) - Ripple.W * .5f;
        surface = separation >= 0 ? LiquidContactSurface.CurvedTop : LiquidContactSurface.CurvedBottom;
        float edge = Math.Abs(position.X) - Ripple.Y;
        if (edge > distance)
        {
            distance = edge; normal = new(MathF.Sign(position.X), 0, 0);
            surface = position.X > 0 ? LiquidContactSurface.CurvedPositiveX : LiquidContactSurface.CurvedNegativeX;
        }
        edge = Math.Abs(position.Z) - Ripple.Z;
        if (edge > distance)
        {
            distance = edge; normal = new(0, 0, MathF.Sign(position.Z));
            surface = position.Z > 0 ? LiquidContactSurface.CurvedPositiveZ : LiquidContactSurface.CurvedNegativeZ;
        }
        return distance;
    }

    public float Height(float x, float z, out Vector3 normal)
    {
        float ridge = MathF.Exp(-z * z * Ridges.Y);
        float height = Profile.X + Profile.Y * x + Profile.Z * MathF.Sin(Ridges.X * x) * ridge +
            Profile.W * z * z + Ridges.Z * MathF.Sin(Ridges.W * z + Ripple.X * x);
        float dx = Profile.Y + Profile.Z * Ridges.X * MathF.Cos(Ridges.X * x) * ridge +
            Ridges.Z * Ripple.X * MathF.Cos(Ridges.W * z + Ripple.X * x);
        float dz = -2 * Ridges.Y * z * Profile.Z * MathF.Sin(Ridges.X * x) * ridge + 2 * Profile.W * z +
            Ridges.Z * Ridges.W * MathF.Cos(Ridges.W * z + Ripple.X * x);
        normal = Vector3.Normalize(new(-dx, 1, -dz));
        return height;
    }

    public Vector2 CalculateLaplacianGradient(float x, float z)
    {
        double frequency = Ridges.X, decay = Ridges.Y, waveX = Ripple.X, waveZ = Ridges.W;
        double ridge = Math.Exp(-decay * z * z), phase = waveZ * z + waveX * x;
        double curvature = -frequency * frequency + 4 * decay * decay * z * z - 2 * decay;
        double dx = Profile.Z * frequency * Math.Cos(frequency * x) * ridge * curvature -
            Ridges.Z * waveX * (waveX * waveX + waveZ * waveZ) * Math.Cos(phase);
        double dz = Profile.Z * Math.Sin(frequency * x) * ridge * (8 * decay * decay * z - 2 * decay * z * curvature) -
            Ridges.Z * waveZ * (waveX * waveX + waveZ * waveZ) * Math.Cos(phase);
        return new((float)dx, (float)dz);
    }
}
