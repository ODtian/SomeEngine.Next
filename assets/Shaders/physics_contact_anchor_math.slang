#ifndef PHYSICS_CONTACT_ANCHOR_MATH
#define PHYSICS_CONTACT_ANCHOR_MATH
#include "physics_constraint_math.slang"
#include "physics_contact_body.slang"

float3 deviceContactRelativeAnchor(DeviceBodyState body,float3 localPoint,float3 origin)
{
    return (body.position-origin)+bodyRotate(body.rotation,localPoint);
}

float3 deviceContactArm(DeviceBodyState body,float3 localPoint)
{
    return bodyRotate(body.rotation,localPoint-body.centerOfMass);
}
interface IDeviceContactBodyLinks { DeviceContactBody read(uint index); };
void deviceContactPositions<TLinks : IDeviceContactBodyLinks>(uint bodyOffset,uint firstBodyCount,uint bodyCount,
    bool start,TLinks links,bool normal,out float3 delta,out float3 midpoint)
{
    DeviceBodyState origin=DeviceBodyStates[links.read(bodyOffset+firstBodyCount).stateIndex];
    if(start) origin=deviceBodyStart(origin);
    float3 first=float3(0),second=float3(0);
    for(uint end=0;end<bodyCount;end++)
    {
        DeviceContactBody link=links.read(bodyOffset+end);
        DeviceBodyState body=DeviceBodyStates[link.stateIndex];
        if(start) body=deviceBodyStart(body);
        float3 position=deviceContactRelativeAnchor(body,normal?deviceContactNormalLocalPoint(link):link.localPoint,origin.position)*link.weight;
        if(end<firstBodyCount) first+=position;
        else second+=position;
    }
    // Each side's interpolation weights form an affine point about the same origin.
    delta=second-first; midpoint=origin.position+(first+second)*.5f;
}
void deviceContactPositions<TLinks : IDeviceContactBodyLinks>(uint bodyOffset,uint firstBodyCount,uint bodyCount,
    bool start,TLinks links,out float3 delta,out float3 midpoint)
{ deviceContactPositions(bodyOffset,firstBodyCount,bodyCount,start,links,false,delta,midpoint); }
#endif
