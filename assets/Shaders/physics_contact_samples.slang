#ifndef PHYSICS_CONTACT_SAMPLES
#define PHYSICS_CONTACT_SAMPLES
#include "physics_contact_geometry_math.slang"

struct DeviceContactEventSample
{
    uint2 entity; uint active; uint padding0;
    float3 worldPoint; float separation;
    float3 normal; uint padding1;
    float3 force; uint padding2;
    uint2 first; uint2 second;
};
RWStructuredBuffer<DeviceContactEventSample> DeviceContactEventSamples;

interface IDeviceContactEventForces { float3 read(uint row); };
struct DeviceTriggerEventForces : IDeviceContactEventForces
{
    float3 read(uint row) { return float3(0); }
};
void readDeviceContactSamples<TForces : IDeviceContactEventForces>(uint row, TForces forces)
{
    uint count, stride;
    DeviceContactEventSamples.GetDimensions(count, stride);
    if (row >= count) return;
    DeviceContactEventSample sample;
    sample.entity = uint2(0); sample.active = sample.padding0 = sample.padding1 = sample.padding2 = 0;
    sample.worldPoint = sample.normal = sample.force = float3(0); sample.separation = 0;
    sample.first = sample.second = uint2(0);
    if (deviceContactSelected(row))
    {
        DeviceContactConstraint point = DeviceContacts[row];
        float3 midpoint, delta;
        deviceContactPositions(point, false, delta, midpoint);
        sample.entity = uint2(DeviceContactsMetadata[row].entityIndex, DeviceContactsMetadata[row].entityGeneration);
        sample.active = 1; sample.worldPoint = midpoint; sample.normal = point.normal;
        sample.separation = point.separation + dot(point.normal, deviceContactNormalDelta(point,false) - point.bodyAnchorDelta);
        sample.force = forces.read(row); sample.first = point.firstGeometry; sample.second = point.secondGeometry;
    }
    DeviceContactEventSamples[row] = sample;
}
#endif
