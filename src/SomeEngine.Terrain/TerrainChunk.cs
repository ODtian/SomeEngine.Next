using System.Numerics;
using SomeEngine.ECS;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

public readonly record struct TerrainChunkCoordinate(int X, int Y, int Z);

/// <summary>Number of cells per chunk edge and the finest terrain-local cell spacing.</summary>
public readonly record struct TerrainChunkGrid(int CellCount, float CellSize)
{
    public TerrainChunkBounds GetBounds(TerrainChunkCoordinate coordinates, int lod) =>
        new(coordinates, CellCount, MathF.ScaleB(CellSize, lod), lod);
}

[Flags]
public enum TerrainTransitionFaces : byte
{
    None = 0,
    NegativeX = 1, PositiveX = 2,
    NegativeY = 4, PositiveY = 8,
    NegativeZ = 16, PositiveZ = 32,
}

/// <summary>Spatial samples and the finer neighbors joined by this chunk's transition cells.</summary>
public readonly record struct TerrainChunkBounds(TerrainChunkCoordinate Coordinates, int CellCount, float CellSize,
    int Lod = 0, TerrainTransitionFaces TransitionFaces = TerrainTransitionFaces.None)
{
    public float FinestCellSize => MathF.ScaleB(CellSize, -Lod);
    public Vector3 Min => SamplePosition(0, 0, 0);
    public Vector3 Max => SamplePosition(CellCount, CellCount, CellCount);
    public TerrainBounds Volume => new(Min, Max);

    public Vector3 SamplePosition(int x, int y, int z) => new(
        (Coordinates.X * CellCount + x) * CellSize,
        (Coordinates.Y * CellCount + y) * CellSize,
        (Coordinates.Z * CellCount + z) * CellSize);
}

public readonly record struct TerrainChunk(
    int PlacementId, TerrainChunkCoordinate Coordinates, int Lod, ulong Revision, int GeometryId,
    TerrainTransitionFaces TransitionFaces = TerrainTransitionFaces.None) : IComponent;
