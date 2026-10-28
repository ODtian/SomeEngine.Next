#ifndef PHYSICS_CONTACT_GEOMETRY_MATH
#define PHYSICS_CONTACT_GEOMETRY_MATH
#include "physics_contact_values.slang"
#include "physics_constraint_math.slang"
#include "physics_contact_anchor_math.slang"
bool deviceContactFirstBody(DeviceContactConstraint point,uint end)
{
    uint body=DeviceContactBodyLinks[point.bodyOffset+end].stateIndex;
    for(uint earlier=0;earlier<end;earlier++)
        if(DeviceContactBodyLinks[point.bodyOffset+earlier].stateIndex==body) return false;
    return true;
}
struct DeviceStoredContactBodyLinks : IDeviceContactBodyLinks
{
    DeviceContactBody read(uint index) { return DeviceContactBodyLinks[index]; }
};
void deviceContactPositions(DeviceContactConstraint point,bool start,out float3 delta,out float3 midpoint)
{
    DeviceStoredContactBodyLinks links;
    deviceContactPositions(point.bodyOffset,point.firstBodyCount,point.bodyCount,start,links,delta,midpoint);
}
float3 deviceContactDelta(DeviceContactConstraint point,bool start)
{
    float3 delta,midpoint;
    deviceContactPositions(point,start,delta,midpoint);
    return delta;
}
void deviceContactJacobian(DeviceContactConstraint point,uint bodyIndex,float3 direction,out float3 linear,out float3 angular)
{
    linear=angular=float3(0);
    DeviceBodyState body=DeviceBodyStates[bodyIndex];
    for(uint end=0;end<point.bodyCount;end++)
    {
        DeviceContactBody link=DeviceContactBodyLinks[point.bodyOffset+end];
        if(link.stateIndex!=bodyIndex) continue;
        float weight=link.weight*(end<point.firstBodyCount?-1:1);
        float3 offset=deviceContactArm(body,link.localPoint);
        linear+=direction*weight;
        angular+=cross(offset,direction)*weight;
    }
}
float deviceContactSpeed(DeviceContactConstraint point,float duration,bool stepInput)
{
    float speed=0;
    for(uint end=0;end<point.bodyCount;end++)
    {
        if(!deviceContactFirstBody(point,end)) continue;
        uint bodyIndex=DeviceContactBodyLinks[point.bodyOffset+end].stateIndex;
        DeviceBodyState body=DeviceBodyStates[bodyIndex];
        float3 linearVelocity=body.linearVelocity,angularVelocity=body.angularVelocity;
        if(stepInput&&body.motion==1)
        {
            linearVelocity=(body.predictedCenterOfMass-body.startCenterOfMass)/duration;
            angularVelocity=bodyRotationVelocity(body.startRotation,body.predictedRotation,duration);
        }
        float3 linear,angular;
        deviceContactJacobian(point,bodyIndex,point.normal,linear,angular);
        speed+=dot(linear,linearVelocity)+dot(angular,angularVelocity);
    }
    return speed;
}
float deviceContactWeight<TSelection : IDeviceBodySelection>(DeviceContactConstraint point,float3 direction,TSelection selection)
{
    float weight=0;
    for(uint end=0;end<point.bodyCount;end++)
    {
        if(!deviceContactFirstBody(point,end)) continue;
        uint bodyIndex=DeviceContactBodyLinks[point.bodyOffset+end].stateIndex;
        if(!selection.contains(bodyIndex)) continue;
        float3 linear,angular;
        deviceContactJacobian(point,bodyIndex,direction,linear,angular);
        weight+=bodyConstraintWeight(DeviceBodyStates[bodyIndex],linear,angular);
    }
    return weight;
}
void deviceContactApplyCorrection<TSelection : IDeviceBodySelection>(DeviceContactConstraint point,float3 direction,float multiplier,TSelection selection)
{
    for(uint end=0;end<point.bodyCount;end++)
    {
        if(!deviceContactFirstBody(point,end)) continue;
        uint bodyIndex=DeviceContactBodyLinks[point.bodyOffset+end].stateIndex;
        if(!selection.contains(bodyIndex)) continue;
        float3 linear,angular;
        deviceContactJacobian(point,bodyIndex,direction,linear,angular);
        bodyApplyCorrection(bodyIndex,linear,angular,multiplier);
    }
}
#endif
