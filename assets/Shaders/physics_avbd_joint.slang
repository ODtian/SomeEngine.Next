// SPDX-License-Identifier: Apache-2.0
// Compliant joint assembly and duals follow AvbdJointMath and Newton v1.7.0.
#ifndef PHYSICS_AVBD_JOINT
#define PHYSICS_AVBD_JOINT
#include "physics_avbd.slang"
#include "physics_joint_math.slang"
#include "physics_joint_bodies.slang"
#include "physics_constraint_math.slang"
#include "physics_avbd_joint_state.slang"
struct DeviceJointAvbdConstraints : IDeviceAvbdConstraints
{
    DeviceJointBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row)
    { return participants.active(row)&&deviceConstraintSelected(DeviceJointsMetadata[row],DeviceJointAvbdSelection[row]); }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
    void initialize(uint row,DeviceAvbdStep context)
    {
        DeviceJointConstraint joint=DeviceJoints[row];
        DeviceAvbdJointState state=DeviceAvbdJointStates[row];
        state.linearMultiplier*=context.retention; state.angularMultiplier*=context.retention;
        float3 linear,angular;
        deviceJointCoordinates(joint,deviceBodyStart(DeviceBodyStates[joint.sourceBody]),
            deviceBodyStart(DeviceBodyStates[joint.targetBody]),linear,angular);
        for(uint axis=0;axis<3;axis++)
        {
            state.initialLinearError[axis]=deviceJointInitialError(deviceJointLimit(joint,axis),linear[axis]);
            state.initialAngularError[axis]=deviceJointInitialError(deviceJointLimit(joint,axis+3),angular[axis]);
        }
        DeviceAvbdJointStates[row]=state;
    }
    void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    {
        DeviceJointConstraint joint=DeviceJoints[row];
        DeviceAvbdJointState state=DeviceAvbdJointStates[row];
        DeviceBodyState source=DeviceBodyStates[joint.sourceBody],target=DeviceBodyStates[joint.targetBody];
        bool isSource=bodyIndex==joint.sourceBody;
        for(uint dof=0;dof<6;dof++)
        {
            uint axis=dof%3;
            DeviceJointLimit limit=deviceJointLimit(joint,dof);
            DeviceJointRow derivative=deviceJointRow(joint,source,target,dof);
            float multiplier=dof<3?state.linearMultiplier[axis]:state.angularMultiplier[axis];
            float error; uint side;
            if(!deviceJointTryError(limit,derivative.coordinate,multiplier,error,side)) continue;
            float stiffness=dof<3?joint.linearStiffness[axis]:joint.angularStiffness[axis];
            float penalty=dof<3?state.linearPenalty[axis]:state.angularPenalty[axis];
            float initial=dof<3?state.initialLinearError[axis]:state.initialAngularError[axis];
            float2 terms=deviceAvbdMaterial(stiffness,penalty);
            float force=deviceJointProjectForce(side,terms.x*multiplier+terms.y*(error-context.initialErrorFraction*initial));
            if(limit.motion==2&&force==0) continue;
            deviceAvbdAddRow(equation,isSource?derivative.sourceLinear:derivative.targetLinear,
                isSource?derivative.sourceAngular:derivative.targetAngular,force,terms.y);
        }
    }
    void updateDuals(uint row,DeviceAvbdStep context)
    {
        DeviceJointConstraint joint=DeviceJoints[row];
        DeviceAvbdJointState state=DeviceAvbdJointStates[row];
        float3 linear,angular;
        deviceJointCoordinates(joint,DeviceBodyStates[joint.sourceBody],DeviceBodyStates[joint.targetBody],linear,angular);
        for(uint dof=0;dof<6;dof++)
        {
            uint axis=dof%3;
            float multiplier=dof<3?state.linearMultiplier[axis]:state.angularMultiplier[axis];
            float coordinate=dof<3?linear[axis]:angular[axis];
            float error; uint side;
            float force=0;
            if(deviceJointTryError(deviceJointLimit(joint,dof),coordinate,multiplier,error,side))
            {
                float stiffness=dof<3?joint.linearStiffness[axis]:joint.angularStiffness[axis];
                float penalty=dof<3?state.linearPenalty[axis]:state.angularPenalty[axis];
                float initial=dof<3?state.initialLinearError[axis]:state.initialAngularError[axis];
                float2 terms=deviceAvbdMaterial(stiffness,penalty);
                force=deviceJointProjectForce(side,terms.x*multiplier+terms.y*(error-context.initialErrorFraction*initial));
            }
            if(dof<3) state.linearMultiplier[axis]=force; else state.angularMultiplier[axis]=force;
        }
        DeviceAvbdJointStates[row]=state;
    }
    override bool adjustsBody() { return false; }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
#endif
