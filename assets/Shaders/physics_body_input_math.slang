#ifndef PHYSICS_BODY_INPUT_MATH
#define PHYSICS_BODY_INPUT_MATH
#include "physics_body_math.slang"
#include "physics_body_input_types.slang"
bool deviceBodyInputMatches(DeviceBodyState body, DeviceBodyInput input)
{ return body.entityIndex == input.entityIndex && body.entityGeneration == input.entityGeneration; }
void applyDeviceBodyInput(inout DeviceBodyState body, DeviceBodyInput input)
{
    if (body.motion == 0) return;
    if (body.motion == 2)
    {
        body.linearVelocity += input.linearImpulse * body.inverseMass;
        body.angularVelocity += bodyInverseInertia(body, input.angularImpulse);
    }
    if ((input.kinds & 2u) != 0)
    { body.linearVelocity = input.targetLinearVelocity; body.angularVelocity = input.targetAngularVelocity; }
}
void applyDeviceKinematicTarget(inout DeviceBodyState body, float3 position, float4 rotation)
{
    if (body.motion != 1) return;
    body.position = position; body.rotation = rotation;
    body.predictedCenterOfMass = bodyCenterOfMass(body); body.predictedRotation = body.rotation;
}
#endif
