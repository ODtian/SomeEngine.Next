using System.Numerics;
using System.Runtime.InteropServices;

namespace SomeEngine.Resources.Geometry;

/// <summary>Oriented distance samples and geometry bounds in the mesh's own coordinates.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MeshDistanceGrid
{
    public uint Width;
    public uint Height;
    public uint Depth;
    public uint FirstDistance;
    public Vector3 Lower;
    public float CellSize;
    public Vector3 AxisX;
    public float CenterX;
    public Vector3 AxisY;
    public float CenterY;
    public Vector3 AxisZ;
    public float CenterZ;
    public Vector3 Upper;
    public float DistanceError;
    public Vector3 GeometryLower;
    public uint FirstTriangle;
    public Vector3 GeometryUpper;
    public uint TriangleCount;
}

/// <summary>Triangle cell ranges use the mesh's axis-aligned local bounds.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public struct MeshTriangleGrid
{
    public Vector3 Lower;
    public uint Side;
    public Vector3 Upper;
    public uint FirstRange;
}

/// <summary>A high bit in Count identifies a dense cell's subdivision size.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct MeshTriangleRange(uint First, uint Count);

/// <summary>Addresses the original triangle in the mesh's existing streamed geometry.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
public readonly record struct MeshTrianglePointer(uint Page, uint Cluster, uint Triangle);

/// <summary>Immutable mesh-local data shared by every instance of the mesh.</summary>
public sealed class MeshDistanceField(
    MeshDistanceGrid grid,
    MeshTriangleGrid triangleGrid,
    ReadOnlyMemory<byte> distances,
    ReadOnlyMemory<byte> cellRanges,
    ReadOnlyMemory<byte> triangleReferences,
    ReadOnlyMemory<byte> trianglePointers,
    ReadOnlyMemory<uint> triangleMaterials = default,
    ReadOnlyMemory<Vector4> materialBounds = default)
{
    private readonly (ReadOnlyMemory<byte> References, uint MaterialCount) _referenceGroups =
        GroupTriangleReferences(cellRanges, triangleReferences, triangleMaterials);
    public MeshDistanceGrid Grid { get; } = grid;
    public MeshTriangleGrid TriangleGrid { get; } = triangleGrid;
    public ReadOnlyMemory<byte> Distances { get; } = distances;
    public ReadOnlyMemory<byte> CellRanges { get; } = cellRanges;
    public ReadOnlyMemory<byte> TriangleReferences => _referenceGroups.References;
    public ReadOnlyMemory<byte> TrianglePointers { get; } = trianglePointers;
    public ReadOnlyMemory<uint> TriangleMaterials { get; } = triangleMaterials;
    /// <summary>Two vectors per local material: lower bounds with a presence flag, then upper bounds.</summary>
    public ReadOnlyMemory<Vector4> MaterialBounds { get; } = materialBounds;

    /// <summary>Mesh headers, payload locations, and material-grouped triangle references.</summary>
    public int GpuByteLength => 192 + Distances.Length + CellRanges.Length +
        TriangleReferences.Length + TrianglePointers.Length + TriangleMaterials.Length * sizeof(uint) +
        MaterialBounds.Length * 16;

    /// <summary>Relocates triangle pointers to the existing hierarchy's page references.</summary>
    public void WriteGpuData(Span<byte> destination, Mesh mesh, uint firstBvhNode)
    {
        MeshDistanceGrid grid = Grid;
        MeshTriangleGrid cells = TriangleGrid;
        MemoryMarshal.Write(destination, in grid);
        MemoryMarshal.Write(destination[128..], in cells);
        Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(destination.Slice(160, 16));
        int next = 192;
        Copy(Distances.Span, destination, offsets, 0, ref next);
        Copy(CellRanges.Span, destination, offsets, 1, ref next);
        Copy(TriangleReferences.Span, destination, offsets, 2, ref next);
        offsets[3] = (uint)next;
        Span<MeshTrianglePointer> pointers = MemoryMarshal.Cast<byte, MeshTrianglePointer>(
            destination.Slice(next, TrianglePointers.Length));
        Span<uint> materials = MemoryMarshal.Cast<byte, uint>(destination.Slice(176, 16));
        materials[0] = (uint)(next + TrianglePointers.Length);
        materials[1] = _referenceGroups.MaterialCount;
        int boundsOffset = next + TrianglePointers.Length + TriangleMaterials.Length * sizeof(uint);
        materials[2] = (uint)boundsOffset;
        materials[3] = (uint)MaterialBounds.Length / 2u;
        TriangleMaterials.Span.CopyTo(MemoryMarshal.Cast<byte, uint>(destination.Slice(
            next + TrianglePointers.Length, TriangleMaterials.Length * sizeof(uint))));
        MemoryMarshal.AsBytes(MaterialBounds.Span).CopyTo(destination[boundsOffset..]);

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
        ReadOnlySpan<MeshTrianglePointer> sourcePointers = MemoryMarshal.Cast<byte, MeshTrianglePointer>(TrianglePointers.Span);
        for (int triangle = 0; triangle < sourcePointers.Length; triangle++)
        {
            MeshTrianglePointer source = sourcePointers[triangle];
            pointers[triangle] = source with
            {
                Page = clusterParts[pageStarts[source.Page] + (int)source.Cluster],
            };
        }

        static void Copy(ReadOnlySpan<byte> source, Span<byte> destination, Span<uint> offsets,
            int index, ref int next)
        {
            offsets[index] = (uint)next;
            source.CopyTo(destination[next..]);
            next += source.Length;
        }
    }

    private static (ReadOnlyMemory<byte> References, uint MaterialCount) GroupTriangleReferences(
        ReadOnlyMemory<byte> cellRanges, ReadOnlyMemory<byte> references, ReadOnlyMemory<uint> materials)
    {
        if (materials.IsEmpty) return (references, 0);
        uint materialCount = 0;
        foreach (uint material in materials.Span) materialCount = Math.Max(materialCount, material + 1);
        if (materialCount <= 1 || references.IsEmpty) return (references, materialCount);

        ReadOnlySpan<MeshTriangleRange> ranges = MemoryMarshal.Cast<byte, MeshTriangleRange>(cellRanges.Span);
        int largest = 0;
        foreach (MeshTriangleRange range in ranges)
            if ((range.Count & 0x80000000u) == 0) largest = Math.Max(largest, (int)range.Count);
        byte[] grouped = references.ToArray();
        Span<uint> indices = MemoryMarshal.Cast<byte, uint>(grouped.AsSpan());
        uint[] ordered = new uint[largest];
        int[] counts = new int[materialCount], positions = new int[materialCount];
        foreach (MeshTriangleRange range in ranges)
        {
            if ((range.Count & 0x80000000u) != 0 || range.Count <= 1) continue;
            Span<uint> entries = indices.Slice((int)range.First, (int)range.Count);
            Array.Clear(counts);
            foreach (uint triangle in entries) counts[materials.Span[(int)triangle]]++;
            int first = 0;
            for (int material = 0; material < counts.Length; material++)
            {
                positions[material] = first;
                first += counts[material];
            }
            foreach (uint triangle in entries)
                ordered[positions[materials.Span[(int)triangle]]++] = triangle;
            ordered.AsSpan(0, entries.Length).CopyTo(entries);
        }
        return (grouped, materialCount);
    }
}
