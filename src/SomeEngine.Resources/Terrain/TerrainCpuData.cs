using System.Collections.Immutable;

namespace SomeEngine.Resources;

/// <summary>Immutable CPU field values and the surface graph from one Terrain revision.</summary>
public sealed record TerrainCpuData(ulong Revision, ImmutableArray<HeightFieldValues> HeightFields,
    ImmutableArray<VoxelFieldValues> VoxelFields, TerrainSurfaceValues? Surface)
{
    public Terrain CreateTerrain(TerrainEditing editing) => Terrain.FromCpuData(editing, this);
}

public sealed record HeightFieldValues(int Id, string Name, HeightGrid Grid, float InitialHeight,
    ulong Revision, ImmutableArray<HeightPageValues> Pages);
public sealed record VoxelFieldValues(int Id, string Name, VoxelGrid Grid, float InitialValue,
    ulong Revision, ImmutableArray<VoxelPageValues> Pages);
public readonly record struct HeightPageValues(int X, int Z, ReadOnlyMemory<float> Samples);
public readonly record struct VoxelPageValues(int X, int Y, int Z, ReadOnlyMemory<float> Samples);

/// <summary>Field references and immutable parameters of a captured surface.</summary>
public abstract record TerrainSurfaceValues;
public sealed record HeightSurfaceValues(int FieldId) : TerrainSurfaceValues;
public sealed record VoxelSurfaceValues(int FieldId) : TerrainSurfaceValues;
public sealed record BlendSurfaceValues(TerrainSurfaceValues First, TerrainSurfaceValues Second, float Weight,
    TerrainBounds? Region = null, float FadeDistance = 0f) : TerrainSurfaceValues;
