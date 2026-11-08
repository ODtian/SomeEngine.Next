#include "physics_contact_types.slang"
#include "physics_collision_contact.slang"
#include "physics_constraint_math.slang"
#include "physics_contact_anchor_math.slang"
struct DeviceContactGeometrySettings { uint contacts; uint bodiesPerContact; uint inputBodiesPerContact; };
ConstantBuffer<DeviceContactGeometrySettings, ScalarDataLayout> ContactGeometrySettings;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactGeometryRows;
#include "physics_dispatch.slang"
StructuredBuffer<DeviceCollisionContact, ScalarDataLayout> DeviceMatchInputContacts;
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputCounts;
StructuredBuffer<uint, ScalarDataLayout> DeviceMatchInputBodyCounts;
StructuredBuffer<DeviceContactBody, ScalarDataLayout> DeviceMatchInputBodies;
RWStructuredBuffer<DeviceContactConstraint, ScalarDataLayout> DeviceContacts;
RWStructuredBuffer<DeviceContactBody, ScalarDataLayout> DeviceContactBodyLinks;
[shader("compute")][numthreads(64,1,1)]
void UpdateDeviceContactGeometry(uint3 id:SV_DispatchThreadID)
{
    uint input=devicePhysicsThreadIndex(id,64);
    if(input>=ContactGeometrySettings.contacts) return;
    uint row=DeviceContactGeometryRows[input]&0x7fffffffu;
    uint count=DeviceMatchInputBodyCounts[input];
    if(count==0) return;
    DeviceCollisionContact current=DeviceMatchInputContacts[input];
    DeviceContactConstraint point=DeviceContacts[row];
    point.bodyCount=count; point.firstBodyCount=uint(current.firstBodyCount);
    point.bodyOffset=row*ContactGeometrySettings.bodiesPerContact;
    point.normal=current.normal; point.separation=current.separation; point.worldPoint=current.worldPoint;
    point.firstGeometry=current.first; point.secondGeometry=current.second;
    point.firstFeature=current.firstFeature; point.secondFeature=current.secondFeature; point.pointIndex=current.pointIndex;
    point.predicted=current.prediction.enabled; point.sampleTime=current.prediction.sampleTime;
    DeviceContactBody originLink=DeviceMatchInputBodies[input*ContactGeometrySettings.inputBodiesPerContact+point.firstBodyCount];
    DeviceBodyState origin=DeviceBodyStates[originLink.stateIndex];
    if(point.predicted!=0) origin=deviceBodyStart(origin);
    float3 first=float3(0),second=float3(0);
    for(uint end=0;end<count;end++)
    {
        DeviceContactBody link=DeviceMatchInputBodies[input*ContactGeometrySettings.inputBodiesPerContact+end];
        DeviceContactBodyLinks[point.bodyOffset+end]=link;
        DeviceBodyState body=DeviceBodyStates[link.stateIndex];
        if(point.predicted!=0) body=deviceBodyStart(body);
        float3 anchor=deviceContactRelativeAnchor(body,deviceContactNormalLocalPoint(link),origin.position)*link.weight;
        if(end<point.firstBodyCount) first+=anchor;
        else second+=anchor;
    }
    point.bodyAnchorDelta=second-first; DeviceContacts[row]=point;
}
