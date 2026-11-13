__exported import vsm_prefilter_request_common;

// These views read the same request buffers that PublishVirtualShadowRequest appends to.
RWStructuredBuffer<uint> FilterRequestPages;
RWStructuredBuffer<uint> FilterRequestCount;
RWByteAddressBuffer FilterRequestDispatchArgs;
ByteAddressBuffer FilterRequestArguments;

[shader("compute")]
[numthreads(1,1,1)]
void CaptureVirtualShadowFilterRequests()
{
    uint count = min(FilterRequestCount[0], FilterRequests.PageTableEntryCount);
    uint groups = (count + 63u) / 64u;
    FilterRequestDispatchArgs.Store4(0u, uint4(min(groups, 65535u), max((groups + 65534u) / 65535u, 1u), 1u, count));
}

[shader("compute")]
[numthreads(64,1,1)]
void PropagateVirtualShadowFilterRequestIndices(uint3 thread : SV_DispatchThreadID)
{
    uint requestIndex = thread.x + thread.y * (65535u * 64u);
    if (requestIndex >= FilterRequestArguments.Load(12u)) return;
    PropagateVirtualShadowFilterRequest(FilterRequestPages[requestIndex]);
}
