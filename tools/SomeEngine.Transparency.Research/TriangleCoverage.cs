using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Transparency.Research;

[StructLayout(LayoutKind.Sequential)]
public readonly record struct TriangleCoverage(Vector4 EdgeA, Vector4 EdgeB, Vector4 EdgeC,
    uint TriangleId, uint ColorIndex)
{
    public static TriangleCoverage Create(ProjectedTriangle triangle, uint colorIndex)
    {
        Vector2 a = new(triangle.A.X, triangle.A.Y), b = new(triangle.B.X, triangle.B.Y), c = new(triangle.C.X, triangle.C.Y);
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        return new(Edge(b, c, triangle.A.Z), Edge(c, a, triangle.B.Z), Edge(a, b, triangle.C.Z), triangle.TriangleId, colorIndex);

        Vector4 Edge(Vector2 from, Vector2 to, float depth)
        {
            Vector2 direction = to - from;
            return new(-direction.Y / area, direction.X / area,
                (direction.Y * from.X - direction.X * from.Y) / area, depth);
        }
    }
}
