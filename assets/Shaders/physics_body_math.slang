#ifndef PHYSICS_BODY_MATH
#define PHYSICS_BODY_MATH
#include "physics_bodies.slang"

float4 bodyRotationMultiply(float4 first, float4 second)
{
    return float4(first.w * second.xyz + second.w * first.xyz + cross(first.xyz, second.xyz),
        first.w * second.w - dot(first.xyz, second.xyz));
}
float4 bodyRotationConjugate(float4 rotation) { return float4(-rotation.xyz, rotation.w); }
float3 bodyRotate(float4 rotation, float3 value)
{
    return value + 2 * cross(rotation.xyz, cross(rotation.xyz, value) + rotation.w * value);
}
float3 bodyCenterOfMass(DeviceBodyState body) { return body.position + bodyRotate(body.rotation, body.centerOfMass); }
void bodySetCenterOfMass(inout DeviceBodyState body, float3 center)
{
    body.position = center - bodyRotate(body.rotation, body.centerOfMass);
}
float3 bodyInverseInertia(DeviceBodyState body, float3 value)
{
    float3 local = bodyRotate(bodyRotationConjugate(body.rotation), value);
    return bodyRotate(body.rotation, local.x * body.inverseInertiaX +
        local.y * body.inverseInertiaY + local.z * body.inverseInertiaZ);
}
bool bodyHasRotation(DeviceBodyState body)
{
    return body.inverseInertiaX.x != 0 || body.inverseInertiaY.y != 0 || body.inverseInertiaZ.z != 0;
}
float3 bodyLocalInverseInertia(DeviceBodyState body, float3 value)
{
    return value.x * body.inverseInertiaX + value.y * body.inverseInertiaY + value.z * body.inverseInertiaZ;
}
float3 bodyLocalInertia(DeviceBodyState body, float3 value)
{
    float3 first = cross(body.inverseInertiaY, body.inverseInertiaZ);
    float3 second = cross(body.inverseInertiaZ, body.inverseInertiaX);
    float3 third = cross(body.inverseInertiaX, body.inverseInertiaY);
    return float3(dot(value, first), dot(value, second), dot(value, third)) / dot(body.inverseInertiaX, first);
}
float4 bodyIntegrateRotation(float4 rotation, float3 velocity, float duration)
{
    float speed = length(velocity);
    if (speed == 0) return rotation;
    float angle = speed * duration * 0.5;
    return normalize(bodyRotationMultiply(float4(velocity * (sin(angle) / speed), cos(angle)), rotation));
}
float4 bodyApplyRotationChange(float4 rotation, float3 change)
{
    return normalize(rotation + bodyRotationMultiply(float4(change, 0), rotation) * 0.5);
}
float3 bodyRotationVector(float4 rotation)
{
    if (rotation.w < 0) rotation = -rotation;
    float magnitude = length(rotation.xyz);
    return magnitude == 0 ? float3(0) : rotation.xyz * (2 * atan2(magnitude, rotation.w) / magnitude);
}
float3 bodyRotationVelocity(float4 start, float4 end, float duration)
{
    float4 change = normalize(bodyRotationMultiply(end, bodyRotationConjugate(start)));
    if (change.w < 0) change = -change;
    float sine = length(change.xyz);
    return sine < 1e-8 ? change.xyz * (2 / duration) :
        change.xyz * (2 * atan2(sine, change.w) / (sine * duration));
}
float3 bodyConstraintJacobian(float4 sourceFrame, float4 relative, float3 error, float3 direction)
{
    float3 right = direction - cross(error, direction) * 0.5 +
        cross(error, cross(error, direction)) * (1.0 / 12);
    return bodyRotate(sourceFrame, bodyRotate(relative, right));
}
float bodyConstraintWeight(DeviceBodyState body, float3 linear, float3 angular)
{
    return body.motion == 2 ? body.inverseMass * dot(linear, linear) + dot(angular, bodyInverseInertia(body, angular)) : 0;
}
void bodyApplyCorrection(uint index, float3 linear, float3 angular, float change)
{
    DeviceBodyState body = DeviceBodyStates[index];
    if (body.motion != 2) return;
    float3 center = bodyCenterOfMass(body) + linear * (body.inverseMass * change);
    body.rotation = bodyApplyRotationChange(body.rotation, bodyInverseInertia(body, angular) * change);
    bodySetCenterOfMass(body, center);
    DeviceBodyStates[index] = body;
}
#endif
