using System.Numerics;

namespace SomeEngine.Physics;

internal static class GeometrySegments
{
    internal static void Closest(Vector3 firstStart, Vector3 firstEnd, Vector3 secondStart, Vector3 secondEnd,
        out Vector3 first, out Vector3 second)
    {
        Vector3 firstEdge = firstEnd - firstStart, secondEdge = secondEnd - secondStart;
        Vector3 offset = firstStart - secondStart;
        float firstSquared = firstEdge.LengthSquared(), secondSquared = secondEdge.LengthSquared();
        float edgeDot = Vector3.Dot(firstEdge, secondEdge), firstProjection = Vector3.Dot(firstEdge, offset);
        float secondProjection = Vector3.Dot(secondEdge, offset);
        float denominator = firstSquared * secondSquared - edgeDot * edgeDot;
        float firstParameter = denominator > 0 ? Math.Clamp((edgeDot * secondProjection - firstProjection * secondSquared) / denominator, 0, 1) : 0;
        float secondParameter = (edgeDot * firstParameter + secondProjection) / secondSquared;
        if (secondParameter < 0)
        {
            secondParameter = 0;
            firstParameter = Math.Clamp(-firstProjection / firstSquared, 0, 1);
        }
        else if (secondParameter > 1)
        {
            secondParameter = 1;
            firstParameter = Math.Clamp((edgeDot - firstProjection) / firstSquared, 0, 1);
        }
        first = firstStart + firstEdge * firstParameter;
        second = secondStart + secondEdge * secondParameter;
    }
}
