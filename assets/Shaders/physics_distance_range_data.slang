#ifndef PHYSICS_DISTANCE_RANGE_DATA
#define PHYSICS_DISTANCE_RANGE_DATA
#include "physics_constraint_selection.slang"
struct DeviceDistanceRangeConstraint
{
    uint firstBody; uint secondBody; uint2 padding0;
    float3 firstAnchor; uint padding1;
    float3 secondAnchor; uint padding2;
    float minimum; float maximum; float stiffness; uint padding3;
};
RWStructuredBuffer<DeviceDistanceRangeConstraint> DeviceDistanceRanges;
RWStructuredBuffer<DeviceConstraintMetadata> DeviceDistanceRangesMetadata;
#endif
