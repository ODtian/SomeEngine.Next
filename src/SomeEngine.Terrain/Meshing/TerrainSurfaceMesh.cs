using System.Numerics;
using SomeEngine.Resources;

namespace SomeEngine.Terrains;

/// <summary>The source location and outward normal used to generate a surface vertex's attributes.</summary>
public readonly record struct TerrainSurfacePoint(Vector3 SamplePosition, Vector3 Normal);

public sealed class TerrainSurfaceMesh(Vector3[] positions, uint[] indices, TerrainSurfacePoint[] samples)
{
    internal Vector3[] PositionArray { get; } = positions;
    internal uint[] IndexArray { get; } = indices;
    public ReadOnlyMemory<Vector3> Positions => PositionArray;
    public ReadOnlyMemory<uint> Indices => IndexArray;
    public ReadOnlyMemory<TerrainSurfacePoint> Samples { get; } = samples;
}

public interface ITerrainMesher
{
    /// <summary>Required sample neighborhood, measured in cells of the selected LOD.</summary>
    int ReadBorder { get; }
    TerrainSurfaceMesh Build(TerrainSurfaceRead surface, TerrainChunkBounds bounds);
}
