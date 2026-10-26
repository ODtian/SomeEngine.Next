#ifndef PHYSICS_DISTANCE_RANGE_GEOMETRY
#define PHYSICS_DISTANCE_RANGE_GEOMETRY
#include "physics_distance_range_data.slang"
#include "physics_constraint_math.slang"
struct DeviceDistanceRangeGeometry
{
    float3 firstOffset; float3 secondOffset;
    float3 delta; float distance;
};
DeviceDistanceRangeGeometry deviceDistanceRangeGeometry(uint row,bool start)
{
    DeviceDistanceRangeConstraint range=DeviceDistanceRanges[row];
    DeviceBodyState first=DeviceBodyStates[range.firstBody],second=DeviceBodyStates[range.secondBody];
    if(start) { first=deviceBodyStart(first); second=deviceBodyStart(second); }
    DeviceDistanceRangeGeometry geometry;
    geometry.firstOffset=bodyRotate(first.rotation,range.firstAnchor-first.centerOfMass);
    geometry.secondOffset=bodyRotate(second.rotation,range.secondAnchor-second.centerOfMass);
    geometry.delta=bodyCenterOfMass(second)+geometry.secondOffset-bodyCenterOfMass(first)-geometry.firstOffset;
    geometry.distance=length(geometry.delta);
    return geometry;
}
#endif
