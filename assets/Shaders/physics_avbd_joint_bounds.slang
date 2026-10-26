// SPDX-License-Identifier: Apache-2.0
// Current distance forces and curvature follow JointLongRangeData and Newton v1.7.0.
#ifndef PHYSICS_AVBD_JOINT_BOUNDS
#define PHYSICS_AVBD_JOINT_BOUNDS
#include "physics_avbd.slang"
#include "physics_joint_bound_bodies.slang"
#include "physics_constraint_math.slang"
struct DeviceJointBoundState
{
    float2 multiplier; float penalty; uint padding0;
    float2 initialError; uint2 padding1;
};
RWStructuredBuffer<DeviceJointBoundState> DeviceJointBoundStates;
struct DeviceJointBoundGeometry
{
    uint firstBody; uint lastBody;
    float3 firstOffset; float3 lastOffset;
    float3 delta; float distance;
};
DeviceJointBoundGeometry deviceJointBoundGeometry(uint row,bool start)
{
    DeviceJointBoundConstraint bound=DeviceJointBoundValues[row];
    DeviceJointConstraint first=DeviceJoints[DeviceJointBoundSets[deviceJointBoundSetIndex(row)].jointRow];
    DeviceJointConstraint last=DeviceJoints[bound.otherJointRow];
    DeviceJointBoundGeometry geometry;
    geometry.firstBody=bound.firstEnd==0?first.sourceBody:first.targetBody;
    geometry.lastBody=bound.otherEnd==0?last.sourceBody:last.targetBody;
    float3 firstLocal=bound.firstEnd==0?first.sourcePosition:first.targetPosition;
    float3 lastLocal=bound.otherEnd==0?last.sourcePosition:last.targetPosition;
    DeviceBodyState firstBody=DeviceBodyStates[geometry.firstBody],lastBody=DeviceBodyStates[geometry.lastBody];
    if(start) { firstBody=deviceBodyStart(firstBody); lastBody=deviceBodyStart(lastBody); }
    geometry.firstOffset=bodyRotate(firstBody.rotation,firstLocal-firstBody.centerOfMass);
    geometry.lastOffset=bodyRotate(lastBody.rotation,lastLocal-lastBody.centerOfMass);
    geometry.delta=bodyCenterOfMass(lastBody)+geometry.lastOffset-bodyCenterOfMass(firstBody)-geometry.firstOffset;
    geometry.distance=length(geometry.delta);
    return geometry;
}
void deviceEvaluateJointBound(DeviceJointBoundConstraint bound,DeviceJointBoundState state,float distance,
    out float force,out float penalty,out float2 multiplier)
{
    if(bound.minimum==bound.maximum)
    {
        force=state.multiplier.x+state.penalty*(distance-bound.maximum
            -JointLongRangeSettings.initialErrorFraction*(state.initialError.x-state.initialError.y));
        penalty=state.penalty; multiplier=float2(force,0); return;
    }
    float maximum=bound.maximum+JointLongRangeSettings.initialErrorFraction*state.initialError.x;
    float minimum=bound.minimum-JointLongRangeSettings.initialErrorFraction*state.initialError.y;
    float upper=max(0,state.multiplier.x+state.penalty*(distance-maximum));
    float lower=max(0,state.multiplier.y+state.penalty*(minimum-distance));
    force=upper-lower; penalty=state.penalty*((upper>0?1:0)+(lower>0?1:0)); multiplier=float2(upper,lower);
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
        if(penalty==0) return;
        float3 direction=geometry.distance>0?geometry.delta/geometry.distance:float3(0);
        float side=bodyIndex==geometry.lastBody?1:-1;
        float3 offset=bodyIndex==geometry.lastBody?geometry.lastOffset:geometry.firstOffset;
        float3 linear=direction*side,angular=cross(offset,linear);
        deviceAvbdAddRow(equation,linear,angular,force,penalty);
        if(force==0||geometry.distance==0) return;
        float magnitude=abs(force),radiusSquared=dot(offset,offset),radial=dot(offset,direction);
        for(uint axis=0;axis<3;axis++)
        {
            float3 unit=axis==0?float3(1,0,0):axis==1?float3(0,1,0):float3(0,0,1);
            float linearCurvature=magnitude/geometry.distance*max(0,1-direction[axis]*direction[axis]);
            float angularCurvature=max(0,(radiusSquared-offset[axis]*offset[axis]-angular[axis]*angular[axis])/geometry.distance);
            float3 column=offset*direction[axis]-unit*radial;
            deviceAvbdAddRow(equation,unit,float3(0),0,linearCurvature);
            deviceAvbdAddRow(equation,float3(0),unit,0,magnitude*(angularCurvature+length(column)));
        }
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
