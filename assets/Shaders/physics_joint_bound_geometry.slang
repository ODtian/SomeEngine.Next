// SPDX-License-Identifier: Apache-2.0
#ifndef PHYSICS_JOINT_BOUND_GEOMETRY
#define PHYSICS_JOINT_BOUND_GEOMETRY
#include "physics_joint_bound_bodies.slang"
#include "physics_constraint_math.slang"
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
#endif
