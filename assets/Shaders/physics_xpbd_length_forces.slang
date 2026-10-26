#ifndef PHYSICS_XPBD_LENGTH_FORCES
#define PHYSICS_XPBD_LENGTH_FORCES
#include "physics_avbd.slang"
#include "physics_length_graph.slang"
#include "physics_xpbd_length_math.slang"

// XPBD initializes and updates the original multiplier; AVBD consumes its current world force.
struct DeviceXpbdLengthForces : IDeviceAvbdConstraints
{
    DeviceLengthBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    void initialize(uint row,DeviceAvbdStep context) { }
    void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    {
        DeviceLengthConstraint constraint=DeviceLengths[row];
        float3 gradient;
        deviceLengthGradient(DeviceBodyStates[constraint.firstBody].position,DeviceBodyStates[constraint.secondBody].position,gradient);
        float3 onSecond=gradient*(DeviceLengthMultipliers[row]/(context.duration*context.duration));
        if(bodyIndex==constraint.firstBody) equation.linearGradient+=onSecond;
        if(bodyIndex==constraint.secondBody) equation.linearGradient-=onSecond;
    }
    void updateDuals(uint row,DeviceAvbdStep context) { }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
#endif
