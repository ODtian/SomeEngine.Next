#ifndef PHYSICS_BODY_SELECTION
#define PHYSICS_BODY_SELECTION
#include "physics_body_math.slang"
struct DeviceBodyStep
{
    uint count; float duration; uint substep; uint padding0;
    float3 acceleration; uint padding1;
    uint2 number; uint2 time;
};
ConstantBuffer<DeviceBodyStep> BodyStep;
StructuredBuffer<uint> DeviceBodyIndices;
StructuredBuffer<uint> DeviceBodyMembership;
struct DeviceSelectedBodies : IDeviceBodySelection
{
    bool contains(uint body)
    {
        return DeviceBodyMembership[body] != 0;
    }
};
#endif
