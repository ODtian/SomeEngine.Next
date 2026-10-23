#include "terrain_voxel_input.slang"

float SampleTerrainSurface(float3 position)
{
    return SampleTerrainVoxels(TerrainSources[0], position);
}

int2 TerrainColumnCells(int x, int z)
{
    return int2(0, TerrainParameters.CellCount);
}

#include "terrain_surface_cells.slang"

[shader("compute")]
[numthreads(1, 1, 1)]
void ExtractVoxelSurface()
{
    ExtractTerrainCells();
}
