// The host supplies the same regular cell tables used by MarchingCubesTerrainMesher.
StructuredBuffer<uint> TerrainRegularCells;
RWByteAddressBuffer TerrainPositions;
RWByteAddressBuffer TerrainSamples;
RWByteAddressBuffer TerrainIndices;
RWByteAddressBuffer TerrainCounts;

float3 TerrainCorner(int x, int y, int z, uint corner)
{
    return TerrainParameters.Min + TerrainParameters.CellSize *
        float3(x + (corner & 1u), y + ((corner >> 1u) & 1u), z + ((corner >> 2u) & 1u));
}

float3 TerrainSurfaceNormal(float3 position)
{
    float step = TerrainParameters.CellSize;
    return normalize(float3(
        SampleTerrainSurface(position + float3(step, 0, 0)) - SampleTerrainSurface(position - float3(step, 0, 0)),
        SampleTerrainSurface(position + float3(0, step, 0)) - SampleTerrainSurface(position - float3(0, step, 0)),
        SampleTerrainSurface(position + float3(0, 0, step)) - SampleTerrainSurface(position - float3(0, 0, step))));
}

void ExtractTerrainCells()
{
    uint vertexCount = 0;
    uint indexCount = 0;
    for (int z = 0; z < TerrainParameters.CellCount; z++)
    for (int x = 0; x < TerrainParameters.CellCount; x++)
    {
        int2 column = TerrainColumnCells(x, z);
        for (int y = column.x; y < column.y; y++)
        {
            float values[8];
            uint cellCase = 0;
            for (uint corner = 0; corner < 8u; corner++)
            {
                values[corner] = SampleTerrainSurface(TerrainCorner(x, y, z, corner));
                if (values[corner] < 0) cellCase |= 1u << corner;
            }
            uint cellClass = TerrainRegularCells[cellCase];
            uint counts = TerrainRegularCells[256u + cellClass];
            uint vertices = counts >> 4u;
            uint indices = (counts & 15u) * 3u;
            for (uint vertex = 0; vertex < vertices; vertex++)
            {
                uint edge = TerrainRegularCells[512u + cellCase * 12u + vertex];
                uint a = (edge >> 4u) & 15u;
                uint b = edge & 15u;
                float3 position = lerp(TerrainCorner(x, y, z, a), TerrainCorner(x, y, z, b),
                    values[a] / (values[a] - values[b]));
                uint destination = vertexCount + vertex;
                TerrainPositions.Store3(destination * 12u, asuint(position));
                TerrainSamples.Store3(destination * 24u, asuint(position));
                TerrainSamples.Store3(destination * 24u + 12u, asuint(TerrainSurfaceNormal(position)));
            }
            for (uint index = 0; index < indices; index++)
                TerrainIndices.Store((indexCount + index) * 4u,
                    vertexCount + TerrainRegularCells[272u + cellClass * 15u + index]);
            vertexCount += vertices;
            indexCount += indices;
        }
    }
    TerrainCounts.Store2(0, uint2(vertexCount, indexCount));
}
