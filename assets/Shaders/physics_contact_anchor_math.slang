#ifndef PHYSICS_CONTACT_ANCHOR_MATH
#define PHYSICS_CONTACT_ANCHOR_MATH
#include "physics_body_math.slang"

float3 deviceContactRelativeAnchor(DeviceBodyState body,float3 localPoint,float3 origin)
{
    return (body.position-origin)+bodyRotate(body.rotation,localPoint);
}

float3 deviceContactArm(DeviceBodyState body,float3 localPoint)
{
    return bodyRotate(body.rotation,localPoint-body.centerOfMass);
}
#endif
