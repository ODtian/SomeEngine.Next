#ifndef PHYSICS_XPBD
#define PHYSICS_XPBD
#include "physics_xpbd_types.slang"
ConstantBuffer<DeviceXpbdStep> XpbdStep;
StructuredBuffer<uint> DeviceXpbdRows;
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
