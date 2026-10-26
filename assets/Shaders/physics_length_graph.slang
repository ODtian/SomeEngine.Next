#ifndef PHYSICS_LENGTH_GRAPH
#define PHYSICS_LENGTH_GRAPH
#include "physics_constraint_graph.slang"
#include "physics_length_data.slang"
StructuredBuffer<uint2> DeviceLengthsSelection;
struct DeviceLengthBodies : IDeviceConstraintBodies
{
    uint count() { uint rows, stride; DeviceLengthsMetadata.GetDimensions(rows, stride); return rows; }
    bool active(uint row) { return deviceConstraintSelected(DeviceLengthsMetadata[row], DeviceLengthsSelection[row]); }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row, uint end) { return end == 0 ? DeviceLengths[row].firstBody : DeviceLengths[row].secondBody; }
    bool writes(uint row, uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
