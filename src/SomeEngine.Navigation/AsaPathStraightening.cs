using System.Numerics;

namespace SomeEngine.Navigation;

internal static class AsaPathStraightening
{
    internal static Vector3[] Crossings(NavigationMesh mesh, int[] portals, int[] corridor, Vector3 start, Vector3 goal)
    {
        Vector3[] pulled = NavigationFunnel.Pull(mesh, portals, corridor, start, goal);
        var crossings = new Vector3[portals.Length + 2];
        crossings[0] = start;
        crossings[^1] = goal;
        int segment = 0;
        float minimum = 0;
        for (int index = 0; index < portals.Length; index++)
        {
            NavigationPortal portal = mesh.Portals.Span[portals[index]];
            while (segment < pulled.Length - 1)
            {
                if (Intersect(pulled[segment], pulled[segment + 1], portal.Left, portal.Right, minimum, out float along))
                {
                    Vector3 point = Vector3.Lerp(pulled[segment], pulled[segment + 1], along);
                    if (mesh.TrySampleHeight(corridor[index], point, out float height)) point.Y = height;
                    crossings[index + 1] = point;
                    minimum = along;
                    break;
                }
                segment++;
                minimum = 0;
            }
        }
        return crossings;
    }

    private static bool Intersect(Vector3 first, Vector3 second, Vector3 left, Vector3 right, float minimum, out float along)
    {
        Vector2 start = new(first.X, first.Z), delta = new(second.X - first.X, second.Z - first.Z);
        Vector2 a = new(left.X, left.Z), edge = new(right.X - left.X, right.Z - left.Z);
        float denominator = Cross(delta, edge);
        if (MathF.Abs(denominator) <= 0.000001f)
        {
            if (MathF.Abs(Cross(a - start, delta)) > 0.00001f) { along = 0; return false; }
            float from = Vector2.Dot(a - start, delta) / delta.LengthSquared();
            float to = Vector2.Dot(a + edge - start, delta) / delta.LengthSquared();
            along = MathF.Max(minimum, MathF.Max(0, MathF.Min(from, to)));
            return along <= MathF.Min(1, MathF.Max(from, to)) + 0.00001f;
        }
        along = Cross(a - start, edge) / denominator;
        float across = Cross(a - start, delta) / denominator;
        bool inside = along >= minimum - 0.00001f && along <= 1.00001f && across >= -0.00001f && across <= 1.00001f;
        along = Math.Clamp(along, minimum, 1);
        return inside;
    }

    private static float Cross(Vector2 first, Vector2 second) => first.X * second.Y - first.Y * second.X;
}
