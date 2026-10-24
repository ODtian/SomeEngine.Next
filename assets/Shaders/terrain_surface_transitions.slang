// Classes[512], counts[56], triangle indices[56 * 36], vertex edges[512 * 12].
StructuredBuffer<uint> TerrainTransitionCells;

void ExtractTerrainTransitions(inout uint vertexCount, inout uint indexCount)
{
    static const uint weights[9] = { 1, 2, 4, 128, 256, 8, 64, 32, 16 };
    static const uint coarseCorners[4] = { 0, 2, 6, 8 };
    for (int face = 0; face < 6; face++)
    {
        if ((TerrainParameters.TransitionFaces & (1u << face)) == 0) continue;
        float3 u, v;
        switch (face)
        {
            case 0: u = float3(0, 1, 0); v = float3(0, 0, 1); break;
            case 1: u = float3(0, 0, 1); v = float3(0, 1, 0); break;
            case 2: u = float3(0, 0, 1); v = float3(1, 0, 0); break;
            case 3: u = float3(1, 0, 0); v = float3(0, 0, 1); break;
            case 4: u = float3(1, 0, 0); v = float3(0, 1, 0); break;
            default: u = float3(0, 1, 0); v = float3(1, 0, 0); break;
        }
        float3 origin = TerrainParameters.Min;
        if ((face & 1) != 0) origin[face / 2] += TerrainParameters.CellCount * TerrainParameters.CellSize;
        for (int y = 0; y < TerrainParameters.CellCount; y++)
        for (int x = 0; x < TerrainParameters.CellCount; x++)
        {
            float3 corners[13];
            float values[13];
            uint cellCase = 0;
            for (uint corner = 0; corner < 9u; corner++)
            {
                float3 point = origin + (u * (x + (corner % 3u) * .5f)
                    + v * (y + (corner / 3u) * .5f)) * TerrainParameters.CellSize;
                corners[corner] = point;
                values[corner] = SampleTerrainSurface(point);
                if (values[corner] < 0) cellCase |= weights[corner];
            }
            for (uint corner = 0; corner < 4u; corner++)
            {
                corners[9u + corner] = corners[coarseCorners[corner]];
                values[9u + corner] = values[coarseCorners[corner]];
            }

            uint encodedClass = TerrainTransitionCells[cellCase];
            uint cellClass = encodedClass & 127u;
            uint counts = TerrainTransitionCells[512u + cellClass];
            uint vertices = counts >> 4u;
            uint indices = (counts & 15u) * 3u;
            float3 positions[12];
            for (uint vertex = 0; vertex < vertices; vertex++)
            {
                uint edge = TerrainTransitionCells[2584u + cellCase * 12u + vertex];
                uint a = (edge >> 4u) & 15u;
                uint b = edge & 15u;
                float3 point = TerrainIntersection(corners[a], corners[b], values[a], values[b]);
                float3 normal = TerrainSurfaceNormal(point);
                positions[vertex] = a >= 9u ? ShiftTerrainBoundary(point, normal) : point;
                StoreTerrainVertex(vertexCount + vertex, positions[vertex], point, normal);
            }
            for (uint index = 0; index < indices; index += 3u)
            {
                uint a = TerrainTransitionCells[568u + cellClass * 36u + index];
                uint b = TerrainTransitionCells[568u + cellClass * 36u + index + ((encodedClass & 128u) == 0 ? 1u : 2u)];
                uint c = TerrainTransitionCells[568u + cellClass * 36u + index + ((encodedClass & 128u) == 0 ? 2u : 1u)];
                // Primary positions collapse triangles where adjacent coarse faces meet.
                if (all(positions[a] == positions[b]) || all(positions[b] == positions[c]) || all(positions[c] == positions[a])) continue;
                TerrainIndices.Store3(indexCount * 4u, vertexCount + uint3(a, b, c));
                indexCount += 3u;
            }
            vertexCount += vertices;
        }
    }
}
