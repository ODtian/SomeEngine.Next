#include "cluster_view.slang"
#include "cluster_page_requests.slang"

StructuredBuffer<ClusterViewUniforms> Views;
RWByteAddressBuffer PageFaultBuffer;

[shader("compute")]
[numthreads(64, 1, 1)]
void CompleteClusterPageRequests(uint3 id : SV_DispatchThreadID)
{
    uint capacity = Views[0].MaxPageFaults;
    uint count = min(PageFaultBuffer.Load(0u), capacity);
    if (id.x < count)
    {
        uint offset = 4u + id.x * 16u;
        uint page = PageFaultBuffer.Load(offset + 4u);
        uint priority = PageFaultBuffer.Load(ClusterPageRequestPriorityOffset(capacity, page));
        PageFaultBuffer.Store(offset + 12u, priority);
    }
    uint demandCount = min(PageFaultBuffer.Load(ClusterPageDemandCountOffset(capacity)), capacity);
    if (id.x < demandCount)
    {
        uint offset = ClusterPageDemandOffset(capacity, id.x);
        uint page = PageFaultBuffer.Load(offset);
        uint priority = PageFaultBuffer.Load(ClusterPageRequestPriorityOffset(capacity, page));
        PageFaultBuffer.Store(offset + 4u, priority);
    }
}

