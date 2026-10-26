// SPDX-License-Identifier: Apache-2.0
// Compliant ALM elastic rows follow AvbdElasticConnectionMath and Newton v1.7.0.
#ifndef PHYSICS_AVBD_ELASTIC
#define PHYSICS_AVBD_ELASTIC
#include "physics_avbd.slang"
#include "physics_elastic_data.slang"
#include "physics_constraint_math.slang"
struct DeviceAvbdElasticState
{
    float3 linearMultiplier; float linearPenalty;
    float3 angularMultiplier; float angularPenalty;
    float3 initialLinearError; uint padding0;
    float3 initialAngularError; uint padding1;
};
RWStructuredBuffer<DeviceAvbdElasticState> DeviceAvbdElasticStates;
StructuredBuffer<uint2> DeviceElasticAvbdSelection;
float3 deviceElasticLinearError(DeviceElasticConstraint constraint,DeviceBodyState first,DeviceBodyState second)
{ return first.position+bodyRotate(first.rotation,constraint.firstAnchor)-second.position-bodyRotate(second.rotation,constraint.secondAnchor); }
float3 deviceElasticAngularError(DeviceElasticConstraint constraint,float4 first,float4 second)
{ return bodyRotationVector(bodyRotationMultiply(bodyRotationMultiply(bodyRotationConjugate(first),second),bodyRotationConjugate(constraint.restRotation))); }
struct DeviceElasticAvbdConstraints : IDeviceAvbdConstraints
{
    DeviceElasticBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row)
    { return participants.active(row)&&deviceConstraintSelected(DeviceElasticConstraintsMetadata[row],DeviceElasticAvbdSelection[row]); }
    uint bodyCount(uint row) { return 2; }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
    void initialize(uint row,DeviceAvbdStep context)
    {
        DeviceElasticConstraint constraint=DeviceElasticConstraints[row];
        DeviceBodyState first=deviceBodyStart(DeviceBodyStates[constraint.firstBody]);
        DeviceBodyState second=deviceBodyStart(DeviceBodyStates[constraint.secondBody]);
        DeviceAvbdElasticState state=DeviceAvbdElasticStates[row];
        state.initialLinearError=deviceElasticLinearError(constraint,first,second);
        state.initialAngularError=deviceElasticAngularError(constraint,first.rotation,second.rotation);
        state.linearMultiplier*=context.retention; state.angularMultiplier*=context.retention;
        DeviceAvbdElasticStates[row]=state;
    }
    void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    {
        DeviceElasticConstraint constraint=DeviceElasticConstraints[row];
        DeviceAvbdElasticState state=DeviceAvbdElasticStates[row];
        DeviceBodyState first=DeviceBodyStates[constraint.firstBody],second=DeviceBodyStates[constraint.secondBody];
        DeviceBodyState body=DeviceBodyStates[bodyIndex];
        bool isFirst=bodyIndex==constraint.firstBody;
        float sign=isFirst?1:-1;
        float3 anchor=isFirst?constraint.firstAnchor:constraint.secondAnchor;
        float3 offset=bodyRotate(body.rotation,anchor-body.centerOfMass);
        float3 error=deviceElasticLinearError(constraint,first,second)-state.initialLinearError*context.initialErrorFraction;
        float2 linear=deviceAvbdMaterial(constraint.linearStiffness,state.linearPenalty);
        float3 force=deviceAvbdForce(linear,error,state.linearMultiplier);
        for(uint axis=0;axis<3;axis++)
        {
            float3 direction=axis==0?float3(1,0,0):axis==1?float3(0,1,0):float3(0,0,1);
            deviceAvbdAddRow(equation,direction*sign,cross(offset,direction)*sign,force[axis],linear.y);
        }
        float3 angularError=deviceElasticAngularError(constraint,first.rotation,second.rotation);
        float2 angular=deviceAvbdMaterial(constraint.angularStiffness,state.angularPenalty);
        float3 torque=deviceAvbdForce(angular,angularError-state.initialAngularError*context.initialErrorFraction,state.angularMultiplier);
        float4 alignment=bodyRotationMultiply(bodyRotationMultiply(bodyRotationConjugate(first.rotation),second.rotation),bodyRotationConjugate(constraint.restRotation));
        for(uint axis=0;axis<3;axis++)
        {
            float3 direction=axis==0?float3(1,0,0):axis==1?float3(0,1,0):float3(0,0,1);
            float3 jacobian=bodyConstraintJacobian(first.rotation,alignment,angularError,direction)*-sign;
            deviceAvbdAddRow(equation,float3(0),jacobian,torque[axis],angular.y);
        }
    }
    void updateDuals(uint row,DeviceAvbdStep context)
    {
        DeviceElasticConstraint constraint=DeviceElasticConstraints[row];
        DeviceAvbdElasticState state=DeviceAvbdElasticStates[row];
        DeviceBodyState first=DeviceBodyStates[constraint.firstBody],second=DeviceBodyStates[constraint.secondBody];
        state.linearMultiplier=deviceAvbdForce(deviceAvbdMaterial(constraint.linearStiffness,state.linearPenalty),
            deviceElasticLinearError(constraint,first,second)-state.initialLinearError*context.initialErrorFraction,state.linearMultiplier);
        state.angularMultiplier=deviceAvbdForce(deviceAvbdMaterial(constraint.angularStiffness,state.angularPenalty),
            deviceElasticAngularError(constraint,first.rotation,second.rotation)-state.initialAngularError*context.initialErrorFraction,state.angularMultiplier);
        DeviceAvbdElasticStates[row]=state;
    }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};
#endif
