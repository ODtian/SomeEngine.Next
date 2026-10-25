using System.Numerics;

namespace SomeEngine.Clothing.Demo;

internal sealed class BodyMidline
{
    private readonly (SurfacePoint First, SurfacePoint Last)[] sections;
    private readonly Vector3[] outline;
    private readonly float[] distances;

    public BodyMidline(CharacterSurface body)
    {
        List<(SurfacePoint, SurfacePoint)> intersections = [];
        SurfacePoint OnEdge(int triangle, int corner, float along)
        {
            Vector3 coordinates = Vector3.Zero;
            coordinates[corner] = 1 - along; coordinates[(corner + 1) % 3] = along;
            Vector3 a = body.Positions[body.Triangles[triangle * 3 + corner]];
            Vector3 b = body.Positions[body.Triangles[triangle * 3 + (corner + 1) % 3]];
            return new(triangle, Vector3.Lerp(a, b, along), coordinates, 0);
        }
        for (int triangle = 0; triangle < body.Triangles.Length / 3; triangle++)
        {
            List<SurfacePoint> crossings = [];
            for (int corner = 0; corner < 3; corner++)
            {
                Vector3 a = body.Positions[body.Triangles[triangle * 3 + corner]];
                Vector3 b = body.Positions[body.Triangles[triangle * 3 + (corner + 1) % 3]];
                if (a.X == 0 && b.X == 0) intersections.Add((OnEdge(triangle, corner, 0), OnEdge(triangle, corner, 1)));
                else if ((a.X < 0) != (b.X < 0)) crossings.Add(OnEdge(triangle, corner, a.X / (a.X - b.X)));
            }
            SurfacePoint[] points = crossings.DistinctBy(point => point.Position).ToArray();
            if (points.Length == 2) intersections.Add((points[0], points[1]));
        }
        sections = intersections.ToArray();
        Dictionary<(int, int), int> vertices = [];
        List<Vector3> positions = [];
        List<List<int>> neighbours = [];
        int Vertex(Vector3 position)
        {
            var key = ((int)MathF.Round(position.Y * 1000000), (int)MathF.Round(position.Z * 1000000));
            if (vertices.TryGetValue(key, out int vertex)) return vertex;
            vertex = positions.Count;
            position.X = 0;
            vertices.Add(key, vertex); positions.Add(position); neighbours.Add([]);
            return vertex;
        }
        foreach ((SurfacePoint a, SurfacePoint b) in sections)
        {
            int first = Vertex(a.Position), last = Vertex(b.Position);
            if (first == last || neighbours[first].Contains(last)) continue;
            neighbours[first].Add(last); neighbours[last].Add(first);
        }
        int[] waist = Enumerable.Range(0, positions.Count).Where(vertex => neighbours[vertex].Count == 1).ToArray();
        neighbours[waist[0]].Add(waist[1]); neighbours[waist[1]].Add(waist[0]);
        HashSet<(int, int)> visited = [];
        List<int> boundary = [];
        float largestArea = 0;
        for (int first = 0; first < positions.Count; first++)
        foreach (int second in neighbours[first])
        {
            if (visited.Contains((first, second))) continue;
            List<int> path = [first];
            int previous = first, current = second;
            do
            {
                visited.Add((previous, current)); path.Add(current);
                Vector3 reverse = positions[previous] - positions[current];
                float reverseAngle = MathF.Atan2(reverse.Z, reverse.Y);
                int next = neighbours[current].MinBy(vertex =>
                {
                    if (vertex == previous) return MathF.Tau;
                    Vector3 edge = positions[vertex] - positions[current];
                    return (reverseAngle - MathF.Atan2(edge.Z, edge.Y) + MathF.Tau) % MathF.Tau;
                });
                previous = current; current = next;
            } while (previous != first || current != second);
            float area = 0;
            for (int vertex = 1; vertex < path.Count; vertex++)
            {
                Vector3 a = positions[path[vertex - 1]], b = positions[path[vertex]];
                area += a.Y * b.Z - a.Z * b.Y;
            }
            if (MathF.Abs(area) <= largestArea) continue;
            largestArea = MathF.Abs(area); boundary = path;
        }
        outline = boundary.Select(vertex => positions[vertex]).ToArray();
        distances = new float[outline.Length];
        for (int vertex = 1; vertex < outline.Length; vertex++)
            distances[vertex] = distances[vertex - 1] + Vector3.Distance(outline[vertex - 1], outline[vertex]);
    }

    public float FindDistance(Vector3 position)
    {
        float closest = float.PositiveInfinity, distance = 0;
        for (int vertex = 1; vertex < outline.Length; vertex++)
        {
            Vector3 a = outline[vertex - 1], edge = outline[vertex] - a;
            float along = Math.Clamp(Vector3.Dot(position - a, edge) / edge.LengthSquared(), 0, 1);
            float gap = Vector3.DistanceSquared(position, a + edge * along);
            if (gap >= closest) continue;
            closest = gap; distance = float.Lerp(distances[vertex - 1], distances[vertex], along);
        }
        return distance;
    }

    public float MeasureTravel(float from, float to)
    {
        float forward = (to - from + distances[^1]) % distances[^1];
        return forward < distances[^1] * .5f ? forward : forward - distances[^1];
    }

    public Vector3 Sample(float distance)
    {
        distance = (distance % distances[^1] + distances[^1]) % distances[^1];
        int vertex = Array.BinarySearch(distances, distance);
        if (vertex >= 0) return outline[vertex];
        vertex = ~vertex;
        return Vector3.Lerp(outline[vertex - 1], outline[vertex],
            (distance - distances[vertex - 1]) / (distances[vertex] - distances[vertex - 1]));
    }

    public SurfacePoint Nearest(Vector3 position)
    {
        SurfacePoint closest = new(-1, default, default, float.PositiveInfinity);
        foreach ((SurfacePoint a, SurfacePoint b) in sections)
        {
            Vector3 edge = b.Position - a.Position;
            if (edge.LengthSquared() == 0) continue;
            float along = Math.Clamp(Vector3.Dot(position - a.Position, edge) / edge.LengthSquared(), 0, 1);
            SurfacePoint point = Between(a, b, along);
            float distance = Vector3.DistanceSquared(position, point.Position);
            if (distance < closest.DistanceSquared) closest = point with { DistanceSquared = distance };
        }
        return closest;
    }

    private static SurfacePoint Between(SurfacePoint a, SurfacePoint b, float along)
        => new(a.Triangle, Vector3.Lerp(a.Position, b.Position, along), Vector3.Lerp(a.Coordinates, b.Coordinates, along), 0);
}
