#ifndef PHYSICS_XPBD_JOINT
#define PHYSICS_XPBD_JOINT
#include "physics_xpbd_selection.slang"
#include "physics_joint_math.slang"
struct DeviceXpbdJointState
{
    float3 linearMultiplier;
    float3 angularMultiplier;
};
RWStructuredBuffer<DeviceXpbdJointState> DeviceJointMultipliers;
struct DeviceXpbdJointProjection<TSelection : IDeviceBodySelection> : IDeviceXpbdProjection
{
    bool active(uint row) { return DeviceJointsMetadata[row].active != 0; }
    void initialize(uint row, DeviceXpbdStep context) { DeviceJointMultipliers[row] = (DeviceXpbdJointState)0; }
    void project(uint row, DeviceXpbdStep context)
    {
        DeviceJointConstraint joint = DeviceJoints[row];
        TSelection selection;
        if (DeviceBodyStates[joint.sourceBody].motion != 2 && DeviceBodyStates[joint.targetBody].motion != 2) return;
        DeviceXpbdJointState state = DeviceJointMultipliers[row];
        for (uint dof = 0; dof < 6; dof++)
        {
            uint axis = dof % 3;
            DeviceJointLimit limit = deviceJointLimit(joint, dof);
            float stiffness = dof < 3 ? joint.linearStiffness[axis] : joint.angularStiffness[axis];
            if (limit.motion == 0 || stiffness == 0) continue;
            DeviceBodyState source = DeviceBodyStates[joint.sourceBody], target = DeviceBodyStates[joint.targetBody];
            DeviceJointRow derivative = deviceJointRow(joint, source, target, dof);
            float multiplier = dof < 3 ? state.linearMultiplier[axis] : state.angularMultiplier[axis];
            float error; uint side;
            if (!deviceJointTryError(limit, derivative.coordinate, -multiplier, error, side)) continue;
            float alpha = 1 / (stiffness * context.duration * context.duration);
            float weight = deviceXpbdBodyWeight(joint.sourceBody, derivative.sourceLinear, derivative.sourceAngular, selection) +
                deviceXpbdBodyWeight(joint.targetBody, derivative.targetLinear, derivative.targetAngular, selection);
            float change = (-error - alpha * multiplier) / (weight + alpha);
            float next = -deviceJointProjectForce(side, -(multiplier + change));
            change = next - multiplier;
            if (dof < 3) state.linearMultiplier[axis] = next;
            else state.angularMultiplier[axis] = next;
            deviceXpbdBodyCorrection(joint.sourceBody, derivative.sourceLinear, derivative.sourceAngular, change, selection);
            deviceXpbdBodyCorrection(joint.targetBody, derivative.targetLinear, derivative.targetAngular, change, selection);
        }
        DeviceJointMultipliers[row] = state;
    }
};
struct DeviceXpbdJoint : IDeviceXpbdProjection
{
    DeviceXpbdJointProjection<DeviceSelectedBodies> inner;
    bool active(uint row) { return inner.active(row); }
    void initialize(uint row, DeviceXpbdStep context) { inner.initialize(row, context); }
    void project(uint row, DeviceXpbdStep context) { inner.project(row, context); }
};
#endif
