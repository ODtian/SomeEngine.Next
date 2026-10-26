#ifndef PHYSICS_AWAKE_BODIES
#define PHYSICS_AWAKE_BODIES
#include "physics_body_activity_types.slang"
#include "physics_constraint_graph.slang"

struct DeviceAwakeBodies : IDeviceBodySelection
{
    bool contains(uint body)
    {
        DeviceSelectedBodies selected;
        return selected.contains(body) && DeviceBodyStates[body].motion == 2 && deviceBodyActivityAwake(body);
    }
};
bool deviceConstraintHasAwakeBody<T : IDeviceConstraintBodies>(uint row, T constraints)
{
    DeviceAwakeBodies bodies;
    for (uint end = 0; end < constraints.bodyCount(row); end++)
        if (constraints.writes(row, end) && bodies.contains(constraints.body(row, end))) return true;
    return false;
}
#endif
