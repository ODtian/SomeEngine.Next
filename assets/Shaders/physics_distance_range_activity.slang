#ifndef PHYSICS_DISTANCE_RANGE_ACTIVITY
#define PHYSICS_DISTANCE_RANGE_ACTIVITY
#include "physics_constraint_activity.slang"
#include "physics_distance_range_bodies.slang"
#include "physics_distance_range_geometry.slang"

struct DeviceDistanceRangeActivity : IDeviceConstraintActivity
{
    DeviceDistanceRangeBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    { return uint2(DeviceDistanceRangesMetadata[row].entityIndex,DeviceDistanceRangesMetadata[row].entityGeneration); }
    override uint2 sourceVersions(uint row) { return uint2(DeviceDistanceRangesMetadata[row].inputVersion,0); }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceDistanceRangeConstraint range=DeviceDistanceRanges[row];
        float distance=deviceDistanceRangeGeometry(row,false).distance;
        return float2(max(0,max(range.minimum-distance,distance-range.maximum)),0);
    }
};
#endif
