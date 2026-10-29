#ifndef PHYSICS_CONTACT_SUPPORT_PARALLEL
#define PHYSICS_CONTACT_SUPPORT_PARALLEL
#include "physics_contact_support_groups.slang"

RWStructuredBuffer<uint> DeviceSupportContactOrder;
RWStructuredBuffer<uint2> DeviceSupportContactEnds;
RWStructuredBuffer<uint2> DeviceSupportContactPredecessors;
RWStructuredBuffer<uint2> DeviceSupportContactSuccessors;
globallycoherent RWStructuredBuffer<uint> DeviceSupportGroupRebuild;
struct DeviceSupportContactTask
{
    uint waiting; uint nextReady; uint incoming; uint incomingContacts;
};
globallycoherent RWStructuredBuffer<DeviceSupportContactTask> DeviceSupportContactTasks;
// A source below contacts denotes a contact; the remaining indices denote bodies.
struct DeviceSupportLoadLink { uint source; uint next; float weight; };
RWStructuredBuffer<DeviceSupportLoadLink> DeviceSupportLoadLinks;
// Head of the ready list, followed by the number of unfinished contacts.
globallycoherent RWStructuredBuffer<uint> DeviceSupportWorkQueue;

bool deviceSupportRebuilds(uint row)
{
    uint root=deviceSupportContactRoot(row);
    return root!=0xffffffff&&DeviceSupportGroupRebuild[root]!=0;
}
void pushDeviceSupportContact(uint row)
{
    uint head;
    InterlockedAdd(DeviceSupportWorkQueue[0],0,head);
    for(;;)
    {
        DeviceSupportContactTasks[row].nextReady=head;
        DeviceMemoryBarrier();
        uint previous;
        InterlockedCompareExchange(DeviceSupportWorkQueue[0],head,row,previous);
        if(previous==head) return;
        head=previous;
    }
}
uint takeDeviceSupportContact()
{
    uint head;
    InterlockedAdd(DeviceSupportWorkQueue[0],0,head);
    if(head==0xffffffff) return head;
    DeviceMemoryBarrier();
    uint next=DeviceSupportContactTasks[head].nextReady,previous;
    InterlockedCompareExchange(DeviceSupportWorkQueue[0],head,next,previous);
    return previous==head?head:0xffffffff;
}
void releaseDeviceSupportContact(uint row)
{
    if(row==0xffffffff) return;
    uint waiting;
    InterlockedAdd(DeviceSupportContactTasks[row].waiting,0xffffffff,waiting);
    if(waiting==1) pushDeviceSupportContact(row);
}
bool deviceSupportContactsUnfinished()
{
    uint remaining;
    InterlockedAdd(DeviceSupportWorkQueue[1],0,remaining);
    return remaining!=0;
}

[shader("compute")][numthreads(64,1,1)]
void ClearDeviceSupportContactOrder(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row<ContactSupportSettings.contacts) DeviceSupportContactOrder[row]=0xffffffff;
}
[shader("compute")][numthreads(64,1,1)]
void WriteDeviceSupportContactOrder(uint3 id:SV_DispatchThreadID)
{
    uint order=devicePhysicsThreadIndex(id,64);
    uint count=DeviceSupportGroupBlockOffsets[(ContactSupportSettings.bodies+255)/256].y;
    if(order<count) DeviceSupportContactOrder[DeviceSupportGroupContacts[order]]=order;
}
[shader("compute")][numthreads(64,1,1)]
void WriteDeviceSupportContactEnds(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=ContactSupportSettings.contacts) return;
    uint2 ends=uint2(0xffffffff);
    DeviceAwakeBodies bodies;
    if(DeviceSupportContactOrder[row]!=0xffffffff&&deviceSupportContactRuns(row,bodies))
    {
        DeviceContactConstraint point=DeviceContacts[row];
        uint first=DeviceContactBodyLinks[point.bodyOffset].stateIndex;
        uint second=DeviceContactBodyLinks[point.bodyOffset+point.firstBodyCount].stateIndex;
        bool rigid=first!=second;
        for(uint end=0;end<point.bodyCount;end++)
            rigid=rigid&&DeviceContactBodyLinks[point.bodyOffset+end].stateIndex==(end<point.firstBodyCount?first:second);
        float vertical=dot(point.normal,ContactSupportSettings.up),squared=vertical*vertical;
        uint lower=vertical>0?first:second,upper=vertical>0?second:first;
        if(rigid&&isinf(point.normalStiffness)&&point.normalStiffness>0&&vertical!=0
            &&1-squared<=point.staticFriction*point.staticFriction*squared&&deviceSupportBodySelected(upper)
            &&(DeviceBodyStates[lower].motion!=2||deviceSupportBodySelected(lower)))
            ends=uint2(DeviceBodyStates[lower].motion==2?lower:0xffffffff,upper);
    }
    DeviceSupportContactEnds[row]=ends;
}
[shader("compute")][numthreads(64,1,1)]
void WriteDeviceSupportDependencies(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=ContactSupportSettings.contacts) return;
    uint2 previous=uint2(0xffffffff),next=uint2(0xffffffff),ends=DeviceSupportContactEnds[row];
    uint order=DeviceSupportContactOrder[row];
    for(uint side=0;side<2;side++)
    {
        uint body=ends[side];
        if(body==0xffffffff) continue;
        uint previousOrder=0,nextOrder=0xffffffff;
        // SceneGraphRows starts with DeviceContactBodies: contact rows retain their original indices.
        for(uint entry=ConstraintGraphBodyOffsets[body];entry<ConstraintGraphBodyOffsets[body+1];entry++)
        {
            uint other=ConstraintGraphAdjacency[entry];
            if(other>=ContactSupportSettings.contacts||other==row) continue;
            uint2 otherEnds=DeviceSupportContactEnds[other];
            if(all(otherEnds!=body)) continue;
            uint otherOrder=DeviceSupportContactOrder[other];
            if(otherOrder<order&&(previous[side]==0xffffffff||otherOrder>previousOrder))
            { previous[side]=other; previousOrder=otherOrder; }
            if(otherOrder>order&&otherOrder<nextOrder)
            { next[side]=other; nextOrder=otherOrder; }
        }
    }
    DeviceSupportContactPredecessors[row]=previous; DeviceSupportContactSuccessors[row]=next;
}

[shader("compute")][numthreads(64,1,1)]
void InitializeDeviceSupportRebuild(uint3 id:SV_DispatchThreadID)
{
    uint body=devicePhysicsThreadIndex(id,64);
    if(body<ContactSupportSettings.bodies) DeviceSupportGroupRebuild[body]=1;
}
[shader("compute")][numthreads(64,1,1)]
void ClearDeviceSupportRebuild(uint3 id:SV_DispatchThreadID)
{
    uint body=devicePhysicsThreadIndex(id,64);
    if(body<ContactSupportSettings.bodies) DeviceSupportGroupRebuild[body]=0;
}
[shader("compute")][numthreads(64,1,1)]
void CheckDeviceSupportMotion(uint3 id:SV_DispatchThreadID)
{
    uint body=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(body>=ContactSupportSettings.bodies||!deviceSupportBodySelected(body)||!bodies.contains(body)) return;
    DeviceBodySupportState state=DeviceSupportBodyStates[body];
    if(state.moving!=0||!deviceSupportHasMotion(body)) return;
    state.moving=1; DeviceSupportBodyStates[body]=state;
    InterlockedOr(DeviceSupportGroupRebuild[ConstraintGraphParents[body]],1);
}
[shader("compute")][numthreads(64,1,1)]
void InitializeDeviceSupportWarmStart(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(row>=ContactSupportSettings.contacts||!deviceSupportContactRuns(row,bodies)) return;
    DeviceContactSupportState state=DeviceSupportContactStates[row];
    state.extraForceOnSecond*=DeviceContacts[row].warmStart!=0?ContactSupportSettings.retention:0;
    DeviceSupportContactStates[row]=state;
}
[shader("compute")][numthreads(64,1,1)]
void PrepareDeviceSupportBodies(uint3 id:SV_DispatchThreadID)
{
    uint body=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(body>=ContactSupportSettings.bodies||!deviceSupportBodySelected(body)||!bodies.contains(body)
        ||DeviceSupportGroupRebuild[ConstraintGraphParents[body]]==0) return;
    DeviceBodySupportState state=DeviceSupportBodyStates[body];
    state.enabled=0; state.reaction=0; state.pointCount=state.loadCount=state.equationCount=0;
    DeviceSupportBodyStates[body]=state;
}
[shader("compute")][numthreads(64,1,1)]
void PrepareDeviceSupportCandidates(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(row>=ContactSupportSettings.contacts||!deviceSupportContactRuns(row,bodies)||!deviceSupportRebuilds(row)) return;
    DeviceContactSupportState state=DeviceSupportContactStates[row];
    state.enabled=0; state.reaction=state.load=0; state.loadCount=0; DeviceSupportContactStates[row]=state;
    DeviceSupportCandidate candidate;
    bool accepted=deviceSupportCandidate(row,AvbdStep,candidate);
    candidate.padding.x=accepted?1:0; DeviceSupportCandidates[row]=candidate;
}
[shader("compute")][numthreads(1,1,1)]
void ClearDeviceSupportWorkQueue(uint3 id:SV_DispatchThreadID)
{ DeviceSupportWorkQueue[0]=0xffffffff; DeviceSupportWorkQueue[1]=0; }
[shader("compute")][numthreads(64,1,1)]
void QueueDeviceSupportCandidates(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(row>=ContactSupportSettings.contacts||!deviceSupportContactRuns(row,bodies)||!deviceSupportRebuilds(row)) return;
    uint2 previous=DeviceSupportContactPredecessors[row];
    DeviceSupportContactTasks[row].waiting=(previous.x!=0xffffffff?1:0)+(previous.y!=0xffffffff?1:0);
    InterlockedAdd(DeviceSupportWorkQueue[1],1);
}
[shader("compute")][numthreads(64,1,1)]
void SeedDeviceSupportCandidates(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(row<ContactSupportSettings.contacts&&deviceSupportContactRuns(row,bodies)&&deviceSupportRebuilds(row)
        &&DeviceSupportContactTasks[row].waiting==0) pushDeviceSupportContact(row);
}
void buildDeviceSupportCandidatesParallel()
{
    while(deviceSupportContactsUnfinished())
    {
        uint row=takeDeviceSupportContact();
        if(row!=0xffffffff)
        {
            DeviceSupportCandidate candidate=DeviceSupportCandidates[row];
            if(candidate.padding.x!=0) deviceBuildSupportCandidate(candidate,DeviceSupportContactOrder[row]);
            DeviceMemoryBarrier();
            uint2 next=DeviceSupportContactSuccessors[row];
            releaseDeviceSupportContact(next.x); releaseDeviceSupportContact(next.y);
            InterlockedAdd(DeviceSupportWorkQueue[1],0xffffffff);
        }
    }
}
[shader("compute")][numthreads(64,1,1)]
void FinishDeviceSupportBodies(uint3 id:SV_DispatchThreadID)
{
    uint body=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(body<ContactSupportSettings.bodies&&deviceSupportBodySelected(body)&&bodies.contains(body)
        &&DeviceSupportGroupRebuild[ConstraintGraphParents[body]]!=0) deviceBuildSupportBody(body);
}
[shader("compute")][numthreads(64,1,1)]
void ReleaseDeviceUnsupportedContacts(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(row<ContactSupportSettings.contacts&&deviceSupportContactRuns(row,bodies)&&deviceSupportRebuilds(row)
        &&DeviceSupportContactStates[row].enabled==0) deviceReleaseContactSupport(row);
}
[shader("compute")][numthreads(64,1,1)]
void ClearDeviceSupportIncoming(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row<ContactSupportSettings.contacts&&DeviceSupportContactOrder[row]!=0xffffffff&&deviceSupportRebuilds(row))
    { DeviceSupportContactTasks[row].incoming=0xffffffff; DeviceSupportContactTasks[row].incomingContacts=0; }
}
void writeDeviceSupportLoadLink(uint index,uint source,DeviceSupportLoad load,bool contact)
{
    uint previous;
    InterlockedExchange(DeviceSupportContactTasks[load.contact].incoming,index,previous);
    DeviceSupportLoadLink link; link.source=source; link.next=previous; link.weight=load.weight;
    DeviceSupportLoadLinks[index]=link;
    if(contact) InterlockedAdd(DeviceSupportContactTasks[load.contact].incomingContacts,1);
}
[shader("compute")][numthreads(64,1,1)]
void WriteDeviceSupportIncoming(uint3 id:SV_DispatchThreadID)
{
    uint index=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(index<ContactSupportSettings.contacts&&deviceSupportContactRuns(index,bodies)&&deviceSupportRebuilds(index))
    {
        DeviceContactSupportState state=DeviceSupportContactStates[index];
        if(state.enabled!=0)
            for(uint load=0;load<state.loadCount;load++)
                writeDeviceSupportLoadLink(index*3+load,index,DeviceSupportContactLoads[index*3+load],true);
    }
    if(index<ContactSupportSettings.bodies&&deviceSupportBodySelected(index)&&bodies.contains(index)
        &&DeviceSupportGroupRebuild[ConstraintGraphParents[index]]!=0)
    {
        DeviceBodySupportState state=DeviceSupportBodyStates[index];
        if(state.enabled!=0)
            for(uint load=0;load<state.loadCount;load++)
                writeDeviceSupportLoadLink((ContactSupportSettings.contacts+index)*3+load,ContactSupportSettings.contacts+index,
                    DeviceSupportBodyLoads[index*3+load],false);
    }
}
[shader("compute")][numthreads(64,1,1)]
void QueueDeviceSupportLoads(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(row>=ContactSupportSettings.contacts||!deviceSupportContactRuns(row,bodies)||DeviceSupportContactStates[row].enabled==0) return;
    DeviceSupportContactTasks[row].waiting=DeviceSupportContactTasks[row].incomingContacts;
    InterlockedAdd(DeviceSupportWorkQueue[1],1);
}
[shader("compute")][numthreads(64,1,1)]
void SeedDeviceSupportLoads(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    DeviceAwakeBodies bodies;
    if(row<ContactSupportSettings.contacts&&deviceSupportContactRuns(row,bodies)&&DeviceSupportContactStates[row].enabled!=0
        &&DeviceSupportContactTasks[row].waiting==0) pushDeviceSupportContact(row);
}
void updateDeviceSupportLoadsParallel()
{
    while(deviceSupportContactsUnfinished())
    {
        uint row=takeDeviceSupportContact();
        if(row!=0xffffffff)
        {
            DeviceContactSupportState state=DeviceSupportContactStates[row]; float load=state.reaction;
            for(uint index=DeviceSupportContactTasks[row].incoming;index!=0xffffffff;)
            {
                DeviceSupportLoadLink link=DeviceSupportLoadLinks[index];
                float reaction=link.source<ContactSupportSettings.contacts?DeviceSupportContactStates[link.source].load
                    :DeviceSupportBodyStates[link.source-ContactSupportSettings.contacts].reaction;
                load+=reaction*link.weight; index=link.next;
            }
            state.load=load; DeviceSupportContactStates[row]=state; deviceApplySupportLoad(row);
            DeviceMemoryBarrier();
            for(uint index=0;index<state.loadCount;index++)
                releaseDeviceSupportContact(DeviceSupportContactLoads[row*3+index].contact);
            InterlockedAdd(DeviceSupportWorkQueue[1],0xffffffff);
        }
    }
}
#endif
