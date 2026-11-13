#ifndef PHYSICS_BODY_SELECTION
#define PHYSICS_BODY_SELECTION
#include "physics_body_step.slang"
ConstantBuffer<DeviceBodyStep, ScalarDataLayout> BodyStep;
StructuredBuffer<uint, ScalarDataLayout> DeviceBodyIndices;
StructuredBuffer<uint, ScalarDataLayout> DeviceBodyMembership;
struct DeviceSelectedBodies : IDeviceBodySelection
{
    bool contains(uint body)
    {
        return DeviceBodyMembership[body] != 0;
    }
};
#endif
