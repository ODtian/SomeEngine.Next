#ifndef PHYSICS_CONTACT_SUPPORT_GROUPS
#define PHYSICS_CONTACT_SUPPORT_GROUPS
#include "physics_contact_support_solve.slang"
#include "physics_dispatch.slang"

RWStructuredBuffer<uint4> DeviceSupportGroupRanges;
RWStructuredBuffer<uint2> DeviceSupportGroupWriteCounts;
RWStructuredBuffer<uint2> DeviceSupportGroupBlockSums;
RWStructuredBuffer<uint2> DeviceSupportGroupBlockOffsets;
RWStructuredBuffer<uint> DeviceSupportGroupBodies;
RWStructuredBuffer<uint> DeviceSupportGroupContacts;
RWStructuredBuffer<uint> DeviceSupportGroupOrderCounts;
RWStructuredBuffer<uint4> DeviceSupportGroupContactKeys;
RWStructuredBuffer<uint> DeviceSupportGroupSortContacts;
RWStructuredBuffer<uint2> DeviceSupportGroupSortRange;

struct DeviceContactSupportGroupWork : IDeviceContactSupportWork
{
    uint root;
    uint bodyCount() { return DeviceSupportGroupRanges[root].y; }
    uint body(uint index) { return DeviceSupportGroupBodies[DeviceSupportGroupRanges[root].x+index]; }
    uint contactCount() { return DeviceSupportGroupRanges[root].w; }
    uint contact(uint index) { return DeviceSupportGroupContacts[DeviceSupportGroupRanges[root].z+index]; }
    uint candidateStart() { return DeviceSupportGroupRanges[root].z; }
    uint orderStart() { return DeviceSupportGroupRanges[root].z+1; }
    uint orderCount() { return DeviceSupportGroupOrderCounts[root]; }
    void setOrderCount(uint count) { DeviceSupportGroupOrderCounts[root]=count; }
    override bool contactsAreHeightOrdered() { return true; }
};

uint deviceSupportContactRoot(uint row)
{
    if(!deviceSupportContactSelected(row)) return 0xffffffff;
    DeviceContactConstraint point=DeviceContacts[row];
    for(uint end=0;end<point.bodyCount;end++)
    {
        uint body=DeviceContactBodyLinks[point.bodyOffset+end].stateIndex;
        if(deviceSupportBodySelected(body)) return ConstraintGraphParents[body];
    }
    return 0xffffffff;
}

[shader("compute")][numthreads(64,1,1)]
void ClearDeviceSupportGroups(uint3 id:SV_DispatchThreadID)
{
    uint root=devicePhysicsThreadIndex(id,64);
    if(root>=ContactSupportSettings.bodies) return;
    DeviceSupportGroupRanges[root]=uint4(0);
    DeviceSupportGroupWriteCounts[root]=uint2(0);
    DeviceSupportGroupOrderCounts[root]=0;
    if(root==0) DeviceSupportGroupSortRange[0]=uint2(1,0);
}

[shader("compute")][numthreads(64,1,1)]
void CountDeviceSupportGroups(uint3 id:SV_DispatchThreadID)
{
    uint index=devicePhysicsThreadIndex(id,64);
    if(index<ContactSupportSettings.bodies&&deviceSupportBodySelected(index))
    {
        uint root=ConstraintGraphParents[index];
        if(root!=0xffffffff) InterlockedAdd(DeviceSupportGroupRanges[root].y,1);
    }
    if(index<ContactSupportSettings.contacts)
    {
        uint root=deviceSupportContactRoot(index);
        if(root!=0xffffffff) InterlockedAdd(DeviceSupportGroupRanges[root].w,1);
    }
}

groupshared uint2 DeviceSupportGroupScan[256];
[shader("compute")][numthreads(256,1,1)]
void ScanDeviceSupportGroups(uint3 id:SV_DispatchThreadID,uint3 lane:SV_GroupThreadID,uint3 group:SV_GroupID)
{
    uint index=devicePhysicsThreadIndex(id,256);
    uint2 count=index<ContactSupportSettings.bodies?DeviceSupportGroupRanges[index].yw:uint2(0);
    DeviceSupportGroupScan[lane.x]=count; GroupMemoryBarrierWithGroupSync();
    for(uint offset=1;offset<256;offset*=2)
    {
        uint2 previous=lane.x>=offset?DeviceSupportGroupScan[lane.x-offset]:uint2(0);
        GroupMemoryBarrierWithGroupSync();
        DeviceSupportGroupScan[lane.x]+=previous; GroupMemoryBarrierWithGroupSync();
    }
    if(index<ContactSupportSettings.bodies)
    {
        uint4 range=DeviceSupportGroupRanges[index];
        uint2 start=DeviceSupportGroupScan[lane.x]-count;
        range.x=start.x; range.z=start.y; DeviceSupportGroupRanges[index]=range;
    }
    if(lane.x==255) DeviceSupportGroupBlockSums[devicePhysicsGroupIndex(group)]=DeviceSupportGroupScan[255];
}

[shader("compute")][numthreads(1,1,1)]
void OffsetDeviceSupportGroupBlocks(uint3 id:SV_DispatchThreadID)
{
    uint count=(ContactSupportSettings.bodies+255)/256;
    uint2 total=uint2(0);
    for(uint block=0;block<count;block++)
    {
        DeviceSupportGroupBlockOffsets[block]=total;
        total+=DeviceSupportGroupBlockSums[block];
    }
    DeviceSupportGroupBlockOffsets[count]=total;
}

[shader("compute")][numthreads(64,1,1)]
void AddDeviceSupportGroupOffsets(uint3 id:SV_DispatchThreadID)
{
    uint root=devicePhysicsThreadIndex(id,64);
    if(root>=ContactSupportSettings.bodies) return;
    uint4 range=DeviceSupportGroupRanges[root];
    uint2 offset=DeviceSupportGroupBlockOffsets[root/256];
    range.x+=offset.x; range.z+=offset.y; DeviceSupportGroupRanges[root]=range;
}

[shader("compute")][numthreads(64,1,1)]
void WriteDeviceSupportGroups(uint3 id:SV_DispatchThreadID)
{
    uint index=devicePhysicsThreadIndex(id,64);
    if(index<ContactSupportSettings.bodies&&deviceSupportBodySelected(index))
    {
        uint root=ConstraintGraphParents[index],position;
        if(root!=0xffffffff)
        {
            InterlockedAdd(DeviceSupportGroupWriteCounts[root].x,1,position);
            DeviceSupportGroupBodies[DeviceSupportGroupRanges[root].x+position]=index;
        }
    }
    if(index<ContactSupportSettings.contacts)
    {
        uint root=deviceSupportContactRoot(index),position;
        if(root!=0xffffffff)
        {
            InterlockedAdd(DeviceSupportGroupWriteCounts[root].y,1,position);
            DeviceSupportGroupContacts[DeviceSupportGroupRanges[root].z+position]=index;
            DeviceConstraintMetadata metadata=DeviceContactsMetadata[index];
            float heightValue=dot(DeviceContacts[index].worldPoint,ContactSupportSettings.up);
            uint height=heightValue==0?0:asuint(heightValue);
            uint heightOrder=(height&0x80000000)!=0?~height:height^0x80000000;
            DeviceSupportGroupContactKeys[index]=uint4(root,heightOrder,metadata.entityIndex,metadata.entityGeneration);
        }
    }
}

bool deviceSupportKeyBefore(uint4 first,uint4 second)
{
    if(first.x!=second.x) return first.x<second.x;
    if(first.y!=second.y) return first.y<second.y;
    if(first.z!=second.z) return first.z<second.z;
    return first.w<second.w;
}

uint deviceSupportSortContact(uint index,bool scratch)
{ return scratch?DeviceSupportGroupSortContacts[index]:DeviceSupportGroupContacts[index]; }

[shader("compute")][numthreads(64,1,1)]
void MergeDeviceSupportContacts(uint3 id:SV_DispatchThreadID)
{
    uint index=devicePhysicsThreadIndex(id,64);
    uint count=DeviceSupportGroupBlockOffsets[(ContactSupportSettings.bodies+255)/256].y;
    if(index>=count) return;
    uint2 sorting=DeviceSupportGroupSortRange[0];
    uint width=sorting.x,start=index/(width*2)*(width*2);
    bool right=index-start>=width,scratch=sorting.y!=0;
    uint ownStart=start+(right?width:0);
    uint otherStart=min(start+(right?0:width),count);
    uint low=otherStart,high=min(start+(right?width:width*2),count);
    uint row=deviceSupportSortContact(index,scratch);
    uint4 key=DeviceSupportGroupContactKeys[row];
    while(low<high)
    {
        uint middle=low+(high-low)/2;
        uint4 other=DeviceSupportGroupContactKeys[deviceSupportSortContact(middle,scratch)];
        bool before=right?!deviceSupportKeyBefore(key,other):deviceSupportKeyBefore(other,key);
        if(before) low=middle+1; else high=middle;
    }
    uint target=start+index-ownStart+low-otherStart;
    if(scratch) DeviceSupportGroupContacts[target]=row;
    else DeviceSupportGroupSortContacts[target]=row;
}

[shader("compute")][numthreads(1,1,1)]
void AdvanceDeviceSupportSort(uint3 id:SV_DispatchThreadID)
{
    uint2 sorting=DeviceSupportGroupSortRange[0];
    DeviceSupportGroupSortRange[0]=uint2(sorting.x*2,sorting.y^1);
}

[shader("compute")][numthreads(64,1,1)]
void FinishDeviceSupportSort(uint3 id:SV_DispatchThreadID)
{
    uint index=devicePhysicsThreadIndex(id,64);
    uint count=DeviceSupportGroupBlockOffsets[(ContactSupportSettings.bodies+255)/256].y;
    if(index<count&&DeviceSupportGroupSortRange[0].y!=0)
        DeviceSupportGroupContacts[index]=DeviceSupportGroupSortContacts[index];
}
#endif
