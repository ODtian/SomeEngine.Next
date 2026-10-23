#include "terrain_height_input.slang"

float SampleTerrainSurface(float3 position)
{
    return position.y - SampleTerrainHeight(position.xz);
}

int2 TerrainColumnCells(int x, int z)
{
    float2 minimum = TerrainParameters.Min.xz + float2(x, z) * TerrainParameters.CellSize;
    float2 heights = TerrainColumnHeightRange(minimum, minimum + TerrainParameters.CellSize);
    int minY = int(floor((heights.x - TerrainParameters.Min.y) / TerrainParameters.CellSize)) - 1;
    int maxY = int(ceil((heights.y - TerrainParameters.Min.y) / TerrainParameters.CellSize)) + 1;
    return clamp(int2(minY, maxY), 0, TerrainParameters.CellCount);
}

#include "terrain_surface_cells.slang"

[shader("compute")]
[numthreads(1, 1, 1)]
void ExtractHeightSurface()
{
    ExtractTerrainCells();
}
