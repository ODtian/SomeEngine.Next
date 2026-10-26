#ifndef PHYSICS_AVBD_ELASTIC_STATE
#define PHYSICS_AVBD_ELASTIC_STATE
struct DeviceAvbdElasticState
{
    float3 linearMultiplier; float linearPenalty;
    float3 angularMultiplier; float angularPenalty;
    float3 initialLinearError; uint padding0;
    float3 initialAngularError; uint padding1;
};
RWStructuredBuffer<DeviceAvbdElasticState> DeviceAvbdElasticStates;
StructuredBuffer<uint2> DeviceElasticAvbdSelection;
#endif
