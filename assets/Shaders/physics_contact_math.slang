#ifndef PHYSICS_CONTACT_MATH
#define PHYSICS_CONTACT_MATH
#include "physics_contact_data.slang"
#include "physics_contact_geometry_math.slang"
#include "physics_contact_response_data.slang"
void deviceInitializeContactResponse(uint row,float duration,uint2 startTime,uint2 endTime)
{
    DeviceContactResponseState response;
    response.initialNormalVelocity=deviceContactSpeed(DeviceContacts[row],duration,true);
    response.restitutionImpulse=0; response.forceOnSecond=float3(0);
    response.startTime=startTime; response.endTime=endTime;
    response.padding0=uint2(0); response.padding1=0;
    DeviceContactResponses[row]=response;
}
#endif
