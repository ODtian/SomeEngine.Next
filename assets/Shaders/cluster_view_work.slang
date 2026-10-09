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

#endif
