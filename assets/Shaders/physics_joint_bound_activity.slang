#ifndef PHYSICS_JOINT_BOUND_ACTIVITY
#define PHYSICS_JOINT_BOUND_ACTIVITY
#include "physics_constraint_activity.slang"
#include "physics_joint_bound_geometry.slang"

struct DeviceJointBoundActivity : IDeviceConstraintActivity
{
    DeviceJointBoundBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    {
        DeviceConstraintMetadata set=DeviceJointBoundSetsMetadata[deviceJointBoundSetIndex(row)];
        return uint2(set.entityIndex,set.entityGeneration);
    }
    override uint2 sourceVersions(uint row)
    {
        DeviceJointBoundSet set=DeviceJointBoundSets[deviceJointBoundSetIndex(row)];
        return uint2(DeviceJointsMetadata[set.jointRow].inputVersion,DeviceJointsMetadata[DeviceJointBoundValues[row].otherJointRow].inputVersion);
    }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceJointBoundConstraint bound=DeviceJointBoundValues[row];
        float distance=deviceJointBoundGeometry(row,false).distance;
        return float2(max(0,max(bound.minimum-distance,distance-bound.maximum)),0);
    }
};
#endif
