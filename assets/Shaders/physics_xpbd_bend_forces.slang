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
        DeviceBodyState firstBody=DeviceBodyStates[constraint.firstBody],middleBody=DeviceBodyStates[constraint.middleBody],
            lastBody=DeviceBodyStates[constraint.lastBody];
        float3 first,middle,last,gradient=float3(0);
        float residual=deviceBendGradients(firstBody.position,middleBody.position,lastBody.position,first,middle,last)-constraint.minimumCosine;
        float multiplier=DeviceBendMultipliers[row];
        if(residual>=0&&multiplier==0) return;
        DeviceGraphBodies selection;
        float firstWeight=firstBody.motion==2&&selection.contains(constraint.firstBody)?firstBody.inverseMass:0;
        float middleWeight=middleBody.motion==2&&selection.contains(constraint.middleBody)?middleBody.inverseMass:0;
        float lastWeight=lastBody.motion==2&&selection.contains(constraint.lastBody)?lastBody.inverseMass:0;
        float mobility=firstWeight*dot(first,first)+middleWeight*dot(middle,middle)+lastWeight*dot(last,last),stiffness;
        multiplier=max(0,multiplier+deviceXpbdMultiplierChange(residual,multiplier,mobility,
            constraint.compliance,context.duration,stiffness));
        if(bodyIndex==constraint.firstBody) gradient+=first;
        if(bodyIndex==constraint.middleBody) gradient+=middle;
        if(bodyIndex==constraint.lastBody) gradient+=last;
        deviceAvbdAddRow(equation,gradient,float3(0),-multiplier/(context.duration*context.duration),stiffness);
    }
    void updateDuals(uint row,DeviceAvbdStep context) { }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
#endif
