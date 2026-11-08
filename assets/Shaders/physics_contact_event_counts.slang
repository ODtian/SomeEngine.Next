#include "physics_dispatch.slang"
#include "physics_contact_geometry_math.slang"
struct DeviceContactCountSettings { uint contacts; uint reset; float distance; };
ConstantBuffer<DeviceContactCountSettings, ScalarDataLayout> ContactCountSettings;
RWStructuredBuffer<uint4, ScalarDataLayout> DeviceContactEventHistory;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactEventCounts;
[shader("compute")][numthreads(64,1,1)]
void ClearDeviceContactEventCounts(uint3 id:SV_DispatchThreadID)
{
    uint index=devicePhysicsThreadIndex(id,64);
    if(index<6) DeviceContactEventCounts[index]=0;
    if(ContactCountSettings.reset!=0&&index<ContactCountSettings.contacts) DeviceContactEventHistory[index]=uint4(0);
}
[shader("compute")][numthreads(64,1,1)]
void CountDeviceContactEvents(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=ContactCountSettings.contacts) return;
    uint4 previous=DeviceContactEventHistory[row];
    uint2 entity=uint2(0); uint kind=0; bool touching=false;
    if(deviceContactSelected(row))
    {
        DeviceContactConstraint point=DeviceContacts[row];
        entity=uint2(DeviceContactsMetadata[row].entityIndex,DeviceContactsMetadata[row].entityGeneration);
        kind=point.trigger;
        float separation=point.separation+dot(point.normal,deviceContactNormalDelta(point,false)-point.bodyAnchorDelta);
        touching=separation<=ContactCountSettings.distance;
    }
    bool continues=all(entity==previous.xy)&&kind==previous.z;
    if(previous.w!=0&&(!touching||!continues)) InterlockedAdd(DeviceContactEventCounts[previous.z*3+2],1);
    if(touching) InterlockedAdd(DeviceContactEventCounts[kind*3+(previous.w!=0&&continues?1:0)],1);
    DeviceContactEventHistory[row]=uint4(entity,kind,touching?1:0);
}
