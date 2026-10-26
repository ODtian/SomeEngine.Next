#ifndef PHYSICS_ELASTIC_ACTIVITY
#define PHYSICS_ELASTIC_ACTIVITY
#include "physics_constraint_activity.slang"
#include "physics_elastic_math.slang"

struct DeviceElasticActivity : IDeviceConstraintActivity
{
    DeviceElasticBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    { return uint2(DeviceElasticConstraintsMetadata[row].entityIndex,DeviceElasticConstraintsMetadata[row].entityGeneration); }
    override uint2 sourceVersions(uint row) { return uint2(DeviceElasticConstraintsMetadata[row].inputVersion,0); }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceElasticConstraint constraint=DeviceElasticConstraints[row];
        DeviceBodyState first=DeviceBodyStates[constraint.firstBody],second=DeviceBodyStates[constraint.secondBody];
        return float2(constraint.linearStiffness>0?length(deviceElasticLinearError(constraint,first,second)):0,
            constraint.angularStiffness>0?length(deviceElasticAngularError(constraint,first.rotation,second.rotation)):0);
    }
};
#endif
