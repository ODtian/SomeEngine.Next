// SPDX-License-Identifier: Apache-2.0
// Joint coordinates and Jacobians follow the shared C# JointMath and Newton v1.7.0.
#ifndef PHYSICS_JOINT_MATH
#define PHYSICS_JOINT_MATH
#include "physics_body_math.slang"
#include "physics_joint_data.slang"
struct DeviceJointRow
{
    float coordinate;
    float3 sourceLinear; float3 sourceAngular;
    float3 targetLinear; float3 targetAngular;
};
struct DeviceJointLimit { uint motion; float minimum; float maximum; };
void deviceJointCoordinates(DeviceJointConstraint joint, DeviceBodyState source, DeviceBodyState target,
    out float3 linear, out float3 angular)
{
    float3 first = source.position + bodyRotate(source.rotation, joint.sourcePosition);
    float3 second = target.position + bodyRotate(target.rotation, joint.targetPosition);
    float4 firstFrame = bodyRotationMultiply(source.rotation, joint.sourceRotation);
    float4 secondFrame = bodyRotationMultiply(target.rotation, joint.targetRotation);
    linear = bodyRotate(bodyRotationConjugate(firstFrame), second - first);
    angular = bodyRotationVector(bodyRotationMultiply(bodyRotationConjugate(firstFrame), secondFrame));
}
DeviceJointRow deviceJointRow(DeviceJointConstraint joint, DeviceBodyState source, DeviceBodyState target, uint dof)
{
    uint axis = dof % 3;
    float3 local = axis == 0 ? float3(1,0,0) : axis == 1 ? float3(0,1,0) : float3(0,0,1);
    float4 frame = bodyRotationMultiply(source.rotation, joint.sourceRotation);
    DeviceJointRow row;
    if (dof < 3)
    {
        float3 first = source.position + bodyRotate(source.rotation, joint.sourcePosition);
        float3 second = target.position + bodyRotate(target.rotation, joint.targetPosition);
        float3 direction = bodyRotate(frame, local);
        row.coordinate = dot(second - first, direction);
        row.sourceLinear = -direction;
        row.sourceAngular = -cross(second - bodyCenterOfMass(source), direction);
        row.targetLinear = direction;
        row.targetAngular = cross(second - bodyCenterOfMass(target), direction);
    }
    else
    {
        float4 otherFrame = bodyRotationMultiply(target.rotation, joint.targetRotation);
        float4 relative = bodyRotationMultiply(bodyRotationConjugate(frame), otherFrame);
        float3 error = bodyRotationVector(relative);
        float3 derivative = bodyConstraintJacobian(frame, relative, error, local);
        row.coordinate = error[axis]; row.sourceLinear = row.targetLinear = float3(0);
        row.sourceAngular = -derivative; row.targetAngular = derivative;
    }
    return row;
}
DeviceJointLimit deviceJointLimit(DeviceJointConstraint joint, uint dof)
{
    uint axis = dof % 3;
    DeviceJointLimit limit;
    limit.motion = dof < 3 ? joint.linearMotion[axis] : joint.angularMotion[axis];
    limit.minimum = dof < 3 ? joint.linearMinimum[axis] : joint.angularMinimum[axis];
    limit.maximum = dof < 3 ? joint.linearMaximum[axis] : joint.angularMaximum[axis];
    return limit;
}
float deviceJointInitialError(DeviceJointLimit limit, float coordinate)
{
    if (limit.motion == 0) return 0;
    return limit.motion == 1 ? coordinate : coordinate - clamp(coordinate, limit.minimum, limit.maximum);
}
bool deviceJointTryError(DeviceJointLimit limit, float coordinate, float force, out float error, out uint side)
{
    error = coordinate; side = 0;
    if (limit.motion == 0) return false;
    if (limit.motion == 1) return true;
    if (coordinate < limit.minimum || (coordinate <= limit.maximum && force < 0))
    { error = coordinate - limit.minimum; side = 1; return true; }
    if (coordinate > limit.maximum || force > 0)
    { error = coordinate - limit.maximum; side = 2; return true; }
    return false;
}
float deviceJointProjectForce(uint side, float force)
{ return side == 1 ? min(0, force) : side == 2 ? max(0, force) : force; }
#endif
