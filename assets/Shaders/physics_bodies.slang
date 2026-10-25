#ifndef PHYSICS_BODIES
#define PHYSICS_BODIES
struct DeviceBodyState
{
    float3 position; uint motion;
    float4 rotation;
    float3 linearVelocity; float inverseMass;
    float3 angularVelocity; uint entityIndex;
    float3 centerOfMass; uint entityGeneration;
    float3 inverseInertiaX; uint padding0;
    float3 inverseInertiaY; uint padding1;
    float3 inverseInertiaZ; uint padding2;
    float3 startCenterOfMass; int substep;
    float4 startRotation;
    float3 predictedCenterOfMass; uint padding3;
    float4 predictedRotation;
    uint2 number;
    uint2 time;
};
RWStructuredBuffer<DeviceBodyState> DeviceBodyStates;
#endif
