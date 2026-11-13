__exported import vsm_prefilter_request_indices;

[shader("compute")]
[numthreads(64,1,1)]
void PropagateVirtualShadowFilterRequests(uint3 thread : SV_DispatchThreadID)
{
    uint requestIndex = thread.x + thread.y * (65535u * 64u);
    if (requestIndex >= FilterRequestArguments.Load(12u)) return;
    PropagateVirtualShadowFilterRequest(FilterRequestPages[requestIndex]);
}
