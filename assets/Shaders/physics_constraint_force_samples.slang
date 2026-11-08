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
RWStructuredBuffer<uint4, ScalarDataLayout> DeviceConstraintForceRows;
RWStructuredBuffer<DeviceBodyForceSample, ScalarDataLayout> DeviceConstraintBodyForces;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceConstraintForceCount;
StructuredBuffer<uint, ScalarDataLayout> DeviceConstraintForceInputRows;
StructuredBuffer<float2, ScalarDataLayout> DeviceConstraintForceLimits;

interface IDeviceConstraintForces : IDeviceConstraintBodies
{
    uint2 entity(uint row);
    void force(uint row,uint body,DeviceConstraintForceStep context,out float3 force,out float3 torque);
};

void readDeviceConstraintForces<T : IDeviceConstraintForces>(uint work,T constraints)
{
    if(work>=ConstraintForceStep.rows) return;
    bool limited=ConstraintForceStep.padding.x!=0;
    uint row=limited?DeviceConstraintForceInputRows[work+1]:work;
    uint output=work;
    if(limited)
    {
        if(row>=constraints.count()||!constraints.active(row)) return;
        float2 limits=DeviceConstraintForceLimits[row]; bool exceeded=false;
        for(uint end=0;end<constraints.bodyCount(row);end++)
        {
            if(!constraints.writes(row,end)) continue;
            float3 force,torque; constraints.force(row,constraints.body(row,end),ConstraintForceStep,force,torque);
            exceeded=exceeded||dot(force,force)>limits.x*limits.x||dot(torque,torque)>limits.y*limits.y;
        }
        if(!exceeded) return;
        InterlockedAdd(DeviceConstraintForceCount[0],1,output);
    }
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
            DeviceConstraintBodyForces[output*ConstraintForceStep.bodiesPerConstraint+count++]=sample;
        }
        result=uint4(constraints.entity(row),count,row+1);
    }
    DeviceConstraintForceRows[output]=result;
}
#endif
