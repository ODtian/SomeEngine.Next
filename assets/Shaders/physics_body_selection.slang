#ifndef PHYSICS_BODY_SELECTION
#define PHYSICS_BODY_SELECTION
#include "physics_body_step.slang"
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
