#ifndef PHYSICS_AVBD_MOTOR
#define PHYSICS_AVBD_MOTOR
#include "physics_avbd.slang"
#include "physics_motor_math.slang"
#include "physics_motor_bodies.slang"
struct DeviceAvbdMotorState
{
    float3 linearMultiplier; uint padding0;
    float3 linearPenalty; uint padding1;
    float3 angularMultiplier; uint padding2;
    float3 angularPenalty; uint padding3;
};
RWStructuredBuffer<DeviceAvbdMotorState> DeviceAvbdMotorStates;
StructuredBuffer<uint2> DeviceMotorAvbdSelection;
bool deviceMotorAxisEnabled(DeviceMotorConstraint motor,uint dof)
{
    uint axis=dof%3;
    return dof<3?motor.linearStiffness[axis]!=0||motor.linearDamping[axis]!=0
        :motor.angularStiffness[axis]!=0||motor.angularDamping[axis]!=0;
}
struct DeviceMotorAvbdConstraints : IDeviceAvbdConstraints
{
    DeviceMotorBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row)
    { return participants.active(row)&&deviceConstraintSelected(DeviceMotorsMetadata[row],DeviceMotorAvbdSelection[row]); }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
    void initialize(uint row,DeviceAvbdStep context)
    {
        DeviceAvbdMotorState state=DeviceAvbdMotorStates[row];
        state.linearMultiplier*=context.retention; state.angularMultiplier*=context.retention;
        DeviceAvbdMotorStates[row]=state;
    }
    void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    {
        DeviceMotorConstraint motor=DeviceMotors[row];
        DeviceJointConstraint joint=DeviceJoints[motor.jointRow];
        DeviceAvbdMotorState state=DeviceAvbdMotorStates[row];
        DeviceBodyState source=DeviceBodyStates[joint.sourceBody],target=DeviceBodyStates[joint.targetBody];
        bool isSource=bodyIndex==joint.sourceBody;
        for(uint dof=0;dof<6;dof++)
        {
            if(!deviceMotorAxisEnabled(motor,dof)) continue;
            uint axis=dof%3;
            DeviceMotorRow drive=deviceMotorRow(motor,source,target,context.duration,asdouble(context.endTime.x,context.endTime.y),dof);
            float penalty=dof<3?state.linearPenalty[axis]:state.angularPenalty[axis];
            float multiplier=dof<3?state.linearMultiplier[axis]:state.angularMultiplier[axis];
            float2 terms=deviceAvbdMaterial(drive.stiffness,penalty);
            float force=terms.x*multiplier+terms.y*(drive.error-context.initialErrorFraction*drive.initialError);
            float limited=clamp(force,-drive.maximum,drive.maximum);
            float curvature=abs(force)<drive.maximum&&!(isSource&&motor.space==1)?terms.y:0;
            deviceAvbdAddRow(equation,isSource?drive.derivative.sourceLinear:drive.derivative.targetLinear,
                isSource?drive.derivative.sourceAngular:drive.derivative.targetAngular,limited,curvature);
        }
    }
    void updateDuals(uint row,DeviceAvbdStep context)
    {
        DeviceMotorConstraint motor=DeviceMotors[row];
        DeviceJointConstraint joint=DeviceJoints[motor.jointRow];
        DeviceAvbdMotorState state=DeviceAvbdMotorStates[row];
        DeviceBodyState source=DeviceBodyStates[joint.sourceBody],target=DeviceBodyStates[joint.targetBody];
        for(uint dof=0;dof<6;dof++)
        {
            if(!deviceMotorAxisEnabled(motor,dof)) continue;
            uint axis=dof%3;
            DeviceMotorRow drive=deviceMotorRow(motor,source,target,context.duration,asdouble(context.endTime.x,context.endTime.y),dof);
            float penalty=dof<3?state.linearPenalty[axis]:state.angularPenalty[axis];
            float multiplier=dof<3?state.linearMultiplier[axis]:state.angularMultiplier[axis];
            float2 terms=deviceAvbdMaterial(drive.stiffness,penalty);
            float force=clamp(terms.x*multiplier+terms.y*(drive.error-context.initialErrorFraction*drive.initialError),-drive.maximum,drive.maximum);
            if(dof<3) state.linearMultiplier[axis]=force; else state.angularMultiplier[axis]=force;
        }
        DeviceAvbdMotorStates[row]=state;
    }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
#endif
