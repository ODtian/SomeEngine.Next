#ifndef PHYSICS_CONTACT_DATA
#define PHYSICS_CONTACT_DATA
#include "physics_constraint_graph.slang"
#include "physics_contact_values.slang"
struct DeviceContactBodies : IDeviceConstraintBodies
{
    uint count() { uint rows,stride; DeviceContactPairIndices.GetDimensions(rows,stride); return rows; }
    bool active(uint row) { return deviceContactSelected(row)&&(DeviceContactFlags[row]&2)==0; }
    uint bodyCount(uint row) { return DeviceContactBodyCounts[row]; }
    uint body(uint row,uint end) { return DeviceContactBodyStateIndices[DeviceContactBodyStarts[row]+end]; }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
