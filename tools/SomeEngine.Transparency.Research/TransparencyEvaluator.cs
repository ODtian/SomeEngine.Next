using System.Numerics;

namespace SomeEngine.Transparency.Research;

public readonly record struct TransparencyResult(Vector3 Color, int Surfaces, int NodeVisits, float ColorErrorBound);

public static class TransparencyEvaluator
{
    // Linear research colors lie in [0,1]. The midpoint has a .5 channel error bound.
    public static TransparencyResult Evaluate(TriangleHierarchy geometry, Vector2 point, float opaqueDepth,
        Vector3 background, float colorError)
    {
        Vector3 color = Vector3.Zero;
        float transmittance = 1, previousDepth = -1;
        uint previousTriangleId = 0, completedCoverage = 0, previousCoverage = uint.MaxValue;
        int surfaces = 0, visits = 0, firstNode = 0;
        Vector3 upperColor = Vector3.Max(Vector3.One, background);
        float errorScale = .5f * MathF.Max(upperColor.X, MathF.Max(upperColor.Y, upperColor.Z));
        while (errorScale * transmittance > colorError)
        {
            int triangleIndex = geometry.FindNext(point, previousDepth, previousTriangleId, opaqueDepth, ref firstNode,
                completedCoverage, previousCoverage, out float depth, out int nodeVisits, out uint coverageIndex);
            visits += nodeVisits;
            if (triangleIndex < 0) return new(color + transmittance * background, surfaces, visits, 0);
            Vector4 surface = geometry.Triangles[triangleIndex].Color;
            color += transmittance * surface.W * new Vector3(surface.X, surface.Y, surface.Z);
            transmittance *= 1 - surface.W;
            previousDepth = depth;
            previousTriangleId = geometry.Triangles[triangleIndex].TriangleId;
            previousCoverage = coverageIndex;
            if (coverageIndex < 32) completedCoverage |= 1u << (int)coverageIndex;
            surfaces++;
        }
        return new(color + upperColor * (.5f * transmittance), surfaces, visits, errorScale * transmittance);
    }

    public static Vector3 CompositeReference(TriangleHierarchy geometry, Vector2 point, float opaqueDepth, Vector3 background)
    {
        var surfaces = new List<(float Depth, uint TriangleId, Vector4 Color)>();
        foreach (ProjectedTriangle triangle in geometry.Triangles)
            if (triangle.Covers(point, out float depth) && depth < opaqueDepth)
                surfaces.Add((depth, triangle.TriangleId, triangle.Color));
        surfaces.Sort(static (a, b) => a.Depth == b.Depth ? a.TriangleId.CompareTo(b.TriangleId) : a.Depth.CompareTo(b.Depth));
        Vector3 color = Vector3.Zero;
        float transmittance = 1;
        foreach (var surface in surfaces)
        {
            color += transmittance * surface.Color.W * new Vector3(surface.Color.X, surface.Color.Y, surface.Color.Z);
            transmittance *= 1 - surface.Color.W;
        }
        return color + transmittance * background;
    }

    public static Vector3 CompositeWeighted(TriangleHierarchy geometry, Vector2 point, float opaqueDepth, Vector3 background)
    {
        Vector3 color = Vector3.Zero;
        float weightSum = 0, transmittance = 1;
        foreach (ProjectedTriangle triangle in geometry.Triangles)
        {
            if (!triangle.Covers(point, out float depth) || depth >= opaqueDepth) continue;
            float weight = triangle.Color.W * MathF.Pow(1 - depth, 3);
            color += weight * new Vector3(triangle.Color.X, triangle.Color.Y, triangle.Color.Z);
            weightSum += weight;
            transmittance *= 1 - triangle.Color.W;
        }
        return weightSum > 0 ? color / weightSum * (1 - transmittance) + transmittance * background : background;
    }
}
