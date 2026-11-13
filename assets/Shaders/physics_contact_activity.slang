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
    { return DeviceContactPairs[DeviceContactPairIndices[row]].firstGeometry; }
    override uint2 sourceVersions(uint row) { return DeviceContactInputVersions[row]; }
    override uint previousRow(uint row)
    {
        if((ActivityStep.flags&1)==0) return row;
        return row<DeviceContactCounts[1]?DeviceContactPreviousPoints[row].x:0xffffffff;
    }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceContactConstraint point=loadDeviceContact(row);
        float separation=point.separation+dot(point.normal,deviceContactNormalDelta(point,false)-point.bodyAnchorDelta);
        return float2(max(0,-separation),0);
    }
};
#endif
