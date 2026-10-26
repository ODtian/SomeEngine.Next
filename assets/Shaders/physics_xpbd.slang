#ifndef PHYSICS_XPBD
#define PHYSICS_XPBD
#include "physics_xpbd_types.slang"
#include "physics_constraint_data.slang"
ConstantBuffer<DeviceXpbdStep> XpbdStep;
StructuredBuffer<uint4> DeviceXpbdRows;
RWStructuredBuffer<DeviceConstraintMetadata> DeviceXpbdMetadata;
bool deviceXpbdSelected(uint4 selected)
{
    DeviceConstraintMetadata metadata = DeviceXpbdMetadata[selected.x];
    return metadata.active != 0 && metadata.entityIndex == selected.y && metadata.entityGeneration == selected.z;
}
void initializeDeviceXpbd<T : IDeviceXpbdProjection>(uint index)
{
    if (index >= XpbdStep.capacity) return;
    uint4 selected = DeviceXpbdRows[index];
    uint row = selected.x;
    T kernel;
    if (deviceXpbdSelected(selected) && kernel.active(row)) kernel.initialize(row, XpbdStep);
}
void projectDeviceXpbd<T : IDeviceXpbdProjection>(uint index)
{
    if (index >= XpbdStep.capacity) return;
    uint4 selected = DeviceXpbdRows[index];
    uint row = selected.x;
    T kernel;
    if (deviceXpbdSelected(selected) && kernel.active(row)) kernel.project(row, XpbdStep);
}
#endif
