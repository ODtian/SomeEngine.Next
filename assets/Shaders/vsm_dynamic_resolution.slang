#include "resource_effects.slang"
#define VSM_PAGE_LIST_READ_ONLY 1
#include "vsm_page_lists.slang"

struct VirtualShadowDynamicResolutionUniforms
{
    uint MaxPhysicalPages;
    uint PadRasterWork;
    float PagePoolLoadStart;
    float MaxDirectionalLodBias;
    float MaxLocalLodBias;
    uint Enabled;
    uint Pad0;
    uint Pad1;
};

ConstantBuffer<VirtualShadowDynamicResolutionUniforms> DynamicResolution;
StructuredBuffer<uint> AllocationFailures;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<float4> DynamicResolutionBias;

[shader("compute")]
[numthreads(1, 1, 1)]
void UpdateVirtualShadowDynamicResolution(uint3 threadId : SV_DispatchThreadID)
{
    if (threadId.x != 0u)
        return;

    if (DynamicResolution.Enabled == 0u)
    {
        DynamicResolutionBias[0] = 0.0.xxxx;
        return;
    }

    uint maxPages = max(DynamicResolution.MaxPhysicalPages, 1u);
    uint availablePages = ShadowPageListCount(VSM_PAGE_LIST_AVAILABLE, DynamicResolution.MaxPhysicalPages);
    uint freePages = min(availablePages, maxPages);
    float pageLoad = float(maxPages - freePages + AllocationFailures[0]) / float(maxPages);
    float allocationRatio = DynamicResolution.PagePoolLoadStart > 0.0
        ? pageLoad / DynamicResolution.PagePoolLoadStart : 1.0;

    float4 state = DynamicResolutionBias[0];
    float2 limits = float2(DynamicResolution.MaxDirectionalLodBias, DynamicResolution.MaxLocalLodBias);
    float2 target = clamp(state.xy + log2(max(allocationRatio, 1e-6)), 0.0.xx, limits);
    if (allocationRatio > 1.0)
    {
        state.xy = lerp(state.xy, target, 0.5);
        state.z = 0.0;
    }
    else
    {
        // React quickly to overload. Restore detail after ten frames below
        // the allocation budget, using a slower exponential response.
        state.z = min(state.z + 1.0, 11.0);
        if (state.z > 10.0) state.xy = lerp(state.xy, target, 0.1);
    }
    DynamicResolutionBias[0] = state;
}
