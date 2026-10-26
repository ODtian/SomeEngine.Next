#ifndef PHYSICS_JOINT_PARTICIPANTS
#define PHYSICS_JOINT_PARTICIPANTS
#include "physics_constraint_bodies.slang"
#include "physics_joint_data.slang"
StructuredBuffer<uint2> DeviceJointsSelection;
struct DeviceJointBodies : IDeviceConstraintBodies
{
    uint count() { uint rows,stride; DeviceJoints.GetDimensions(rows,stride); return rows; }
    bool active(uint row) { return deviceConstraintSelected(DeviceJointsMetadata[row],DeviceJointsSelection[row]); }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end) { return end==0?DeviceJoints[row].sourceBody:DeviceJoints[row].targetBody; }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
};
#endif
