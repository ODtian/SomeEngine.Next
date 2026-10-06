#include "resource_effects.slang"
#define VSM_WHOLE_PAGE_SAMPLING 1
#include "vsm_sampling.slang"
#include "vsm_work_throttle.slang"

struct VirtualShadowWorkThrottleUniforms
{
    uint ViewCount;
    uint HasPreviousWork;
    float LoadBudget;
    float DynamicResolutionStrength;
    float HistoryWeight;
    float MaxDirectionalBias;
    float MaxLocalBias;
    float MaxGlobalBias;
};
ConstantBuffer<VirtualShadowWorkThrottleUniforms> WorkThrottle;
StructuredBuffer<uint> PreviousWorkCounts;
StructuredBuffer<float> PreviousWorkPressure;
StructuredBuffer<uint> PreviousWorkViewIndices;
StructuredBuffer<float2> FramePageBias;
[ResourceEffect(ResourceEffects.ReadWrite, ResourceQualifiers.None)]
RWStructuredBuffer<VirtualShadowMapParameters> WorkShadowViews;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<float> FrameWorkPressure;

[shader("compute")]
[numthreads(1, 1, 1)]
void UpdateVirtualShadowWorkThrottle()
{
    uint2 total = 0u.xx;
    uint2 maximum = 0u.xx;
    uint previousCount, stride;
    PreviousWorkCounts.GetDimensions(previousCount, stride);
    if (WorkThrottle.HasPreviousWork != 0u)
    {
        for (uint offset = 1u; offset + 1u < previousCount; offset += 2u)
            total += uint2(PreviousWorkCounts[offset], PreviousWorkCounts[offset + 1u]);
        for (uint index = 0u; index < WorkThrottle.ViewCount; index++)
        {
            uint previous = PreviousWorkViewIndices[index];
            if (previous != 0xFFFFFFFFu)
                maximum = max(maximum, uint2(PreviousWorkCounts[1u + previous * 2u],
                    PreviousWorkCounts[2u + previous * 2u]));
        }
    }
    float global = WorkThrottle.HasPreviousWork != 0u
        ? CalculateVirtualShadowWorkPressure(total, PreviousWorkPressure[0],
            WorkThrottle.LoadBudget, WorkThrottle.DynamicResolutionStrength) : 0.0;
    uint pressureCount;
    FrameWorkPressure.GetDimensions(pressureCount, stride);
    for (uint index = 0u; index < pressureCount; index++) FrameWorkPressure[index] = 0.0;
    FrameWorkPressure[0] = global;
    for (uint index = 0u; index < WorkThrottle.ViewCount; index++)
    {
        uint previous = PreviousWorkViewIndices[index];
        uint2 clusters = 0u.xx;
        float history = 0.0;
        if (WorkThrottle.HasPreviousWork != 0u && previous != 0xFFFFFFFFu)
        {
            clusters = uint2(PreviousWorkCounts[1u + previous * 2u], PreviousWorkCounts[2u + previous * 2u]);
            history = PreviousWorkPressure[1u + previous];
        }
        float pressure = CalculateVirtualShadowViewWorkPressure(clusters, maximum, global,
            history, WorkThrottle.HistoryWeight);
        FrameWorkPressure[1u + index] = pressure;
        VirtualShadowMapParameters view = WorkShadowViews[index];
        bool directional = view.LightKind == VSM_LIGHT_DIRECTIONAL;
        float pageBias = directional ? FramePageBias[0].x : FramePageBias[0].y;
        float limit = directional ? WorkThrottle.MaxDirectionalBias : WorkThrottle.MaxLocalBias;
        view.ResolutionLodBias += pressure * max(0.0, min(WorkThrottle.MaxGlobalBias, limit) - pageBias);
        WorkShadowViews[index] = view;
    }
}
