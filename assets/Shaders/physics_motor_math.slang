#ifndef PHYSICS_MOTOR_MATH
#define PHYSICS_MOTOR_MATH
#include "physics_joint_math.slang"
#include "physics_motor_data.slang"
struct DeviceMotorRow
{
    DeviceJointRow derivative;
    float error; float initialError; float stiffness; float maximum;
};
DeviceBodyState deviceBodyStart(DeviceBodyState body)
{
    body.rotation = body.startRotation;
    bodySetCenterOfMass(body, body.startCenterOfMass);
    return body;
}
DeviceMotorRow deviceMotorRow(DeviceMotorConstraint motor, DeviceBodyState source, DeviceBodyState target,
    float duration, double endTime, uint dof)
{
    DeviceJointConstraint joint = DeviceJoints[motor.jointRow];
    DeviceBodyState sourceStart = deviceBodyStart(source), targetStart = deviceBodyStart(target);
    uint axis = dof % 3;
    float3 direction = axis == 0 ? float3(1,0,0) : axis == 1 ? float3(0,1,0) : float3(0,0,1);
    float3 start;
    DeviceJointRow derivative;
    if (motor.space == 0)
    {
        float3 linear, angular;
        deviceJointCoordinates(joint, sourceStart, targetStart, linear, angular);
        start = dof < 3 ? linear : angular;
        derivative = deviceJointRow(joint, source, target, dof);
    }
    else if (dof < 3)
    {
        float3 point = target.position + bodyRotate(target.rotation, joint.targetPosition);
        start = targetStart.position + bodyRotate(targetStart.rotation, joint.targetPosition);
        derivative.coordinate = point[axis]; derivative.sourceLinear = -direction;
        derivative.sourceAngular = -cross(point - bodyCenterOfMass(source), direction);
        derivative.targetLinear = direction; derivative.targetAngular = cross(point - bodyCenterOfMass(target), direction);
    }
    else
    {
        float4 rotation = bodyRotationMultiply(target.rotation, joint.targetRotation);
        float3 angular = bodyRotationVector(rotation);
        float3 jacobian = bodyConstraintJacobian(float4(0,0,0,1), rotation, angular, direction);
        start = bodyRotationVector(bodyRotationMultiply(targetStart.rotation, joint.targetRotation));
        derivative.coordinate = angular[axis]; derivative.sourceLinear = derivative.targetLinear = float3(0);
        derivative.sourceAngular = -jacobian; derivative.targetAngular = jacobian;
    }
    float spring = dof < 3 ? motor.linearStiffness[axis] : motor.angularStiffness[axis];
    float damping = (dof < 3 ? motor.linearDamping[axis] : motor.angularDamping[axis]) / duration;
    float stiffness = spring + damping;
    float elapsed = float(endTime - asdouble(motor.targetTime.x, motor.targetTime.y));
    float position, speed;
    if (dof < 3)
    { position = motor.targetPosition[axis] + motor.targetLinearVelocity[axis] * elapsed; speed = motor.targetLinearVelocity[axis]; }
    else
    {
        float4 rotation = bodyIntegrateRotation(motor.targetRotation, motor.targetAngularVelocity, elapsed);
        float3 angular = bodyRotationVector(rotation);
        float3 jacobian = bodyConstraintJacobian(float4(0,0,0,1), rotation, angular, direction);
        position = angular[axis]; speed = dot(jacobian, motor.targetAngularVelocity);
    }
    float equilibrium = (spring * position + damping * (start[axis] + speed * duration)) / stiffness;
    DeviceMotorRow row;
    row.derivative = derivative; row.error = derivative.coordinate - equilibrium;
    row.initialError = start[axis] - equilibrium; row.stiffness = stiffness;
    row.maximum = dof < 3 ? motor.maximumForce[axis] : motor.maximumTorque[axis];
    return row;
}
#endif
