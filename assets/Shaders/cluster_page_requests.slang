#ifndef CLUSTER_PAGE_REQUESTS_SLANG
#define CLUSTER_PAGE_REQUESTS_SLANG

uint PackClusterStreamingPriority(float projectedError, float lodThreshold, uint category)
{
    float priority = projectedError / max(lodThreshold, 1e-12);
    return clamp((category << 30u) | (asuint(priority) >> 2u), 1u, 0xffffffe0u);
}

uint ClusterPageRequestPriorityOffset(uint capacity, uint page)
{
    // Count, fixed request records, and the completed-frame work result precede priorities.
    return 8u + capacity * 16u + page * 4u;
}

#endif
