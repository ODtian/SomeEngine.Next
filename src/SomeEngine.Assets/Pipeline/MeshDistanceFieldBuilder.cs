using System.Numerics;
using System.Runtime.InteropServices;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;

namespace SomeEngine.Assets.Pipeline;

internal sealed class MeshDistanceFieldBuilder
{
    private readonly Triangle[] _triangles;
    private readonly float _error;
    private readonly CancellationToken _cancellation;

    private MeshDistanceFieldBuilder(Triangle[] triangles, CancellationToken cancellation)
    {
        _triangles = triangles;
        _cancellation = cancellation;
        Vector3 lower = triangles[0].Minimum, upper = triangles[0].Maximum;
        foreach (ref readonly Triangle triangle in triangles.AsSpan())
        {
            lower = Vector3.Min(lower, triangle.Minimum);
            upper = Vector3.Max(upper, triangle.Maximum);
        }
        Vector3 absolute = Vector3.Max(Vector3.Abs(lower), Vector3.Abs(upper));
        float magnitude = Math.Max(1, Math.Max(absolute.X, Math.Max(absolute.Y, absolute.Z)));
        _error = (upper - lower).Length() * 1e-5f + magnitude * 9.5367431640625e-7f + 1e-6f;
    }

    internal static MeshDistanceFieldBlock Build(ClusterGeometryBlock geometry,
        ClusterVertexAttributesBlock attributes, int distanceBudgetPerTriangle, int triangleGridSide,
        CancellationToken cancellationToken)
    {
        Mesh mesh = geometry.GetMeshData(attributes);
        var triangles = new List<Triangle>();
        int drawCluster = 0;
        for (int pageIndex = 0; pageIndex < mesh.PageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            mesh.TryGetPage(pageIndex, out ReadOnlyMemory<byte> bytes);
            ReadOnlySpan<byte> page = bytes.Span;
            MeshPageHeader header = MemoryMarshal.Read<MeshPageHeader>(page);
            for (int clusterIndex = 0; clusterIndex < header.ClusterCount; clusterIndex++, drawCluster++)
            {
                PackedCluster packed = MemoryMarshal.Read<PackedCluster>(
                    page.Slice((int)header.ClustersOffset + clusterIndex * PackedCluster.SizeInBytes));
                if (((packed.PackedCounts >> 16) & 255u) != 0) continue;
                ClusterGeometry source = geometry.Clusters[(int)mesh.ClusterGeometryIndices[drawCluster]];
                int count = (int)((packed.PackedCounts >> 8) & 255u);
                int first = (int)header.IndicesOffset + packed.TriangleStart * 3;
                for (int triangle = 0; triangle < count; triangle++)
                {
                    Vector3 a = source.DecodePosition(page[first + triangle * 3]);
                    Vector3 b = source.DecodePosition(page[first + triangle * 3 + 1]);
                    Vector3 c = source.DecodePosition(page[first + triangle * 3 + 2]);
                    if (Vector3.Cross(b - a, c - a).LengthSquared() == 0) continue;
                    triangles.Add(new(a, b, c, new((uint)pageIndex, (uint)clusterIndex, (uint)triangle)));
                }
            }
        }
        var builder = new MeshDistanceFieldBuilder(triangles.ToArray(), cancellationToken);
        MeshDistanceField data = builder.BuildData(distanceBudgetPerTriangle, triangleGridSide);
        var block = new MeshDistanceFieldBlock();
        try { block.SetData(geometry.VertexIdentity, data); return block; }
        catch { block.Dispose(); throw; }
    }

    private MeshDistanceField BuildData(int distanceBudgetPerTriangle, int triangleGridSide)
    {
        MeshTrianglePointer[] pointers = _triangles.Select(static value => value.Pointer).ToArray();
        if (_triangles.Length <= 4)
            return new(new() { TriangleCount = (uint)pointers.Length }, default, default, default, default,
                MemoryMarshal.AsBytes(pointers.AsSpan()).ToArray());

        (Vector3 center, Vector3 x, Vector3 y, Vector3 z) = PrincipalAxes();
        Vector3 lower = new(float.MaxValue), upper = new(float.MinValue);
        double area = 0;
        foreach (ref readonly Triangle triangle in _triangles.AsSpan())
        {
            Include(triangle.A); Include(triangle.B); Include(triangle.C);
            area += Vector3.Cross(triangle.B - triangle.A, triangle.C - triangle.A).Length() * .5;
        }
        Vector3 geometryLower = lower - new Vector3(_error), geometryUpper = upper + new Vector3(_error);
        double pitch = Math.Sqrt(area / _triangles.Length);
        Vector3 shape = Shape(pitch);
        while ((double)shape.X * shape.Y * shape.Z > (double)_triangles.Length * distanceBudgetPerTriangle)
        {
            pitch *= 1.1;
            shape = Shape(pitch);
        }
        float cellSize = (float)pitch;
        Vector3 gridLower = (geometryLower + geometryUpper - shape * cellSize) * .5f;
        var grid = new MeshDistanceGrid
        {
            Width = (uint)shape.X, Height = (uint)shape.Y, Depth = (uint)shape.Z,
            Lower = gridLower, CellSize = cellSize, Upper = gridLower + shape * cellSize,
            AxisX = x, AxisY = y, AxisZ = z, CenterX = center.X, CenterY = center.Y, CenterZ = center.Z,
            DistanceError = _error, GeometryLower = geometryLower, GeometryUpper = geometryUpper,
            TriangleCount = (uint)pointers.Length,
        };
        byte[] distanceBytes = new byte[(int)(grid.Width * grid.Height * grid.Depth) * sizeof(float)];
        Span<float> distances = MemoryMarshal.Cast<byte, float>(distanceBytes.AsSpan());
        var tree = new TriangleTree(_triangles);
        for (uint iz = 0; iz < grid.Depth; iz++)
        {
            _cancellation.ThrowIfCancellationRequested();
            for (uint iy = 0; iy < grid.Height; iy++)
            for (uint ix = 0; ix < grid.Width; ix++)
            {
                Vector3 sample = gridLower + (new Vector3(ix, iy, iz) + new Vector3(.5f)) * cellSize;
                Vector3 point = center + x * sample.X + y * sample.Y + z * sample.Z;
                distances[(int)(ix + iy * grid.Width + iz * grid.Width * grid.Height)] = tree.Distance(point);
            }
        }
        Vector3 meshLower = _triangles[0].Minimum, meshUpper = _triangles[0].Maximum;
        foreach (ref readonly Triangle triangle in _triangles.AsSpan())
        {
            meshLower = Vector3.Min(meshLower, triangle.Minimum);
            meshUpper = Vector3.Max(meshUpper, triangle.Maximum);
        }
        meshLower -= new Vector3(_error); meshUpper += new Vector3(_error);
        uint[] all = Enumerable.Range(0, _triangles.Length).Select(static value => (uint)value).ToArray();
        (MeshTriangleRange[] coarse, uint[] coarseReferences) = CellReferences(all, meshLower, meshUpper, triangleGridSide);
        var ranges = new List<MeshTriangleRange>(coarse);
        var references = new List<uint>();
        Vector3 cellPitch = (meshUpper - meshLower) / triangleGridSide;
        for (int index = 0; index < coarse.Length; index++)
        {
            _cancellation.ThrowIfCancellationRequested();
            MeshTriangleRange range = coarse[index];
            ReadOnlySpan<uint> candidates = coarseReferences.AsSpan((int)range.First, (int)range.Count);
            if (range.Count <= 4)
            {
                ranges[index] = new((uint)references.Count, range.Count);
                references.AddRange(candidates);
                continue;
            }
            int side = 2;
            while (side < 8 && side * side * 4 < range.Count) side *= 2;
            Vector3 coordinate = new(index % triangleGridSide, index / triangleGridSide % triangleGridSide,
                index / (triangleGridSide * triangleGridSide));
            Vector3 cellLower = meshLower + coordinate * cellPitch;
            (MeshTriangleRange[] dense, uint[] denseReferences) = CellReferences(candidates, cellLower, cellLower + cellPitch, side);
            ranges[index] = new((uint)ranges.Count, 0x80000000u | (uint)side);
            uint firstReference = (uint)references.Count;
            foreach (MeshTriangleRange leaf in dense) ranges.Add(new(firstReference + leaf.First, leaf.Count));
            references.AddRange(denseReferences.AsSpan());
        }
        MeshTriangleRange[] rangeData = ranges.ToArray();
        return new(grid, new() { Lower = meshLower, Upper = meshUpper, Side = (uint)triangleGridSide }, distanceBytes,
            MemoryMarshal.AsBytes(rangeData.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(references)).ToArray(),
            MemoryMarshal.AsBytes(pointers.AsSpan()).ToArray());

        void Include(Vector3 value)
        {
            Vector3 offset = value - center;
            Vector3 p = new(Vector3.Dot(offset, x), Vector3.Dot(offset, y), Vector3.Dot(offset, z));
            lower = Vector3.Min(lower, p); upper = Vector3.Max(upper, p);
        }
        Vector3 Shape(double size) => new(Math.Max(1, (float)Math.Ceiling((geometryUpper.X - geometryLower.X) / size)),
            Math.Max(1, (float)Math.Ceiling((geometryUpper.Y - geometryLower.Y) / size)),
            Math.Max(1, (float)Math.Ceiling((geometryUpper.Z - geometryLower.Z) / size)));
    }

    private (MeshTriangleRange[] Ranges, uint[] References) CellReferences(ReadOnlySpan<uint> triangles,
        Vector3 lower, Vector3 upper, int side)
    {
        int[] next = new int[side * side * side];
        Vector3 pitch = (upper - lower) / side;
        Action<int, uint> countCell = (cell, _) => next[cell]++;
        foreach (uint triangle in triangles) Rasterize(triangle, countCell);
        var ranges = new MeshTriangleRange[next.Length];
        int total = 0;
        for (int cell = 0; cell < next.Length; cell++)
        {
            int count = next[cell]; ranges[cell] = new((uint)total, (uint)count);
            next[cell] = total; total += count;
        }
        uint[] references = new uint[total];
        Action<int, uint> fillCell = (cell, triangle) => references[next[cell]++] = triangle;
        foreach (uint triangle in triangles) Rasterize(triangle, fillCell);
        return (ranges, references);

        void Rasterize(uint index, Action<int, uint> visit)
        {
            ref readonly Triangle triangle = ref _triangles[index];
            Vector3 low = Vector3.Clamp(Floor((triangle.Minimum - lower) / pitch - new Vector3(1e-4f)), Vector3.Zero, new Vector3(side - 1));
            Vector3 high = Vector3.Clamp(Floor((triangle.Maximum - lower) / pitch + new Vector3(1e-4f)), Vector3.Zero, new Vector3(side - 1));
            Vector3 half = pitch * .5f + new Vector3(_error);
            for (int iz = (int)low.Z; iz <= high.Z; iz++)
            for (int iy = (int)low.Y; iy <= high.Y; iy++)
            for (int ix = (int)low.X; ix <= high.X; ix++)
            {
                Vector3 center = lower + (new Vector3(ix, iy, iz) + new Vector3(.5f)) * pitch;
                if (Overlaps(triangle.A - center, triangle.B - center, triangle.C - center, half))
                    visit(ix + iy * side + iz * side * side, index);
            }
        }
        static Vector3 Floor(Vector3 value) => new(MathF.Floor(value.X), MathF.Floor(value.Y), MathF.Floor(value.Z));
    }

    private (Vector3 Center, Vector3 X, Vector3 Y, Vector3 Z) PrincipalAxes()
    {
        double sx = 0, sy = 0, sz = 0;
        foreach (ref readonly Triangle triangle in _triangles.AsSpan())
        {
            sx += (double)triangle.A.X + triangle.B.X + triangle.C.X;
            sy += (double)triangle.A.Y + triangle.B.Y + triangle.C.Y;
            sz += (double)triangle.A.Z + triangle.B.Z + triangle.C.Z;
        }
        Vector3 center = new((float)(sx / (_triangles.Length * 3)), (float)(sy / (_triangles.Length * 3)), (float)(sz / (_triangles.Length * 3)));
        double[,] covariance = new double[3, 3], axes = { { 1d, 0d, 0d }, { 0d, 1d, 0d }, { 0d, 0d, 1d } };
        foreach (ref readonly Triangle triangle in _triangles.AsSpan()) { Add(triangle.A); Add(triangle.B); Add(triangle.C); }
        for (int sweep = 0; sweep < 12; sweep++)
        for (int p = 0; p < 2; p++)
        for (int q = p + 1; q < 3; q++)
        {
            double off = covariance[p, q];
            if (off == 0) continue;
            double tau = (covariance[q, q] - covariance[p, p]) / (2 * off);
            double t = Math.CopySign(1d, tau) / (Math.Abs(tau) + Math.Sqrt(1 + tau * tau));
            double c = 1 / Math.Sqrt(1 + t * t), s = t * c;
            covariance[p, p] -= t * off; covariance[q, q] += t * off;
            covariance[p, q] = covariance[q, p] = 0;
            for (int k = 0; k < 3; k++)
            {
                if (k != p && k != q)
                {
                    double a = covariance[k, p], b = covariance[k, q];
                    covariance[k, p] = covariance[p, k] = c * a - s * b;
                    covariance[k, q] = covariance[q, k] = s * a + c * b;
                }
                double x = axes[k, p], y = axes[k, q];
                axes[k, p] = c * x - s * y; axes[k, q] = s * x + c * y;
            }
        }
        int[] order = [0, 1, 2];
        Array.Sort(order, (a, b) => covariance[a, a].CompareTo(covariance[b, b]));
        return (center, Axis(order[0]), Axis(order[1]), Axis(order[2]));

        Vector3 Axis(int column) => new((float)axes[0, column], (float)axes[1, column], (float)axes[2, column]);
        void Add(Vector3 point)
        {
            Vector3 v = point - center;
            for (int row = 0; row < 3; row++)
            for (int column = 0; column < 3; column++) covariance[row, column] += (double)v[row] * v[column];
        }
    }

    private static bool Overlaps(Vector3 a, Vector3 b, Vector3 c, Vector3 half)
    {
        Vector3 minimum = Vector3.Min(a, Vector3.Min(b, c)), maximum = Vector3.Max(a, Vector3.Max(b, c));
        if (minimum.X > half.X || minimum.Y > half.Y || minimum.Z > half.Z ||
            maximum.X < -half.X || maximum.Y < -half.Y || maximum.Z < -half.Z) return false;
        Vector3 first = b - a, second = c - b, third = a - c;
        return !Separated(Vector3.Cross(first, second)) &&
            Edge(first) && Edge(second) && Edge(third);

        bool Edge(Vector3 value) => !Separated(new(0, value.Z, -value.Y)) &&
            !Separated(new(-value.Z, 0, value.X)) && !Separated(new(value.Y, -value.X, 0));
        bool Separated(Vector3 axis)
        {
            float x = Vector3.Dot(a, axis), y = Vector3.Dot(b, axis), z = Vector3.Dot(c, axis);
            float radius = Vector3.Dot(Vector3.Abs(axis), half);
            return Math.Min(x, Math.Min(y, z)) > radius || Math.Max(x, Math.Max(y, z)) < -radius;
        }
    }

    private readonly struct Triangle(Vector3 a, Vector3 b, Vector3 c, MeshTrianglePointer pointer)
    {
        internal readonly Vector3 A = a, B = b, C = c;
        internal readonly Vector3 Minimum = Vector3.Min(a, Vector3.Min(b, c));
        internal readonly Vector3 Maximum = Vector3.Max(a, Vector3.Max(b, c));
        internal readonly Vector3 Center = (a + b + c) / 3;
        internal readonly MeshTrianglePointer Pointer = pointer;
    }

    private sealed class TriangleTree
    {
        private readonly Triangle[] _triangles;
        private readonly int[] _order;
        private readonly List<Branch> _branches = [];
        private readonly IComparer<int>[] _comparers;

        internal TriangleTree(Triangle[] triangles)
        {
            _triangles = triangles;
            _order = Enumerable.Range(0, triangles.Length).ToArray();
            _comparers = Enumerable.Range(0, 3).Select(axis => Comparer<int>.Create(
                (a, b) => triangles[a].Center[axis].CompareTo(triangles[b].Center[axis]))).ToArray();
            Build(0, triangles.Length);
        }

        private int Build(int first, int count)
        {
            Vector3 minimum = new(float.MaxValue), maximum = new(float.MinValue);
            for (int i = first; i < first + count; i++)
            {
                minimum = Vector3.Min(minimum, _triangles[_order[i]].Minimum);
                maximum = Vector3.Max(maximum, _triangles[_order[i]].Maximum);
            }
            int index = _branches.Count;
            _branches.Add(default);
            if (count <= 8) { _branches[index] = new(minimum, maximum, first, count, 0, 0); return index; }
            Vector3 extent = maximum - minimum;
            int axis = extent.X >= extent.Y && extent.X >= extent.Z ? 0 : extent.Y >= extent.Z ? 1 : 2;
            Array.Sort(_order, first, count, _comparers[axis]);
            int left = Build(first, count / 2), right = Build(first + count / 2, count - count / 2);
            _branches[index] = new(minimum, maximum, 0, 0, left, right);
            return index;
        }

        internal float Distance(Vector3 point)
        {
            float squared = float.MaxValue;
            Visit(0, point, ref squared);
            return MathF.Sqrt(squared);
        }

        private void Visit(int index, Vector3 point, ref float minimum)
        {
            Branch branch = _branches[index];
            if (BoxDistance(point, branch) >= minimum) return;
            if (branch.Count != 0)
            {
                for (int i = branch.First; i < branch.First + branch.Count; i++)
                    minimum = Math.Min(minimum, TriangleDistance(point, _triangles[_order[i]]));
                return;
            }
            int first = branch.Left, second = branch.Right;
            if (BoxDistance(point, _branches[first]) > BoxDistance(point, _branches[second])) (first, second) = (second, first);
            Visit(first, point, ref minimum); Visit(second, point, ref minimum);
        }

        private static float BoxDistance(Vector3 point, Branch branch)
        {
            Vector3 delta = Vector3.Max(Vector3.Max(branch.Minimum - point, point - branch.Maximum), Vector3.Zero);
            return delta.LengthSquared();
        }

        private static float TriangleDistance(Vector3 point, Triangle triangle)
        {
            Vector3 ab = triangle.B - triangle.A, ac = triangle.C - triangle.A, ap = point - triangle.A;
            float d1 = Vector3.Dot(ab, ap), d2 = Vector3.Dot(ac, ap);
            if (d1 <= 0 && d2 <= 0) return ap.LengthSquared();
            Vector3 bp = point - triangle.B;
            float d3 = Vector3.Dot(ab, bp), d4 = Vector3.Dot(ac, bp);
            if (d3 >= 0 && d4 <= d3) return bp.LengthSquared();
            float vc = d1 * d4 - d3 * d2;
            if (vc <= 0 && d1 >= 0 && d3 <= 0) return (point - (triangle.A + ab * (d1 / (d1 - d3)))).LengthSquared();
            Vector3 cp = point - triangle.C;
            float d5 = Vector3.Dot(ab, cp), d6 = Vector3.Dot(ac, cp);
            if (d6 >= 0 && d5 <= d6) return cp.LengthSquared();
            float vb = d5 * d2 - d1 * d6;
            if (vb <= 0 && d2 >= 0 && d6 <= 0) return (point - (triangle.A + ac * (d2 / (d2 - d6)))).LengthSquared();
            float va = d3 * d6 - d5 * d4;
            if (va <= 0 && d4 - d3 >= 0 && d5 - d6 >= 0)
                return (point - (triangle.B + (triangle.C - triangle.B) * ((d4 - d3) / (d4 - d3 + d5 - d6)))).LengthSquared();
            float denominator = 1 / (va + vb + vc);
            return (point - (triangle.A + ab * (vb * denominator) + ac * (vc * denominator))).LengthSquared();
        }

        private readonly record struct Branch(Vector3 Minimum, Vector3 Maximum, int First, int Count, int Left, int Right);
    }
}
