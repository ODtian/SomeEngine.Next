#ifndef PHYSICS_CONTACT_BODY
#define PHYSICS_CONTACT_BODY
struct DeviceContactBody
{
    uint2 entity;
    float3 localPoint;
    float weight;
    uint stateIndex;
    uint padding;
};
#endif
