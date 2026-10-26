#ifndef PHYSICS_BODY_ACTIVITY
#define PHYSICS_BODY_ACTIVITY
#include "physics_body_activity_types.slang"
#include "physics_constraint_graph.slang"
#include "physics_body_input_changes.slang"
struct DeviceActivityStep
{
    uint bodies; uint rows; float duration; float speedThreshold;
    float quietDuration; float linearErrorThreshold; float angularErrorThreshold; uint phase;
    uint substep; uint padding; uint2 number; uint2 startTime; uint2 endTime;
};
uniform DeviceActivityStep ActivityStep;
RWStructuredBuffer<uint> DeviceActivityIslandSignals;
struct DeviceActivityRow
{
    uint entityIndex; uint entityGeneration; uint bodyCount;
    uint firstVersion; uint secondVersion;
};
RWStructuredBuffer<DeviceActivityRow> DeviceActivityPreviousRows;
// Body index, write role and actual Body Entity/generation.
RWStructuredBuffer<uint4> DeviceActivityPreviousParticipants;
interface IDeviceConstraintActivity : IDeviceConstraintBodies
{
    uint2 entity(uint row);
    float2 error(uint row,DeviceActivityStep context);
    uint2 sourceVersions(uint row) { return uint2(0); }
};
struct DeviceNoConstraintActivity : IDeviceConstraintActivity
{
    uint count() { return 0; }
    bool active(uint row) { return false; }
    uint bodyCount(uint row) { return 0; }
    uint body(uint row,uint end) { return 0; }
    bool writes(uint row,uint end) { return false; }
    uint groupCount() { return 0; }
    uint group(uint row) { return 0; }
    uint2 entity(uint row) { return uint2(0); }
    float2 error(uint row,DeviceActivityStep context) { return float2(0); }
};
[shader("compute")][numthreads(64,1,1)]
void ApplyDeviceActivityInputs(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;
    if(index>=ActivityStep.bodies) return;
    DeviceBodyActivityInput input=DeviceBodyActivityInputs[index];
    DeviceBodyActivityState activity;
    if((input.flags&1)==0)
    {
        activity.entityIndex=activity.entityGeneration=activity.flags=0;
        activity.quietSeconds=activity.radius=0; activity.substep=int(ActivityStep.substep); activity.number=ActivityStep.number;
        DeviceBodyActivities[index]=activity; return;
    }
    if((input.flags&4)!=0)
    {
        activity.entityIndex=input.entityIndex; activity.entityGeneration=input.entityGeneration;
        activity.flags=(input.flags&3)|((input.sleeping!=0&&(input.flags&2)!=0)?4:0);
        activity.quietSeconds=input.quietSeconds; activity.radius=input.radius;
    }
    else activity=DeviceBodyActivities[index];
    activity.flags&=~(8u|16u|32u|64u|128u);
    if((input.flags&8)!=0) activity.flags|=128;
    activity.number=ActivityStep.number; activity.substep=int(ActivityStep.substep);
    DeviceBodyActivities[index]=activity;
}
[shader("compute")][numthreads(64,1,1)]
void ClearDeviceActivitySignals(uint3 id:SV_DispatchThreadID)
{
    if(ActivityStep.phase==0)
    {
        uint rows,stride,participants;
        DeviceActivityPreviousRows.GetDimensions(rows,stride);
        DeviceActivityPreviousParticipants.GetDimensions(participants,stride);
        if(id.x<rows) DeviceActivityPreviousRows[id.x]=(DeviceActivityRow)0;
        if(id.x<participants) DeviceActivityPreviousParticipants[id.x]=uint4(0);
    }
    else if(id.x<ActivityStep.bodies) DeviceActivityIslandSignals[id.x]=0;
}
bool deviceActivityBodyChanged(uint body)
{
    return (DeviceBodyActivities[body].flags&128)!=0||deviceBodyCpuInputChanged(body,ActivityStep.number,ActivityStep.substep);
}
[shader("compute")][numthreads(64,1,1)]
void CollectDeviceActivityBodies(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;
    if(index>=ActivityStep.bodies||ConstraintGraphParents[index]==0xffffffff) return;
    uint root=ConstraintGraphParents[index];
    DeviceBodyState body=DeviceBodyStates[index];
    DeviceBodyActivityState activity=DeviceBodyActivities[index];
    if(ActivityStep.phase==1)
    {
        if(deviceBodyActivityAwake(index)||any(body.linearVelocity!=float3(0))||any(body.angularVelocity!=float3(0)))
            InterlockedOr(DeviceActivityIslandSignals[root],1);
        if(deviceActivityBodyChanged(index)) InterlockedOr(DeviceActivityIslandSignals[root],9);
        return;
    }
    if((activity.flags&2)==0) { InterlockedOr(DeviceActivityIslandSignals[root],2); return; }
    if((activity.flags&4)!=0) return;
    float speed=length(body.linearVelocity)+length(body.angularVelocity)*activity.radius;
    float movement=length(bodyCenterOfMass(body)-body.startCenterOfMass)+
        length(bodyRotationVelocity(body.startRotation,body.rotation,1))*activity.radius;
    if(speed>ActivityStep.speedThreshold||movement>ActivityStep.speedThreshold*ActivityStep.duration)
        InterlockedOr(DeviceActivityIslandSignals[root],2);
}
void deviceActivityWakeParticipant(uint4 participant)
{
    DeviceBodyState body=DeviceBodyStates[participant.x];
    uint root=ConstraintGraphParents[participant.x];
    if(root!=0xffffffff&&body.entityIndex==participant.z&&body.entityGeneration==participant.w)
        InterlockedOr(DeviceActivityIslandSignals[root],9);
}
uint4 deviceActivityParticipant<T : IDeviceConstraintActivity>(T rows,uint index,uint end)
{
    uint body=rows.body(index,end);
    DeviceBodyState state=DeviceBodyStates[body];
    return uint4(body,rows.writes(index,end)?1:0,state.entityIndex,state.entityGeneration);
}
void readDeviceActivityRows<T : IDeviceConstraintActivity>(uint index,T rows)
{
    if(index>=ActivityStep.rows) return;
    bool active=index<rows.count()&&rows.active(index);
    uint count=active?rows.bodyCount(index):0;
    if(ActivityStep.phase==1)
    {
        uint2 entity=active?rows.entity(index):uint2(0);
        uint2 versions=active?rows.sourceVersions(index):uint2(0);
        DeviceActivityRow current;
        current.entityIndex=entity.x; current.entityGeneration=entity.y; current.bodyCount=count;
        current.firstVersion=versions.x; current.secondVersion=versions.y;
        DeviceActivityRow previous=DeviceActivityPreviousRows[index];
        uint start=index*ConstraintGraphParameters.bodiesPerRow;
        bool changed=current.entityIndex!=previous.entityIndex||current.entityGeneration!=previous.entityGeneration
            ||count!=previous.bodyCount||current.firstVersion!=previous.firstVersion||current.secondVersion!=previous.secondVersion;
        for(uint end=0;end<min(previous.bodyCount,count);end++)
            changed=changed||any(DeviceActivityPreviousParticipants[start+end]!=deviceActivityParticipant(rows,index,end));
        if(changed)
        {
            for(uint end=0;end<previous.bodyCount;end++) deviceActivityWakeParticipant(DeviceActivityPreviousParticipants[start+end]);
            for(uint end=0;end<count;end++) deviceActivityWakeParticipant(deviceActivityParticipant(rows,index,end));
        }
        for(uint end=0;end<count;end++) DeviceActivityPreviousParticipants[start+end]=deviceActivityParticipant(rows,index,end);
        DeviceActivityPreviousRows[index]=current;
    }
    if(!active) return;
    float2 error=rows.error(index,ActivityStep);
    bool exceeds=error.x>ActivityStep.linearErrorThreshold||error.y>ActivityStep.angularErrorThreshold;
    bool changed=false;
    for(uint end=0;end<rows.bodyCount(index);end++)
    {
        uint body=rows.body(index,end);
        changed=changed||deviceActivityBodyChanged(body);
        if(ConstraintGraphParents[body]==0xffffffff)
            changed=changed||any(DeviceBodyStates[body].linearVelocity!=float3(0))||any(DeviceBodyStates[body].angularVelocity!=float3(0));
    }
    if(!exceeds&&!changed) return;
    for(uint end=0;end<rows.bodyCount(index);end++)
    {
        uint body=rows.body(index,end),root=ConstraintGraphParents[body];
        if(root!=0xffffffff) InterlockedOr(DeviceActivityIslandSignals[root],(exceeds?3u:0u)|(changed?9u:0u));
    }
}
[shader("compute")][numthreads(64,1,1)]
void WakeDeviceBodies(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;
    if(index>=ActivityStep.bodies||ConstraintGraphParents[index]==0xffffffff) return;
    uint signals=DeviceActivityIslandSignals[ConstraintGraphParents[index]];
    if((signals&1)==0) return;
    DeviceBodyActivityState activity=DeviceBodyActivities[index];
    if((activity.flags&2)==0) return;
    if((activity.flags&4)!=0)
    {
        DeviceBodyState body=DeviceBodyStates[index];
        activity.flags|=16;
        if(all(body.number==ActivityStep.number)&&body.substep==int(ActivityStep.substep)&&all(body.time==ActivityStep.endTime)) activity.flags|=64;
        activity.quietSeconds=0;
    }
    if((signals&8)!=0) activity.quietSeconds=0;
    activity.flags&=~4u; DeviceBodyActivities[index]=activity;
    ConstraintGraphActiveBodies[index]=1;
}
[shader("compute")][numthreads(64,1,1)]
void AdvanceDeviceBodyQuietTime(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;
    if(index>=ActivityStep.bodies||ConstraintGraphParents[index]==0xffffffff) return;
    DeviceBodyActivityState activity=DeviceBodyActivities[index];
    if((activity.flags&2)==0||(activity.flags&4)!=0) return;
    uint root=ConstraintGraphParents[index];
    activity.quietSeconds=(DeviceActivityIslandSignals[root]&2)==0?activity.quietSeconds+ActivityStep.duration:0;
    if(activity.quietSeconds<ActivityStep.quietDuration) InterlockedOr(DeviceActivityIslandSignals[root],4);
    DeviceBodyActivities[index]=activity;
}
[shader("compute")][numthreads(64,1,1)]
void FinishDeviceBodyActivity(uint3 id:SV_DispatchThreadID)
{
    uint index=id.x;
    if(index>=ActivityStep.bodies||ConstraintGraphParents[index]==0xffffffff||
        (DeviceActivityIslandSignals[ConstraintGraphParents[index]]&6)!=0) return;
    DeviceBodyActivityState activity=DeviceBodyActivities[index];
    if((activity.flags&2)==0||(activity.flags&4)!=0) return;
    activity.flags|=4|32; DeviceBodyActivities[index]=activity;
    DeviceBodyState body=DeviceBodyStates[index]; body.linearVelocity=body.angularVelocity=float3(0);
    DeviceBodyStates[index]=body; ConstraintGraphActiveBodies[index]=0;
}
#endif
