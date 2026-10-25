using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public enum ClothingShape { Briefs, Tights }

public sealed class ClothingMesh
{
    public required string Name { get; init; }
    public required Vector3[] Vertices { get; init; }
    public required int[] Triangles { get; init; }
    public required TrajectoryBinding[] Bindings { get; init; }
    public required (int A, int B)[] Edges { get; init; }
    public required (int A, int B)[] Bends { get; init; }
    public required int[] BoundaryEdges { get; init; }
    public required float[] Trim { get; init; }
    public required float[] InverseMasses { get; init; }

    public static ClothingMesh Create(ClothingShape shape, CharacterAsset character, TrajectoryField trajectory)
    {
        const int columns = 32;
        const int rows = 64;
        float crotch = trajectory.DistanceAtHeight(character.CrotchHeight + .005f);
        float[] distances = Enumerable.Range(0, rows).Select(row => trajectory.RowDistance(0, row * (TrajectoryField.Rows - 1) / (rows - 1))).ToArray();
        distances[Enumerable.Range(0, rows).MinBy(row => MathF.Abs(distances[row] - crotch))] = crotch;
        int crotchRow = Array.IndexOf(distances, crotch);
        List<Vector3> points = [];
        List<TrajectoryBinding> pointBindings = [];
        List<int[]> faces = [];
        Dictionary<(int, int, int), int> verticesByCoordinate = [];
        int Vertex(int side, int row, int column)
        {
            column %= columns;
            bool seam = row >= crotchRow && (column == 0 || column == columns / 2 || row == crotchRow && column > columns / 2);
            var key = (seam ? 0 : side, row, row == 0 ? 0 : column);
            if (verticesByCoordinate.TryGetValue(key, out int vertex)) return vertex;
            var binding = new TrajectoryBinding(side, (float)column / columns, distances[row], Vector3.Zero);
            Vector3 position = trajectory.Sample(binding, 0, true).Position;
            if (seam)
            {
                var other = binding with { Surface = 1 - side };
                Vector3 second = trajectory.Sample(other, 0, true).Position;
                Vector3 center = (position + second) * .5f;
                binding = binding with { Offset = center - position };
                binding = trajectory.BindAcross(binding, .5f);
                position = center;
            }
            else
            {
                float across = .5f * (1 - BodyMotion.Smooth(.025f, .14f, MathF.Abs(position.X)))
                    * BodyMotion.Smooth(character.CrotchHeight - .09f, character.CrotchHeight + .015f, position.Y);
                if (across > 0) binding = trajectory.BindAcross(binding, across);
            }
            vertex = points.Count;
            points.Add(position); pointBindings.Add(binding);
            verticesByCoordinate.Add(key, vertex);
            return vertex;
        }
        for (int side = 0; side < 2; side++)
        for (int row = 0; row < distances.Length - 1; row++)
        for (int column = 0; column < (row >= crotchRow ? columns / 2 : columns); column++)
        {
            int[] polygon = new[] { Vertex(side, row, column), Vertex(side, row, column + 1),
                Vertex(side, row + 1, column + 1), Vertex(side, row + 1, column) }.Distinct().ToArray();
            if (polygon.Length >= 3) faces.Add(side == 1 ? polygon.Reverse().ToArray() : polygon);
        }
        TrajectoryBinding InterpolateBinding(int a, int b, float along, Vector3 position)
        {
            TrajectoryBinding first = pointBindings[a], second = pointBindings[b];
            if (first.Surface != second.Surface) second = second with { Surface = first.Surface, Across = 1 - second.Across };
            float start = first.Distance == 0 ? second.Around : first.Around;
            float end = second.Distance == 0 ? start : second.Around;
            if (end - start > .5f) end -= 1;
            if (end - start < -.5f) end += 1;
            var binding = first with { Around = float.Lerp(start, end, along), Distance = float.Lerp(first.Distance, second.Distance, along),
                Across = float.Lerp(first.Across, second.Across, along), Offset = Vector3.Zero };
            return binding with { Offset = position - trajectory.Sample(binding, 0, true).Position };
        }
        void Clip(Func<Vector3, float> distance)
        {
            Dictionary<(int, int), int> intersections = [];
            List<int[]> result = [];
            foreach (int[] face in faces)
            {
                List<int> polygon = [];
                for (int edge = 0; edge < face.Length; edge++)
                {
                    int a = face[edge], b = face[(edge + 1) % face.Length];
                    float da = distance(points[a]), db = distance(points[b]);
                    if (da >= 0) polygon.Add(a);
                    if ((da >= 0) == (db >= 0)) continue;
                    var key = a < b ? (a, b) : (b, a);
                    float along = da / (da - db);
                    float length = Vector3.Distance(points[a], points[b]);
                    if (along * length < .006f) { polygon.Add(a); continue; }
                    if ((1 - along) * length < .006f) { polygon.Add(b); continue; }
                    if (!intersections.TryGetValue(key, out int vertex))
                    {
                        vertex = points.Count;
                        points.Add(Vector3.Lerp(points[a], points[b], along));
                        pointBindings.Add(InterpolateBinding(a, b, along, points[vertex]));
                        intersections.Add(key, vertex);
                    }
                    polygon.Add(vertex);
                }
                int[] clipped = polygon.Distinct().ToArray();
                if (clipped.Length >= 3) result.Add(clipped);
            }
            faces = result;
        }
        Clip(point => character.WaistHeight - point.Y);
        if (shape == ClothingShape.Briefs)
            Clip(point => point.Y - character.CrotchHeight + .012f - .17f * BodyMotion.Smooth(.025f, .19f, MathF.Abs(point.X))
                * (.72f + .28f * BodyMotion.Smooth(-.04f, .10f, point.Z)));
        SurfaceTriangles body = new(character.Body.Positions, character.Body.Triangles);
        foreach (int vertex in faces.SelectMany(face => face).Distinct())
        {
            Vector3 point = trajectory.Sample(pointBindings[vertex], 0, true).Position;
            SurfacePoint closest = body.Nearest(point);
            Vector3 normal = Vector3.Zero;
            for (int corner = 0; corner < 3; corner++)
                normal += character.Body.Normals[character.Body.Triangles[closest.Triangle * 3 + corner]] * closest.Coordinates[corner];
            normal = Vector3.Normalize(normal);
            Vector3 correction = normal * MathF.Max(0, .008f - Vector3.Dot(point - closest.Position, normal));
            pointBindings[vertex] = pointBindings[vertex] with { Offset = pointBindings[vertex].Offset + correction };
            points[vertex] = point + correction;
        }
        const float spacing = .005f;
        int[] vertexParents = Enumerable.Range(0, points.Count).ToArray();
        int FindVertex(int vertex)
        {
            while (vertexParents[vertex] != vertex) vertex = vertexParents[vertex];
            return vertex;
        }
        foreach (int[] face in faces)
        for (int edge = 0; edge < face.Length; edge++)
        {
            int a = FindVertex(face[edge]), b = FindVertex(face[(edge + 1) % face.Length]);
            if (a == b || Vector3.DistanceSquared(points[a], points[b]) >= spacing * spacing) continue;
            if (a > b) (a, b) = (b, a);
            pointBindings[a] = InterpolateBinding(a, b, .5f, points[a]);
            vertexParents[b] = a;
        }
        faces = faces.Select(face => face.Select(FindVertex).Distinct().ToArray()).Where(face => face.Length >= 3).ToList();
        faces = CharacterAsset.FindLargestSurface(faces.ToArray()).ToList();
        int[] used = faces.SelectMany(face => face).Distinct().Order().ToArray();
        Dictionary<int, int> remap = used.Select((vertex, index) => (vertex, index)).ToDictionary(pair => pair.vertex, pair => pair.index);
        Vector3[] vertices = used.Select(index => trajectory.Sample(pointBindings[index], 0, true).Position).ToArray();
        int[][] polygons = faces.Select(face => face.Select(index => remap[index]).ToArray()).ToArray();
        int[] triangles = CharacterAsset.Triangulate(polygons);
        TrajectoryBinding[] bindings = used.Select((index, vertex) => trajectory.BindSupport(pointBindings[index],
            vertices[vertex].Y < .2f ? 0 : MathF.Max(0, MathF.Sqrt(body.Nearest(vertices[vertex]).DistanceSquared) - .010f))).ToArray();
        float[] areas = new float[vertices.Length];
        for (int triangle = 0; triangle < triangles.Length; triangle += 3)
        {
            int a = triangles[triangle], b = triangles[triangle + 1], c = triangles[triangle + 2];
            float area = Vector3.Cross(vertices[b] - vertices[a], vertices[c] - vertices[a]).Length() / 6;
            areas[a] += area; areas[b] += area; areas[c] += area;
        }
        HashSet<(int, int)> edges = [];
        Dictionary<(int, int), int> boundaryCounts = [];
        foreach (int[] face in polygons)
        {
            for (int corner = 0; corner < face.Length; corner++)
            {
                int a = face[corner], b = face[(corner + 1) % face.Length];
                var edge = a < b ? (a, b) : (b, a);
                edges.Add(edge); boundaryCounts[edge] = boundaryCounts.GetValueOrDefault(edge) + 1;
            }
            for (int a = 0; a < face.Length; a++)
            for (int b = a + 2; b < face.Length; b++)
                edges.Add(face[a] < face[b] ? (face[a], face[b]) : (face[b], face[a]));
        }
        int[] boundary = boundaryCounts.Where(pair => pair.Value == 1).SelectMany(pair => new[] { pair.Key.Item1, pair.Key.Item2 }).ToArray();
        HashSet<int> rim = boundary.ToHashSet();
        Dictionary<(int, int), int> oppositeVertices = [];
        HashSet<(int, int)> bends = [];
        for (int triangle = 0; triangle < triangles.Length; triangle += 3)
        for (int corner = 0; corner < 3; corner++)
        {
            int a = triangles[triangle + corner], b = triangles[triangle + (corner + 1) % 3], c = triangles[triangle + (corner + 2) % 3];
            var edge = a < b ? (a, b) : (b, a);
            if (oppositeVertices.TryGetValue(edge, out int other)) bends.Add(c < other ? (c, other) : (other, c));
            else oppositeVertices.Add(edge, c);
        }
        return new()
        {
            Name = shape == ClothingShape.Briefs ? "内裤" : "裤袜", Vertices = vertices, Triangles = triangles, Bindings = bindings,
            Edges = edges.ToArray(), Bends = bends.ToArray(), BoundaryEdges = boundary,
            Trim = vertices.Select((point, index) => MathF.Max(rim.Contains(index) ? 1 : 0,
                1 - BodyMotion.Smooth(.012f, .02f, character.WaistHeight - point.Y))).ToArray(),
            InverseMasses = areas.Select(area => .0004f / area).ToArray(),
        };
    }
}
