#ifndef CLUSTER_TRAVERSAL_SEED_INPUT_SLANG
#define CLUSTER_TRAVERSAL_SEED_INPUT_SLANG

struct ClusterTraversalSeedUniforms
{
    uint ViewCount;
    uint ViewIndexMode;
    uint MaxNodes;
    uint ArgsOffset;
    uint UseInstanceGroups;
    uint InvocationCountIndex;
    uint2 Padding;
};

uint ResolveClusterTraversalViewCount(ClusterTraversalSeedUniforms seed, StructuredBuffer<uint> counts)
{
    if (seed.ViewIndexMode == 0u) return seed.ViewCount;
    return min(counts[seed.InvocationCountIndex], seed.ViewCount);
}

#endif
