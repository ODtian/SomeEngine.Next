#include "physics_dispatch.slang"
#include "physics_contact_values.slang"
#include "physics_contact_anchor_math.slang"
struct DeviceContactMatchSettings
{
    uint pointCapacity; uint buckets; float squaredDistance; float normalDot;
};
ConstantBuffer<DeviceContactMatchSettings, ScalarDataLayout> ContactMatchSettings;
RWStructuredBuffer<uint, ScalarDataLayout> ContactMatchHeads;
RWStructuredBuffer<uint, ScalarDataLayout> ContactMatchNext;
RWStructuredBuffer<uint2, ScalarDataLayout> ContactMatches;
RWStructuredBuffer<uint2, ScalarDataLayout> ContactMatchClaims;
RWStructuredBuffer<uint, ScalarDataLayout> ContactNewInputs;
RWStructuredBuffer<uint, ScalarDataLayout> ContactRemovedRows;
uint deviceContactPairBucket(DeviceContactPair pair)
{
    return (pair.firstGeometry.x*73856093u^pair.firstGeometry.y*19349663u^
        pair.secondGeometry.x*83492791u^pair.secondGeometry.y*2654435761u^
        pair.firstShape*374761393u^pair.secondShape*668265263u)%ContactMatchSettings.buckets;
}
struct DeviceMatchingStoredBodyLinks : IDeviceContactBodyLinks
{ DeviceContactBody read(uint index) { return loadPreviousContactBody(index); } };
struct DeviceMatchingCurrentBodyLinks : IDeviceContactBodyLinks
{ DeviceContactBody read(uint index) { return loadDeviceContactBody(index); } };
[shader("compute")][numthreads(64,1,1)]
void ClearDeviceContactMatches(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row<ContactMatchSettings.buckets) ContactMatchHeads[row]=0xffffffff;
    if(row<ContactMatchSettings.pointCapacity) ContactMatchClaims[row]=uint2(0xffffffff);
    if(row==0)
    { DeviceContactCounts[0]=0; DeviceContactCounts[1]=0; DeviceContactCounts[2]=0;
      ContactNewInputs[0]=0; ContactRemovedRows[0]=0; }
}
[shader("compute")][numthreads(64,1,1)]
void IndexDeviceContactMatches(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=PreviousContactCounts[1]) return;
    DeviceContactPair pair=PreviousContactPairs[PreviousContactPairIndices[row]];
    uint previous;
    InterlockedExchange(ContactMatchHeads[deviceContactPairBucket(pair)],row,previous);
    ContactMatchNext[row]=previous;
}
[shader("compute")][numthreads(64,1,1)]
void MatchDeviceContacts(uint3 id:SV_DispatchThreadID)
{
    uint pairIndex=devicePhysicsThreadIndex(id,64);
    if(pairIndex>=DeviceContactCounts[0]) return;
    DeviceContactPair pair=DeviceContactPairs[pairIndex];
    uint used[4]={0xffffffff,0xffffffff,0xffffffff,0xffffffff};
    for(uint point=0;point<pair.pointCount;point++)
    {
    uint input=pair.firstPoint+point;
    bool start=DeviceContactPredictions[input].enabled!=0;
    DeviceMatchingCurrentBodyLinks currentLinks;
    float3 currentDelta,currentPosition;
    deviceContactPositions(DeviceContactBodyStarts[input],DeviceContactFirstBodyCounts[input],
        DeviceContactBodyCounts[input],start,currentLinks,true,currentDelta,currentPosition);
    uint match=0xffffffff; float distance=ContactMatchSettings.squaredDistance; bool featureMatch=false;
    for(uint row=ContactMatchHeads[deviceContactPairBucket(pair)];row!=0xffffffff;row=ContactMatchNext[row])
    {
        bool claimed=false;
        for(uint earlier=0;earlier<point;earlier++) claimed=claimed||used[earlier]==row;
        if(claimed) continue;
        DeviceContactPair prior=PreviousContactPairs[PreviousContactPairIndices[row]];
        if(any(pair.firstGeometry!=prior.firstGeometry)||any(pair.secondGeometry!=prior.secondGeometry)||
            pair.firstShape!=prior.firstShape||pair.secondShape!=prior.secondShape||
            dot(DeviceContactNormals[input],PreviousContactNormals[row])<ContactMatchSettings.normalDot) continue;
        uint count=DeviceContactBodyCounts[input];
        if(count!=PreviousContactBodyCounts[row]||DeviceContactFirstBodyCounts[input]!=PreviousContactFirstBodyCounts[row]) continue;
        bool sameBodies=true;
        for(uint end=0;end<count;end++)
        {
            DeviceContactBody a=loadPreviousContactBody(PreviousContactBodyStarts[row]+end);
            DeviceContactBody b=loadDeviceContactBody(DeviceContactBodyStarts[input]+end);
            sameBodies=sameBodies&&all(a.entity==b.entity)&&a.stateIndex==b.stateIndex;
        }
        if(!sameBodies) continue;
        DeviceMatchingStoredBodyLinks oldLinks;
        float3 oldDelta,oldPosition;
        deviceContactPositions(PreviousContactBodyStarts[row],PreviousContactFirstBodyCounts[row],count,
            start,oldLinks,true,oldDelta,oldPosition);
        float3 delta=currentPosition-oldPosition;
        float squared=dot(delta,delta);
        if(squared>ContactMatchSettings.squaredDistance) continue;
        bool same=DeviceContactFirstFeatures[input]==PreviousContactFirstFeatures[row]&&
            DeviceContactSecondFeatures[input]==PreviousContactSecondFeatures[row];
        if((featureMatch&&!same)||(same==featureMatch&&(squared>distance||(squared==distance&&row>match)))) continue;
        match=row; distance=squared; featureMatch=same;
    }
    used[point]=match;
    ContactMatches[input]=uint2(match,asuint(distance));
    if(match!=0xffffffff) InterlockedMin(ContactMatchClaims[match].x,asuint(distance));
    }
}
[shader("compute")][numthreads(64,1,1)]
void ClaimContactInputs(uint3 id:SV_DispatchThreadID)
{
    uint input=devicePhysicsThreadIndex(id,64);
    if(input>=DeviceContactCounts[1]) return;
    uint2 match=ContactMatches[input];
    if(match.x!=0xffffffff&&match.y==ContactMatchClaims[match.x].x)
        InterlockedMin(ContactMatchClaims[match.x].y,input);
}
[shader("compute")][numthreads(64,1,1)]
void CollectNewContactInputs(uint3 id:SV_DispatchThreadID)
{
    uint input=devicePhysicsThreadIndex(id,64);
    if(input>=DeviceContactCounts[1]) return;
    uint2 match=ContactMatches[input];
    if(match.x!=0xffffffff&&ContactMatchClaims[match.x].y==input)
    { return; }
    ContactMatches[input]=uint2(0xffffffff,match.y);
    DeviceContactFlags[input]&=~1u;
    uint output; InterlockedAdd(ContactNewInputs[0],1,output);
    ContactNewInputs[1+output]=input;
}
[shader("compute")][numthreads(64,1,1)]
void CollectRemovedContactRows(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=PreviousContactCounts[1]||ContactMatchClaims[row].y!=0xffffffff) return;
    uint output; InterlockedAdd(ContactRemovedRows[0],1,output);
    ContactRemovedRows[1+output]=row;
}
