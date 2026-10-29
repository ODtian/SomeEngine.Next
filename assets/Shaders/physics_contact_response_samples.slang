#ifndef PHYSICS_CONTACT_RESPONSE_SAMPLES
#define PHYSICS_CONTACT_RESPONSE_SAMPLES
#include "physics_contact_samples.slang"
#include "physics_contact_response_data.slang"
struct DeviceResponseEventForces : IDeviceContactEventForces
{
    float3 read(uint row)
    {
        return deviceConstraintSelected(DeviceContactsMetadata[row], DeviceContactResponsesSelection[row])
            ? DeviceContactResponses[row].forceOnSecond : float3(0);
    }
};
#endif
