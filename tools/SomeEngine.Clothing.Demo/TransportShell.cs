using System.Numerics;

namespace SomeEngine.Clothing.Demo;

public readonly record struct ShellPoint(Vector3 Position, BoneInfluence[] BoneWeights,
    int BodyTriangle, Vector3 BodyCoordinates, Vector3 BodyOffset, bool ToeContour, float Progress);

public sealed class TransportShell
{
    public const float Clearance = .001f;
    public ShellPoint[] Points { get; }
    public Vector3[] Positions { get; }
    public int[][] Faces { get; }
    public int[][] Rings { get; }
    public int[] Triangles { get; }
    public int[] TransportEdges { get; }
    public int[] SectionEdges { get; }
    public int[] BoundaryEdges { get; }
    private readonly BodyMesh body;
    private readonly int[] bodyTriangles;
    private readonly int[] contourTriangles;
    private readonly SurfaceTriangles bodySurface;
    private readonly SurfaceTriangles shellSurface;
    private readonly Vector3[] bodyNormals;
    private readonly List<SurfaceCrossing> crossings = [];
    private readonly HashSet<(int, int)> facePairs = [];
    private readonly List<FacePoint> polygon = [], clipped = [];
    private readonly record struct FacePoint(Vector3 Position, Vector3 Coordinates);

    public TransportShell(CharacterAsset character, BodyMesh bodyMesh)
    {
        this.body = bodyMesh;
        bodyTriangles = character.Body.Triangles;
        bodyNormals = new Vector3[bodyMesh.Positions.Length];
        CharacterSurface guide = character.Clothing, body = character.Body;
        List<Vector3> positions = guide.Positions.ToList();
        List<float> heights = positions.Select(point => point.Y).ToList();
        List<int[]> faces = [];
        Dictionary<(int, int), int> cuts = [];
        static (int, int) Edge(int a, int b) => a < b ? (a, b) : (b, a);
        float waist = character.WaistHeight + .015f;
        foreach (int[] face in guide.Faces)
        {
            List<int> clipped = [];
            for (int corner = 0; corner < face.Length; corner++)
            {
                int a = face[corner], b = face[(corner + 1) % face.Length];
                float first = positions[a].Y - waist, last = positions[b].Y - waist;
                if (first <= 0) clipped.Add(a);
                if ((first < 0) == (last < 0)) continue;
                var edge = Edge(a, b);
                if (!cuts.TryGetValue(edge, out int vertex))
                {
                    float along = first / (first - last);
                    vertex = positions.Count; cuts.Add(edge, vertex);
                    positions.Add(Vector3.Lerp(positions[a], positions[b], along));
                    heights.Add(waist);
                }
                clipped.Add(vertex);
            }
            int[] polygon = clipped.Distinct().ToArray();
            if (polygon.Length >= 3) faces.Add(polygon);
        }
        faces = CharacterAsset.FindLargestSurface(faces.ToArray()).ToList();
        int[] fieldVertices = faces.SelectMany(face => face).Distinct().ToArray();
        Dictionary<int, HashSet<int>> fieldNeighbours = fieldVertices.ToDictionary(vertex => vertex, _ => new HashSet<int>());
        foreach (int[] face in faces)
        for (int corner = 0; corner < face.Length; corner++)
        {
            int a = face[corner], b = face[(corner + 1) % face.Length];
            fieldNeighbours[a].Add(b); fieldNeighbours[b].Add(a);
        }
        int[] freeHeights = fieldVertices.Where(vertex => heights[vertex] > .60f && heights[vertex] < waist - .000001f).ToArray();
        float[] progressHeights = heights.ToArray(), nextHeights = progressHeights.ToArray();
        for (int iteration = 0; iteration < 512; iteration++)
        {
            foreach (int vertex in freeHeights) nextHeights[vertex] = fieldNeighbours[vertex].Average(other => progressHeights[other]);
            (progressHeights, nextHeights) = (nextHeights, progressHeights);
        }
        heights = progressHeights.ToList();
        SurfaceTriangles skin = new(body.Positions, body.Triangles);
        int[][] lowerFaces = CharacterAsset.FindLargestSurface(body.Faces
            .Where(face => face.All(vertex => body.Positions[vertex].Y < waist + .05f)).ToArray());
        contourTriangles = CharacterAsset.Triangulate(lowerFaces);
        SurfaceTriangles lowerBody = new(body.Positions, contourTriangles);
        bodySurface = new(bodyMesh.Positions, contourTriangles);
        FootSections.ToeCap[] toes = new FootSections.ToeCap[2];
        for (int side = 0; side < 2; side++)
        {
            float sign = side == 0 ? 1 : -1;
            float front = body.Positions.Where(position => position.X * sign > 0 && position.Y < .12f).Max(position => position.Z);
            toes[side] = new FootSections(body, sign).CreateToeCap(front);
        }
        SurfaceTriangles Contour(Vector3 position)
        {
            FootSections.ToeCap toe = toes[position.X >= 0 ? 0 : 1];
            return position.Y < .12f && position.Z > toe.Tip.Z - FootSections.ToeLength ? toe.Surface : lowerBody;
        }
        void Relax(int iterations)
        {
            int[] active = faces.SelectMany(face => face).Distinct().ToArray();
            Dictionary<int, HashSet<int>> neighbours = active.ToDictionary(vertex => vertex, _ => new HashSet<int>());
            Dictionary<(int, int), int> uses = [];
            foreach (int[] face in faces)
            for (int corner = 0; corner < face.Length; corner++)
            {
                int a = face[corner], b = face[(corner + 1) % face.Length];
                neighbours[a].Add(b); neighbours[b].Add(a);
                var edge = Edge(a, b);
                uses[edge] = uses.GetValueOrDefault(edge) + 1;
            }
            HashSet<int> boundary = uses.Where(edge => edge.Value == 1).SelectMany(edge => new[] { edge.Key.Item1, edge.Key.Item2 }).ToHashSet();
            HashSet<int> midline = active.Where(vertex => MathF.Abs(positions[vertex].X) < .000001f).ToHashSet();
            int[] triangles = CharacterAsset.Triangulate(faces);
            Vector3[] points = positions.ToArray(), next = points.ToArray();
            Parallel.ForEach(active, vertex =>
            {
                SurfaceTriangles contour = Contour(points[vertex]);
                points[vertex] = contour.Nearest(points[vertex]).Position;
            });
            for (int iteration = 0; iteration < iterations; iteration++)
            {
                Vector3[] surfaceNormals = CharacterAsset.CalculateNormals(points, triangles);
                Parallel.ForEach(active, vertex =>
                {
                    Vector3 position = points[vertex];
                    if (boundary.Contains(vertex)) { next[vertex] = position; return; }
                    Vector3 average = neighbours[vertex].Aggregate(Vector3.Zero, (sum, other) => sum + points[other]) / neighbours[vertex].Count;
                    Vector3 shift = average - position, normal = surfaceNormals[vertex];
                    Vector3 candidate = position + (shift - normal * Vector3.Dot(shift, normal)) * .5f;
                    if (midline.Contains(vertex)) candidate.X = 0;
                    SurfaceTriangles contour = Contour(candidate);
                    next[vertex] = contour.Nearest(candidate).Position;
                    if (midline.Contains(vertex)) next[vertex].X = 0;
                });
                (points, next) = (next, points);
            }
            positions = points.ToList();
        }
        Relax(48);
        for (int subdivision = 0; subdivision < 3; subdivision++)
        {
            int vertexCount = positions.Count;
            Vector3[] previousPositions = positions.ToArray();
            float[] previousHeights = heights.ToArray();
            Dictionary<(int, int), List<int>> edgeFaces = [];
            List<int>[] vertexFaces = Enumerable.Range(0, vertexCount).Select(_ => new List<int>()).ToArray();
            HashSet<int>[] neighbours = Enumerable.Range(0, vertexCount).Select(_ => new HashSet<int>()).ToArray();
            int[] centers = new int[faces.Count];
            for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
            {
                int[] face = faces[faceIndex];
                centers[faceIndex] = positions.Count;
                positions.Add(face.Aggregate(Vector3.Zero, (sum, vertex) => sum + previousPositions[vertex]) / face.Length);
                heights.Add(face.Average(vertex => previousHeights[vertex]));
                for (int corner = 0; corner < face.Length; corner++)
                {
                    int a = face[corner], b = face[(corner + 1) % face.Length];
                    if (!edgeFaces.TryGetValue(Edge(a, b), out List<int>? adjacent)) edgeFaces.Add(Edge(a, b), adjacent = []);
                    adjacent.Add(faceIndex);
                    vertexFaces[a].Add(faceIndex);
                    neighbours[a].Add(b); neighbours[b].Add(a);
                }
            }
            Dictionary<(int, int), int> midpoints = [];
            int Midpoint(int a, int b)
            {
                var edge = Edge(a, b);
                if (midpoints.TryGetValue(edge, out int vertex)) return vertex;
                vertex = positions.Count; midpoints.Add(edge, vertex);
                List<int> adjacent = edgeFaces[edge];
                positions.Add(adjacent.Count == 2
                    ? (previousPositions[a] + previousPositions[b] + positions[centers[adjacent[0]]] + positions[centers[adjacent[1]]]) * .25f
                    : (previousPositions[a] + previousPositions[b]) * .5f);
                heights.Add(adjacent.Count == 2
                    ? (previousHeights[a] + previousHeights[b] + heights[centers[adjacent[0]]] + heights[centers[adjacent[1]]]) * .25f
                    : (previousHeights[a] + previousHeights[b]) * .5f);
                return vertex;
            }
            for (int vertex = 0; vertex < vertexCount; vertex++)
            {
                if (vertexFaces[vertex].Count == 0) continue;
                int[] boundary = neighbours[vertex].Where(other => edgeFaces[Edge(vertex, other)].Count == 1).ToArray();
                if (boundary.Length == 2)
                {
                    positions[vertex] = previousPositions[vertex] * .75f
                        + (previousPositions[boundary[0]] + previousPositions[boundary[1]]) * .125f;
                    heights[vertex] = previousHeights[vertex] * .75f
                        + (previousHeights[boundary[0]] + previousHeights[boundary[1]]) * .125f;
                    continue;
                }
                int count = neighbours[vertex].Count;
                Vector3 averageFaces = vertexFaces[vertex].Aggregate(Vector3.Zero, (sum, face) => sum + positions[centers[face]]) / vertexFaces[vertex].Count;
                Vector3 averageEdges = neighbours[vertex].Aggregate(Vector3.Zero,
                    (sum, other) => sum + (previousPositions[vertex] + previousPositions[other]) * .5f) / count;
                positions[vertex] = (averageFaces + 2 * averageEdges + (count - 3) * previousPositions[vertex]) / count;
                float faceHeight = vertexFaces[vertex].Average(face => heights[centers[face]]);
                float edgeHeight = neighbours[vertex].Average(other => (previousHeights[vertex] + previousHeights[other]) * .5f);
                heights[vertex] = (faceHeight + 2 * edgeHeight + (count - 3) * previousHeights[vertex]) / count;
            }
            List<int[]> refined = [];
            for (int faceIndex = 0; faceIndex < faces.Count; faceIndex++)
            {
                int[] face = faces[faceIndex];
                int center = centers[faceIndex];
                for (int corner = 0; corner < face.Length; corner++)
                {
                    int a = face[corner], b = face[(corner + 1) % face.Length], previous = face[(corner + face.Length - 1) % face.Length];
                    refined.Add([a, Midpoint(a, b), center, Midpoint(previous, a)]);
                }
            }
            faces = refined;
            Relax(12);
        }
        Vector3[] normals = CharacterAsset.CalculateNormals(positions.ToArray(), CharacterAsset.Triangulate(faces));
        int[] vertices = faces.SelectMany(face => face).Distinct().Order().ToArray();
        Dictionary<int, int> indices = vertices.Select((vertex, index) => (vertex, index)).ToDictionary(pair => pair.vertex, pair => pair.index);
        int[][] guideFaces = faces.Select(face => face.Select(vertex => indices[vertex]).ToArray()).ToArray();
        int[] guideTriangles = CharacterAsset.Triangulate(guideFaces);
        Vector3[] fitted = new Vector3[vertices.Length];
        Parallel.For(0, vertices.Length, index =>
        {
            Vector3 reference = positions[vertices[index]], direction = normals[vertices[index]];
            FootSections.ToeCap toe = toes[reference.X >= 0 ? 0 : 1];
            bool toeRegion = reference.Y < .12f && reference.Z > toe.Tip.Z - FootSections.ToeLength;
            if (toeRegion)
            {
                float blend = BodyMotion.Smooth(toe.Tip.Z - FootSections.ToeLength, toe.Tip.Z - .05f, reference.Z);
                direction = Vector3.Normalize(Vector3.Lerp(direction, Vector3.Normalize(reference - toe.Center), blend));
            }
            SurfaceTriangles contour = toeRegion ? toe.Surface : lowerBody;
            SurfacePoint point = contour.Project(reference, direction, .08f);
            if (point.Triangle < 0) point = contour.Nearest(reference);
            float DistanceSquared(Vector3 position) => toeRegion
                ? MathF.Min(skin.Nearest(position).DistanceSquared, contour.Nearest(position).DistanceSquared)
                : skin.Nearest(position).DistanceSquared;
            float low = 0, high = Clearance, gapSquared = Clearance * Clearance;
            while (DistanceSquared(point.Position + direction * high) < gapSquared) high *= 2;
            for (int step = 0; step < 12; step++)
            {
                float distance = (low + high) * .5f;
                if (DistanceSquared(point.Position + direction * distance) < gapSquared) low = distance; else high = distance;
            }
            fitted[index] = point.Position + direction * high;
        });
        float ankleZ = body.Positions.Where(point => point.Y > .14f && point.Y < .16f).Average(point => point.Z);
        float[] sweepHeights = vertices.Select((vertex, index) => float.Lerp(fitted[index].Y, heights[vertex],
            BodyMotion.Smooth(.60f, .74f, fitted[index].Y))).ToArray();
        ShellSweep sweep = new(fitted, guideTriangles, sweepHeights, waist - .0002f, ankleZ);
        Positions = sweep.Positions; Faces = sweep.Faces; Rings = sweep.Rings;
        Triangles = CharacterAsset.Triangulate(Faces);
        TransportEdges = sweep.TransportEdges; SectionEdges = sweep.SectionEdges; BoundaryEdges = sweep.BoundaryEdges;
        Points = new ShellPoint[Positions.Length];
        Parallel.For(0, Points.Length, index =>
        {
            Vector3 position = Positions[index];
            FootSections.ToeCap toe = toes[position.X >= 0 ? 0 : 1];
            bool toeRegion = position.Y < .12f && position.Z > toe.Tip.Z - FootSections.ToeLength;
            SurfacePoint attachment = skin.Nearest(position);
            Dictionary<int, float> weights = [];
            for (int corner = 0; corner < 3; corner++)
            foreach (BoneInfluence influence in body.BoneWeights[body.Triangles[attachment.Triangle * 3 + corner]])
                weights[influence.Bone] = weights.GetValueOrDefault(influence.Bone) + influence.Weight * attachment.Coordinates[corner];
            float total = weights.Values.Sum();
            BoneInfluence[] influences = weights.OrderByDescending(weight => weight.Value)
                .Select(weight => new BoneInfluence(weight.Key, weight.Value / total)).ToArray();
            Points[index] = new(position, influences, attachment.Triangle, attachment.Coordinates,
                position - attachment.Position, toeRegion, sweep.Progress[index]);
        });
        shellSurface = new(Positions, Triangles);
    }

    public void Update(BodyMotion motion, bool dualQuaternion = true)
    {
        bodySurface.Update();
        Array.Clear(bodyNormals);
        for (int face = 0; face < contourTriangles.Length; face += 3)
        {
            int a = contourTriangles[face], b = contourTriangles[face + 1], c = contourTriangles[face + 2];
            Vector3 normal = Vector3.Cross(body.Positions[b] - body.Positions[a], body.Positions[c] - body.Positions[a]);
            bodyNormals[a] += normal; bodyNormals[b] += normal; bodyNormals[c] += normal;
        }
        for (int vertex = 0; vertex < bodyNormals.Length; vertex++) bodyNormals[vertex] = Vector3.Normalize(bodyNormals[vertex]);
        Parallel.For(0, Points.Length, index =>
        {
            ShellPoint point = Points[index];
            if (point.ToeContour)
            {
                Positions[index] = dualQuaternion ? motion.Blend(point.BoneWeights).Transform(point.Position)
                    : Vector3.Transform(point.Position, motion.BlendMatrix(point.BoneWeights));
                return;
            }
            int triangle = point.BodyTriangle * 3;
            Vector3 position = body.Positions[bodyTriangles[triangle]] * point.BodyCoordinates.X
                + body.Positions[bodyTriangles[triangle + 1]] * point.BodyCoordinates.Y
                + body.Positions[bodyTriangles[triangle + 2]] * point.BodyCoordinates.Z;
            position += dualQuaternion ? Vector3.Transform(point.BodyOffset, motion.Blend(point.BoneWeights).Rotation)
                : Vector3.TransformNormal(point.BodyOffset, motion.BlendMatrix(point.BoneWeights));
            Positions[index] = FitPoint(position);
        });
        while (FitFaces())
            Parallel.For(0, Points.Length, index =>
            {
                if (!Points[index].ToeContour) Positions[index] = FitPoint(Positions[index]);
            });
    }

    private Vector3 FitPoint(Vector3 position)
    {
        for (int step = 0; step < 3; step++)
        {
            SurfacePoint contact = bodySurface.Nearest(position);
            int face = contact.Triangle * 3;
            Vector3 direction = bodyNormals[contourTriangles[face]] * contact.Coordinates.X
                + bodyNormals[contourTriangles[face + 1]] * contact.Coordinates.Y
                + bodyNormals[contourTriangles[face + 2]] * contact.Coordinates.Z;
            Vector3 offset = position - contact.Position;
            direction = Vector3.Normalize(Vector3.Dot(offset, direction) > 0 ? offset : direction);
            position = contact.Position + direction * Clearance;
        }
        return position;
    }

    private bool FitFaces()
    {
        bool intersections = false;
        for (int iteration = 0; iteration < 16; iteration++)
        {
            shellSurface.Update();
            facePairs.Clear();
            int count = 0;
            for (int face = 0; face < contourTriangles.Length; face += 3)
            {
                Vector3 a = body.Positions[contourTriangles[face]], b = body.Positions[contourTriangles[face + 1]],
                    c = body.Positions[contourTriangles[face + 2]];
                crossings.Clear();
                shellSurface.FindCrossings(a, b, c, crossings);
                count += crossings.Count;
                Vector3 normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                foreach (SurfaceCrossing crossing in crossings)
                {
                    if (!facePairs.Add((face, crossing.Triangle))) continue;
                    FitFace(crossing.Triangle, a, b, c, normal);
                }
            }
            if (count == 0) break;
            intersections = true;
        }
        return intersections;
    }

    private void FitFace(int triangle, Vector3 a, Vector3 b, Vector3 c, Vector3 normal)
    {
        int first = triangle * 3;
        polygon.Clear();
        polygon.Add(new(Positions[Triangles[first]], Vector3.UnitX));
        polygon.Add(new(Positions[Triangles[first + 1]], Vector3.UnitY));
        polygon.Add(new(Positions[Triangles[first + 2]], Vector3.UnitZ));
        ReadOnlySpan<Vector3> corners = stackalloc Vector3[3] { a, b, c };
        for (int edge = 0; edge < 3; edge++)
        {
            Vector3 start = corners[edge], axis = Vector3.Cross(normal, corners[(edge + 1) % 3] - start);
            clipped.Clear();
            for (int corner = 0; corner < polygon.Count; corner++)
            {
                FacePoint p = polygon[corner], q = polygon[(corner + 1) % polygon.Count];
                float d = Vector3.Dot(p.Position - start, axis), e = Vector3.Dot(q.Position - start, axis);
                if (d >= 0) clipped.Add(p);
                if ((d < 0) != (e < 0))
                {
                    float along = d / (d - e);
                    clipped.Add(new(Vector3.Lerp(p.Position, q.Position, along), Vector3.Lerp(p.Coordinates, q.Coordinates, along)));
                }
            }
            polygon.Clear(); polygon.AddRange(clipped);
        }
        if (polygon.Count == 0) return;
        FacePoint deepest = polygon.MinBy(point => Vector3.Dot(point.Position - a, normal));
        float distance = Vector3.Dot(deepest.Position - a, normal);
        Vector3 correction = normal * ((Clearance - distance) / deepest.Coordinates.LengthSquared());
        for (int corner = 0; corner < 3; corner++) Positions[Triangles[first + corner]] += correction * deepest.Coordinates[corner];
    }
}
