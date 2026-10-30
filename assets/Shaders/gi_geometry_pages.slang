#ifndef GI_GEOMETRY_PAGES_SLANG
#define GI_GEOMETRY_PAGES_SLANG

#include "cluster_common.slang"
#include "cluster_page_requests.slang"

ByteAddressBuffer GiHierarchy;
RWByteAddressBuffer GiPageRequests;

bool GetGiTrianglePage(uint3 pointer, uint capacity, out uint page)
{
    page = 0u;
    uint node = pointer.x / 240u;
    uint child = (pointer.x % 240u - 140u) / 16u;
    ClusterHierarchySlice slice = LoadClusterNode(GiHierarchy, node, child);
    for (uint pageIndex = ClusterSliceFirstPage(slice); pageIndex < ClusterSliceFirstPage(slice) + ClusterSlicePageCount(slice); pageIndex++)
    {
        uint previous;
        GiPageRequests.InterlockedMax(ClusterPageRequestPriorityOffset(capacity, pageIndex),
            PackClusterStreamingPriority(1.0, 1.0, 2u), previous);
        if (previous == 0u)
        {
            uint demand;
            GiPageRequests.InterlockedAdd(ClusterPageDemandCountOffset(capacity), 1u, demand);
            if (demand < capacity) GiPageRequests.Store2(ClusterPageDemandOffset(capacity, demand), uint2(pageIndex, 0u));
            if (!ClusterSliceAvailable(slice))
            {
                uint at;
                GiPageRequests.InterlockedAdd(0u, 1u, at);
                if (at < capacity) GiPageRequests.Store4(4u + at * 16u, uint4(node * 4u + child, pageIndex, 1u, 0u));
            }
        }
    }
    if (!ClusterSliceAvailable(slice)) return false;
    page = ClusterSlicePageOffset(slice);
    return true;
}

#endif
