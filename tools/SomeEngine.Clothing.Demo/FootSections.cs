using System.Numerics;

namespace SomeEngine.Clothing.Demo;

internal sealed class FootSections
{
    public const float ToeLength = .09f;
    public readonly record struct ToeCap(Vector3 Tip, Vector3 Center, SurfaceTriangles Surface, int[] Triangles, Vector3[] Positions)
    {
        public Vector3 Normal(SurfacePoint point)
        {
            return Vector3.Normalize(point.Position - Center);
        }

    }
    private readonly Vector3[] positions;
    private readonly int[] triangles;

    public FootSections(CharacterSurface body, float side)
    {
        positions = body.Positions;
        triangles = Enumerable.Range(0, body.Triangles.Length / 3)
            .Where(triangle => Enumerable.Range(0, 3).All(corner =>
            {
                Vector3 position = positions[body.Triangles[triangle * 3 + corner]];
                return position.X * side > 0 && position.Y < .25f;
            }))
            .SelectMany(triangle => body.Triangles.Skip(triangle * 3).Take(3)).ToArray();
    }

    public ToeCap CreateToeCap(float front)
    {
        float rootDepth = front - ToeLength;
        List<Vector3> toePoints = [];
        for (int triangle = 0; triangle < triangles.Length; triangle += 3)
        for (int corner = 0; corner < 3; corner++)
        {
            Vector3 a = positions[triangles[triangle + corner]], b = positions[triangles[triangle + (corner + 1) % 3]];
            if (a.Z >= rootDepth) toePoints.Add(a);
            if ((a.Z < rootDepth) != (b.Z < rootDepth))
            {
                Vector3 crossing = Vector3.Lerp(a, b, (rootDepth - a.Z) / (b.Z - a.Z));
                crossing.Z = rootDepth;
                toePoints.Add(crossing);
            }
        }
        Vector3[] toes = toePoints.Distinct().ToArray();
        int[] toeTriangles = CreateToeTriangles(toes);
        toeTriangles = Enumerable.Range(0, toeTriangles.Length / 3)
            .Where(triangle => Enumerable.Range(0, 3).Any(corner => toes[toeTriangles[triangle * 3 + corner]].Z > rootDepth))
            .SelectMany(triangle => toeTriangles.Skip(triangle * 3).Take(3)).ToArray();
        return new(toes.MaxBy(point => point.Z), toes.Aggregate(Vector3.Zero, (sum, point) => sum + point) / toes.Length,
            new(toes, toeTriangles), toeTriangles, toes);
    }

    private static int[] CreateToeTriangles(Vector3[] positions)
    {
        int[] vertices = Enumerable.Range(0, positions.Length).ToArray();
        int first = vertices.MinBy(vertex => positions[vertex].X);
        int second = vertices.MaxBy(vertex => Vector3.DistanceSquared(positions[vertex], positions[first]));
        Vector3 axis = positions[second] - positions[first];
        int third = vertices.MaxBy(vertex => Vector3.Cross(axis, positions[vertex] - positions[first]).LengthSquared());
        Vector3 normal = Vector3.Cross(axis, positions[third] - positions[first]);
        int fourth = vertices.MaxBy(vertex => MathF.Abs(Vector3.Dot(normal, positions[vertex] - positions[first])));
        Vector3 inside = (positions[first] + positions[second] + positions[third] + positions[fourth]) * .25f;
        (int A, int B, int C) Face(int a, int b, int c)
            => Vector3.Dot(Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]), inside - positions[a]) < 0
                ? (a, b, c) : (a, c, b);
        List<(int A, int B, int C)> faces = [Face(first, second, third), Face(first, fourth, second),
            Face(second, fourth, third), Face(third, fourth, first)];
        foreach (int vertex in vertices)
        {
            HashSet<(int A, int B)> border = [];
            for (int index = faces.Count - 1; index >= 0; index--)
            {
                (int a, int b, int c) = faces[index];
                Vector3 outward = Vector3.Cross(positions[b] - positions[a], positions[c] - positions[a]);
                if (Vector3.Dot(outward, positions[vertex] - positions[a]) <= outward.Length() * .0000001f) continue;
                foreach ((int from, int to) in new[] { (a, b), (b, c), (c, a) })
                    if (!border.Remove((to, from))) border.Add((from, to));
                faces.RemoveAt(index);
            }
            foreach ((int a, int b) in border) faces.Add(Face(a, b, vertex));
        }
        return faces.SelectMany(face => new[] { face.A, face.B, face.C }).ToArray();
    }

    public Vector3[] CreateRing(Vector3 center, Vector3 front, float side, int columns)
        => CreateRing(positions, triangles, center, front, side, columns);

    public static Vector3[] CreateRing(Vector3[] positions, int[] triangles, Vector3 center, Vector3 front, float side, int columns)
    {
        Vector3 along = Vector3.Cross(Vector3.UnitX, front);
        List<Vector2> points = [];
        for (int triangle = 0; triangle < triangles.Length; triangle += 3)
        for (int corner = 0; corner < 3; corner++)
        {
            Vector3 a = positions[triangles[triangle + corner]], b = positions[triangles[triangle + (corner + 1) % 3]];
            float da = Vector3.Dot(a - center, along), db = Vector3.Dot(b - center, along);
            if ((da < 0) == (db < 0)) continue;
            Vector3 point = Vector3.Lerp(a, b, da / (da - db)) - center;
            points.Add(new(point.X, Vector3.Dot(point, front)));
        }
        Vector2[] sorted = points.Distinct().OrderBy(point => point.X).ThenBy(point => point.Y).ToArray();
        List<Vector2> hull = [];
        static float Cross(Vector2 a, Vector2 b) => a.X * b.Y - a.Y * b.X;
        foreach (Vector2 point in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^1] - hull[^2], point - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        int lower = hull.Count;
        for (int i = sorted.Length - 2; i >= 0; i--)
        {
            Vector2 point = sorted[i];
            while (hull.Count > lower && Cross(hull[^1] - hull[^2], point - hull[^1]) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(point);
        }
        hull.RemoveAt(hull.Count - 1);
        Vector2 firstPoint = hull[0];
        Vector2 origin = Vector2.Zero;
        float area = 0;
        for (int edge = 0; edge < hull.Count; edge++)
        {
            Vector2 a = hull[edge] - firstPoint, b = hull[(edge + 1) % hull.Count] - firstPoint;
            float weight = Cross(a, b);
            origin += (a + b) * weight; area += weight;
        }
        origin = firstPoint + origin / (3 * area);
        Vector3[] ring = new Vector3[columns];
        for (int column = 0; column < columns; column++)
        {
            float angle = column * MathF.Tau / columns;
            Vector2 direction = new(MathF.Sin(angle) * side, MathF.Cos(angle));
            float radius = float.PositiveInfinity;
            for (int edge = 0; edge < hull.Count; edge++)
            {
                Vector2 a = hull[edge] - origin, b = hull[(edge + 1) % hull.Count] - origin;
                Vector2 difference = b - a;
                float denominator = Cross(direction, difference);
                if (MathF.Abs(denominator) < 1e-8f) continue;
                float distance = Cross(a, difference) / denominator, across = Cross(a, direction) / denominator;
                if (distance >= 0 && across >= 0 && across <= 1) radius = MathF.Min(radius, distance);
            }
            Vector2 point = origin + direction * radius;
            ring[column] = center + Vector3.UnitX * point.X + front * point.Y;
        }
        return ring;
    }
}
