#ifndef PHYSICS_CONTACT_ACTIVITY
#define PHYSICS_CONTACT_ACTIVITY
#include "physics_body_activity.slang"
#include "physics_contact_data.slang"
#include "physics_contact_geometry_math.slang"

struct DeviceContactActivity : IDeviceConstraintActivity
{
    DeviceContactBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    { return uint2(DeviceContactsMetadata[row].entityIndex,DeviceContactsMetadata[row].entityGeneration); }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceContactConstraint point=DeviceContacts[row];
        float separation=point.separation+dot(point.normal,deviceContactDelta(point,false)-point.bodyAnchorDelta);
        return float2(max(0,-separation),0);
    }
};
#endif
