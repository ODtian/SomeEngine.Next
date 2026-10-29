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
    ReadOnlyMemory<byte> trianglePointers)
{
    public MeshDistanceGrid Grid { get; } = grid;
    public MeshTriangleGrid TriangleGrid { get; } = triangleGrid;
    public ReadOnlyMemory<byte> Distances { get; } = distances;
    public ReadOnlyMemory<byte> CellRanges { get; } = cellRanges;
    public ReadOnlyMemory<byte> TriangleReferences { get; } = triangleReferences;
    public ReadOnlyMemory<byte> TrianglePointers { get; } = trianglePointers;

    /// <summary>Two mesh headers, four byte offsets, and their immutable payloads.</summary>
    public int GpuByteLength => 176 + Distances.Length + CellRanges.Length +
        TriangleReferences.Length + TrianglePointers.Length;

    /// <summary>Relocates triangle pointers to the existing hierarchy's page references.</summary>
    public void WriteGpuData(Span<byte> destination, Mesh mesh, uint firstBvhNode)
    {
        MeshDistanceGrid grid = Grid;
        MeshTriangleGrid cells = TriangleGrid;
        MemoryMarshal.Write(destination, in grid);
        MemoryMarshal.Write(destination[128..], in cells);
        Span<uint> offsets = MemoryMarshal.Cast<byte, uint>(destination.Slice(160, 16));
        int next = 176;
        Copy(Distances.Span, destination, offsets, 0, ref next);
        Copy(CellRanges.Span, destination, offsets, 1, ref next);
        Copy(TriangleReferences.Span, destination, offsets, 2, ref next);
        Copy(TrianglePointers.Span, destination, offsets, 3, ref next);

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
        Span<uint> pointers = MemoryMarshal.Cast<byte, uint>(destination.Slice((int)offsets[3], TrianglePointers.Length));
        for (int triangle = 0; triangle < pointers.Length; triangle += 3)
            pointers[triangle] = clusterParts[pageStarts[pointers[triangle]] + (int)pointers[triangle + 1]];

        static void Copy(ReadOnlySpan<byte> source, Span<byte> destination, Span<uint> offsets,
            int index, ref int next)
        {
            offsets[index] = (uint)next;
            source.CopyTo(destination[next..]);
            next += source.Length;
        }
    }
}
