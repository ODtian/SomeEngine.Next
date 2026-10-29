#include "physics_dispatch.slang"
#include "physics_collision_contact.slang"
#include "physics_contact_types.slang"
#include "physics_constraint_selection.slang"
#include "physics_contact_anchor_math.slang"
RWStructuredBuffer<DeviceContactConstraint> DeviceContacts;
RWStructuredBuffer<DeviceConstraintMetadata> DeviceContactsMetadata;
RWStructuredBuffer<DeviceContactBody> DeviceContactBodyLinks;
struct DeviceMatchingStoredBodyLinks : IDeviceContactBodyLinks
{
    DeviceContactBody read(uint index) { return DeviceContactBodyLinks[index]; }
};
struct DeviceContactMatchSettings
{
    uint points; uint pairs; uint buckets; float squaredDistance;
    float normalDot; uint inputBodiesPerContact;
};
ConstantBuffer<DeviceContactMatchSettings> ContactMatchSettings;
StructuredBuffer<DeviceCollisionContact> DeviceMatchInputContacts;
StructuredBuffer<uint> DeviceMatchInputCounts;
StructuredBuffer<uint> DeviceMatchInputBodyCounts;
StructuredBuffer<DeviceContactBody> DeviceMatchInputBodies;
struct DeviceDetectedContactBodyLinks : IDeviceContactBodyLinks
{
    DeviceContactBody read(uint index) { return DeviceMatchInputBodies[index]; }
};
StructuredBuffer<uint2> DeviceContactMatchingSelection;
RWStructuredBuffer<uint> ContactMatchHeads;
RWStructuredBuffer<uint> ContactMatchNext;
// Actual point Entity, original parameter row and geometric matching distance.
RWStructuredBuffer<uint4> ContactMatches;
uint deviceContactPairBucket(uint2 first,uint2 second)
{
    return (first.x*73856093u^first.y*19349663u^second.x*83492791u^second.y*2654435761u)%ContactMatchSettings.buckets;
}
[shader("compute")][numthreads(64,1,1)]
void ClearDeviceContactMatches(uint3 id:SV_DispatchThreadID)
{
    uint bucket=devicePhysicsThreadIndex(id,64);
    if(bucket<ContactMatchSettings.buckets) ContactMatchHeads[bucket]=0xffffffff;
}
[shader("compute")][numthreads(64,1,1)]
void IndexDeviceContactMatches(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=ContactMatchSettings.points||!deviceConstraintSelected(DeviceContactsMetadata[row],DeviceContactMatchingSelection[row])) return;
    DeviceContactConstraint point=DeviceContacts[row];
    uint previous;
    InterlockedExchange(ContactMatchHeads[deviceContactPairBucket(point.firstGeometry,point.secondGeometry)],row,previous);
    ContactMatchNext[row]=previous;
}
[shader("compute")][numthreads(64,1,1)]
void MatchDeviceContacts(uint3 id:SV_DispatchThreadID)
{
    uint pair=devicePhysicsThreadIndex(id,64);
    if(pair>=ContactMatchSettings.pairs) return;
    uint used[4]={0xffffffff,0xffffffff,0xffffffff,0xffffffff};
    uint count=DeviceMatchInputCounts[pair];
    for(uint input=0;input<count;input++)
    {
        uint inputIndex=pair*4+input;
        DeviceCollisionContact current=DeviceMatchInputContacts[inputIndex];
        bool start=current.prediction.enabled!=0;
        float3 currentPosition=current.worldPoint;
        if(ContactMatchSettings.inputBodiesPerContact!=0)
        {
            DeviceDetectedContactBodyLinks links;
            float3 currentDelta;
            deviceContactPositions(inputIndex*ContactMatchSettings.inputBodiesPerContact,uint(current.firstBodyCount),
                DeviceMatchInputBodyCounts[inputIndex],start,links,true,currentDelta,currentPosition);
        }
        uint match=0xffffffff; float distance=ContactMatchSettings.squaredDistance; bool featureMatch=false;
        for(uint row=ContactMatchHeads[deviceContactPairBucket(current.first,current.second)];row!=0xffffffff;row=ContactMatchNext[row])
        {
            bool claimed=false;
            for(uint index=0;index<input;index++) claimed=claimed||used[index]==row;
            if(claimed) continue;
            DeviceContactConstraint old=DeviceContacts[row];
            if(any(current.first!=old.firstGeometry)||any(current.second!=old.secondGeometry)||dot(current.normal,old.normal)<ContactMatchSettings.normalDot) continue;
            float3 oldDelta,oldPosition;
            DeviceMatchingStoredBodyLinks oldLinks;
            deviceContactPositions(old.bodyOffset,old.firstBodyCount,old.bodyCount,start,oldLinks,true,oldDelta,oldPosition);
            float3 delta=currentPosition-oldPosition;
            float squared=dot(delta,delta);
            if(squared>ContactMatchSettings.squaredDistance) continue;
            bool same=current.firstFeature==old.firstFeature&&current.secondFeature==old.secondFeature;
            if((featureMatch&&!same)||(same==featureMatch&&squared>distance)) continue;
            match=row; distance=squared; featureMatch=same;
        }
        used[input]=match;
        uint2 entity=match==0xffffffff?uint2(0):uint2(DeviceContactsMetadata[match].entityIndex,DeviceContactsMetadata[match].entityGeneration);
        ContactMatches[pair*4+input]=uint4(entity,match,asuint(distance));
    }
}
