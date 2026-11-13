#include "physics_dispatch.slang"
#include "physics_contact_geometry_math.slang"
struct DeviceContactCountSettings { uint reset; uint buckets; float distance; };
ConstantBuffer<DeviceContactCountSettings, ScalarDataLayout> ContactCountSettings;
struct DeviceContactEventPair
{
    uint2 firstGeometry; uint2 secondGeometry; uint firstShape; uint secondShape; uint flags;
};
StructuredBuffer<DeviceContactEventPair, ScalarDataLayout> PreviousContactEventPairs;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactEventCount;
RWStructuredBuffer<DeviceContactEventPair, ScalarDataLayout> CurrentContactEventPairs;
RWStructuredBuffer<uint, ScalarDataLayout> CurrentContactEventCount;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactEventCounts;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactEventHeads;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactEventNext;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactEventClaims;
uint contactEventBucket(DeviceContactEventPair pair)
{
    return (pair.firstGeometry.x*73856093u^pair.firstGeometry.y*19349663u^
        pair.secondGeometry.x*83492791u^pair.secondGeometry.y*2654435761u^
        pair.firstShape*374761393u^pair.secondShape*668265263u)%ContactCountSettings.buckets;
}
bool sameContactEventPair(DeviceContactEventPair a,DeviceContactEventPair b)
{ return all(a.firstGeometry==b.firstGeometry)&&all(a.secondGeometry==b.secondGeometry)
    &&a.firstShape==b.firstShape&&a.secondShape==b.secondShape; }
[shader("compute")][numthreads(64,1,1)]
void ClearDeviceContactEventCounts(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row<6) DeviceContactEventCounts[row]=0;
    if(row==0) CurrentContactEventCount[0]=DeviceContactCounts[0];
    if(row<ContactCountSettings.buckets) DeviceContactEventHeads[row]=0xffffffff;
    if(ContactCountSettings.reset==0&&row<PreviousContactEventCount[0]) DeviceContactEventClaims[row]=0;
}
[shader("compute")][numthreads(64,1,1)]
void IndexDeviceContactEventPairs(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(ContactCountSettings.reset!=0||row>=PreviousContactEventCount[0]) return;
    uint prior; InterlockedExchange(DeviceContactEventHeads[contactEventBucket(PreviousContactEventPairs[row])],row,prior);
    DeviceContactEventNext[row]=prior;
}
[shader("compute")][numthreads(64,1,1)]
void CountDeviceContactEvents(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=DeviceContactCounts[0]) return;
    DeviceContactPair pair=DeviceContactPairs[row];
    bool touching=false; uint kind=0;
    for(uint point=pair.firstPoint;point<pair.firstPoint+pair.pointCount;point++)
    {
        if(!deviceContactSelected(point)) continue;
        DeviceContactConstraint contact=loadDeviceContact(point);
        float gap=contact.separation+dot(contact.normal,deviceContactNormalDelta(contact,false)-contact.bodyAnchorDelta);
        if(gap<=ContactCountSettings.distance) { touching=true; kind|=contact.trigger; }
    }
    DeviceContactEventPair eventPair;
    eventPair.firstGeometry=pair.firstGeometry; eventPair.secondGeometry=pair.secondGeometry;
    eventPair.firstShape=pair.firstShape; eventPair.secondShape=pair.secondShape;
    eventPair.flags=(touching?1u:0u)|(kind<<1); CurrentContactEventPairs[row]=eventPair;
    uint previous=0xffffffff;
    for(uint old=DeviceContactEventHeads[contactEventBucket(eventPair)];old!=0xffffffff;old=DeviceContactEventNext[old])
        if(sameContactEventPair(eventPair,PreviousContactEventPairs[old])) { previous=old; break; }
    uint flags=previous==0xffffffff?0:PreviousContactEventPairs[previous].flags;
    if(previous!=0xffffffff) DeviceContactEventClaims[previous]=1;
    bool continues=(flags&1)!=0&&(flags>>1)==kind;
    if((flags&1)!=0&&(!touching||!continues)) InterlockedAdd(DeviceContactEventCounts[(flags>>1)*3+2],1);
    if(touching) InterlockedAdd(DeviceContactEventCounts[kind*3+(continues?1:0)],1);
}
[shader("compute")][numthreads(64,1,1)]
void CountRemovedDeviceContactEvents(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(ContactCountSettings.reset!=0||row>=PreviousContactEventCount[0]||DeviceContactEventClaims[row]!=0) return;
    uint flags=PreviousContactEventPairs[row].flags;
    if((flags&1)!=0) InterlockedAdd(DeviceContactEventCounts[(flags>>1)*3+2],1);
}
