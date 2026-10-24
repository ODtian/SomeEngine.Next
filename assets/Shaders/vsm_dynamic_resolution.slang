#include "resource_effects.slang"

struct VirtualShadowDynamicResolutionUniforms
{
    uint MaxPhysicalPages;
    uint TargetRasterWork;
    float PagePoolLoadStart;
    float MaxDirectionalLodBias;
    float MaxLocalLodBias;
    uint Enabled;
    uint Pad0;
    uint Pad1;
};

ConstantBuffer<VirtualShadowDynamicResolutionUniforms> DynamicResolution;
StructuredBuffer<uint> FreePageCount;
StructuredBuffer<uint> PolicyRasterWorkCount;
StructuredBuffer<uint> AllocationFailures;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
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
    uint freePages = min(FreePageCount[0], maxPages);
    float pageLoad = 1.0 - float(freePages) / float(maxPages);
    float poolRange = max(1.0 - DynamicResolution.PagePoolLoadStart, 1e-4);
    float poolPressure = saturate(
        (pageLoad - DynamicResolution.PagePoolLoadStart) / poolRange);

    uint targetWork = max(DynamicResolution.TargetRasterWork, 1u);
    float workRatio = max(float(PolicyRasterWorkCount[0]) / float(targetWork), 1.0);
    float workBias = log2(workRatio);
    bool allocationFailed = AllocationFailures[0] != 0u;

    float directional = max(
        workBias,
        poolPressure * DynamicResolution.MaxDirectionalLodBias);
    float local = max(
        workBias,
        poolPressure * DynamicResolution.MaxLocalLodBias);
    if (allocationFailed)
    {
        directional = DynamicResolution.MaxDirectionalLodBias;
        local = DynamicResolution.MaxLocalLodBias;
    }

    DynamicResolutionBias[0] = float2(
        clamp(directional, 0.0, DynamicResolution.MaxDirectionalLodBias),
        clamp(local, 0.0, DynamicResolution.MaxLocalLodBias));
}
