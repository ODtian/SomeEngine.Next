#include "physics_contact_types.slang"
#include "physics_collision_contact.slang"
#include "physics_constraint_math.slang"
struct DeviceContactGeometrySettings { uint pairs; uint bodiesPerContact; };
ConstantBuffer<DeviceContactGeometrySettings> ContactGeometrySettings;
StructuredBuffer<uint> DeviceContactGeometryRows;
#include "physics_dispatch.slang"
StructuredBuffer<DeviceCollisionContact> DeviceMatchInputContacts;
StructuredBuffer<uint> DeviceMatchInputCounts;
StructuredBuffer<uint> DeviceMatchInputBodyCounts;
StructuredBuffer<DeviceContactBody> DeviceMatchInputBodies;
RWStructuredBuffer<DeviceContactConstraint> DeviceContacts;
RWStructuredBuffer<DeviceContactBody> DeviceContactBodyLinks;
[shader("compute")][numthreads(64,1,1)]
void UpdateDeviceContactGeometry(uint3 id:SV_DispatchThreadID)
{
    uint input=devicePhysicsThreadIndex(id,64);
    if(input>=ContactGeometrySettings.pairs*4) return;
    if(input%4>=DeviceMatchInputCounts[input/4]) return;
    uint row=DeviceContactGeometryRows[input];
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
    float3 delta=float3(0);
    for(uint end=0;end<count;end++)
    {
        DeviceContactBody link=DeviceMatchInputBodies[input*6+end];
        DeviceContactBodyLinks[point.bodyOffset+end]=link;
        DeviceBodyState body=DeviceBodyStates[link.stateIndex];
        if(point.predicted!=0) body=deviceBodyStart(body);
        float3 anchor=body.position+bodyRotate(body.rotation,link.localPoint);
        delta+=anchor*(link.weight*(end<point.firstBodyCount?-1:1));
    }
    point.bodyAnchorDelta=delta; DeviceContacts[row]=point;
}
