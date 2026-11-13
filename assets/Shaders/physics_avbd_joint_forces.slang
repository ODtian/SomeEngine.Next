// SPDX-License-Identifier: Apache-2.0
// Current joint forces follow AvbdJointMath and the shared Newton v1.7.0 Jacobians.
#ifndef PHYSICS_AVBD_JOINT_FORCES
#define PHYSICS_AVBD_JOINT_FORCES
#include "physics_constraint_force_samples.slang"
#include "physics_joint_participants.slang"
#include "physics_avbd_joint_math.slang"
#include "physics_avbd_joint_state.slang"

struct DeviceJointAvbdForces : IDeviceConstraintForces
{
    DeviceJointBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row)
    { return participants.active(row)&&deviceConstraintSelected(DeviceJointsMetadata[row],DeviceJointAvbdSelection[row]); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    { return uint2(DeviceJointsMetadata[row].entityIndex,DeviceJointsMetadata[row].entityGeneration); }
    void force(uint row,uint bodyIndex,DeviceConstraintForceStep context,out float3 force,out float3 torque)
    {
        DeviceJointConstraint joint=DeviceJoints[row];
        DeviceAvbdJointState state=DeviceAvbdJointStates[row];
        DeviceBodyState source=DeviceBodyStates[joint.sourceBody],target=DeviceBodyStates[joint.targetBody];
        bool worldPoint=deviceJointAvbdUsesWorldPointCoordinates(joint);
        force=torque=float3(0);
        for(uint dof=0;dof<6;dof++)
        {
            uint axis=dof%3;
            float stiffness=dof<3?joint.linearStiffness[axis]:joint.angularStiffness[axis];
            if(deviceJointLimit(joint,dof).motion==0||stiffness==0) continue;
            float multiplier=dof<3?state.linearMultiplier[axis]:state.angularMultiplier[axis];
            DeviceJointRow derivative=deviceJointAvbdRow(joint,source,target,dof,worldPoint);
            if(bodyIndex==joint.sourceBody)
            { force-=derivative.sourceLinear*multiplier; torque-=derivative.sourceAngular*multiplier; }
            if(bodyIndex==joint.targetBody)
            { force-=derivative.targetLinear*multiplier; torque-=derivative.targetAngular*multiplier; }
        }
    }
};
#endif
