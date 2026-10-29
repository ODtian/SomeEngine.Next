// SPDX-License-Identifier: Apache-2.0
// Weighted support witnesses follow the shared ContactSupportData geometry.
#ifndef PHYSICS_CONTACT_SUPPORT_GEOMETRY
#define PHYSICS_CONTACT_SUPPORT_GEOMETRY
#include "physics_contact_support_data.slang"
#include "physics_contact_values.slang"
#include "physics_constraint_math.slang"

float3 deviceSupportSidePoint(DeviceContactConstraint point,bool second,bool start)
{
    float3 result=float3(0);
    uint first=second?point.firstBodyCount:0,last=second?point.bodyCount:point.firstBodyCount;
    for(uint end=first;end<last;end++)
    {
        DeviceContactBody link=DeviceContactBodyLinks[point.bodyOffset+end];
        DeviceBodyState body=DeviceBodyStates[link.stateIndex];
        if(start) body=deviceBodyStart(body);
        result+=(body.position+bodyRotate(body.rotation,deviceContactNormalLocalPoint(link)))*link.weight;
    }
    return result;
}
#endif
