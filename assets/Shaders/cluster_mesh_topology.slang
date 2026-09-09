#ifndef CLUSTER_MESH_TOPOLOGY_SLANG
#define CLUSTER_MESH_TOPOLOGY_SLANG

// Packed triangle indices are uint8, independent of the importer's default
// cluster size. A 32-triangle batch references at most 96 distinct vertices.
struct ClusterMeshVertexMask
{
    uint Words[8];
};

groupshared uint HardwareRasterVertexWords[8];

ClusterMeshVertexMask BuildClusterMeshVertexMask(uint3 triangle, bool active, uint lane)
{
    ClusterMeshVertexMask result;
    [unroll]
    for (uint word = 0u; word < 8u; word++)
    {
        uint mask = 0u;
        [unroll]
        for (uint corner = 0u; corner < 3u; corner++)
            if (active && triangle[corner] / 32u == word)
                mask |= 1u << (triangle[corner] & 31u);
        result.Words[word] = WaveActiveBitOr(mask);
    }
    if (WaveGetLaneCount() < 32u)
    {
        if (lane < 8u) HardwareRasterVertexWords[lane] = 0u;
        GroupMemoryBarrierWithGroupSync();
        if (WaveIsFirstLane())
        {
            [unroll]
            for (uint word = 0u; word < 8u; word++)
                InterlockedOr(HardwareRasterVertexWords[word], result.Words[word]);
        }
        GroupMemoryBarrierWithGroupSync();
        [unroll]
        for (uint word = 0u; word < 8u; word++)
            result.Words[word] = HardwareRasterVertexWords[word];
    }
    return result;
}

uint ClusterMeshVertexCount(ClusterMeshVertexMask mask)
{
    uint result = 0u;
    [unroll]
    for (uint word = 0u; word < 8u; word++) result += countbits(mask.Words[word]);
    return result;
}

uint ClusterMeshVertexRank(ClusterMeshVertexMask mask, uint index)
{
    uint result = 0u;
    [unroll]
    for (uint word = 0u; word < 8u; word++)
        if (word < index / 32u) result += countbits(mask.Words[word]);
    uint bit = index & 31u;
    return result + countbits(mask.Words[index / 32u] & ((1u << bit) - 1u));
}

#endif
