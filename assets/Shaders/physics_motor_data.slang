#ifndef PHYSICS_MOTOR_DATA
#define PHYSICS_MOTOR_DATA
#include "physics_joint_data.slang"
struct DeviceMotorConstraint
{
    uint jointRow; uint space; uint response; uint padding0;
    float3 linearStiffness; uint padding1;
    float3 linearDamping; uint padding2;
    float3 maximumForce; uint padding3;
    float3 angularStiffness; uint padding4;
    float3 angularDamping; uint padding5;
    float3 maximumTorque; uint padding6;
    float3 targetPosition; uint padding7;
    float4 targetRotation;
    float3 targetLinearVelocity; uint padding8;
    float3 targetAngularVelocity; uint padding9;
    uint2 targetTime; uint2 padding10;
};
RWStructuredBuffer<DeviceMotorConstraint> DeviceMotors;
RWStructuredBuffer<DeviceConstraintMetadata> DeviceMotorsMetadata;
#endif
