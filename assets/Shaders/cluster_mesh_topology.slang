#ifndef CLUSTER_MESH_TOPOLOGY_SLANG
#define CLUSTER_MESH_TOPOLOGY_SLANG

// Packed triangle indices are uint8, independent of the importer's default
// cluster size. A 32-triangle batch references at most 96 distinct vertices.
struct ClusterMeshVertexMask
{
    uint Words[8];
    uint2 PrefixBytes;
};

groupshared uint HardwareRasterVertexWords[8];

ClusterMeshVertexMask BuildClusterMeshVertexMask(uint3 triangle, bool active, uint lane)
{
    ClusterMeshVertexMask result;
    uint lastWord = WaveActiveMax(active ? max(triangle.x, max(triangle.y, triangle.z)) : 0u) / 32u;
    [unroll]
    for (uint word = 0u; word < 8u; word++)
    {
        uint mask = 0u;
        [unroll]
        for (uint corner = 0u; corner < 3u; corner++)
            if (active && triangle[corner] / 32u == word)
                mask |= 1u << (triangle[corner] & 31u);
        result.Words[word] = word <= lastWord ? WaveActiveBitOr(mask) : 0u;
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
    // Every prefix counts at most 224 bits and therefore fits in one byte.
    // Compute it once per group rather than scanning all words for every index.
    result.PrefixBytes = 0u.xx;
    uint prefix = 0u;
    [unroll]
    for (uint word = 0u; word < 8u; word++)
    {
        result.PrefixBytes[word / 4u] |= prefix << ((word & 3u) * 8u);
        prefix += countbits(result.Words[word]);
    }
    return result;
}

uint ClusterMeshVertexCount(ClusterMeshVertexMask mask)
{
    return (mask.PrefixBytes.y >> 24u) + countbits(mask.Words[7]);
}

uint ClusterMeshVertexRank(ClusterMeshVertexMask mask, uint index)
{
    uint word = index / 32u;
    uint prefix = (mask.PrefixBytes[word / 4u] >> ((word & 3u) * 8u)) & 255u;
    uint bit = index & 31u;
    return prefix + countbits(mask.Words[word] & ((1u << bit) - 1u));
}

#endif
