#ifndef PHYSICS_XPBD_MOTOR
#define PHYSICS_XPBD_MOTOR
#include "physics_xpbd_selection.slang"
#include "physics_motor_math.slang"
struct DeviceXpbdMotorState
{
    float3 linearMultiplier;
    float3 angularMultiplier;
};
RWStructuredBuffer<DeviceXpbdMotorState> DeviceMotorMultipliers;
struct DeviceXpbdMotorProjection<TSelection : IDeviceBodySelection> : IDeviceXpbdProjection
{
    bool active(uint row) { return DeviceMotorsMetadata[row].active != 0; }
    void initialize(uint row, DeviceXpbdStep context) { DeviceMotorMultipliers[row] = (DeviceXpbdMotorState)0; }
    void project(uint row, DeviceXpbdStep context)
    {
        DeviceMotorConstraint motor = DeviceMotors[row];
        TSelection selection;
        DeviceJointConstraint joint = DeviceJoints[motor.jointRow];
        bool react = motor.response == 1;
        if (DeviceBodyStates[joint.targetBody].motion != 2 && !(react && DeviceBodyStates[joint.sourceBody].motion == 2)) return;
        DeviceXpbdMotorState state = DeviceMotorMultipliers[row];
        for (uint dof = 0; dof < 6; dof++)
        {
            uint axis = dof % 3;
            if (dof < 3 ? motor.linearStiffness[axis] == 0 && motor.linearDamping[axis] == 0
                : motor.angularStiffness[axis] == 0 && motor.angularDamping[axis] == 0) continue;
            DeviceBodyState source = DeviceBodyStates[joint.sourceBody], target = DeviceBodyStates[joint.targetBody];
            DeviceMotorRow drive = deviceMotorRow(motor, source, target, context.duration,
                asdouble(context.endTime.x, context.endTime.y), dof);
            float alpha = 1 / (drive.stiffness * context.duration * context.duration);
            float weight = deviceXpbdBodyWeight(joint.targetBody, drive.derivative.targetLinear, drive.derivative.targetAngular, selection);
            if (react && motor.space == 0)
                weight += deviceXpbdBodyWeight(joint.sourceBody, drive.derivative.sourceLinear, drive.derivative.sourceAngular, selection);
            float multiplier = dof < 3 ? state.linearMultiplier[axis] : state.angularMultiplier[axis];
            float change = (-drive.error - alpha * multiplier) / (weight + alpha);
            float maximum = drive.maximum * context.duration * context.duration;
            float next = clamp(multiplier + change, -maximum, maximum);
            change = next - multiplier;
            if (dof < 3) state.linearMultiplier[axis] = next;
            else state.angularMultiplier[axis] = next;
            deviceXpbdBodyCorrection(joint.targetBody, drive.derivative.targetLinear, drive.derivative.targetAngular, change, selection);
            if (react) deviceXpbdBodyCorrection(joint.sourceBody, drive.derivative.sourceLinear, drive.derivative.sourceAngular, change, selection);
        }
        DeviceMotorMultipliers[row] = state;
    }
};
struct DeviceXpbdMotor : IDeviceXpbdProjection
{
    DeviceXpbdMotorProjection<DeviceSelectedBodies> inner;
    bool active(uint row) { return inner.active(row); }
    void initialize(uint row, DeviceXpbdStep context) { inner.initialize(row, context); }
    void project(uint row, DeviceXpbdStep context) { inner.project(row, context); }
};
#endif
