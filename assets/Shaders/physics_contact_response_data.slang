#ifndef PHYSICS_CONTACT_RESPONSE_DATA
#define PHYSICS_CONTACT_RESPONSE_DATA
struct DeviceContactResponseState
{
    float initialNormalVelocity; float restitutionImpulse; uint2 padding0;
    float3 forceOnSecond; uint padding1;
    uint2 startTime; uint2 endTime;
};
RWStructuredBuffer<DeviceContactResponseState> DeviceContactResponses;
StructuredBuffer<uint2> DeviceContactResponsesSelection;
#endif
