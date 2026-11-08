#ifndef GPU_POSE_PHYSICS
#define GPU_POSE_PHYSICS
#include "gpu_pose_rig.slang"
#include "physics_body_prediction.slang"
#include "physics_xpbd_length_math.slang"
#include "physics_xpbd_bend_math.slang"

struct GpuPosePhysicsFrame
{
    float4x4 modelToWorld;
    float4x4 worldToModel;
    float duration; uint reset; uint substep; float targetDuration;
    uint2 number;
    uint2 time;
};
StructuredBuffer<GpuPosePhysicsFrame> GpuPosePhysicsFrames;
RWStructuredBuffer<float4x4> GpuPosePhysicsSources;
struct DevicePoseChainBodies : IDeviceBodySelection { bool contains(uint body) { return true; } };

void captureDevicePoseTargets(uint4 chain,GpuPosePhysicsFrame frame)
{
    DeviceXpbdLengthProjection<DevicePoseChainBodies> lengthProjection;
    DeviceXpbdBendProjection<DevicePoseChainBodies> bendProjection;
    DeviceXpbdStep projection = (DeviceXpbdStep)0;
    projection.number = frame.number;
    for (uint point = 0u; point < chain.y; point++)
    {
        uint4 bone = GpuPoseClips.Load4(chain.x + point * 32u);
        float4x4 source = GpuPoseModels[bone.x];
        GpuPosePhysicsSources[bone.w] = source;
        if (point > 0u)
        {
            uint4 parent = GpuPoseClips.Load4(chain.x + (point - 1u) * 32u);
            float3 rest = asfloat(GpuPoseClips.Load3(chain.x + point * 32u + 16u));
            uint lengthRow = GpuPoseClips.Load(chain.x + point * 32u + 28u);
            DeviceLengthConstraint constraint = DeviceLengths[lengthRow];
            constraint.restLength = length(mul(float4(rest, 0), mul(GpuPoseModels[parent.x], frame.modelToWorld)).xyz);
            DeviceLengths[lengthRow] = constraint;
            if (frame.reset != 0) lengthProjection.initialize(lengthRow, projection);
        }
        if (frame.reset != 0)
        {
            DeviceBodyState body = DeviceBodyStates[bone.w];
            body.position = mul(float4(source[3].xyz, 1), frame.modelToWorld).xyz;
            body.linearVelocity = float3(0); body.angularVelocity = float3(0);
            body.startCenterOfMass = bodyCenterOfMass(body); body.predictedCenterOfMass = body.startCenterOfMass;
            body.startRotation = body.rotation; body.predictedRotation = body.rotation;
            body.number = frame.number; body.time = frame.time; body.substep = int(frame.substep);
            DeviceBodyStates[bone.w] = body;
        }
    }
    if (frame.reset != 0)
        for (uint bend = 0u; bend < chain.w; bend++)
            bendProjection.initialize(GpuPoseClips.Load(chain.z + bend * 4u), projection);
}

void mapDevicePoseBones(uint4 chain,GpuPosePhysicsFrame frame)
{
    for (uint point = 0u; point < chain.y; point++)
    {
        uint4 bone = GpuPoseClips.Load4(chain.x + point * 32u);
        uint first = point + 1u < chain.y ? point : point - 1u;
        uint a = GpuPoseClips.Load(chain.x + first * 32u + 12u);
        uint b = GpuPoseClips.Load(chain.x + (first + 1u) * 32u + 12u);
        float3 sourceDirection = GpuPosePhysicsSources[b][3].xyz - GpuPosePhysicsSources[a][3].xyz;
        float3 worldDirection = DeviceBodyStates[b].position - DeviceBodyStates[a].position;
        float3 direction = mul(float4(worldDirection, 0), frame.worldToModel).xyz;
        float4 swing = DirectionRotation(sourceDirection, direction);
        float4 targetRotation = ModelRotation(mul(GpuPosePhysicsSources[bone.w], TrsMatrix(float3(0), swing, float3(1))));
        float4 currentRotation = ModelRotation(GpuPoseModels[bone.x]);
        float4 delta = MultiplyRotation(targetRotation, float4(-currentRotation.xyz, currentRotation.w));
        float3 current = GpuPoseModels[bone.x][3].xyz;
        float3 position = mul(float4(DeviceBodyStates[bone.w].position, 1), frame.worldToModel).xyz;
        RotateModelDescendants(bone.yz, current, delta);
        TranslateModelDescendants(bone.yz, position - current);
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void WriteDevicePoseBodyTargets(uint3 thread : SV_DispatchThreadID)
{
    uint count,stride; GpuPoseWork.GetDimensions(count,stride);
    if(thread.x>=count) return;
    uint4 work=GpuPoseWork[thread.x];
    if((GpuPoseControls[work.x].flags&1u)==0u) return;
    uint4 chain=GpuPoseClips.Load4(work.y);
    GpuPosePhysicsFrame frame=GpuPosePhysicsFrames[work.x];
    captureDevicePoseTargets(chain,frame);
    float damping=asfloat(GpuPoseClips.Load(work.y+32u));
    for(uint point=0;point<chain.y;point++)
    {
        uint bodyIndex=GpuPoseClips.Load(chain.x+point*32u+12u);
        DeviceBodyState body=DeviceBodyStates[bodyIndex];
        if(body.motion==1)
        {
            float3 target=mul(float4(GpuPosePhysicsSources[bodyIndex][3].xyz,1),frame.modelToWorld).xyz;
            body.linearVelocity=(target-body.position)/frame.targetDuration;
        }
        else body.linearVelocity*=exp(-damping*frame.duration);
        DeviceBodyStates[bodyIndex]=body;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void MapDevicePoseBodyBones(uint3 thread : SV_DispatchThreadID)
{
    uint count,stride; GpuPoseWork.GetDimensions(count,stride);
    if(thread.x>=count) return;
    uint4 work=GpuPoseWork[thread.x];
    if((GpuPoseControls[work.x].flags&1u)==0u) return;
    mapDevicePoseBones(GpuPoseClips.Load4(work.y),GpuPosePhysicsFrames[work.x]);
}

#include "physics_xpbd_chains.slang"
struct DevicePoseChainInput : IDeviceXpbdChain
{
    bool active(uint index)
    {
        uint count,stride; GpuPoseWork.GetDimensions(count,stride);
        return index<count && (GpuPoseControls[GpuPoseWork[index].x].flags&1u)!=0;
    }
    DeviceXpbdChainStep step(uint index)
    {
        uint4 work=GpuPoseWork[index];
        GpuPosePhysicsFrame frame=GpuPosePhysicsFrames[work.x];
        DeviceXpbdChainStep result;
        result.duration=frame.duration; result.maximumStep=asfloat(GpuPoseClips.Load(work.y+28));
        result.damping=asfloat(GpuPoseClips.Load(work.y+32)); result.iterations=GpuPoseClips.Load(work.y+36);
        result.acceleration=asfloat(GpuPoseClips.Load3(work.y+16));
        result.number=frame.number; result.time=frame.time; result.substep=frame.substep;
        return result;
    }
    uint bodyCount(uint index) { return GpuPoseClips.Load(GpuPoseWork[index].y+4); }
    uint body(uint index,uint point) { return GpuPoseClips.Load(GpuPoseClips.Load(GpuPoseWork[index].y)+point*32+12); }
    float3 target(uint index,uint point)
    {
        uint4 work=GpuPoseWork[index];
        return mul(float4(GpuPosePhysicsSources[body(index,point)][3].xyz,1),GpuPosePhysicsFrames[work.x].modelToWorld).xyz;
    }
    uint lengthRow(uint index,uint point) { return GpuPoseClips.Load(GpuPoseClips.Load(GpuPoseWork[index].y)+point*32+28); }
    uint bendCount(uint index) { return GpuPoseClips.Load(GpuPoseWork[index].y+12); }
    uint bendRow(uint index,uint bend) { return GpuPoseClips.Load(GpuPoseClips.Load(GpuPoseWork[index].y+8)+bend*4); }
};
[shader("compute")][numthreads(64,1,1)]
void SolveDevicePoseBodies(uint3 thread:SV_DispatchThreadID)
{ DevicePoseChainInput input; solveDeviceXpbdChain(thread.x,input); }

[shader("compute")][numthreads(64,1,1)]
void CaptureDevicePoseBodies(uint3 thread:SV_DispatchThreadID)
{
    DevicePoseChainInput input;
    if (!input.active(thread.x)) return;
    uint4 work=GpuPoseWork[thread.x];
    captureDevicePoseTargets(GpuPoseClips.Load4(work.y),GpuPosePhysicsFrames[work.x]);
}

[shader("compute")][numthreads(64,1,1)]
void SimulateDevicePoseChain(uint3 thread:SV_DispatchThreadID)
{
    DevicePoseChainInput input;
    if (!input.active(thread.x)) return;
    uint4 work=GpuPoseWork[thread.x];
    uint4 chain=GpuPoseClips.Load4(work.y);
    GpuPosePhysicsFrame frame=GpuPosePhysicsFrames[work.x];
    captureDevicePoseTargets(chain,frame);
    solveDeviceXpbdChain(thread.x,input);
    mapDevicePoseBones(chain,frame);
}
#endif
