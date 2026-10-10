#ifndef CLUSTER_PAGE_REQUESTS_SLANG
#define CLUSTER_PAGE_REQUESTS_SLANG

uint PackClusterStreamingPriority(float projectedError, float lodThreshold, uint category)
{
    float priority = projectedError / max(lodThreshold, 1e-12);
    return clamp((category << 30u) | (asuint(priority) >> 2u), 1u, 0xffffffe0u);
}

uint ClusterPageDemandCountOffset(uint capacity)
{
    return 8u + capacity * 16u;
}

uint ClusterPageDemandOffset(uint capacity, uint index)
{
    return ClusterPageDemandCountOffset(capacity) + 4u + index * 8u;
}

uint ClusterPageRequestPriorityOffset(uint capacity, uint page)
{
    // Faults, the work result, and compact demand records precede priorities.
    return ClusterPageDemandOffset(capacity, capacity) + page * 4u;
}

#endif
