#ifndef PHYSICS_MOTOR_BODIES
#define PHYSICS_MOTOR_BODIES
#include "physics_constraint_graph.slang"
#include "physics_motor_data.slang"
StructuredBuffer<uint2> DeviceMotorsSelection;
struct DeviceMotorBodies : IDeviceConstraintBodies
{
    uint count() { uint rows,stride; DeviceMotors.GetDimensions(rows,stride); return rows; }
    bool active(uint row)
    {
        DeviceConstraintMetadata motor=DeviceMotorsMetadata[row];
        DeviceConstraintMetadata joint=DeviceJointsMetadata[DeviceMotors[row].jointRow];
        return deviceConstraintSelected(motor,DeviceMotorsSelection[row])&&joint.active!=0
            &&motor.entityIndex==joint.entityIndex&&motor.entityGeneration==joint.entityGeneration;
    }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end)
    {
        DeviceJointConstraint joint=DeviceJoints[DeviceMotors[row].jointRow];
        return end==0?joint.sourceBody:joint.targetBody;
    }
    bool writes(uint row,uint end) { return end==1||DeviceMotors[row].response==1; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
