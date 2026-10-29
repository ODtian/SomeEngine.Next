#ifndef PHYSICS_JOINT_BOUND_BODIES
#define PHYSICS_JOINT_BOUND_BODIES
#include "physics_constraint_graph.slang"
#include "physics_joint_bound_data.slang"
StructuredBuffer<uint2> DeviceJointBoundSetsSelection;
uint deviceJointBoundSetIndex(uint row) { return row/JointLongRangeSettings.boundsPerJoint; }
struct DeviceJointBoundBodies : IDeviceConstraintBodies
{
    uint count() { uint rows,stride; DeviceJointBoundValues.GetDimensions(rows,stride); return rows; }
    bool active(uint row)
    {
        uint setIndex=deviceJointBoundSetIndex(row);
        DeviceJointBoundSet set=DeviceJointBoundSets[setIndex];
        if(row%JointLongRangeSettings.boundsPerJoint>=set.count) return false;
        DeviceConstraintMetadata metadata=DeviceJointBoundSetsMetadata[setIndex];
        if(!deviceConstraintSelected(metadata,DeviceJointBoundSetsSelection[setIndex])) return false;
        DeviceConstraintMetadata first=DeviceJointsMetadata[set.jointRow];
        DeviceJointBoundConstraint bound=DeviceJointBoundValues[row];
        DeviceConstraintMetadata last=DeviceJointsMetadata[bound.otherJointRow];
        return first.active!=0&&first.entityIndex==metadata.entityIndex&&first.entityGeneration==metadata.entityGeneration
            &&last.active!=0&&last.entityIndex==bound.otherEntityIndex&&last.entityGeneration==bound.otherEntityGeneration;
    }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end)
    {
        DeviceJointBoundConstraint bound=DeviceJointBoundValues[row];
        DeviceJointConstraint first=DeviceJoints[DeviceJointBoundSets[deviceJointBoundSetIndex(row)].jointRow];
        DeviceJointConstraint last=DeviceJoints[bound.otherJointRow];
        uint firstBody=bound.firstEnd==0?first.sourceBody:first.targetBody;
        uint lastBody=bound.otherEnd==0?last.sourceBody:last.targetBody;
        return end==0?firstBody:lastBody;
    }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
