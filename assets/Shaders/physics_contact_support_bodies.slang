#ifndef PHYSICS_CONTACT_SUPPORT_BODIES
#define PHYSICS_CONTACT_SUPPORT_BODIES
#include "physics_constraint_graph.slang"
StructuredBuffer<uint4> DeviceSupportBodySelection;
bool deviceSupportBodySelected(uint body)
{
    DeviceSelectedBodies selectedBodies;
    uint4 selected=DeviceSupportBodySelection[body];
    DeviceBodyState state=DeviceBodyStates[body];
    return selectedBodies.contains(body)&&selected.z!=0&&state.entityIndex==selected.x&&state.entityGeneration==selected.y&&state.motion==2;
}
struct DeviceSupportBodies : IDeviceConstraintBodies
{
    uint count() { uint rows,stride; DeviceSupportBodySelection.GetDimensions(rows,stride); return rows; }
    bool active(uint row) { return deviceSupportBodySelected(row); }
    uint bodyCount(uint row) { return 1; }
    uint body(uint row,uint end) { return row; }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
