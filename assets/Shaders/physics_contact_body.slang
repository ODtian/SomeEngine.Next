#ifndef PHYSICS_CONTACT_BODY
#define PHYSICS_CONTACT_BODY
struct DeviceContactBody
{
    uint2 entity;
    float3 localPoint;
    float weight;
    uint stateIndex;
    uint normalReference;
};
float3 deviceContactNormalLocalPoint(DeviceContactBody link)
{ return link.normalReference==1?float3(0):link.localPoint; }
#endif
