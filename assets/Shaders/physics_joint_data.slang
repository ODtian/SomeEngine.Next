#ifndef PHYSICS_JOINT_DATA
#define PHYSICS_JOINT_DATA
#include "physics_constraint_data.slang"
struct DeviceJointConstraint
{
    uint sourceBody; uint targetBody; uint2 padding0;
    float3 sourcePosition; uint padding1;
    float4 sourceRotation;
    float3 targetPosition; uint padding2;
    float4 targetRotation;
    uint3 linearMotion; uint padding3;
    uint3 angularMotion; uint padding4;
    float3 linearMinimum; uint padding5;
    float3 linearMaximum; uint padding6;
    float3 angularMinimum; uint padding7;
    float3 angularMaximum; uint padding8;
    float3 linearStiffness; uint padding9;
    float3 angularStiffness; uint padding10;
};
RWStructuredBuffer<DeviceJointConstraint> DeviceJoints;
RWStructuredBuffer<DeviceConstraintMetadata> DeviceJointsMetadata;
#endif
