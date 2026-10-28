// SPDX-License-Identifier: Apache-2.0
// Isotropic point joints follow Newton v1.7.0 world-space point forces.
#ifndef PHYSICS_AVBD_JOINT_MATH
#define PHYSICS_AVBD_JOINT_MATH
#include "physics_joint_math.slang"
bool deviceJointAvbdUsesWorldPointCoordinates(DeviceJointConstraint joint)
{
    return all(joint.linearMotion==uint3(1))&&joint.linearStiffness.x==joint.linearStiffness.y
        &&joint.linearStiffness.x==joint.linearStiffness.z;
}
void deviceJointAvbdCoordinates(DeviceJointConstraint joint,DeviceBodyState source,DeviceBodyState target,
    out float3 linear,out float3 angular)
{
    deviceJointCoordinates(joint,source,target,linear,angular);
    if(deviceJointAvbdUsesWorldPointCoordinates(joint))
    {
        linear=(target.position-source.position)+bodyRotate(target.rotation,joint.targetPosition)
            -bodyRotate(source.rotation,joint.sourcePosition);
    }
}
DeviceJointRow deviceJointAvbdRow(DeviceJointConstraint joint,DeviceBodyState source,DeviceBodyState target,
    uint dof,bool worldPoint)
{
    if(dof>=3||!worldPoint) return deviceJointRow(joint,source,target,dof);
    float3 direction=dof==0?float3(1,0,0):dof==1?float3(0,1,0):float3(0,0,1);
    float3 displacement=(target.position-source.position)+bodyRotate(target.rotation,joint.targetPosition)
        -bodyRotate(source.rotation,joint.sourcePosition);
    float3 sourceArm=bodyRotate(source.rotation,joint.sourcePosition-source.centerOfMass);
    float3 targetArm=bodyRotate(target.rotation,joint.targetPosition-target.centerOfMass);
    DeviceJointRow row;
    row.coordinate=dot(displacement,direction);
    row.sourceLinear=-direction;
    row.sourceAngular=-cross(sourceArm,direction);
    row.targetLinear=direction;
    row.targetAngular=cross(targetArm,direction);
    return row;
}
#endif
