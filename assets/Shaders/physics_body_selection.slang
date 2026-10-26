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
struct DeviceSelectedBodies : IDeviceBodySelection
{
    bool contains(uint body)
    {
        for (uint index = 0; index < BodyStep.count; index++)
            if (DeviceBodyIndices[index] == body) return true;
        return false;
    }
};
#endif
