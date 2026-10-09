#ifndef CLUSTER_VIEW_WORK_SLANG
#define CLUSTER_VIEW_WORK_SLANG

// A traversal item retains its 32-view mask. Threads share each item's view
// tests; a single view uses all 64 threads for different instances or children.
uint ClusterParallelViewLanes(uint viewCount)
{
    uint count = min(max(viewCount, 1u), 16u);
    return count == 1u ? 1u : 1u << (firstbithigh(count - 1u) + 1u);
}

uint ClusterViewWorkGroups(uint viewCount)
{
    uint tail = viewCount & 31u;
    return (viewCount >> 5u) * 16u + (tail == 0u ? 0u : ClusterParallelViewLanes(tail));
}

uint ClusterViewWorkGroupIndex(uint3 groupID)
{
    return groupID.y + groupID.z * 65535u;
}

uint ClusterViewWaveSpan(uint viewLanes, uint stride)
{
    return min(viewLanes, max(1u, WaveGetLaneCount() / stride));
}

uint ClusterViewWaveFirstLane(uint viewLanes, uint stride)
{
    uint lane = WaveGetLaneIndex();
    return lane - ((lane / stride) % ClusterViewWaveSpan(viewLanes, stride)) * stride;
}

uint MergeClusterViewMaskWave(uint mask, uint viewLanes, uint stride)
{
    uint first = ClusterViewWaveFirstLane(viewLanes, stride);
    uint count = ClusterViewWaveSpan(viewLanes, stride);
    uint result = 0u;
    for (uint view = 0u; view < count; view++)
        result |= WaveReadLaneAt(mask, first + view * stride);
    return result;
}

uint MergeClusterViewPriorityWave(uint priority, uint viewLanes, uint stride)
{
    uint first = ClusterViewWaveFirstLane(viewLanes, stride);
    uint count = ClusterViewWaveSpan(viewLanes, stride);
    uint result = 1u;
    for (uint view = 0u; view < count; view++)
        result = max(result, WaveReadLaneAt(priority, first + view * stride));
    return result;
}

uint DistributeClusterViewMask(uint mask, uint viewLane, uint viewLanes)
{
    uint result = 0u;
    uint ordinal = 0u;
    while (mask != 0u)
    {
        uint bit = firstbitlow(mask);
        mask &= mask - 1u;
        if ((ordinal++ & (viewLanes - 1u)) == viewLane) result |= 1u << bit;
    }
    return result;
}

#endif
