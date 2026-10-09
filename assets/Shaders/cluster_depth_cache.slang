#ifndef CLUSTER_DEPTH_CACHE_SLANG
#define CLUSTER_DEPTH_CACHE_SLANG

StructuredBuffer<uint> RasterTargetDepthCacheClasses;

uint LoadTargetDepthCacheClass(uint instanceId)
{
    return (RasterTargetDepthCacheClasses[instanceId >> 5u] >> (instanceId & 31u)) & 1u;
}

#endif
