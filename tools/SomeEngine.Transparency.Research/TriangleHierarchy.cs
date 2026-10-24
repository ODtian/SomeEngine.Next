using System.Numerics;

namespace SomeEngine.Transparency.Research;

/// <summary>One continuous geometry tree with projected bounds and triangle leaves.</summary>
public sealed class TriangleHierarchy
{
    public TriangleHierarchy(ReadOnlySpan<ProjectedTriangle> source)
        : this(source.ToArray().Select(static triangle => new[] { triangle }).ToArray()) { }

    public TriangleHierarchy(ProjectedTriangle[][] models)
    {
        Triangles = models.SelectMany(static triangles => triangles).ToArray();
        Coverage = new TriangleCoverage[Triangles.Length];
        var colors = new List<Vector4>();
        var colorIndices = new Dictionary<Vector4, uint>();
        for (int index = 0; index < Triangles.Length; index++)
        {
            Vector4 color = Triangles[index].Color;
            if (!colorIndices.TryGetValue(color, out uint colorIndex))
            {
                colorIndex = (uint)colors.Count;
                colorIndices.Add(color, colorIndex);
                colors.Add(color);
            }
            Coverage[index] = TriangleCoverage.Create(Triangles[index], colorIndex);
        }
        Colors = colors.ToArray();

        var ranges = new ModelRange[models.Length];
        int firstTriangle = 0;
        for (int model = 0; model < models.Length; model++)
        {
            Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            foreach (ProjectedTriangle triangle in models[model])
            {
                minimum = Vector3.Min(minimum, triangle.Minimum);
                maximum = Vector3.Max(maximum, triangle.Maximum);
            }
            ranges[model] = new(firstTriangle, models[model].Length, minimum, maximum);
            firstTriangle += models[model].Length;
        }

        var nodes = new List<TriangleBounds>();
        BuildModels(0, ranges.Length);
        var singleCoverage = new bool[nodes.Count];
        for (int index = nodes.Count - 1; index >= 0; index--)
        {
            if (nodes[index].IsTriangle) { singleCoverage[index] = true; continue; }
            int left = index + 1, right = (int)nodes[left].GetSubtreeEnd((uint)left);
            singleCoverage[index] = singleCoverage[left] && singleCoverage[right] && !Overlaps(left, right);
        }
        RebuildCoverage(0);
        var remainingDepth = new float[nodes.Count + 1];
        remainingDepth[^1] = float.PositiveInfinity;
        for (int index = nodes.Count - 1; index >= 0; index--)
        {
            float minimum = remainingDepth[index + 1];
            if (nodes[index].IsTriangle)
            {
                ProjectedTriangle triangle = Triangles[nodes[index].TriangleIndex];
                float margin = MathF.Max(MathF.Abs(triangle.Minimum.Z), MathF.Abs(triangle.Maximum.Z)) * 1e-6f;
                minimum = MathF.Min(minimum, triangle.Minimum.Z - margin);
            }
            remainingDepth[index] = minimum;
        }
        var coverageRanges = new List<CoverageRange>();
        AssignCoverage(0, uint.MaxValue);
        Bounds = nodes.ToArray();
        CoverageRanges = coverageRanges.ToArray();
        SeparatedCoverage = CoverageRanges.All(static range => range.MaximumDepth < range.MinimumDepthAfter);

        void RebuildCoverage(int index)
        {
            if (singleCoverage[index])
            {
                int end = (int)nodes[index].GetSubtreeEnd((uint)index);
                var triangles = new List<int>();
                for (int node = index; node < end; node++)
                    if (nodes[node].IsTriangle) triangles.Add((int)nodes[node].TriangleIndex);
                int writeIndex = index;
                BuildScreenTriangles(triangles.ToArray(), 0, triangles.Count, ref writeIndex);
                return;
            }
            int left = index + 1, right = (int)nodes[left].GetSubtreeEnd((uint)left);
            RebuildCoverage(left);
            RebuildCoverage(right);
        }

        void BuildScreenTriangles(int[] indices, int first, int count, ref int writeIndex)
        {
            int index = writeIndex++;
            Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            Vector2 centerMinimum = new(float.PositiveInfinity), centerMaximum = new(float.NegativeInfinity);
            for (int triangleIndex = first; triangleIndex < first + count; triangleIndex++)
            {
                ProjectedTriangle triangle = Triangles[indices[triangleIndex]];
                minimum = Vector3.Min(minimum, triangle.Minimum);
                maximum = Vector3.Max(maximum, triangle.Maximum);
                Vector2 center = new(triangle.Center.X, triangle.Center.Y);
                centerMinimum = Vector2.Min(centerMinimum, center);
                centerMaximum = Vector2.Max(centerMaximum, center);
            }
            if (count == 1)
            {
                nodes[index] = TriangleBounds.Create(minimum, maximum, (uint)writeIndex, (uint)indices[first]);
                return;
            }
            Vector2 extent = centerMaximum - centerMinimum;
            int axis = extent.X >= extent.Y ? 0 : 1;
            float split = (centerMinimum[axis] + centerMaximum[axis]) * .5f;
            int middle = first;
            for (int triangleIndex = first; triangleIndex < first + count; triangleIndex++)
            {
                if (Triangles[indices[triangleIndex]].Center[axis] >= split) continue;
                (indices[middle], indices[triangleIndex]) = (indices[triangleIndex], indices[middle]);
                middle++;
            }
            if (middle == first || middle == first + count) middle = first + count / 2;
            BuildScreenTriangles(indices, first, middle - first, ref writeIndex);
            BuildScreenTriangles(indices, middle, first + count - middle, ref writeIndex);
            nodes[index] = TriangleBounds.Create(minimum, maximum, (uint)writeIndex, uint.MaxValue);
        }

        void AssignCoverage(int index, uint coverageIndex)
        {
            if (coverageIndex == uint.MaxValue && singleCoverage[index])
            {
                coverageIndex = (uint)coverageRanges.Count;
                uint end = nodes[index].GetSubtreeEnd((uint)index);
                coverageRanges.Add(new((uint)index, end, remainingDepth[end], nodes[index].Maximum.Z));
            }
            if (!nodes[index].IsTriangle)
            {
                int left = index + 1, right = (int)nodes[left].GetSubtreeEnd((uint)left);
                AssignCoverage(left, coverageIndex);
                AssignCoverage(right, coverageIndex);
            }
            nodes[index] = nodes[index] with { CoverageIndex = coverageIndex };
        }

        bool Overlaps(int first, int second)
        {
            TriangleBounds a = nodes[first], b = nodes[second];
            if (a.Maximum.X < b.Minimum.X || b.Maximum.X < a.Minimum.X ||
                a.Maximum.Y < b.Minimum.Y || b.Maximum.Y < a.Minimum.Y) return false;
            if (a.IsTriangle && b.IsTriangle)
                return TrianglesOverlap(Triangles[a.TriangleIndex], Triangles[b.TriangleIndex]);
            if (!a.IsTriangle)
            {
                int left = first + 1, right = (int)nodes[left].GetSubtreeEnd((uint)left);
                return Overlaps(left, second) || Overlaps(right, second);
            }
            int secondLeft = second + 1, secondRight = (int)nodes[secondLeft].GetSubtreeEnd((uint)secondLeft);
            return Overlaps(first, secondLeft) || Overlaps(first, secondRight);
        }

        void BuildModels(int first, int count)
        {
            if (count == 1)
            {
                ModelRange range = ranges[first];
                int[] indices = Enumerable.Range(range.FirstTriangle, range.TriangleCount).ToArray();
                BuildTriangles(indices, 0, indices.Length, range.Maximum - range.Minimum);
                return;
            }
            int nodeIndex = nodes.Count;
            nodes.Add(default);
            Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            float centerMinimum = float.PositiveInfinity, centerMaximum = float.NegativeInfinity;
            for (int index = first; index < first + count; index++)
            {
                ModelRange range = ranges[index];
                minimum = Vector3.Min(minimum, range.Minimum);
                maximum = Vector3.Max(maximum, range.Maximum);
                float center = (range.Minimum.Z + range.Maximum.Z) * .5f;
                centerMinimum = MathF.Min(centerMinimum, center);
                centerMaximum = MathF.Max(centerMaximum, center);
            }
            float split = (centerMinimum + centerMaximum) * .5f;
            int middle = first;
            for (int index = first; index < first + count; index++)
            {
                ModelRange range = ranges[index];
                if ((range.Minimum.Z + range.Maximum.Z) * .5f >= split) continue;
                (ranges[middle], ranges[index]) = (ranges[index], ranges[middle]);
                middle++;
            }
            if (middle == first || middle == first + count) middle = first + count / 2;
            BuildModels(first, middle - first);
            BuildModels(middle, first + count - middle);
            nodes[nodeIndex] = TriangleBounds.Create(minimum, maximum, (uint)nodes.Count, uint.MaxValue);
        }

        void BuildTriangles(int[] indices, int first, int count, Vector3 modelExtent)
        {
            int nodeIndex = nodes.Count;
            nodes.Add(default);
            Vector3 minimum = new(float.PositiveInfinity), maximum = new(float.NegativeInfinity);
            Vector3 centerMinimum = minimum, centerMaximum = maximum;
            for (int index = first; index < first + count; index++)
            {
                ProjectedTriangle triangle = Triangles[indices[index]];
                minimum = Vector3.Min(minimum, triangle.Minimum);
                maximum = Vector3.Max(maximum, triangle.Maximum);
                centerMinimum = Vector3.Min(centerMinimum, triangle.Center);
                centerMaximum = Vector3.Max(centerMaximum, triangle.Center);
            }
            if (count == 1)
            {
                nodes[nodeIndex] = TriangleBounds.Create(minimum, maximum, (uint)nodes.Count, (uint)indices[first]);
                return;
            }
            Vector3 centerExtent = centerMaximum - centerMinimum;
            const float depthWeight = 2;
            Vector3 scores = new(
                modelExtent.X > 0 ? centerExtent.X / modelExtent.X : 0,
                modelExtent.Y > 0 ? centerExtent.Y / modelExtent.Y : 0,
                modelExtent.Z > 0 ? centerExtent.Z / modelExtent.Z * depthWeight : 0);
            int axis = scores.Z >= scores.X && scores.Z >= scores.Y ? 2 : scores.Y >= scores.X ? 1 : 0;
            float split = (centerMinimum[axis] + centerMaximum[axis]) * .5f;
            int middle = first;
            for (int index = first; index < first + count; index++)
            {
                if (Triangles[indices[index]].Center[axis] >= split) continue;
                (indices[middle], indices[index]) = (indices[index], indices[middle]);
                middle++;
            }
            if (middle == first || middle == first + count) middle = first + count / 2;
            BuildTriangles(indices, first, middle - first, modelExtent);
            BuildTriangles(indices, middle, first + count - middle, modelExtent);
            nodes[nodeIndex] = TriangleBounds.Create(minimum, maximum, (uint)nodes.Count, uint.MaxValue);
        }
    }

    public ProjectedTriangle[] Triangles { get; }
    public TriangleCoverage[] Coverage { get; }
    public TriangleBounds[] Bounds { get; }
    public Vector4[] Colors { get; }
    public CoverageRange[] CoverageRanges { get; }
    public bool SeparatedCoverage { get; }

    public int FindNext(Vector2 point, float previousDepth, uint previousTriangleId, float opaqueDepth,
        ref int firstNode, uint completedCoverage, uint previousCoverage, out float depth, out int visits, out uint coverageIndex)
    {
        int triangleIndex = -1;
        uint closestTriangleId = uint.MaxValue;
        depth = opaqueDepth;
        visits = 0;
        coverageIndex = uint.MaxValue;
        int nodeIndex = firstNode;
        while (nodeIndex < Bounds.Length)
        {
            TriangleBounds node = Bounds[nodeIndex];
            visits++;
            int subtreeEnd = (int)node.GetSubtreeEnd((uint)nodeIndex);
            bool completed = node.CoverageIndex != uint.MaxValue &&
                (node.CoverageIndex == previousCoverage ||
                 (node.CoverageIndex < 32 && (completedCoverage & (1u << (int)node.CoverageIndex)) != 0));
            if (completed)
            {
                CoverageRange coverage = CoverageRanges[node.CoverageIndex];
                if (nodeIndex == firstNode) firstNode = (int)coverage.EndIndex;
                nodeIndex = (int)coverage.EndIndex;
                if (triangleIndex >= 0 && depth < coverage.MinimumDepthAfter) return triangleIndex;
                continue;
            }
            if (!node.Contains(point))
            {
                if (nodeIndex == firstNode) firstNode = subtreeEnd;
                nodeIndex = subtreeEnd;
                continue;
            }
            if (node.Maximum.Z < previousDepth || node.Minimum.Z >= opaqueDepth)
            {
                if (nodeIndex == firstNode) firstNode = subtreeEnd;
                nodeIndex = subtreeEnd;
                continue;
            }
            if (node.Minimum.Z > depth)
            {
                nodeIndex = subtreeEnd;
                continue;
            }
            if (!node.IsTriangle)
            {
                if (nodeIndex == firstNode) firstNode = nodeIndex + 1;
            }
            else
            {
                ProjectedTriangle triangle = Triangles[node.TriangleIndex];
                if (triangle.Covers(point, out float triangleDepth))
                {
                    bool afterPrevious = triangleDepth > previousDepth ||
                        (triangleDepth == previousDepth && triangle.TriangleId > previousTriangleId);
                    if (afterPrevious && triangleDepth < opaqueDepth)
                    {
                        if (triangleDepth < depth || (triangleDepth == depth && triangle.TriangleId < closestTriangleId))
                        {
                            triangleIndex = (int)node.TriangleIndex;
                            closestTriangleId = triangle.TriangleId;
                            depth = triangleDepth;
                            coverageIndex = node.CoverageIndex;
                        }
                    }
                    CoverageRange coverage = CoverageRanges[node.CoverageIndex];
                    if ((!afterPrevious || triangleDepth >= opaqueDepth) && nodeIndex == firstNode)
                        firstNode = (int)coverage.EndIndex;
                    nodeIndex = (int)coverage.EndIndex;
                    if (triangleIndex >= 0 && depth < coverage.MinimumDepthAfter) return triangleIndex;
                    continue;
                }
                else if (nodeIndex == firstNode) firstNode = nodeIndex + 1;
            }
            nodeIndex++;
        }
        return triangleIndex;
    }

    private readonly record struct ModelRange(int FirstTriangle, int TriangleCount, Vector3 Minimum, Vector3 Maximum);

    private static bool TrianglesOverlap(ProjectedTriangle a, ProjectedTriangle b)
    {
        return OverlapsEdge(a.A, a.B) && OverlapsEdge(a.B, a.C) && OverlapsEdge(a.C, a.A) &&
            OverlapsEdge(b.A, b.B) && OverlapsEdge(b.B, b.C) && OverlapsEdge(b.C, b.A);

        bool OverlapsEdge(Vector4 from, Vector4 to)
        {
            double x = (double)from.Y - to.Y, y = (double)to.X - from.X;
            double a0 = Project(a.A), a1 = Project(a.B), a2 = Project(a.C);
            double b0 = Project(b.A), b1 = Project(b.B), b2 = Project(b.C);
            return Math.Max(a0, Math.Max(a1, a2)) > Math.Min(b0, Math.Min(b1, b2)) &&
                Math.Max(b0, Math.Max(b1, b2)) > Math.Min(a0, Math.Min(a1, a2));

            double Project(Vector4 point) => ((double)point.X - from.X) * x + ((double)point.Y - from.Y) * y;
        }
    }

}
