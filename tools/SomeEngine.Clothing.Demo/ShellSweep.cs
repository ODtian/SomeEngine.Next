using System.Numerics;

namespace SomeEngine.Clothing.Demo;

internal sealed class ShellSweep
{
    public const int Columns = 128;
    private const int FootRows = 80;
    private const int LegRows = 256;
    private const float AnkleHeight = .10f;
    private const float BendRadius = .06f;
    public Vector3[] Positions { get; }
    public float[] Progress { get; }
    public int[][] Faces { get; }
    public int[][] Rings { get; }
    public int[] TransportEdges { get; }
    public int[] SectionEdges { get; }
    public int[] BoundaryEdges { get; }

    private readonly Vector3[] source;
    private readonly int[] triangles;
    private readonly int[] footTriangles;
    private readonly float[] heights;
    private readonly float bendZ;
    private readonly float toeZ;
    private readonly float footLength;
    private readonly Vector3 crotch;
    private readonly float crotchHeight;

    public ShellSweep(Vector3[] source, int[] triangles, float[] heights, float waist, float ankleZ)
    {
        this.source = source;
        this.triangles = triangles;
        this.heights = heights;
        for (int vertex = 0; vertex < source.Length; vertex++)
            if (MathF.Abs(source[vertex].X) < .000001f) source[vertex].X = 0;
        footTriangles = Enumerable.Range(0, triangles.Length / 3)
            .Where(face => Enumerable.Range(0, 3).Any(corner => source[triangles[face * 3 + corner]].Y <= .25f))
            .SelectMany(face => triangles.Skip(face * 3).Take(3)).ToArray();
        toeZ = source.Where(point => point.Y < .12f).Max(point => point.Z);
        bendZ = ankleZ + BendRadius;
        footLength = toeZ - bendZ + BendRadius * MathF.PI * .5f;
        int crotchVertex = Enumerable.Range(0, source.Length).Where(vertex => source[vertex].X == 0).MinBy(vertex => heights[vertex]);
        crotch = source[crotchVertex]; crotchHeight = heights[crotchVertex];
        for (int face = 0; face < triangles.Length; face += 3)
        for (int corner = 0; corner < 3; corner++)
        {
            int a = triangles[face + corner], b = triangles[face + (corner + 1) % 3];
            if (source[a].X * source[b].X >= 0) continue;
            float along = source[a].X / (source[a].X - source[b].X);
            float height = float.Lerp(heights[a], heights[b], along);
            if (height >= crotchHeight) continue;
            crotch = Vector3.Lerp(source[a], source[b], along); crotchHeight = height;
        }
        float length = footLength + waist - AnkleHeight;
        float crotchDistance = footLength + crotchHeight - AnkleHeight;
        List<float> distances = Enumerable.Range(0, FootRows + 1).Select(row => footLength * row / FootRows).ToList();
        distances.AddRange(Enumerable.Range(1, LegRows).Select(row => footLength + (length - footLength) * row / LegRows));
        distances.Add(crotchDistance);
        foreach (float near in new[] { .00003f, .00012f, .0003f, .0006f, .0012f, .002f })
        {
            distances.Add(crotchDistance - near);
            distances.Add(crotchDistance + near);
        }
        distances.Sort();
        Dictionary<(int, float), Vector3[]> sections = [];
        Vector3[] Ring(int sideIndex, float distance)
        {
            if (sections.TryGetValue((sideIndex, distance), out Vector3[]? ring)) return ring;
            float side = sideIndex == 0 ? 1 : -1;
            Vector3 tip = source.Where(point => point.X * side > 0 && point.Y < .12f).MaxBy(point => point.Z);
            ring = distance == 0 ? Enumerable.Repeat(tip, Columns + 1).ToArray()
                : Section(distance, side, distance >= crotchDistance);
            if (distance == crotchDistance) ring[0] = ring[^1] = crotch;
            sections.Add((sideIndex, distance), ring);
            return ring;
        }
        for (int refinement = 0; refinement < 3; refinement++)
        {
            List<float> additional = [];
            for (int row = 2; row < distances.Count; row++)
            {
                float longest = 0;
                for (int side = 0; side < 2; side++)
                {
                    Vector3[] first = Ring(side, distances[row - 1]), last = Ring(side, distances[row]);
                    for (int column = 0; column <= Columns; column++) longest = MathF.Max(longest, Vector3.DistanceSquared(first[column], last[column]));
                }
                if (longest > .004f * .004f) additional.Add((distances[row - 1] + distances[row]) * .5f);
            }
            distances.AddRange(additional); distances.Sort();
        }
        int crotchRow = distances.IndexOf(crotchDistance), rows = distances.Count;
        List<Vector3> positions = [];
        List<float> progress = [];
        int[,,] vertices = new int[2, rows, Columns + 1];
        List<int[]> rings = [], faces = [];
        List<int> alongEdges = [], aroundEdges = [], boundaryEdges = [];
        for (int sideIndex = 0; sideIndex < 2; sideIndex++)
        {
            for (int row = 0; row < rows; row++)
            {
                float distance = distances[row];
                Vector3[] ring = Ring(sideIndex, distance);
                int[] indices = new int[Columns + 1];
                for (int column = 0; column <= Columns; column++)
                {
                    int vertex;
                    if (column == Columns && row <= crotchRow || row == 0 && column > 0) vertex = indices[0];
                    else if (sideIndex == 1 && row >= crotchRow && (column == 0 || column == Columns))
                        vertex = vertices[0, row, column];
                    else
                    {
                        vertex = positions.Count;
                        positions.Add(ring[column]); progress.Add(1 - distance / length);
                    }
                    vertices[sideIndex, row, column] = indices[column] = vertex;
                }
                rings.Add(indices);
            }
        }
        HashSet<(int, int)> displayed = [];
        void Edge(List<int> edges, int a, int b)
        {
            if (a == b || !displayed.Add(a < b ? (a, b) : (b, a))) return;
            edges.Add(a); edges.Add(b);
        }
        for (int side = 0; side < 2; side++)
        for (int row = 0; row < rows - 1; row++)
        for (int column = 0; column < Columns; column++)
        {
            int a = vertices[side, row, column], b = vertices[side, row, column + 1];
            int c = vertices[side, row + 1, column + 1], d = vertices[side, row + 1, column];
            int[] face = a == b ? [a, c, d] : [a, b, c, d];
            if (side == 1) Array.Reverse(face);
            faces.Add(face);
            if (column % 8 == 0) Edge(alongEdges, a, d);
            if (row % 8 == 0 || row == crotchRow) Edge(aroundEdges, a, b);
            if (row == rows - 2)
            {
                Edge(aroundEdges, d, c);
                boundaryEdges.Add(side == 0 ? c : d); boundaryEdges.Add(side == 0 ? d : c);
            }
        }
        Positions = positions.ToArray(); Progress = progress.ToArray(); Faces = faces.ToArray(); Rings = rings.ToArray();
        TransportEdges = alongEdges.ToArray(); SectionEdges = aroundEdges.ToArray(); BoundaryEdges = boundaryEdges.ToArray();
    }

    private (Vector3 Center, Vector3 Front, Vector3 Normal) Frame(float distance)
    {
        float straight = toeZ - bendZ;
        if (distance <= straight) return (new(0, AnkleHeight - BendRadius, toeZ - distance), Vector3.UnitY, -Vector3.UnitZ);
        if (distance >= footLength) return (new(0, AnkleHeight + distance - footLength, bendZ - BendRadius), Vector3.UnitZ, Vector3.UnitY);
        float angle = (distance - straight) / BendRadius;
        return (new(0, AnkleHeight - BendRadius * MathF.Cos(angle), bendZ - BendRadius * MathF.Sin(angle)),
            new(0, MathF.Cos(angle), MathF.Sin(angle)), new(0, MathF.Sin(angle), -MathF.Cos(angle)));
    }

    private Vector3[] Section(float distance, float side, bool upper)
    {
        var frame = Frame(distance);
        if (upper && MathF.Abs(frame.Center.Y - crotchHeight) < .000001f) frame.Center.Y += .000002f;
        Dictionary<(int, int), int> vertices = [];
        List<Vector3> points = [];
        List<List<int>> neighbours = [];
        int Vertex(Vector3 point, (int, int) key)
        {
            if (vertices.TryGetValue(key, out int vertex)) return vertex;
            vertex = points.Count; vertices.Add(key, vertex); points.Add(point); neighbours.Add([]);
            return vertex;
        }
        Span<Vector3> intersections = stackalloc Vector3[3];
        Span<(int, int)> intersectionEdges = stackalloc (int, int)[3];
        int[] sectionTriangles = distance < footLength ? footTriangles : triangles;
        for (int face = 0; face < sectionTriangles.Length; face += 3)
        {
            int count = 0;
            for (int corner = 0; corner < 3; corner++)
            {
                int firstVertex = sectionTriangles[face + corner], lastVertex = sectionTriangles[face + (corner + 1) % 3];
                Vector3 a = source[firstVertex], b = source[lastVertex];
                float da = distance < footLength ? Vector3.Dot(a - frame.Center, frame.Normal) : heights[firstVertex] - frame.Center.Y;
                float db = distance < footLength ? Vector3.Dot(b - frame.Center, frame.Normal) : heights[lastVertex] - frame.Center.Y;
                if ((da < 0) == (db < 0)) continue;
                intersectionEdges[count] = da == 0 ? (firstVertex, firstVertex) : db == 0 ? (lastVertex, lastVertex)
                    : firstVertex < lastVertex ? (firstVertex, lastVertex) : (lastVertex, firstVertex);
                intersections[count++] = Vector3.Lerp(a, b, da / (da - db));
            }
            if (count != 2) continue;
            Vector3 first = intersections[0], last = intersections[1];
            if (first.X * side <= 0 && last.X * side <= 0) continue;
            if ((first.X * side < 0) != (last.X * side < 0))
            {
                Vector3 middle = Vector3.Lerp(first, last, first.X / (first.X - last.X));
                middle.X = 0;
                if (first.X * side < 0) { first = middle; intersectionEdges[0] = (-face - 1, -face - 1); }
                else { last = middle; intersectionEdges[1] = (-face - 1, -face - 1); }
            }
            int x = Vertex(first, intersectionEdges[0]), y = Vertex(last, intersectionEdges[1]);
            if (x == y || neighbours[x].Contains(y)) continue;
            neighbours[x].Add(y); neighbours[y].Add(x);
        }
        List<Vector3> contour = [];
        HashSet<int> visited = [];
        float largestArea = 0;
        for (int first = 0; first < points.Count; first++)
        {
            if (neighbours[first].Count == 0 || !visited.Add(first)) continue;
            List<int> component = [first];
            for (int i = 0; i < component.Count; i++)
                foreach (int next in neighbours[component[i]]) if (visited.Add(next)) component.Add(next);
            int start = component.FirstOrDefault(vertex => neighbours[vertex].Count == 1, first);
            List<Vector3> curve = [];
            int current = start, previous = -1;
            HashSet<(int, int)> curveEdges = [];
            do
            {
                curve.Add(points[current]);
                List<int> links = neighbours[current];
                int next;
                if (previous < 0) next = links[0];
                else
                {
                    Vector3 reverse = points[previous] - points[current];
                    float reverseAngle = MathF.Atan2(Vector3.Dot(reverse, frame.Front), reverse.X);
                    next = links.Where(vertex => vertex != previous).OrderBy(vertex =>
                    {
                        Vector3 edge = points[vertex] - points[current];
                        return (reverseAngle - MathF.Atan2(Vector3.Dot(edge, frame.Front), edge.X) + MathF.Tau) % MathF.Tau;
                    }).FirstOrDefault(-1);
                }
                if (next < 0) break;
                if (!curveEdges.Add((current, next))) break;
                previous = current; current = next;
            } while (current != start);
            float area = 0;
            for (int point = 0; point < curve.Count; point++)
            {
                Vector3 a = curve[point], b = curve[(point + 1) % curve.Count];
                area += a.X * Vector3.Dot(b, frame.Front) - b.X * Vector3.Dot(a, frame.Front);
            }
            if (MathF.Abs(area) <= largestArea) continue;
            largestArea = MathF.Abs(area); contour = curve;
        }
        if (upper)
        {
            if (contour[0].Z < contour[^1].Z) contour.Reverse();
        }
        else
        {
            float area = 0;
            for (int point = 0; point < contour.Count; point++)
            {
                Vector3 a = contour[point], b = contour[(point + 1) % contour.Count];
                area += a.X * Vector3.Dot(b, frame.Front) - b.X * Vector3.Dot(a, frame.Front);
            }
            if (area * side > 0) contour.Reverse();
            float frontMinimum = contour.Min(point => Vector3.Dot(point, frame.Front)), frontMaximum = contour.Max(point => Vector3.Dot(point, frame.Front));
            float middle = (frontMinimum + frontMaximum) * .5f;
            float merge = BodyMotion.Smooth(crotchHeight - .18f, crotchHeight, frame.Center.Y);
            middle = float.Lerp(middle, Vector3.Dot(crotch, frame.Front), merge);
            middle = Math.Clamp(middle, float.Lerp(frontMinimum, frontMaximum, .001f), float.Lerp(frontMinimum, frontMaximum, .999f));
            int anchor = 0;
            Vector3 start = default;
            float inner = float.PositiveInfinity;
            for (int point = 0; point < contour.Count; point++)
            {
                Vector3 a = contour[point], b = contour[(point + 1) % contour.Count];
                float first = Vector3.Dot(a, frame.Front) - middle, last = Vector3.Dot(b, frame.Front) - middle;
                if ((first < 0) == (last < 0)) continue;
                Vector3 closest = Vector3.Lerp(a, b, first / (first - last));
                float gap = closest.X * side;
                if (gap >= inner) continue;
                inner = gap; anchor = point; start = closest;
            }
            contour = new[] { start }.Concat(contour.Skip(anchor + 1)).Concat(contour.Take(anchor + 1)).Append(start).ToList();
        }
        float[] lengths = new float[contour.Count];
        for (int point = 1; point < contour.Count; point++) lengths[point] = lengths[point - 1] + Vector3.Distance(contour[point - 1], contour[point]);
        Vector3[] ring = new Vector3[Columns + 1];
        for (int column = 0; column <= Columns; column++)
        {
            float along = lengths[^1] * column / Columns;
            int point = Array.BinarySearch(lengths, along);
            if (point >= 0) ring[column] = contour[point];
            else
            {
                point = ~point;
                ring[column] = Vector3.Lerp(contour[point - 1], contour[point], (along - lengths[point - 1]) / (lengths[point] - lengths[point - 1]));
            }
        }
        return ring;
    }
}
