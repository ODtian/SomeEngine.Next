#ifndef PHYSICS_CONTACT_ACTIONS
#define PHYSICS_CONTACT_ACTIONS
#include "physics_contact_math.slang"
struct DeviceContactStep
{
    float duration; uint stage; uint2 padding;
    uint2 startTime; uint2 endTime;
};
uniform DeviceContactStep ContactStep;
interface IDeviceContactResponseActions
{
    uint count(); bool active(uint row);
    void initialize(uint row,DeviceContactStep context);
    void applyRestitution(uint row,DeviceContactStep context);
};
struct DeviceContactResponseGroups<TFirst : IDeviceContactResponseActions,TSecond : IDeviceContactResponseActions> : IDeviceContactResponseActions
{
    TFirst first; TSecond second;
    uint count() { return first.count()+second.count(); }
    bool active(uint row) { return row<first.count()?first.active(row):second.active(row-first.count()); }
    void initialize(uint row,DeviceContactStep context)
    { if(row<first.count()) first.initialize(row,context); else second.initialize(row-first.count(),context); }
    void applyRestitution(uint row,DeviceContactStep context)
    { if(row<first.count()) first.applyRestitution(row,context); else second.applyRestitution(row-first.count(),context); }
};
struct DeviceNoContactResponses<T : IDeviceConstraintBodies> : IDeviceContactResponseActions
{
    T participants;
    uint count() { return participants.count(); }
    bool active(uint row) { return participants.active(row); }
    void initialize(uint row,DeviceContactStep context) { }
    void applyRestitution(uint row,DeviceContactStep context) { }
};
struct DeviceContactResponseActions<TSelection : IDeviceBodySelection> : IDeviceContactResponseActions
{
    DeviceContactBodies participants;
    uint count() { return participants.count(); }
    bool active(uint row)
    { return participants.active(row)&&deviceConstraintSelected(DeviceContactsMetadata[row],DeviceContactResponsesSelection[row]); }
    void initialize(uint row,DeviceContactStep context)
    { deviceInitializeContactResponse(row,context.duration,context.startTime,context.endTime); }
    void applyRestitution(uint row,DeviceContactStep context)
    {
        DeviceContactConstraint point=DeviceContacts[row];
        DeviceContactResponseState response=DeviceContactResponses[row];
        if(dot(response.forceOnSecond,point.normal)<=0||response.initialNormalVelocity>=-point.restitutionThreshold||point.restitution==0) return;
        TSelection selection;
        float mobility=deviceContactWeight(point,point.normal,selection);
        if(mobility==0) return;
        float speed=deviceContactSpeed(point,context.duration,false);
        float next=max(0,response.restitutionImpulse+(-point.restitution*response.initialNormalVelocity-speed)/mobility);
        float change=next-response.restitutionImpulse;
        response.restitutionImpulse=next;
        DeviceContactResponses[row]=response;
        for(uint end=0;end<point.bodyCount;end++)
        {
            if(!deviceContactFirstBody(point,end)) continue;
            uint bodyIndex=DeviceContactBodyLinks[point.bodyOffset+end].stateIndex;
            DeviceBodyState body=DeviceBodyStates[bodyIndex];
            if(body.motion!=2||!selection.contains(bodyIndex)) continue;
            float3 linear,angular;
            deviceContactJacobian(point,bodyIndex,point.normal,linear,angular);
            body.linearVelocity+=linear*(body.inverseMass*change);
            body.angularVelocity+=bodyInverseInertia(body,angular)*change;
            DeviceBodyStates[bodyIndex]=body;
        }
    }
};
void initializeDeviceContactResponses<T : IDeviceContactResponseActions>(uint row,T actions)
{ if(row<actions.count()&&actions.active(row)) actions.initialize(row,ContactStep); }
void applyDeviceContactRestitution<T : IDeviceContactResponseActions>(uint index,T actions)
{
    uint2 range=ConstraintGraphRowRanges[ContactStep.stage];
    if(index>=range.y) return;
    uint row=ConstraintGraphRowOrder[range.x+index];
    if(row<actions.count()&&actions.active(row)) actions.applyRestitution(row,ContactStep);
}
#endif
