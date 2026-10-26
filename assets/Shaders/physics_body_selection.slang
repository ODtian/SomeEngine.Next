#ifndef PHYSICS_BODY_SELECTION
#define PHYSICS_BODY_SELECTION
struct DeviceBodyStep
{
    uint count; float duration; uint substep; uint padding0;
    float3 acceleration; uint padding1;
    uint2 number; uint2 time;
};
ConstantBuffer<DeviceBodyStep> BodyStep;
StructuredBuffer<uint> DeviceBodyIndices;
#endif
