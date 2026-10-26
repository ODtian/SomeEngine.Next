#ifndef PHYSICS_BODY_ACTIVITY_TYPES
#define PHYSICS_BODY_ACTIVITY_TYPES
#include "physics_body_math.slang"
struct DeviceBodyActivityState
{
    uint entityIndex; uint entityGeneration; uint flags; float quietSeconds;
    float radius; int substep; uint2 number;
};
struct DeviceBodyActivityInput
{
    uint entityIndex; uint entityGeneration; uint flags; uint sleeping;
    float quietSeconds; float radius; uint2 padding;
};
RWStructuredBuffer<DeviceBodyActivityState> DeviceBodyActivities;
StructuredBuffer<DeviceBodyActivityInput> DeviceBodyActivityInputs;
bool deviceBodyActivityAwake(uint index)
{
    DeviceBodyActivityState activity=DeviceBodyActivities[index];
    DeviceBodyState body=DeviceBodyStates[index];
    return activity.entityIndex!=body.entityIndex||activity.entityGeneration!=body.entityGeneration||
        (activity.flags&2)==0||(activity.flags&4)==0;
}
#endif
