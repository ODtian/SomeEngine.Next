#ifndef PHYSICS_AVBD_DISTANCE_RANGE
#define PHYSICS_AVBD_DISTANCE_RANGE
#include "physics_distance_range_bodies.slang"
#include "physics_avbd_distance_math.slang"
#include "physics_constraint_math.slang"
struct DeviceAvbdDistanceRangeState
{
    float2 multiplier; float penalty; uint padding0;
    float2 initialError; uint2 padding1;
};
RWStructuredBuffer<DeviceAvbdDistanceRangeState> DeviceAvbdDistanceRangeStates;
StructuredBuffer<uint2> DeviceDistanceRangeAvbdSelection;
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
struct DeviceDistanceRangeAvbdConstraints : IDeviceAvbdConstraints
{
    DeviceDistanceRangeBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row)
    { return participants.active(row)&&deviceConstraintSelected(DeviceDistanceRangesMetadata[row],DeviceDistanceRangeAvbdSelection[row]); }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
    void initialize(uint row,DeviceAvbdStep context)
    {
        DeviceDistanceRangeConstraint range=DeviceDistanceRanges[row];
        DeviceDistanceRangeGeometry start=deviceDistanceRangeGeometry(row,true);
        DeviceAvbdDistanceRangeState state=DeviceAvbdDistanceRangeStates[row];
        state.multiplier*=context.retention;
        state.initialError=float2(max(0,start.distance-range.maximum),max(0,range.minimum-start.distance));
        DeviceAvbdDistanceRangeStates[row]=state;
    }
    void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    {
        DeviceDistanceRangeConstraint range=DeviceDistanceRanges[row];
        DeviceAvbdDistanceRangeState state=DeviceAvbdDistanceRangeStates[row];
        DeviceDistanceRangeGeometry geometry=deviceDistanceRangeGeometry(row,false);
        float force,curvature; float2 multiplier;
        deviceEvaluateDistanceRange(range.minimum,range.maximum,range.stiffness,state.penalty,state.multiplier,
            state.initialError,context.initialErrorFraction,geometry.distance,force,curvature,multiplier);
        bool second=bodyIndex==range.secondBody;
        deviceAccumulateDistanceRange(geometry.delta,geometry.distance,second?geometry.secondOffset:geometry.firstOffset,
            second?1:-1,force,curvature,equation);
    }
    void updateDuals(uint row,DeviceAvbdStep context)
    {
        DeviceDistanceRangeConstraint range=DeviceDistanceRanges[row];
        DeviceAvbdDistanceRangeState state=DeviceAvbdDistanceRangeStates[row];
        float force,curvature; float2 multiplier;
        deviceEvaluateDistanceRange(range.minimum,range.maximum,range.stiffness,state.penalty,state.multiplier,
            state.initialError,context.initialErrorFraction,deviceDistanceRangeGeometry(row,false).distance,force,curvature,multiplier);
        state.multiplier=multiplier; DeviceAvbdDistanceRangeStates[row]=state;
    }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
#endif
