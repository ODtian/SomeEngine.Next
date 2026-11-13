#ifndef PHYSICS_BODY_INPUT_TYPES
#define PHYSICS_BODY_INPUT_TYPES
struct DeviceBodyInput
{
    uint bodyIndex; uint entityIndex; uint entityGeneration; uint kinds;
    float3 linearImpulse; uint padding0;
    float3 angularImpulse; uint padding1;
    float3 targetLinearVelocity; uint padding2;
    float3 targetAngularVelocity; uint padding3;
    float3 targetPosition; uint padding4;
    float4 targetRotation;
};
#endif
