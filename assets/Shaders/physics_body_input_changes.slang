#ifndef PHYSICS_BODY_INPUT_CHANGES
#define PHYSICS_BODY_INPUT_CHANGES
#include "physics_bodies.slang"
struct DeviceBodyCpuInputChange
{
    uint entityIndex, entityGeneration, components, substep;
    uint2 number;
};
StructuredBuffer<DeviceBodyCpuInputChange> DeviceBodyCpuInputChanges;
bool deviceBodyCpuInputChanged(uint body, uint2 number, uint substep)
{
    DeviceBodyCpuInputChange change = DeviceBodyCpuInputChanges[body];
    DeviceBodyState state = DeviceBodyStates[body];
    return (change.components & 7u) != 0 && change.entityIndex == state.entityIndex
        && change.entityGeneration == state.entityGeneration && all(change.number == number) && change.substep == substep;
}
#endif
