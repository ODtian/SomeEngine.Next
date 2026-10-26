#ifndef PHYSICS_CONSTRAINT_FORCE_SAMPLES
#define PHYSICS_CONSTRAINT_FORCE_SAMPLES
#include "physics_body_math.slang"
#include "physics_constraint_bodies.slang"

struct DeviceConstraintForceStep
{
    float duration; uint rows; uint bodiesPerConstraint; uint substep;
    uint2 number; uint2 startTime; uint2 endTime; uint2 padding;
};
uniform DeviceConstraintForceStep ConstraintForceStep;
struct DeviceBodyForceSample
{
    uint2 bodyEntity; uint2 padding0;
    float3 force; uint padding1;
    float3 torque; uint padding2;
};
RWStructuredBuffer<uint4> DeviceConstraintForceRows;
RWStructuredBuffer<DeviceBodyForceSample> DeviceConstraintBodyForces;

interface IDeviceConstraintForces : IDeviceConstraintBodies
{
    uint2 entity(uint row);
    void force(uint row,uint body,DeviceConstraintForceStep context,out float3 force,out float3 torque);
};

void readDeviceConstraintForces<T : IDeviceConstraintForces>(uint row,T constraints)
{
    if(row>=ConstraintForceStep.rows) return;
    uint4 result=uint4(0);
    if(row<constraints.count()&&constraints.active(row))
    {
        uint count=0;
        for(uint end=0;end<constraints.bodyCount(row);end++)
        {
            if(!constraints.writes(row,end)) continue;
            uint body=constraints.body(row,end); bool first=true;
            for(uint previous=0;previous<end;previous++)
                if(constraints.writes(row,previous)&&constraints.body(row,previous)==body) { first=false; break; }
            if(!first) continue;
            DeviceBodyState state=DeviceBodyStates[body];
            DeviceBodyForceSample sample=(DeviceBodyForceSample)0;
            sample.bodyEntity=uint2(state.entityIndex,state.entityGeneration);
            constraints.force(row,body,ConstraintForceStep,sample.force,sample.torque);
            DeviceConstraintBodyForces[row*ConstraintForceStep.bodiesPerConstraint+count++]=sample;
        }
        result=uint4(constraints.entity(row),count,1);
    }
    DeviceConstraintForceRows[row]=result;
}
#endif
