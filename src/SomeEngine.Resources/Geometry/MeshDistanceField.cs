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
    ReadOnlyMemory<uint> triangleMaterials = default)
{
    public MeshDistanceGrid Grid { get; } = grid;
    public MeshTriangleGrid TriangleGrid { get; } = triangleGrid;
    public ReadOnlyMemory<byte> Distances { get; } = distances;
    public ReadOnlyMemory<byte> CellRanges { get; } = cellRanges;
    public ReadOnlyMemory<byte> TriangleReferences { get; } = triangleReferences;
    public ReadOnlyMemory<byte> TrianglePointers { get; } = trianglePointers;
    public ReadOnlyMemory<uint> TriangleMaterials { get; } = triangleMaterials;

    /// <summary>Builds complete surface neighborhoods for the declared local displacement range.</summary>
    public MeshSurfaceNeighborhood CreateSurfaceNeighborhood(float expansion = 0) => new(this, expansion);
}
