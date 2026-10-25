using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Liquid;

/// <summary>Three float4 values, shared with the demo shader. Size.w selects sphere (0) or box (1).</summary>
[StructLayout(LayoutKind.Sequential)]
public readonly record struct LiquidObstacle(Vector4 Center, Vector4 Size, Vector4 Rotation)
{
    public float Distance(Vector3 position, out Vector3 normal)
    {
        Vector3 center = new(Center.X, Center.Y, Center.Z);
        if (Size.W == 0)
        {
            Vector3 offset = position - center;
            float length = offset.Length();
            normal = length > 0 ? offset / length : Vector3.UnitY;
            return length - Size.X;
        }
        Quaternion rotation = new(Rotation.X, Rotation.Y, Rotation.Z, Rotation.W);
        Vector3 local = Vector3.Transform(position - center, Quaternion.Conjugate(rotation));
        Vector3 delta = Vector3.Abs(local) - new Vector3(Size.X, Size.Y, Size.Z);
        Vector3 outside = Vector3.Max(delta, Vector3.Zero);
        float distance = outside.Length() + Math.Min(Math.Max(delta.X, Math.Max(delta.Y, delta.Z)), 0);
        Vector3 sign = new(MathF.CopySign(1, local.X), MathF.CopySign(1, local.Y), MathF.CopySign(1, local.Z));
        Vector3 localNormal = outside.LengthSquared() > 0 ? Vector3.Normalize(outside) * sign :
            delta.X > delta.Y && delta.X > delta.Z ? new(sign.X, 0, 0) :
            delta.Y > delta.Z ? new(0, sign.Y, 0) : new(0, 0, sign.Z);
        normal = Vector3.Transform(localNormal, rotation);
        return distance;
    }
}
