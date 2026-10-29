#ifndef PHYSICS_AVBD_JOINT_STATE
#define PHYSICS_AVBD_JOINT_STATE
struct DeviceAvbdJointState
{
    float3 linearMultiplier; uint padding0;
    float3 angularMultiplier; uint padding1;
    float3 linearPenalty; uint padding2;
    float3 angularPenalty; uint padding3;
    float3 initialLinearError; uint padding4;
    float3 initialAngularError; uint padding5;
};
RWStructuredBuffer<DeviceAvbdJointState> DeviceAvbdJointStates;
StructuredBuffer<uint2> DeviceJointAvbdSelection;
#endif
