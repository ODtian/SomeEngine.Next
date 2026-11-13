#ifndef PHYSICS_BEND_ACTIVITY
#define PHYSICS_BEND_ACTIVITY
#include "physics_constraint_activity.slang"
#include "physics_bend_graph.slang"

struct DeviceBendActivity : IDeviceConstraintActivity
{
    DeviceBendBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    { return uint2(DeviceBendsMetadata[row].entityIndex,DeviceBendsMetadata[row].entityGeneration); }
    override uint2 sourceVersions(uint row) { return uint2(DeviceBendsMetadata[row].inputVersion,0); }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceBendConstraint bend=DeviceBends[row];
        float3 firstEdge=DeviceBodyStates[bend.middleBody].position-DeviceBodyStates[bend.firstBody].position;
        float3 secondEdge=DeviceBodyStates[bend.lastBody].position-DeviceBodyStates[bend.middleBody].position;
        float cosine=dot(normalize(firstEdge),normalize(secondEdge));
        return float2(0,max(0,acos(clamp(cosine,-1,1))-acos(bend.minimumCosine)));
    }
};
#endif
