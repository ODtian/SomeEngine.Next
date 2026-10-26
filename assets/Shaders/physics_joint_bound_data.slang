#ifndef PHYSICS_JOINT_BOUND_DATA
#define PHYSICS_JOINT_BOUND_DATA
#include "physics_joint_data.slang"
struct DeviceJointBoundSet { uint jointRow; uint boundOffset; uint count; uint padding; };
struct DeviceJointBoundConstraint
{
    uint otherJointRow; uint firstEnd; uint otherEnd; uint otherEntityIndex;
    uint otherEntityGeneration; float minimum; float maximum; uint padding;
};
struct DeviceJointLongRangeSettings
{ float penaltyScale; float retention; float initialErrorFraction; uint boundsPerJoint; };
RWStructuredBuffer<DeviceJointBoundSet> DeviceJointBoundSets;
RWStructuredBuffer<DeviceConstraintMetadata> DeviceJointBoundSetsMetadata;
RWStructuredBuffer<DeviceJointBoundConstraint> DeviceJointBoundValues;
ConstantBuffer<DeviceJointLongRangeSettings> JointLongRangeSettings;
#endif
