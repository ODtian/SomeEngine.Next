#ifndef PHYSICS_CONTACT_VALUES
#define PHYSICS_CONTACT_VALUES
#include "physics_body_math.slang"
#include "physics_constraint_selection.slang"
#include "physics_contact_types.slang"
RWStructuredBuffer<DeviceContactConstraint> DeviceContacts;
RWStructuredBuffer<DeviceConstraintMetadata> DeviceContactsMetadata;
RWStructuredBuffer<DeviceContactBody> DeviceContactBodyLinks;
StructuredBuffer<uint2> DeviceContactsSelection;
bool deviceContactSelected(uint row)
{
    if (!deviceConstraintSelected(DeviceContactsMetadata[row], DeviceContactsSelection[row])) return false;
    DeviceContactConstraint point = DeviceContacts[row];
    for (uint end = 0; end < point.bodyCount; end++)
    {
        DeviceContactBody link = DeviceContactBodyLinks[point.bodyOffset + end];
        DeviceBodyState body = DeviceBodyStates[link.stateIndex];
        if (body.entityIndex != link.entity.x || body.entityGeneration != link.entity.y) return false;
    }
    return true;
}
#endif
