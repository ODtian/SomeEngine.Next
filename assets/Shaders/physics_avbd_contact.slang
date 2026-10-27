// SPDX-License-Identifier: Apache-2.0
// Compliant unilateral contact follows AvbdContactMath and Newton v1.7.0.
#ifndef PHYSICS_AVBD_CONTACT
#define PHYSICS_AVBD_CONTACT
#include "physics_avbd.slang"
#include "physics_contact_math.slang"
struct DeviceAvbdContactState
{
    float normalMultiplier; float normalPenalty; float tangentPenalty; float frictionRegularizationSpeed;
    float3 multiplierNormal; float initialSeparation;
    float3 initialDelta; uint padding0;
    float3 tangentMultiplier; uint padding1;
};
RWStructuredBuffer<DeviceAvbdContactState> DeviceAvbdContactStates;
StructuredBuffer<uint2> DeviceContactAvbdSelection;
struct DeviceAvbdContactEquation
{
    float normalForce; float normalDual; float normalStiffness;
    float3 tangentForce; float3 tangentDirection;
    float alongStiffness; float acrossStiffness;
};
DeviceAvbdContactEquation deviceEvaluateAvbdContact(uint row,DeviceAvbdStep context,float3 extraForceOnSecond)
{
    DeviceContactConstraint point=DeviceContacts[row];
    DeviceAvbdContactState state=DeviceAvbdContactStates[row];
    float3 delta=deviceContactDelta(point,false);
    float gap=point.separation+dot(point.normal,delta-point.bodyAnchorDelta);
    float residual=gap<0?gap-context.initialErrorFraction*min(state.initialSeparation,0):gap;
    float2 terms=deviceAvbdMaterial(point.normalStiffness,state.normalPenalty);
    float force=min(0,terms.x*state.normalMultiplier+terms.y*residual);
    float stiffness=force<0?terms.y:0;
    float3 extra=-extraForceOnSecond;
    float extraNormal=dot(extra,point.normal);
    float3 extraTangent=extra-point.normal*extraNormal;
    float pressure=min(0,force+extraNormal);
    float3 tangent=-extraTangent,direction=float3(0);
    float along=0,across=0;
    if(pressure<0&&(point.staticFriction>0||point.dynamicFriction>0))
    {
        float3 slip=delta-state.initialDelta;
        slip-=point.normal*dot(point.normal,slip);
        float3 trial=state.tangentMultiplier+slip*state.tangentPenalty+extraTangent;
        trial-=point.normal*dot(point.normal,trial);
        float magnitude=length(trial);
        direction=magnitude>0?trial/magnitude:normalize(cross(point.normal,abs(point.normal.x)<0.707?float3(1,0,0):float3(0,1,0)));
        if(magnitude<=-pressure*point.staticFriction)
        {
            tangent=trial-extraTangent; along=across=state.tangentPenalty;
        }
        else
        {
            float bound=-pressure*point.dynamicFriction;
            tangent=direction*bound-extraTangent;
            float travel=length(slip),epsilon=state.frictionRegularizationSpeed*context.duration;
            float reciprocal=travel>epsilon?1/travel:(2-travel/epsilon)/epsilon;
            along=bound*reciprocal; across=state.tangentPenalty*bound/magnitude;
        }
    }
    float normalForce=force,approach=(gap-state.initialSeparation)/context.duration;
    if(point.normalDamping>0&&approach<0&&force<0)
    { normalForce+=point.normalDamping*approach; stiffness+=point.normalDamping/context.duration; }
    DeviceAvbdContactEquation result;
    result.normalForce=normalForce; result.normalDual=force; result.normalStiffness=stiffness;
    result.tangentForce=tangent; result.tangentDirection=direction; result.alongStiffness=along; result.acrossStiffness=across;
    return result;
}
// Positive contact-potential curvature follows Giles AVBD through
// tools/SomeEngine.Physics.Research/Native/AvbdContact.cpp on codex/job331-380.
// Copyright (c) 2026 Chris Giles. Permission to use, copy, modify, distribute and sell
// this software and its documentation for any purpose is hereby granted without fee,
// provided that the above copyright notice appear in all copies. The software is provided
// "as is" without express or implied warranty; the author makes no representations about
// its suitability for any purpose.
void deviceAccumulateAvbdContactEquation(uint row,uint bodyIndex,DeviceAvbdStep context,float3 extraForce,
    bool normalUpperBound,bool angularCurvature,inout DeviceAvbdEquation equation)
{
    DeviceContactConstraint point=DeviceContacts[row];
    DeviceAvbdContactEquation contact=deviceEvaluateAvbdContact(row,context,extraForce);
    float3 linear,angular;
    deviceContactJacobian(point,bodyIndex,point.normal,linear,angular);
    float normalStiffness=contact.normalStiffness;
    if(normalUpperBound&&normalStiffness==0)
        normalStiffness=deviceAvbdMaterial(point.normalStiffness,DeviceAvbdContactStates[row].normalPenalty).y;
    deviceAvbdAddRow(equation,linear,angular,contact.normalForce,normalStiffness);
    float3 across=cross(point.normal,contact.tangentDirection);
    deviceContactJacobian(point,bodyIndex,contact.tangentDirection,linear,angular);
    deviceAvbdAddRow(equation,linear,angular,dot(contact.tangentForce,contact.tangentDirection),contact.alongStiffness);
    deviceContactJacobian(point,bodyIndex,across,linear,angular);
    deviceAvbdAddRow(equation,linear,angular,dot(contact.tangentForce,across),contact.acrossStiffness);
    if(angularCurvature)
    {
        DeviceBodyState body=DeviceBodyStates[bodyIndex];
        float force=length(point.normal*contact.normalForce+contact.tangentForce-extraForce);
        float curvature=0;
        for(uint end=0;end<point.bodyCount;end++)
        {
            DeviceContactBody link=DeviceContactBodyLinks[point.bodyOffset+end];
            if(link.stateIndex!=bodyIndex) continue;
            float3 arm=deviceContactArm(body,link.localPoint);
            curvature+=abs(link.weight)*length(arm)*force;
        }
        deviceAvbdAddRow(equation,float3(0),float3(1,0,0),0,curvature);
        deviceAvbdAddRow(equation,float3(0),float3(0,1,0),0,curvature);
        deviceAvbdAddRow(equation,float3(0),float3(0,0,1),0,curvature);
    }
}
void deviceAccumulateAvbdContact(uint row,uint bodyIndex,DeviceAvbdStep context,float3 extraForce,inout DeviceAvbdEquation equation)
{ deviceAccumulateAvbdContactEquation(row,bodyIndex,context,extraForce,false,false,equation); }
void deviceAccumulateAvbdContactPotential(uint row,uint bodyIndex,DeviceAvbdStep context,float3 extraForce,
    bool supported,inout DeviceAvbdEquation equation)
{ deviceAccumulateAvbdContactEquation(row,bodyIndex,context,extraForce,!supported,true,equation); }
void deviceUpdateAvbdContact(uint row,DeviceAvbdStep context,float3 extraForce)
{
    DeviceContactConstraint point=DeviceContacts[row];
    DeviceAvbdContactEquation contact=deviceEvaluateAvbdContact(row,context,extraForce);
    DeviceAvbdContactState state=DeviceAvbdContactStates[row];
    state.normalMultiplier=contact.normalDual; state.tangentMultiplier=contact.tangentForce;
    DeviceAvbdContactStates[row]=state;
    DeviceContactResponseState response=DeviceContactResponses[row];
    response.forceOnSecond=-(point.normal*contact.normalForce+contact.tangentForce)+extraForce;
    DeviceContactResponses[row]=response;
}
struct DeviceContactAvbdConstraints : IDeviceAvbdConstraints
{
    DeviceContactBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row)
    {
        DeviceConstraintMetadata metadata=DeviceContactsMetadata[row];
        return participants.active(row)&&deviceConstraintSelected(metadata,DeviceContactAvbdSelection[row])
            &&deviceConstraintSelected(metadata,DeviceContactResponsesSelection[row]);
    }
    uint bodyCount(uint row) { return participants.bodyCount(row); }
    uint body(uint row,uint end) { return participants.body(row,end); }
    bool writes(uint row,uint end) { return true; }
    uint groupCount() { return 1; }
    uint group(uint row) { return 0; }
    void initialize(uint row,DeviceAvbdStep context)
    {
        DeviceContactConstraint point=DeviceContacts[row];
        DeviceAvbdContactState state=DeviceAvbdContactStates[row];
        if(point.warmStart!=0)
        {
            float3 force=state.multiplierNormal*state.normalMultiplier+state.tangentMultiplier;
            float normal=dot(force,point.normal);
            state.normalMultiplier=min(normal,0)*context.retention;
            state.tangentMultiplier=(force-point.normal*normal)*context.retention;
            float magnitude=length(state.tangentMultiplier),bound=-state.normalMultiplier*point.staticFriction;
            if(magnitude>bound) state.tangentMultiplier*=bound/magnitude;
        }
        else { state.normalMultiplier=0; state.tangentMultiplier=float3(0); }
        state.multiplierNormal=point.normal;
        state.initialDelta=deviceContactDelta(point,true);
        state.initialSeparation=point.separation+dot(point.normal,state.initialDelta-point.bodyAnchorDelta);
        DeviceAvbdContactStates[row]=state;
    }
    void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    { deviceAccumulateAvbdContact(row,bodyIndex,context,float3(0),equation); }
    void updateDuals(uint row,DeviceAvbdStep context)
    { deviceUpdateAvbdContact(row,context,float3(0)); }
    override bool adjustsBody() { return false; }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation) { }
};

struct DeviceContactPotentialAvbdConstraints : IDeviceAvbdConstraints
{
    DeviceContactAvbdConstraints inner;
    uint count() { return inner.count(); }
    bool active(uint row) { return inner.active(row); }
    uint bodyCount(uint row) { return inner.bodyCount(row); }
    uint body(uint row,uint end) { return inner.body(row,end); }
    bool writes(uint row,uint end) { return inner.writes(row,end); }
    uint groupCount() { return inner.groupCount(); }
    uint group(uint row) { return inner.group(row); }
    void initialize(uint row,DeviceAvbdStep context) { inner.initialize(row,context); }
    void accumulate(uint row,uint bodyIndex,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    { deviceAccumulateAvbdContactPotential(row,bodyIndex,context,float3(0),false,equation); }
    void updateDuals(uint row,DeviceAvbdStep context) { inner.updateDuals(row,context); }
    override bool adjustsBody() { return inner.adjustsBody(); }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation)
    { inner.adjust(body,context,equation,translation,rotation); }
};
#endif
