#ifndef GPU_POSE_TRANSLATION
#define GPU_POSE_TRANSLATION
#include "gpu_pose_sampling.slang"
StructuredBuffer<float4> GpuPoseOffsets;

void TranslateModelDescendants(uint2 range, float3 offset)
{
    for (uint index = 0u; index < range.y; index++)
    {
        uint bone = GpuPoseClips.Load(range.x + index * 4u);
        float4x4 model = GpuPoseModels[bone];
        model[3].xyz += offset;
        GpuPoseModels[bone] = model;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void TranslatePose(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint4 work = GpuPoseWork[thread.x];
    if ((GpuPoseControls[work.x].flags & 1u) == 0u) return;
    TranslateModelDescendants(GpuPoseClips.Load2(work.y), GpuPoseOffsets[work.x].xyz);
}

#endif
