#ifndef PHYSICS_XPBD
#define PHYSICS_XPBD
#include "physics_body_math.slang"
struct DeviceXpbdStep
{
    uint capacity; float duration; uint2 number;
    uint2 startTime; uint2 endTime;
    uint substep; uint substeps;
};
ConstantBuffer<DeviceXpbdStep> XpbdStep;
StructuredBuffer<uint> DeviceXpbdRows;
interface IDeviceXpbdProjection
{
    bool active(uint row);
    void initialize(uint row, DeviceXpbdStep context);
    void project(uint row, DeviceXpbdStep context);
};
void initializeDeviceXpbd<T : IDeviceXpbdProjection>(uint index)
{
    if (index >= XpbdStep.capacity) return;
    uint row = DeviceXpbdRows[index];
    T kernel;
    if (kernel.active(row)) kernel.initialize(row, XpbdStep);
}
void projectDeviceXpbd<T : IDeviceXpbdProjection>(uint index)
{
    if (index >= XpbdStep.capacity) return;
    uint row = DeviceXpbdRows[index];
    T kernel;
    if (kernel.active(row)) kernel.project(row, XpbdStep);
}
#endif
