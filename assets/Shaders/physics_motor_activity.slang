#ifndef PHYSICS_MOTOR_ACTIVITY
#define PHYSICS_MOTOR_ACTIVITY
#include "physics_constraint_activity.slang"
#include "physics_motor_bodies.slang"
#include "physics_motor_math.slang"

struct DeviceMotorActivity : IDeviceConstraintActivity
{
    DeviceMotorBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return participants.writes(row,end); }
    uint groupCount() { return participants.groupCount(); }
    uint group(uint row) { return participants.group(row); }
    uint2 entity(uint row)
    { return uint2(DeviceMotorsMetadata[row].entityIndex,DeviceMotorsMetadata[row].entityGeneration); }
    float2 error(uint row,DeviceActivityStep context)
    {
        DeviceMotorConstraint motor=DeviceMotors[row];
        DeviceJointConstraint joint=DeviceJoints[motor.jointRow];
        DeviceBodyState source=DeviceBodyStates[joint.sourceBody],target=DeviceBodyStates[joint.targetBody];
        float3 linear=float3(0),angular=float3(0);
        double endTime=asdouble(context.endTime.x,context.endTime.y);
        for(uint axis=0;axis<3;axis++)
        {
            if((motor.linearStiffness[axis]>0||motor.linearDamping[axis]>0)&&motor.maximumForce[axis]>0)
                linear[axis]=deviceMotorRow(motor,source,target,context.duration,endTime,axis).error;
            if((motor.angularStiffness[axis]>0||motor.angularDamping[axis]>0)&&motor.maximumTorque[axis]>0)
                angular[axis]=deviceMotorRow(motor,source,target,context.duration,endTime,axis+3).error;
        }
        return float2(length(linear),length(angular));
    }
};
#endif
