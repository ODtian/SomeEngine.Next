#include "resource_effects.slang"
#define VSM_PAGE_LIST_READ_ONLY 1

struct VirtualShadowDynamicResolutionUniforms
{
    uint MaxPhysicalPages;
    uint PadRasterWork;
    float PagePoolLoadStart;
    float MaxLodBias;
    uint PadLodBias;
    uint Enabled;
    uint Pad0;
    uint Pad1;
};

ConstantBuffer<VirtualShadowDynamicResolutionUniforms> DynamicResolution;
#include "vsm_page_lists.slang"
StructuredBuffer<uint> AllocationFailures;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<float2> DynamicResolutionBias;

[shader("compute")]
[numthreads(1, 1, 1)]
void UpdateVirtualShadowDynamicResolution(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x != 0u)
        return;

    if (DynamicResolution.Enabled == 0u)
    {
        DynamicResolutionBias[0] = 0.0.xx;
        return;
    }

    uint maxPages = max(DynamicResolution.MaxPhysicalPages, 1u);
    uint availablePages = ShadowPageListCount(VSM_PAGE_LIST_AVAILABLE, DynamicResolution.MaxPhysicalPages);
    uint freePages = min(availablePages, maxPages);
    float pageLoad = float(maxPages - freePages + AllocationFailures[0]) / float(maxPages);
    float allocationRatio = DynamicResolution.PagePoolLoadStart > 0.0
        ? pageLoad / DynamicResolution.PagePoolLoadStart : 1.0;

    float2 state = DynamicResolutionBias[0];
    float target = clamp(state.x + log2(max(allocationRatio, 1e-6)), 0.0, DynamicResolution.MaxLodBias);
    if (allocationRatio > 1.0)
    {
        state.x = lerp(state.x, target, 0.5);
        state.y = 0.0;
    }
    else
    {
        // React quickly to overload. Restore detail after ten frames below
        // the allocation budget, using a slower exponential response.
        state.y = min(state.y + 1.0, 11.0);
        if (state.y > 10.0) state.x = lerp(state.x, target, 0.1);
    }
    DynamicResolutionBias[0] = state;
}
