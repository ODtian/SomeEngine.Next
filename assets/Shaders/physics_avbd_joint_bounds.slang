// SPDX-License-Identifier: Apache-2.0
// Current distance forces and curvature follow JointLongRangeData and Newton v1.7.0.
#ifndef PHYSICS_AVBD_JOINT_BOUNDS
#define PHYSICS_AVBD_JOINT_BOUNDS
#include "physics_avbd.slang"
#include "physics_joint_bound_bodies.slang"
#include "physics_constraint_math.slang"
#include "physics_avbd_distance_math.slang"
#include "physics_joint_bound_geometry.slang"
struct DeviceJointBoundState
{
    float2 multiplier; float penalty; uint padding0;
    float2 initialError; uint2 padding1;
};
RWStructuredBuffer<DeviceJointBoundState> DeviceJointBoundStates;
void deviceEvaluateJointBound(DeviceJointBoundConstraint bound,DeviceJointBoundState state,float distance,
    out float force,out float penalty,out float2 multiplier)
{
    deviceEvaluateDistanceRange(bound.minimum,bound.maximum,asfloat(0x7f800000u),state.penalty,
        state.multiplier,state.initialError,JointLongRangeSettings.initialErrorFraction,distance,force,penalty,multiplier);
}
struct DeviceJointBoundAvbdConstraints : IDeviceAvbdConstraints
{
    DeviceJointBoundBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
    void initialize(uint row,DeviceAvbdStep context)
    {
        DeviceJointBoundConstraint bound=DeviceJointBoundValues[row];
        DeviceJointBoundState state=DeviceJointBoundStates[row];
        DeviceJointBoundGeometry geometry=deviceJointBoundGeometry(row,false),start=deviceJointBoundGeometry(row,true);
        float3 direction=geometry.distance>0?geometry.delta/geometry.distance:float3(0);
        DeviceSelectedBodies selected;
        float mobility=0;
        if(selected.contains(geometry.firstBody))
            mobility+=bodyConstraintWeight(DeviceBodyStates[geometry.firstBody],direction,cross(geometry.firstOffset,direction));
        if(selected.contains(geometry.lastBody))
            mobility+=bodyConstraintWeight(DeviceBodyStates[geometry.lastBody],direction,cross(geometry.lastOffset,direction));
        state.penalty=mobility>0?JointLongRangeSettings.penaltyScale/(context.duration*context.duration*mobility):0;
        state.initialError=float2(max(0,start.distance-bound.maximum),max(0,bound.minimum-start.distance));
        state.multiplier*=JointLongRangeSettings.retention;
        DeviceJointBoundStates[row]=state;
    }
    void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    {
        DeviceJointBoundGeometry geometry=deviceJointBoundGeometry(row,false);
        float force,penalty; float2 multiplier;
        deviceEvaluateJointBound(DeviceJointBoundValues[row],DeviceJointBoundStates[row],geometry.distance,force,penalty,multiplier);
        float side=bodyIndex==geometry.lastBody?1:-1;
        float3 offset=bodyIndex==geometry.lastBody?geometry.lastOffset:geometry.firstOffset;
        deviceAccumulateDistanceRange(geometry.delta,geometry.distance,offset,side,force,penalty,equation);
    }
    void updateDuals(uint row,DeviceAvbdStep context)
    {
        DeviceJointBoundState state=DeviceJointBoundStates[row];
        float force,penalty; float2 multiplier;
        deviceEvaluateJointBound(DeviceJointBoundValues[row],state,deviceJointBoundGeometry(row,false).distance,force,penalty,multiplier);
        state.multiplier=multiplier; DeviceJointBoundStates[row]=state;
    }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
#endif
