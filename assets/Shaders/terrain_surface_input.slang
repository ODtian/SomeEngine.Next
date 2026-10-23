#ifndef TERRAIN_SURFACE_INPUT
#define TERRAIN_SURFACE_INPUT

struct TerrainSurfaceParameters
{
    float3 Min;
    float CellSize;
    int CellCount;
    uint VertexCapacity;
    uint TriangleCapacity;
};

ConstantBuffer<TerrainSurfaceParameters> TerrainParameters;

#endif
