#ifndef PHYSICS_BEND_GRAPH
#define PHYSICS_BEND_GRAPH
#include "physics_constraint_graph.slang"
#include "physics_bend_data.slang"
StructuredBuffer<uint2> DeviceBendsSelection;
struct DeviceBendBodies : IDeviceConstraintBodies
{
    uint count() { uint rows, stride; DeviceBends.GetDimensions(rows, stride); return rows; }
    bool active(uint row) { return deviceConstraintSelected(DeviceBendsMetadata[row], DeviceBendsSelection[row]); }
    uint bodyCount(uint row) { return 3; }
    uint body(uint row, uint end)
    {
        DeviceBendConstraint bend = DeviceBends[row];
        return end == 0 ? bend.firstBody : end == 1 ? bend.middleBody : bend.lastBody;
    }
    bool writes(uint row, uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
