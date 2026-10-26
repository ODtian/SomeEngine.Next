#ifndef PHYSICS_BODY_DYNAMICS
#define PHYSICS_BODY_DYNAMICS
#include "physics_body_prediction.slang"
#include "physics_body_selection.slang"

// Newton v1.7.0 body-space gyroscopic torque; copyright (c) 2025 The Newton Developers.
// Source and license: ThirdParty/Newton/NOTICE and the repository LICENSE.
[shader("compute")][numthreads(64, 1, 1)]
void ApplyDeviceGyroscopicTorque(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= BodyStep.count) return;
    uint index = DeviceBodyIndices[id.x];
    DeviceBodyState body = DeviceBodyStates[index];
    if (body.motion != 2 || !bodyHasRotation(body)) return;
    float3 velocity = bodyRotate(bodyRotationConjugate(body.rotation), body.angularVelocity);
    float3 torque = -cross(velocity, bodyLocalInertia(body, velocity));
    velocity += bodyLocalInverseInertia(body, torque) * BodyStep.duration;
    body.angularVelocity = bodyRotate(body.rotation, velocity);
    DeviceBodyStates[index] = body;
}

void predictDeviceBody(uint index, bool advance, bool resume)
{
    DeviceBodyStates[index] = predictDeviceBodyState(DeviceBodyStates[index], BodyStep, advance, resume);
}

void updateDeviceBodyVelocity(uint index)
{
    DeviceBodyStates[index] = updateDeviceBodyVelocityState(DeviceBodyStates[index], BodyStep.duration);
}

[shader("compute")][numthreads(64, 1, 1)]
void PredictDeviceBodies(uint3 id : SV_DispatchThreadID)
{ if (id.x < BodyStep.count) predictDeviceBody(DeviceBodyIndices[id.x], true, false); }
[shader("compute")][numthreads(64, 1, 1)]
void UpdateDeviceBodyVelocities(uint3 id : SV_DispatchThreadID)
{ if (id.x < BodyStep.count) updateDeviceBodyVelocity(DeviceBodyIndices[id.x]); }
#endif
