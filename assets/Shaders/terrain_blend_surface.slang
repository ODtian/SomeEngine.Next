__exported import terrain_blend_input;

int2 TerrainColumnCells(int x, int z)
{
    return int2(0, TerrainParameters.CellCount);
}

#include "terrain_surface_cells.slang"

[shader("compute")]
[numthreads(1, 1, 1)]
void ExtractBlendSurface()
{
    ExtractTerrainCells();
}
