#ifndef PHYSICS_XPBD_LENGTH_FORCES
#define PHYSICS_XPBD_LENGTH_FORCES
#include "physics_avbd.slang"
#include "physics_length_graph.slang"
#include "physics_xpbd_length_math.slang"

// XPBD updates the original multiplier; AVBD reads the same projection's trial force and response.
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
        DeviceBodyState first=DeviceBodyStates[constraint.firstBody],second=DeviceBodyStates[constraint.secondBody];
        float3 gradient;
        float distance=deviceLengthGradient(first.position,second.position,gradient);
        DeviceGraphBodies selection;
        float firstWeight=first.motion==2&&selection.contains(constraint.firstBody)?first.inverseMass:0;
        float secondWeight=second.motion==2&&selection.contains(constraint.secondBody)?second.inverseMass:0;
        float multiplier=DeviceLengthMultipliers[row],stiffness;
        multiplier+=deviceXpbdMultiplierChange(distance-constraint.restLength,multiplier,firstWeight+secondWeight,
            constraint.compliance,context.duration,stiffness);
        float3 derivative=bodyIndex==constraint.firstBody?-gradient:gradient;
        deviceAvbdAddRow(equation,derivative,float3(0),-multiplier/(context.duration*context.duration),stiffness);
    }
    void updateDuals(uint row,DeviceAvbdStep context) { }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
#endif
