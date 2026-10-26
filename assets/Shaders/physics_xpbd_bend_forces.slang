#ifndef PHYSICS_XPBD_BEND_FORCES
#define PHYSICS_XPBD_BEND_FORCES
#include "physics_avbd.slang"
#include "physics_bend_graph.slang"
#include "physics_xpbd_bend_math.slang"

struct DeviceXpbdBendForces : IDeviceAvbdConstraints
{
    DeviceBendBodies participants;
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
        DeviceBendConstraint constraint=DeviceBends[row];
        float3 first,middle,last,gradient=float3(0);
        deviceBendGradients(DeviceBodyStates[constraint.firstBody].position,DeviceBodyStates[constraint.middleBody].position,
            DeviceBodyStates[constraint.lastBody].position,first,middle,last);
        if(bodyIndex==constraint.firstBody) gradient+=first;
        if(bodyIndex==constraint.middleBody) gradient+=middle;
        if(bodyIndex==constraint.lastBody) gradient+=last;
        equation.linearGradient-=gradient*(DeviceBendMultipliers[row]/(context.duration*context.duration));
    }
    void updateDuals(uint row,DeviceAvbdStep context) { }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
#endif
