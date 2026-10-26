#include "physics_body_math.slang"
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

[shader("compute")][numthreads(64, 1, 1)]
void PredictDeviceBodies(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= BodyStep.count) return;
    uint index = DeviceBodyIndices[id.x];
    DeviceBodyState body = DeviceBodyStates[index];
    body.startCenterOfMass = bodyCenterOfMass(body);
    body.startRotation = body.rotation;
    body.number = BodyStep.number;
    body.substep = int(BodyStep.substep);
    body.time = BodyStep.time;
    if (body.motion == 2) body.linearVelocity += BodyStep.acceleration * BodyStep.duration;
    if (body.motion != 0)
    {
        body.rotation = bodyIntegrateRotation(body.rotation, body.angularVelocity, BodyStep.duration);
        bodySetCenterOfMass(body, body.startCenterOfMass + body.linearVelocity * BodyStep.duration);
    }
    body.predictedCenterOfMass = bodyCenterOfMass(body);
    body.predictedRotation = body.rotation;
    DeviceBodyStates[index] = body;
}

[shader("compute")][numthreads(64, 1, 1)]
void UpdateDeviceBodyVelocities(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= BodyStep.count) return;
    uint index = DeviceBodyIndices[id.x];
    DeviceBodyState body = DeviceBodyStates[index];
    if (body.motion == 0) return;
    body.linearVelocity = (bodyCenterOfMass(body) - body.startCenterOfMass) / BodyStep.duration;
    body.angularVelocity = bodyRotationVelocity(body.startRotation, body.rotation, BodyStep.duration);
    DeviceBodyStates[index] = body;
}
