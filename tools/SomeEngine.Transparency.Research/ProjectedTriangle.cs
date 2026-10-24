using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Transparency.Research;

// A research material's projected geometry. Depth increases away from the eye.
[StructLayout(LayoutKind.Sequential)]
public readonly record struct ProjectedTriangle(Vector4 A, Vector4 B, Vector4 C, Vector4 Color, uint TriangleId)
{
    public Vector3 Minimum => Vector3.Min(Vector3.Min(Position(A), Position(B)), Position(C));
    public Vector3 Maximum => Vector3.Max(Vector3.Max(Position(A), Position(B)), Position(C));
    public Vector3 Center => (Position(A) + Position(B) + Position(C)) / 3;

    public bool Covers(Vector2 point, out float depth)
    {
        Vector2 a = new(A.X, A.Y), b = new(B.X, B.Y), c = new(C.X, C.Y);
        float area = Cross(b - a, c - a);
        float edgeA = Cross(c - b, point - b);
        float edgeB = Cross(a - c, point - c);
        float edgeC = Cross(b - a, point - a);
        depth = A.Z + (edgeB * (B.Z - A.Z) + edgeC * (C.Z - A.Z)) / area;
        float orientation = MathF.CopySign(1, area);
        return Inside(edgeA * orientation, (c - b) * orientation) &&
            Inside(edgeB * orientation, (a - c) * orientation) && Inside(edgeC * orientation, (b - a) * orientation);
    }

    private static bool Inside(float edge, Vector2 direction) =>
        edge > 0 || (edge == 0 && (direction.Y < 0 || (direction.Y == 0 && direction.X < 0)));

    private static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
    private static Vector3 Position(Vector4 point) => new(point.X, point.Y, point.Z);
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct TriangleBounds(Vector3 Minimum, uint TriangleOrEnd, Vector3 Maximum, uint CoverageIndex)
{
    public static TriangleBounds Create(Vector3 minimum, Vector3 maximum, uint endIndex, uint triangleIndex)
    {
        float margin = MathF.Max(MathF.Abs(minimum.Z), MathF.Abs(maximum.Z)) * 1e-6f;
        minimum.Z -= margin;
        maximum.Z += margin;
        uint triangleOrEnd = triangleIndex == uint.MaxValue ? endIndex : triangleIndex | 0x80000000;
        return new(minimum, triangleOrEnd, maximum, uint.MaxValue);
    }

    public bool IsTriangle => (TriangleOrEnd & 0x80000000) != 0;
    public uint TriangleIndex => TriangleOrEnd & 0x7fffffff;
    public uint GetSubtreeEnd(uint nodeIndex) => IsTriangle ? nodeIndex + 1 : TriangleOrEnd;

    public bool Contains(Vector2 point) =>
        point.X >= Minimum.X && point.X <= Maximum.X && point.Y >= Minimum.Y && point.Y <= Maximum.Y;
}

[StructLayout(LayoutKind.Sequential)]
public readonly record struct CoverageRange(uint FirstNode, uint EndIndex, float MinimumDepthAfter, float MaximumDepth);

