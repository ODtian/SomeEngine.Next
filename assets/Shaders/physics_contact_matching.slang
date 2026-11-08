#include "physics_dispatch.slang"
#include "physics_collision_contact.slang"
#include "physics_contact_types.slang"
#include "physics_constraint_selection.slang"
#include "physics_contact_anchor_math.slang"
RWStructuredBuffer<DeviceContactConstraint, ScalarDataLayout> DeviceContacts;
RWStructuredBuffer<DeviceConstraintMetadata, ScalarDataLayout> DeviceContactsMetadata;
RWStructuredBuffer<DeviceContactBody, ScalarDataLayout> DeviceContactBodyLinks;
struct DeviceMatchingStoredBodyLinks : IDeviceContactBodyLinks
{
    DeviceContactBody read(uint index) { return DeviceContactBodyLinks[index]; }
};
struct DeviceContactMatchSettings
{
    uint points; uint pairs; uint buckets; float squaredDistance;
    float normalDot; uint inputBodiesPerContact; uint inputs;
};
ConstantBuffer<DeviceContactMatchSettings, ScalarDataLayout> ContactMatchSettings;
StructuredBuffer<DeviceCollisionContact, ScalarDataLayout> DeviceMatchInputContacts;
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputCounts;
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputOffsets;
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputBodyCounts;
StructuredBuffer<DeviceContactBody, ScalarDataLayout> DeviceMatchInputBodies;
struct DeviceDetectedContactBodyLinks : IDeviceContactBodyLinks
{
    DeviceContactBody read(uint index) { return DeviceMatchInputBodies[index]; }
};
StructuredBuffer<uint2, ScalarDataLayout> DeviceContactMatchingSelection;
RWStructuredBuffer<uint, ScalarDataLayout> ContactMatchHeads;
RWStructuredBuffer<uint, ScalarDataLayout> ContactMatchNext;
// Actual point Entity, original parameter row and geometric matching distance.
RWStructuredBuffer<uint4, ScalarDataLayout> ContactMatches;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactGeometryRows;
RWStructuredBuffer<uint2, ScalarDataLayout> ContactMatchClaims;
RWStructuredBuffer<uint, ScalarDataLayout> ContactNewInputs;
RWStructuredBuffer<uint4, ScalarDataLayout> ContactNewReferences;
RWStructuredBuffer<uint, ScalarDataLayout> ContactRemovedRows;
RWStructuredBuffer<uint2, ScalarDataLayout> ContactMatchingEntities;
uint deviceContactPairBucket(uint2 first,uint2 second)
{
    return (first.x*73856093u^first.y*19349663u^second.x*83492791u^second.y*2654435761u)%ContactMatchSettings.buckets;
}
[shader("compute")][numthreads(64,1,1)]
void ClearDeviceContactMatches(uint3 id:SV_DispatchThreadID)
{
    uint bucket=devicePhysicsThreadIndex(id,64);
    if(bucket<ContactMatchSettings.buckets) ContactMatchHeads[bucket]=0xffffffff;
    if(bucket<ContactMatchSettings.points) ContactMatchClaims[bucket]=uint2(0xffffffff);
    if(bucket==0) { ContactNewInputs[0]=0; ContactRemovedRows[0]=0; }
}
[shader("compute")][numthreads(64,1,1)]
void IndexDeviceContactMatches(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=ContactMatchSettings.points||!deviceConstraintSelected(DeviceContactsMetadata[row],DeviceContactMatchingSelection[row])) return;
    DeviceContactConstraint point=DeviceContacts[row];
    ContactMatchingEntities[row]=uint2(DeviceContactsMetadata[row].entityIndex,DeviceContactsMetadata[row].entityGeneration);
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
        uint inputIndex=DeviceMatchInputOffsets[pair]+input;
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
            if(ContactMatchSettings.inputBodiesPerContact!=0)
            {
                uint inputCount=DeviceMatchInputBodyCounts[inputIndex];
                if(old.bodyCount!=inputCount||old.firstBodyCount!=current.firstBodyCount) continue;
                bool sameBodies=true;
                for(uint body=0;body<inputCount;body++)
                {
                    DeviceContactBody prior=DeviceContactBodyLinks[old.bodyOffset+body];
                    DeviceContactBody next=DeviceMatchInputBodies[inputIndex*ContactMatchSettings.inputBodiesPerContact+body];
                    sameBodies=sameBodies&&all(prior.entity==next.entity)&&prior.stateIndex==next.stateIndex;
                }
                if(!sameBodies) continue;
            }
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
        ContactMatches[inputIndex]=uint4(entity,match,asuint(distance));
        DeviceContactGeometryRows[inputIndex]=match;
        if(match!=0xffffffff) InterlockedMin(ContactMatchClaims[match].x,asuint(distance));
    }
}
[shader("compute")][numthreads(64,1,1)]
void ClaimContactInputs(uint3 id:SV_DispatchThreadID)
{
    uint input=devicePhysicsThreadIndex(id,64);
    if(input>=ContactMatchSettings.inputs) return;
    uint4 match=ContactMatches[input];
    if(match.z!=0xffffffff&&match.w==ContactMatchClaims[match.z].x)
        InterlockedMin(ContactMatchClaims[match.z].y,input);
}
[shader("compute")][numthreads(64,1,1)]
void CollectNewContactInputs(uint3 id:SV_DispatchThreadID)
{
    uint input=devicePhysicsThreadIndex(id,64);
    if(input>=ContactMatchSettings.inputs) return;
    uint4 match=ContactMatches[input];
    if(match.z!=0xffffffff&&ContactMatchClaims[match.z].y==input) return;
    ContactMatches[input]=uint4(0,0,0xffffffff,match.w);
    DeviceContactGeometryRows[input]=0xffffffff;
    uint output;
    InterlockedAdd(ContactNewInputs[0],1,output);
    ContactNewInputs[1+output]=input;
    DeviceCollisionContact contact=DeviceMatchInputContacts[input];
    ContactNewReferences[output]=uint4(contact.first,contact.second);
}
[shader("compute")][numthreads(64,1,1)]
void CollectRemovedContactRows(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=ContactMatchSettings.points||all(ContactMatchingEntities[row]==uint2(0))) return;
    if(ContactMatchClaims[row].y!=0xffffffff) return;
    uint output;
    InterlockedAdd(ContactRemovedRows[0],1,output);
    ContactRemovedRows[1+output]=row;
}
