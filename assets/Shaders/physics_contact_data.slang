#ifndef PHYSICS_CONTACT_DATA
#define PHYSICS_CONTACT_DATA
#include "physics_constraint_graph.slang"
#include "physics_contact_body.slang"
struct DeviceContactConstraint
{
    uint bodyCount; uint firstBodyCount; uint bodyOffset; uint warmStart;
    float3 normal; float separation;
    float3 bodyAnchorDelta; float normalStiffness;
    float normalDamping; float staticFriction; float dynamicFriction; float restitution;
    float restitutionThreshold; uint predicted; uint2 sampleTime;
};
RWStructuredBuffer<DeviceContactConstraint> DeviceContacts;
RWStructuredBuffer<DeviceConstraintMetadata> DeviceContactsMetadata;
RWStructuredBuffer<DeviceContactBody> DeviceContactBodyLinks;
StructuredBuffer<uint2> DeviceContactsSelection;
struct DeviceContactBodies : IDeviceConstraintBodies
{
    uint count() { uint rows,stride; DeviceContacts.GetDimensions(rows,stride); return rows; }
    bool active(uint row)
    {
        if(!deviceConstraintSelected(DeviceContactsMetadata[row],DeviceContactsSelection[row])) return false;
        DeviceContactConstraint point=DeviceContacts[row];
        for(uint end=0;end<point.bodyCount;end++)
        {
            DeviceContactBody link=DeviceContactBodyLinks[point.bodyOffset+end];
            DeviceBodyState body=DeviceBodyStates[link.stateIndex];
            if(body.entityIndex!=link.entity.x||body.entityGeneration!=link.entity.y) return false;
        }
        return true;
    }
    uint bodyCount(uint row) { return DeviceContacts[row].bodyCount; }
    uint body(uint row,uint end) { return DeviceContactBodyLinks[DeviceContacts[row].bodyOffset+end].stateIndex; }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
