#ifndef PHYSICS_CONTACT_DATA
#define PHYSICS_CONTACT_DATA
#include "physics_constraint_graph.slang"
#include "physics_contact_values.slang"
struct DeviceContactBodies : IDeviceConstraintBodies
{
    uint count() { uint rows,stride; DeviceContacts.GetDimensions(rows,stride); return rows; }
    bool active(uint row) { return deviceContactSelected(row); }
    uint bodyCount(uint row) { return DeviceContacts[row].bodyCount; }
    uint body(uint row,uint end) { return DeviceContactBodyLinks[DeviceContacts[row].bodyOffset+end].stateIndex; }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
