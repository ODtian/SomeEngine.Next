#ifndef PHYSICS_XPBD_TYPES
#define PHYSICS_XPBD_TYPES
#include "physics_body_math.slang"
struct DeviceXpbdStep
{
    uint capacity; float duration; uint2 number;
    uint2 startTime; uint2 endTime;
    uint substep; uint substeps;
};
interface IDeviceXpbdProjection
{
    bool active(uint row);
    void initialize(uint row, DeviceXpbdStep context);
    void project(uint row, DeviceXpbdStep context);
};
float deviceXpbdBodyWeight<T : IDeviceBodySelection>(uint body, float3 linear, float3 angular, T selection)
{
    return selection.contains(body) ? bodyConstraintWeight(DeviceBodyStates[body], linear, angular) : 0;
}
void deviceXpbdBodyCorrection<T : IDeviceBodySelection>(uint body, float3 linear, float3 angular, float change, T selection)
{
    if (selection.contains(body)) bodyApplyCorrection(body, linear, angular, change);
}
#endif
