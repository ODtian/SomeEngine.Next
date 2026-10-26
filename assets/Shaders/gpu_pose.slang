#include "gpu_pose_rig.slang"

StructuredBuffer<uint> GpuPoseSections;
RWStructuredBuffer<float4x4> GpuPosePrevious;
StructuredBuffer<float4x4> GpuPoseActorFrames;

[shader("compute")]
[numthreads(64, 1, 1)]
void LoadPoseHistory(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseModels.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint section = GpuPoseSections[thread.x];
    float4x4 current = GpuPoseHistoryCurrent[thread.x];
    GpuPoseModels[thread.x] = current;
    GpuPosePrevious[thread.x] = section != 0xffffffffu && (GpuPoseControls[section].flags & 1025u) != 0
        ? current : GpuPoseHistoryPrevious[thread.x];
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ReframeDevicePose(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseModels.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint section = GpuPoseSections[thread.x];
    if (section != 0xffffffffu && (GpuPoseControls[section].flags & 2048u) != 0)
        GpuPoseModels[thread.x] = mul(GpuPoseModels[thread.x], GpuPoseActorFrames[section]);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void StorePoseHistory(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseModels.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint section = GpuPoseSections[thread.x];
    if (section == 0xffffffffu) return;
    uint flags = GpuPoseControls[section].flags;
    // Evaluated and held poses both advance the displayed history at their accepted input.
    if ((flags & 1025u) == 0) return;
    if ((flags & 2u) != 0) GpuPosePrevious[thread.x] = GpuPoseModels[thread.x];
    GpuPoseHistoryPrevious[thread.x] = GpuPosePrevious[thread.x];
    GpuPoseHistoryCurrent[thread.x] = GpuPoseModels[thread.x];
}
