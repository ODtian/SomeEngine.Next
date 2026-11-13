#ifndef PHYSICS_CONTACT_SAMPLES
#define PHYSICS_CONTACT_SAMPLES
#include "physics_dispatch.slang"
#include "physics_contact_geometry_math.slang"
struct DeviceContactEventSample
{
    DeviceContactPair pair; int firstFeature; int secondFeature;
    float3 worldPoint; float separation;
    float3 normal; uint flags;
    float3 force; uint active;
};
RWStructuredBuffer<DeviceContactEventSample, ScalarDataLayout> DeviceContactEventSamples;
interface IDeviceContactEventForces { float3 read(uint row); };
struct DeviceTriggerEventForces : IDeviceContactEventForces
{ float3 read(uint row) { return float3(0); } };
void readDeviceContactSamples<TForces : IDeviceContactEventForces>(uint row,TForces forces)
{
    if(row>=DeviceContactCounts[1]) return;
    DeviceContactEventSample sample=(DeviceContactEventSample)0;
    if(deviceContactSelected(row))
    {
        DeviceContactConstraint point=loadDeviceContact(row);
        float3 delta,midpoint; deviceContactPositions(point,false,delta,midpoint);
        sample.pair=DeviceContactPairs[DeviceContactPairIndices[row]];
        sample.firstFeature=point.firstFeature; sample.secondFeature=point.secondFeature;
        sample.worldPoint=midpoint; sample.normal=point.normal;
        sample.separation=point.separation+dot(point.normal,deviceContactNormalDelta(point,false)-point.bodyAnchorDelta);
        sample.flags=DeviceContactFlags[row]; sample.force=forces.read(row); sample.active=1;
    }
    DeviceContactEventSamples[row]=sample;
}
#endif
