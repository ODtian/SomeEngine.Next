#include "physics_contact_types.slang"
#include "physics_constraint_selection.slang"
#include "physics_dispatch.slang"

struct ContactInputChange
{
    uint row; uint detectedInput; uint2 entity; uint inputVersion; uint warmStart; uint trigger;
    float normalStiffness; float normalDamping; float staticFriction; float dynamicFriction;
    float restitution; float restitutionThreshold;
};
struct ContactInputCounts { uint inputs; uint removed; };
ConstantBuffer<ContactInputCounts, ScalarDataLayout> ContactIdentityInputCounts;
StructuredBuffer<ContactInputChange, ScalarDataLayout> ContactIdentityInputs;
RWStructuredBuffer<DeviceContactConstraint, ScalarDataLayout> DeviceContacts;
RWStructuredBuffer<DeviceConstraintMetadata, ScalarDataLayout> DeviceContactsMetadata;
RWStructuredBuffer<uint, ScalarDataLayout> ContactRemovedRows;
RWStructuredBuffer<uint2, ScalarDataLayout> ContactMatchingEntities;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactGeometryRows;

[shader("compute")][numthreads(64,1,1)]
void ClearRemovedContactInputs(uint3 id:SV_DispatchThreadID)
{
    uint input=devicePhysicsThreadIndex(id,64);
    if(input<ContactIdentityInputCounts.removed)
    {
        uint row=ContactRemovedRows[1+input];
        DeviceConstraintMetadata metadata=DeviceContactsMetadata[row];
        if(all(uint2(metadata.entityIndex,metadata.entityGeneration)==ContactMatchingEntities[row]))
        {
            DeviceContactsMetadata[row]=(DeviceConstraintMetadata)0;
            ContactMatchingEntities[row]=uint2(0);
        }
    }
}
[shader("compute")][numthreads(64,1,1)]
void WriteContactIdentityInputs(uint3 id:SV_DispatchThreadID)
{
    uint input=devicePhysicsThreadIndex(id,64);
    if(input>=ContactIdentityInputCounts.inputs) return;
    ContactInputChange change=ContactIdentityInputs[input];
    DeviceContactConstraint point=DeviceContacts[change.row];
    point.warmStart=change.warmStart; point.trigger=change.trigger;
    point.normalStiffness=change.normalStiffness; point.normalDamping=change.normalDamping;
    point.staticFriction=change.staticFriction; point.dynamicFriction=change.dynamicFriction;
    point.restitution=change.restitution; point.restitutionThreshold=change.restitutionThreshold;
    DeviceContacts[change.row]=point;
    DeviceConstraintMetadata metadata;
    metadata.entityIndex=change.entity.x; metadata.entityGeneration=change.entity.y;
    metadata.active=1; metadata.inputVersion=change.inputVersion;
    DeviceContactsMetadata[change.row]=metadata;
    ContactMatchingEntities[change.row]=change.entity;
    if(change.detectedInput!=0xffffffff)
        DeviceContactGeometryRows[change.detectedInput]=change.row|0x80000000u;
}
