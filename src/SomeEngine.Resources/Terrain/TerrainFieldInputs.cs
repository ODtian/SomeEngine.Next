using System.Collections.Immutable;

namespace SomeEngine.Resources;

/// <summary>Additional fields sampled by a terrain geometry attribute generator.</summary>
public readonly record struct TerrainFieldInputs(ImmutableArray<int> HeightFieldIds, ImmutableArray<int> VoxelFieldIds);
