using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Resources.Geometry;

/// <summary>Complete triangle neighborhoods addressed by the mesh SDF's sample cells.</summary>
public sealed class MeshSurfaceNeighborhood
{
    private readonly MeshDistanceField _field;
    private readonly MeshTriangleRange[] _ranges;
    private readonly uint[] _references;

    internal MeshSurfaceNeighborhood(MeshDistanceField field, float expansion)
    {
        _field = field;
        Expansion = expansion;
        Padding = Math.Max(field.Grid.CellSize * .25f, field.Grid.DistanceError * 2) + expansion * 2;
        if (field.Grid.TriangleCount <= 4)
        {
            _ranges = [];
            _references = [];
            return;
        }
        MeshDistanceGrid grid = field.Grid;
        _ranges = new MeshTriangleRange[field.Distances.Length / sizeof(float)];
        var references = new List<uint>();
        uint[] marks = new uint[grid.TriangleCount];
        float radius = grid.CellSize * .5f + Padding;
        Vector3 extent = (Vector3.Abs(grid.AxisX) + Vector3.Abs(grid.AxisY) + Vector3.Abs(grid.AxisZ)) * radius;
        Vector3 center = new(grid.CenterX, grid.CenterY, grid.CenterZ);
        ReadOnlySpan<float> distances = MemoryMarshal.Cast<byte, float>(field.Distances.Span);
        for (int index = 0; index < _ranges.Length; index++)
        {
            if (distances[index] > MathF.Sqrt(3) * radius + grid.DistanceError) continue;
            Vector3 cell = new(index % grid.Width, index / grid.Width % grid.Height,
                index / (grid.Width * grid.Height));
            Vector3 local = grid.Lower + (cell + new Vector3(.5f)) * grid.CellSize;
            Vector3 point = center + grid.AxisX * local.X + grid.AxisY * local.Y + grid.AxisZ * local.Z;
            uint first = (uint)references.Count;
            MeshTriangleGrid cells = field.TriangleGrid;
            AddCells(cells.FirstRange, (int)cells.Side, cells.Lower, cells.Upper, point - extent,
                point + extent, (uint)index + 1, marks, references);
            _ranges[index] = new(first, (uint)references.Count - first);
        }
        _references = references.ToArray();
    }

    public float Expansion { get; }
    public float Padding { get; }
    public int GpuByteLength => 192 + _field.Distances.Length + _ranges.Length * 8 +
        _references.Length * sizeof(uint) + _field.TrianglePointers.Length + _field.TriangleMaterials.Length * sizeof(uint);

    private void AddCells(uint firstRange, int side, Vector3 lower, Vector3 upper,
        Vector3 queryLower, Vector3 queryUpper, uint stamp, uint[] marks, List<uint> references)
    {
        if (queryLower.X > upper.X || queryLower.Y > upper.Y || queryLower.Z > upper.Z ||
            queryUpper.X < lower.X || queryUpper.Y < lower.Y || queryUpper.Z < lower.Z) return;
        Vector3 pitch = (upper - lower) / side;
        Vector3 low = Vector3.Clamp(Vector3.Floor((queryLower - lower) / pitch), Vector3.Zero, new(side - 1));
        Vector3 high = Vector3.Clamp(Vector3.Floor((queryUpper - lower) / pitch), Vector3.Zero, new(side - 1));
        ReadOnlySpan<MeshTriangleRange> ranges = MemoryMarshal.Cast<byte, MeshTriangleRange>(_field.CellRanges.Span);
        ReadOnlySpan<uint> indices = MemoryMarshal.Cast<byte, uint>(_field.TriangleReferences.Span);
        for (int z = (int)low.Z; z <= high.Z; z++)
        for (int y = (int)low.Y; y <= high.Y; y++)
        for (int x = (int)low.X; x <= high.X; x++)
        {
            MeshTriangleRange range = ranges[(int)firstRange + x + y * side + z * side * side];
            if ((range.Count & 0x80000000u) != 0)
            {
                Vector3 cellLower = lower + new Vector3(x, y, z) * pitch;
                AddCells(range.First, (int)(range.Count & 0x7fffffffu), cellLower, cellLower + pitch,
                    queryLower, queryUpper, stamp, marks, references);
                continue;
            }
            foreach (uint triangle in indices.Slice((int)range.First, (int)range.Count))
            {
                if (marks[triangle] == stamp) continue;
                marks[triangle] = stamp;
                references.Add(triangle);
            }
        }
    }

    /// <summary>Writes SDF-to-neighborhood links and relocates pointers into existing streamed Cluster pages.</summary>
    public void WriteGpuData(Span<byte> destination, Mesh mesh, uint firstBvhNode)
    {
        MeshDistanceGrid grid = _field.Grid;
        MemoryMarshal.Write(destination, in grid);
        destination.Slice(128, 32).Clear();
        float padding = Padding;
        MemoryMarshal.Write(destination[128..], in padding);
        Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(destination.Slice(160, 16));
        int next = 192;
        Copy(_field.Distances.Span, destination, offsets, 0, ref next);
        Copy(MemoryMarshal.AsBytes(_ranges.AsSpan()), destination, offsets, 1, ref next);
        Copy(MemoryMarshal.AsBytes(_references.AsSpan()), destination, offsets, 2, ref next);
        offsets[3] = (uint)next;
        Span<MeshTrianglePointer> pointers = MemoryMarshal.Cast<byte, MeshTrianglePointer>(
            destination.Slice(next, _field.TrianglePointers.Length));
        Span<uint> materials = MemoryMarshal.Cast<byte, uint>(destination.Slice(176, 16));
        materials.Clear();
        int materialOffset = next + _field.TrianglePointers.Length;
        materials[0] = (uint)materialOffset;
        materials[1] = (uint)_field.TriangleMaterials.Length;
        _field.TriangleMaterials.Span.CopyTo(MemoryMarshal.Cast<byte, uint>(destination.Slice(
            materialOffset, _field.TriangleMaterials.Length * sizeof(uint))));

        int[] pageStarts = new int[mesh.PageCount];
        int clusterCount = 0;
        for (int page = 0; page < pageStarts.Length; page++)
        {
            pageStarts[page] = clusterCount;
            clusterCount += (int)mesh.PageDigests[page].ClusterCount;
        }
        uint[] clusterParts = new uint[clusterCount];
        ReadOnlySpan<ClusterBVHNode> nodes = MemoryMarshal.Cast<byte, ClusterBVHNode>(mesh.Bvh.Span);
        for (int node = 0; node < nodes.Length; node++)
        for (int child = 0; child < ClusterBVHNode.ChildCount; child++)
        {
            ClusterHierarchySlice part = nodes[node].GetChild(child);
            if (!part.IsEnabled || !part.IsLeaf) continue;
            int first = pageStarts[part.ChildReference >> 8] + (int)part.FirstCluster;
            uint address = (firstBvhNode + (uint)node) * ClusterBVHNode.SizeInBytes + 140u + (uint)child * 16u;
            clusterParts.AsSpan(first, (int)part.ClusterCount).Fill(address);
        }
        ReadOnlySpan<MeshTrianglePointer> source = MemoryMarshal.Cast<byte, MeshTrianglePointer>(_field.TrianglePointers.Span);
        for (int triangle = 0; triangle < source.Length; triangle++)
        {
            MeshTrianglePointer pointer = source[triangle];
            pointers[triangle] = pointer with { Page = clusterParts[pageStarts[pointer.Page] + (int)pointer.Cluster] };
        }

        static void Copy(ReadOnlySpan<byte> source, Span<byte> destination, Span<uint> offsets, int index, ref int next)
        {
            offsets[index] = (uint)next;
            source.CopyTo(destination[next..]);
            next += source.Length;
        }
    }
}
