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
    float duration; uint reset; uint substep; uint padding;
    uint2 number;
    uint2 time;
};
StructuredBuffer<GpuPosePhysicsFrame> GpuPosePhysicsFrames;
RWStructuredBuffer<float4x4> GpuPosePhysicsSources;
struct DevicePoseChainBodies : IDeviceBodySelection { bool contains(uint body) { return true; } };

[shader("compute")]
[numthreads(64, 1, 1)]
void SimulateDevicePoseChain(uint3 thread : SV_DispatchThreadID)
{
    uint count, stride;
    GpuPoseWork.GetDimensions(count, stride);
    if (thread.x >= count) return;
    uint4 work = GpuPoseWork[thread.x];
    if ((GpuPoseControls[work.x].flags & 1u) == 0u) return;
    uint4 chain = GpuPoseClips.Load4(work.y);
    float3 acceleration = asfloat(GpuPoseClips.Load3(work.y + 16u));
    float maximumStep = asfloat(GpuPoseClips.Load(work.y + 28u));
    float damping = asfloat(GpuPoseClips.Load(work.y + 32u));
    uint iterations = GpuPoseClips.Load(work.y + 36u);
    GpuPosePhysicsFrame frame = GpuPosePhysicsFrames[work.x];
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
    uint steps = uint(ceil(frame.duration / maximumStep));
    for (uint step = 0u; step < steps; step++)
    {
        float h = frame.duration / float(steps);
        DeviceBodyStep context = (DeviceBodyStep)0;
        context.duration = h; context.acceleration = acceleration;
        context.number = frame.number; context.time = frame.time;
        context.substep = uint(frame.substep) * steps + step;
        for (uint point = 0u; point < chain.y; point++)
        {
            uint4 bone = GpuPoseClips.Load4(chain.x + point * 32u);
            DeviceBodyState body = DeviceBodyStates[bone.w];
            if (body.motion == 1)
            {
                float3 anchor = mul(float4(GpuPosePhysicsSources[bone.w][3].xyz, 1), frame.modelToWorld).xyz;
                body.linearVelocity = (anchor - body.position) / (float(steps - step) * h);
            }
            else body.linearVelocity *= exp(-damping * h);
            DeviceBodyStates[bone.w] = predictDeviceBodyState(body, context, true, false);
        }
        projection.duration = h;
        for (uint point = 1u; point < chain.y; point++)
            lengthProjection.initialize(GpuPoseClips.Load(chain.x + point * 32u + 28u), projection);
        for (uint bend = 0u; bend < chain.w; bend++)
            bendProjection.initialize(GpuPoseClips.Load(chain.z + bend * 4u), projection);
        for (uint iteration = 0u; iteration < iterations; iteration++)
        {
            for (uint point = 1u; point < chain.y; point++)
                lengthProjection.project(GpuPoseClips.Load(chain.x + point * 32u + 28u), projection);
            for (uint bend = 0u; bend < chain.w; bend++)
                bendProjection.project(GpuPoseClips.Load(chain.z + bend * 4u), projection);
        }
        for (uint point = 0u; point < chain.y; point++)
        {
            uint body = GpuPoseClips.Load(chain.x + point * 32u + 12u);
            DeviceBodyStates[body] = updateDeviceBodyVelocityState(DeviceBodyStates[body], h);
        }
    }
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
#endif
