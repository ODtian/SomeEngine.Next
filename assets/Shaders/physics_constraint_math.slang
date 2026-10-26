#ifndef PHYSICS_CONSTRAINT_MATH
#define PHYSICS_CONSTRAINT_MATH
#include "physics_body_math.slang"
DeviceBodyState deviceBodyStart(DeviceBodyState body)
{
    body.rotation=body.startRotation;
    bodySetCenterOfMass(body,body.startCenterOfMass);
    return body;
}
#endif
