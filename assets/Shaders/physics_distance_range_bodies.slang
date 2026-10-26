#ifndef PHYSICS_DISTANCE_RANGE_BODIES
#define PHYSICS_DISTANCE_RANGE_BODIES
#include "physics_constraint_graph.slang"
#include "physics_distance_range_data.slang"
StructuredBuffer<uint2> DeviceDistanceRangesSelection;
struct DeviceDistanceRangeBodies : IDeviceConstraintBodies
{
    uint count() { uint rows,stride; DeviceDistanceRanges.GetDimensions(rows,stride); return rows; }
    bool active(uint row)
    { return deviceConstraintSelected(DeviceDistanceRangesMetadata[row],DeviceDistanceRangesSelection[row]); }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end)
    { return end==0?DeviceDistanceRanges[row].firstBody:DeviceDistanceRanges[row].secondBody; }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
