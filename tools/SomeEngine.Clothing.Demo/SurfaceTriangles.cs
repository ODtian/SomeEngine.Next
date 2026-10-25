using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public readonly record struct SurfacePoint(int Triangle, Vector3 Position, Vector3 Coordinates, float DistanceSquared);
public readonly record struct SurfaceCrossing(int Triangle, Vector3 MeshCoordinates, Vector3 QueryCoordinates);

public sealed class SurfaceTriangles
{
    private readonly Vector3[] positions;
    private readonly int[] triangles;
    private readonly int[] triangleOrder;
    private readonly Node[] nodes;

    private struct Node
    {
        public Vector3 Minimum, Maximum;
        public int Begin, Count, Left, Right;
    }

    public SurfaceTriangles(Vector3[] positions, int[] triangles)
    {
        this.positions = positions;
        this.triangles = triangles;
        triangleOrder = Enumerable.Range(0, triangles.Length / 3).ToArray();
        List<Node> hierarchy = [];
        Vector3 Center(int triangle) => (positions[triangles[triangle * 3]]
            + positions[triangles[triangle * 3 + 1]] + positions[triangles[triangle * 3 + 2]]) / 3;
        int Divide(int begin, int count)
        {
            int index = hierarchy.Count;
            hierarchy.Add(new() { Begin = begin, Count = count });
            if (count <= 8) return index;
            Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            for (int i = begin; i < begin + count; i++)
            {
                Vector3 center = Center(triangleOrder[i]);
                minimum = Vector3.Min(minimum, center); maximum = Vector3.Max(maximum, center);
            }
            Vector3 extent = maximum - minimum;
            int axis = extent.X > extent.Y ? (extent.X > extent.Z ? 0 : 2) : (extent.Y > extent.Z ? 1 : 2);
            Array.Sort(triangleOrder, begin, count, Comparer<int>.Create((a, b) => Center(a)[axis].CompareTo(Center(b)[axis])));
            int half = count / 2;
            int left = Divide(begin, half), right = Divide(begin + half, count - half);
            hierarchy[index] = new() { Begin = begin, Count = 0, Left = left, Right = right };
            return index;
        }
        Divide(0, triangleOrder.Length);
        nodes = hierarchy.ToArray();
        Update();
    }

    public void Update()
    {
        for (int i = nodes.Length - 1; i >= 0; i--)
        {
            ref Node node = ref nodes[i];
            if (node.Count == 0)
            {
                node.Minimum = Vector3.Min(nodes[node.Left].Minimum, nodes[node.Right].Minimum);
                node.Maximum = Vector3.Max(nodes[node.Left].Maximum, nodes[node.Right].Maximum);
                continue;
            }
            Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            for (int triangle = node.Begin; triangle < node.Begin + node.Count; triangle++)
            for (int corner = 0; corner < 3; corner++)
            {
                int vertex = triangles[triangleOrder[triangle] * 3 + corner];
                minimum = Vector3.Min(minimum, positions[vertex]);
                maximum = Vector3.Max(maximum, positions[vertex]);
            }
            node.Minimum = minimum; node.Maximum = maximum;
        }
    }

    public SurfacePoint Nearest(Vector3 point, float maximumDistance = float.PositiveInfinity)
        => Nearest(point, new SurfacePoint(-1, Vector3.Zero, Vector3.Zero, maximumDistance * maximumDistance));

    public SurfacePoint Nearest(Vector3 point, int triangle, float maximumDistance)
    {
        SurfacePoint closest = new(-1, default, default, maximumDistance * maximumDistance);
        if (triangle >= 0)
        {
            SurfacePoint previous = OnTriangle(point, triangle);
            if (previous.DistanceSquared < closest.DistanceSquared) closest = previous;
        }
        return Nearest(point, closest);
    }

    public SurfacePoint Nearest(Vector3 point, SurfacePoint closest)
    {
        Span<int> stack = stackalloc int[64];
        int count = 1;
        stack[0] = 0;
        while (count > 0)
        {
            ref readonly Node node = ref nodes[stack[--count]];
            if (DistanceToBox(point, node) > closest.DistanceSquared) continue;
            if (node.Count == 0)
            {
                float leftDistance = DistanceToBox(point, nodes[node.Left]);
                float rightDistance = DistanceToBox(point, nodes[node.Right]);
                if (leftDistance < rightDistance) { stack[count++] = node.Right; stack[count++] = node.Left; }
                else { stack[count++] = node.Left; stack[count++] = node.Right; }
                continue;
            }
            for (int i = node.Begin; i < node.Begin + node.Count; i++)
            {
                int triangle = triangleOrder[i];
                SurfacePoint candidate = OnTriangle(point, triangle);
                if (candidate.DistanceSquared < closest.DistanceSquared) closest = candidate;
            }
        }
        return closest;
    }

    public SurfacePoint IntersectSegment(Vector3 start, Vector3 end, bool frontFacesOnly = false)
        => IntersectSegment(start, end, frontFacesOnly, start);

    public SurfacePoint Project(Vector3 position, Vector3 direction, float distance)
        => IntersectSegment(position + direction * distance, position - direction * distance, true, position);

    private SurfacePoint IntersectSegment(Vector3 start, Vector3 end, bool frontFacesOnly, Vector3 nearestTo)
    {
        Vector3 minimum = Vector3.Min(start, end), maximum = Vector3.Max(start, end);
        Span<int> stack = stackalloc int[64];
        int count = 1;
        stack[0] = 0;
        SurfacePoint closest = new(-1, default, default, float.PositiveInfinity);
        while (count > 0)
        {
            ref readonly Node node = ref nodes[stack[--count]];
            if (node.Minimum.X > maximum.X || node.Maximum.X < minimum.X
                || node.Minimum.Y > maximum.Y || node.Maximum.Y < minimum.Y
                || node.Minimum.Z > maximum.Z || node.Maximum.Z < minimum.Z) continue;
            if (node.Count == 0) { stack[count++] = node.Left; stack[count++] = node.Right; continue; }
            for (int index = node.Begin; index < node.Begin + node.Count; index++)
            {
                int triangle = triangleOrder[index];
                Vector3 a = positions[triangles[triangle * 3]], b = positions[triangles[triangle * 3 + 1]], c = positions[triangles[triangle * 3 + 2]];
                if (frontFacesOnly && Vector3.Dot(Vector3.Cross(b - a, c - a), end - start) >= 0) continue;
                if (!Crosses(start, end, a, b, c, out float along, out Vector3 coordinates)) continue;
                Vector3 point = Vector3.Lerp(start, end, along);
                float distanceSquared = Vector3.DistanceSquared(nearestTo, point);
                if (distanceSquared < closest.DistanceSquared) closest = new(triangle, point, coordinates, distanceSquared);
            }
        }
        return closest;
    }

    private SurfacePoint OnTriangle(Vector3 point, int triangle)
    {
        int a = triangles[triangle * 3], b = triangles[triangle * 3 + 1], c = triangles[triangle * 3 + 2];
        Vector3 pa = positions[a], pb = positions[b], pc = positions[c];
        Vector3 coordinates = ClosestCoordinates(point, pa, pb, pc);
        Vector3 position = pa * coordinates.X + pb * coordinates.Y + pc * coordinates.Z;
        return new(triangle, position, coordinates, Vector3.DistanceSquared(point, position));
    }

    private static float DistanceToBox(Vector3 point, in Node node)
        => Vector3.DistanceSquared(point, Vector3.Clamp(point, node.Minimum, node.Maximum));

    public void FindCrossings(Vector3 a, Vector3 b, Vector3 c, List<SurfaceCrossing> crossings)
    {
        Vector3 minimum = Vector3.Min(a, Vector3.Min(b, c)), maximum = Vector3.Max(a, Vector3.Max(b, c));
        ReadOnlySpan<Vector3> query = stackalloc Vector3[3] { a, b, c };
        Span<Vector3> mesh = stackalloc Vector3[3];
        Span<int> stack = stackalloc int[64];
        int count = 1;
        stack[0] = 0;
        while (count > 0)
        {
            ref readonly Node node = ref nodes[stack[--count]];
            if (node.Minimum.X > maximum.X || node.Maximum.X < minimum.X
                || node.Minimum.Y > maximum.Y || node.Maximum.Y < minimum.Y
                || node.Minimum.Z > maximum.Z || node.Maximum.Z < minimum.Z) continue;
            if (node.Count == 0)
            {
                stack[count++] = node.Left; stack[count++] = node.Right;
                continue;
            }
            for (int index = node.Begin; index < node.Begin + node.Count; index++)
            {
                int triangle = triangleOrder[index];
                Vector3 x = positions[triangles[triangle * 3]], y = positions[triangles[triangle * 3 + 1]], z = positions[triangles[triangle * 3 + 2]];
                mesh[0] = x; mesh[1] = y; mesh[2] = z;
                for (int edge = 0; edge < 3; edge++)
                {
                    int next = (edge + 1) % 3;
                    if (Crosses(query[edge], query[next], x, y, z, out float along, out Vector3 coordinates))
                    {
                        Vector3 queryCoordinates = Vector3.Zero;
                        queryCoordinates[edge] = 1 - along; queryCoordinates[next] = along;
                        crossings.Add(new(triangle, coordinates, queryCoordinates));
                    }
                    if (Crosses(mesh[edge], mesh[next], a, b, c, out along, out coordinates))
                    {
                        Vector3 meshCoordinates = Vector3.Zero;
                        meshCoordinates[edge] = 1 - along; meshCoordinates[next] = along;
                        crossings.Add(new(triangle, meshCoordinates, coordinates));
                    }
                }
            }
        }
    }

    private static bool Crosses(Vector3 start, Vector3 end, Vector3 a, Vector3 b, Vector3 c, out float along, out Vector3 coordinates)
    {
        along = 0; coordinates = default;
        Vector3 direction = end - start, ab = b - a, ac = c - a;
        Vector3 p = Vector3.Cross(direction, ac);
        float determinant = Vector3.Dot(ab, p);
        if (MathF.Abs(determinant) < 1e-10f) return false;
        Vector3 offset = start - a, q = Vector3.Cross(offset, ab);
        float u = Vector3.Dot(offset, p) / determinant, v = Vector3.Dot(direction, q) / determinant;
        along = Vector3.Dot(ac, q) / determinant;
        coordinates = new(1 - u - v, u, v);
        return u >= 0 && v >= 0 && u + v <= 1 && along >= 0 && along <= 1;
    }

    public static Vector3 ClosestCoordinates(Vector3 point, Vector3 a, Vector3 b, Vector3 c)
    {
        Vector3 ab = b - a, ac = c - a, ap = point - a;
        float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
        if (d1 <= 0 && d2 <= 0) return Vector3.UnitX;
        Vector3 bp = point - b;
        float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
        if (d3 >= 0 && d4 <= d3) return Vector3.UnitY;
        float vc = d1 * d4 - d3 * d2;
        if (vc <= 0 && d1 >= 0 && d3 <= 0)
        {
            float along = d1 / (d1 - d3);
            return new(1 - along, along, 0);
        }
        Vector3 cp = point - c;
        float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
        if (d6 >= 0 && d5 <= d6) return Vector3.UnitZ;
        float vb = d5 * d2 - d1 * d6;
        if (vb <= 0 && d2 >= 0 && d6 <= 0)
        {
            float along = d2 / (d2 - d6);
            return new(1 - along, 0, along);
        }
        float va = d3 * d6 - d5 * d4;
        if (va <= 0 && d4 >= d3 && d5 >= d6)
        {
            float along = (d4 - d3) / (d4 - d3 + d5 - d6);
            return new(0, 1 - along, along);
        }
        float inverse = 1 / (va + vb + vc);
        float v = vb * inverse, w = vc * inverse;
        return new(1 - v - w, v, w);
    }
}
