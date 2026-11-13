#ifndef PHYSICS_JOINT_ACTIVITY
#define PHYSICS_JOINT_ACTIVITY
#include "physics_constraint_activity.slang"
#include "physics_joint_participants.slang"
#include "physics_joint_math.slang"

struct DeviceJointActivity : IDeviceConstraintActivity
{
    DeviceJointBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    { return uint2(DeviceJointsMetadata[row].entityIndex,DeviceJointsMetadata[row].entityGeneration); }
    override uint2 sourceVersions(uint row) { return uint2(DeviceJointsMetadata[row].inputVersion,0); }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceJointConstraint joint=DeviceJoints[row];
        float3 linear,angular;
        deviceJointCoordinates(joint,DeviceBodyStates[joint.sourceBody],DeviceBodyStates[joint.targetBody],linear,angular);
        for(uint axis=0;axis<3;axis++)
        {
            linear[axis]=deviceJointInitialError(deviceJointLimit(joint,axis),linear[axis]);
            angular[axis]=deviceJointInitialError(deviceJointLimit(joint,axis+3),angular[axis]);
        }
        return float2(length(linear),length(angular));
    }
};
#endif
