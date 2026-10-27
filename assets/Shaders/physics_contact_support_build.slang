// SPDX-License-Identifier: Apache-2.0
// Stationary support classification and affine load paths follow ContactSupportData.
#ifndef PHYSICS_CONTACT_SUPPORT_BUILD
#define PHYSICS_CONTACT_SUPPORT_BUILD
#include "physics_avbd_contact.slang"
#include "physics_contact_support_data.slang"
#include "physics_contact_support_geometry.slang"
bool deviceSupportContactSelected(uint row)
{
    DeviceContactAvbdConstraints contact;
    return contact.active(row)&&deviceConstraintSelected(DeviceContactsMetadata[row],DeviceContactSupportSelection[row]);
}
bool deviceSupportContactRuns<T : IDeviceBodySelection>(uint row,T bodies)
{
    if(!deviceSupportContactSelected(row)) return false;
    DeviceContactConstraint point=DeviceContacts[row];
    for(uint end=0;end<point.bodyCount;end++)
        if(bodies.contains(DeviceContactBodyLinks[point.bodyOffset+end].stateIndex)) return true;
    return false;
}
bool deviceSupportHasMotion(uint bodyIndex)
{
    DeviceBodyState body=DeviceBodyStates[bodyIndex];
    if(body.motion==0) return false;
    float3 travel=bodyCenterOfMass(body)-body.startCenterOfMass;
    travel-=ContactSupportSettings.up*dot(travel,ContactSupportSettings.up);
    float rotation=length(bodyRotationVector(bodyRotationMultiply(bodyRotationConjugate(body.startRotation),body.rotation)));
    return dot(travel,travel)>ContactSupportSettings.motionTolerance*ContactSupportSettings.motionTolerance
        ||rotation>ContactSupportSettings.angularMotionTolerance;
}
bool deviceSupportUpdateMotion<T : IDeviceBodySelection,TWork : IDeviceContactSupportWork>(T bodies,TWork work)
{
    bool changed=false;
    for(uint bodyOffset=0;bodyOffset<work.bodyCount();bodyOffset++)
    {
        uint body=work.body(bodyOffset);
        if(!deviceSupportBodySelected(body)||!bodies.contains(body)) continue;
        DeviceBodySupportState state=DeviceSupportBodyStates[body];
        if(state.moving!=0||!deviceSupportHasMotion(body)) continue;
        state.moving=1; DeviceSupportBodyStates[body]=state; changed=true;
    }
    return changed;
}
float2 deviceSupportWitness(uint contact)
{
    DeviceContactConstraint point=DeviceContacts[contact];
    bool second=DeviceSupportContactStates[contact].upperIsSecond!=0;
    return deviceSupportProject(point.worldPoint+point.normal*(point.separation*(second?.5:-.5)));
}
double deviceSupportOrientation(float2 a,float2 b,float2 c)
{ return (double(b.x)-a.x)*(double(c.y)-a.y)-(double(b.y)-a.y)*(double(c.x)-a.x); }
// A valid affine combination uses at most three footprint points; all such triangles cover their convex hull.
bool deviceSupportContactBefore(uint first,uint second)
{
    DeviceConstraintMetadata a=DeviceContactsMetadata[first],b=DeviceContactsMetadata[second];
    return a.entityIndex<b.entityIndex||(a.entityIndex==b.entityIndex&&a.entityGeneration<b.entityGeneration);
}
uint findDeviceSupportNextContact(uint body,uint current)
{
    uint count=DeviceSupportBodyStates[body].pointCount,start=body*ContactSupportSettings.contactsPerBody;
    uint next=current; float2 origin=deviceSupportWitness(current),nextPoint=origin;
    for(uint index=0;index<count;index++)
    {
        uint contact=DeviceSupportBodyContacts[start+index]; float2 point=deviceSupportWitness(contact);
        if(all(point==origin)) continue;
        double turn=deviceSupportOrientation(origin,nextPoint,point);
        double previousX=double(nextPoint.x)-origin.x,previousY=double(nextPoint.y)-origin.y;
        double candidateX=double(point.x)-origin.x,candidateY=double(point.y)-origin.y;
        double previousDistance=previousX*previousX+previousY*previousY;
        double candidateDistance=candidateX*candidateX+candidateY*candidateY;
        if(next==current||turn<0||(turn==0&&(candidateDistance>previousDistance
            ||(candidateDistance==previousDistance&&deviceSupportContactBefore(contact,next)))))
        { next=contact; nextPoint=point; }
    }
    return next;
}
// Walk the same convex footprint and triangle fan used by ContactSupportPolygon.
bool deviceSupportWeights(uint body,float2 point,out uint3 contacts,out float3 weights,out uint count)
{
    contacts=uint3(0); weights=float3(0); count=0;
    uint length=DeviceSupportBodyStates[body].pointCount,start=body*ContactSupportSettings.contactsPerBody;
    if(length==0) return false;
    uint first=DeviceSupportBodyContacts[start]; float2 a=deviceSupportWitness(first);
    for(uint index=1;index<length;index++)
    {
        uint contact=DeviceSupportBodyContacts[start+index]; float2 vertex=deviceSupportWitness(contact);
        if(vertex.x<a.x||(vertex.x==a.x&&(vertex.y<a.y
            ||(vertex.y==a.y&&deviceSupportContactBefore(contact,first)))))
        { first=contact; a=vertex; }
    }
    float tolerance=ContactSupportSettings.distanceTolerance;
    uint second=findDeviceSupportNextContact(body,first);
    if(second==first)
    {
        if(dot(point-a,point-a)>tolerance*tolerance) return false;
        contacts.x=first; weights.x=1; count=1; return true;
    }
    uint third=findDeviceSupportNextContact(body,second); float2 b=deviceSupportWitness(second);
    if(third==first)
    {
        float2 line=b-a; float fraction=dot(point-a,line)/dot(line,line);
        if(fraction < -tolerance||fraction>1+tolerance) return false;
        fraction=clamp(fraction,0,1); float2 delta=a+line*fraction-point;
        if(dot(delta,delta)>tolerance*tolerance) return false;
        contacts=uint3(first,second,0); weights=float3(1-fraction,fraction,0); count=2; return true;
    }
    for(uint vertex=2;vertex<length;vertex++)
    {
        float2 c=deviceSupportWitness(third);
        double area=deviceSupportOrientation(a,b,c);
        double wb=deviceSupportOrientation(a,point,c)/area,wc=deviceSupportOrientation(a,b,point)/area,wa=1-wb-wc;
        if(min(wa,min(wb,wc))>=-tolerance)
        {
            wa=max(0.0,wa); wb=max(0.0,wb); wc=max(0.0,wc); double total=wa+wb+wc;
            contacts=uint3(first,second,third); weights=float3(float(wa/total),float(wb/total),float(wc/total));
            count=3; return true;
        }
        second=third; b=c; third=findDeviceSupportNextContact(body,second);
        if(third==first) break;
    }
    return false;
}
void deviceReleaseContactSupport(uint row)
{
    DeviceContactSupportState support=DeviceSupportContactStates[row];
    if(all(support.extraForceOnSecond==float3(0))) return;
    DeviceContactConstraint point=DeviceContacts[row];
    DeviceAvbdContactState state=DeviceAvbdContactStates[row];
    float normal=dot(support.extraForceOnSecond,point.normal);
    state.normalMultiplier=min(0,state.normalMultiplier-normal);
    state.tangentMultiplier-=support.extraForceOnSecond-point.normal*normal;
    float magnitude=length(state.tangentMultiplier),bound=-state.normalMultiplier*point.staticFriction;
    if(magnitude>bound) state.tangentMultiplier*=bound/magnitude;
    support.extraForceOnSecond=float3(0);
    DeviceSupportContactStates[row]=support; DeviceAvbdContactStates[row]=state;
}
void buildDeviceContactSupports<T : IDeviceBodySelection,TWork : IDeviceContactSupportWork>(DeviceAvbdStep context,T bodies,TWork work)
{
    for(uint bodyOffset=0;bodyOffset<work.bodyCount();bodyOffset++)
    {
        uint body=work.body(bodyOffset);
        if(!deviceSupportBodySelected(body)||!bodies.contains(body)) continue;
        DeviceBodySupportState state=DeviceSupportBodyStates[body];
        state.enabled=0; state.reaction=0; state.pointCount=state.loadCount=state.equationCount=0;
        DeviceSupportBodyStates[body]=state;
    }
    uint candidateCount=0,candidateStart=work.candidateStart();
    for(uint contactOffset=0;contactOffset<work.contactCount();contactOffset++)
    {
        uint row=work.contact(contactOffset);
        if(!deviceSupportContactRuns(row,bodies)) continue;
        DeviceContactSupportState support=DeviceSupportContactStates[row];
        support.enabled=0; support.reaction=support.load=0; support.loadCount=0; DeviceSupportContactStates[row]=support;
        DeviceContactConstraint point=DeviceContacts[row];
        if(!isinf(point.normalStiffness)||point.normalStiffness<0) continue;
        uint first=DeviceContactBodyLinks[point.bodyOffset].stateIndex;
        uint second=DeviceContactBodyLinks[point.bodyOffset+point.firstBodyCount].stateIndex;
        bool rigid=first!=second;
        for(uint end=0;end<point.bodyCount;end++)
            rigid=rigid&&DeviceContactBodyLinks[point.bodyOffset+end].stateIndex==(end<point.firstBodyCount?first:second);
        if(!rigid) continue;
        float vertical=dot(point.normal,ContactSupportSettings.up),squared=vertical*vertical;
        if(vertical==0||1-squared>point.staticFriction*point.staticFriction*squared) continue;
        bool upperSecond=vertical>0;
        uint lower=upperSecond?first:second,upper=upperSecond?second:first;
        if(!deviceSupportBodySelected(upper)||DeviceSupportBodyStates[upper].moving!=0) continue;
        if(DeviceBodyStates[lower].motion==2)
        { if(!deviceSupportBodySelected(lower)||DeviceSupportBodyStates[lower].moving!=0) continue; }
        else if(deviceSupportHasMotion(lower)) continue;
        float3 delta=deviceContactDelta(point,false);
        float3 gap=point.normal*point.separation+delta-point.bodyAnchorDelta;
        float pressure=dot(DeviceContactResponses[row].forceOnSecond,point.normal);
        if(dot(point.normal,gap)>ContactSupportSettings.distanceTolerance&&pressure<=0) continue;
        float3 slip=delta-deviceContactDelta(point,true); slip-=point.normal*dot(point.normal,slip);
        float travel=length(slip),mobility=0;
        if(travel>0)
        {
            float3 linear,angular;
            deviceContactJacobian(point,first,slip/travel,linear,angular); mobility+=bodyConstraintWeight(DeviceBodyStates[first],linear,angular);
            deviceContactJacobian(point,second,slip/travel,linear,angular); mobility+=bodyConstraintWeight(DeviceBodyStates[second],linear,angular);
        }
        if(travel>ContactSupportSettings.stickDistance+point.staticFriction*abs(pressure)*mobility*context.duration*context.duration) continue;
        float3 firstWitness=point.worldPoint-point.normal*(point.separation*.5),secondWitness=point.worldPoint+point.normal*(point.separation*.5);
        DeviceSupportCandidate candidate=(DeviceSupportCandidate)0;
        candidate.contact=row; candidate.lower=lower; candidate.upper=upper; candidate.upperIsSecond=upperSecond?1:0;
        candidate.lowerPoint=deviceSupportProject(upperSecond?firstWitness:secondWitness);
        candidate.upperPoint=deviceSupportProject(upperSecond?secondWitness:firstWitness);
        candidate.height=dot(point.worldPoint,ContactSupportSettings.up); candidate.gapHeight=dot(gap,ContactSupportSettings.up)*(upperSecond?1:-1);
        uint position=candidateCount++;
        while(position>0)
        {
            DeviceSupportCandidate previous=DeviceSupportCandidates[candidateStart+position-1];
            DeviceConstraintMetadata a=DeviceContactsMetadata[previous.contact],b=DeviceContactsMetadata[row];
            if(previous.height<candidate.height||(previous.height==candidate.height
                &&(a.entityIndex<b.entityIndex||(a.entityIndex==b.entityIndex&&a.entityGeneration<=b.entityGeneration)))) break;
            DeviceSupportCandidates[candidateStart+position]=previous; position--;
        }
        DeviceSupportCandidates[candidateStart+position]=candidate;
    }
    uint order=0;
    for(uint index=0;index<candidateCount;index++)
    {
        DeviceSupportCandidate candidate=DeviceSupportCandidates[candidateStart+index];
        uint3 contacts; float3 weights; uint count=0;
        if(DeviceBodyStates[candidate.lower].motion==2
            &&!deviceSupportWeights(candidate.lower,candidate.lowerPoint,contacts,weights,count)) continue;
        float shift=-candidate.gapHeight;
        for(uint load=0;load<count;load++)
        {
            shift+=weights[load]*DeviceSupportContactStates[contacts[load]].heightShift;
            DeviceSupportLoad value; value.contact=contacts[load]; value.weight=weights[load];
            DeviceSupportContactLoads[candidate.contact*3+load]=value;
        }
        DeviceContactSupportState state=DeviceSupportContactStates[candidate.contact];
        DeviceContactConstraint point=DeviceContacts[candidate.contact];
        state.enabled=1; state.upperIsSecond=candidate.upperIsSecond; state.lowerBody=candidate.lower; state.upperBody=candidate.upper;
        state.heightShift=shift; state.loadCount=count; state.orderIndex=order;
        state.minimumHeight=dot(deviceSupportSidePoint(point,candidate.upperIsSecond!=0,false),ContactSupportSettings.up)+shift;
        state.initialError=max(0,state.minimumHeight-dot(deviceSupportSidePoint(point,candidate.upperIsSecond!=0,true),ContactSupportSettings.up));
        DeviceSupportContactStates[candidate.contact]=state;
        DeviceBodySupportState body=DeviceSupportBodyStates[candidate.upper];
        DeviceSupportBodyContacts[candidate.upper*ContactSupportSettings.contactsPerBody+body.pointCount++]=candidate.contact;
        DeviceSupportBodyStates[candidate.upper]=body; DeviceSupportOrder[work.orderStart()+order++]=candidate.contact;
    }
    work.setOrderCount(order);
    for(uint bodyOffset=0;bodyOffset<work.bodyCount();bodyOffset++)
    {
        uint body=work.body(bodyOffset);
        if(!deviceSupportBodySelected(body)||!bodies.contains(body)) continue;
        DeviceBodySupportState state=DeviceSupportBodyStates[body]; if(state.moving!=0) continue;
        float3 center=bodyCenterOfMass(DeviceBodyStates[body]); uint3 contacts; float3 weights; uint count;
        if(!deviceSupportWeights(body,deviceSupportProject(center),contacts,weights,count)) continue;
        float height=dot(center,ContactSupportSettings.up);
        for(uint load=0;load<count;load++)
        {
            height+=weights[load]*DeviceSupportContactStates[contacts[load]].heightShift;
            DeviceSupportLoad value; value.contact=contacts[load]; value.weight=weights[load]; DeviceSupportBodyLoads[body*3+load]=value;
        }
        state.minimumHeight=height; state.initialError=max(0,height-dot(DeviceBodyStates[body].startCenterOfMass,ContactSupportSettings.up));
        state.enabled=1; state.loadCount=count; DeviceSupportBodyStates[body]=state;
    }
    for(uint contactOffset=0;contactOffset<work.contactCount();contactOffset++)
    {
        uint row=work.contact(contactOffset);
        if(deviceSupportContactRuns(row,bodies)&&DeviceSupportContactStates[row].enabled==0) deviceReleaseContactSupport(row);
    }
}
void initializeDeviceContactSupports<T : IDeviceBodySelection,TWork : IDeviceContactSupportWork>(DeviceAvbdStep context,T bodies,TWork work)
{
    for(uint contactOffset=0;contactOffset<work.contactCount();contactOffset++)
    {
        uint row=work.contact(contactOffset);
        if(!deviceSupportContactRuns(row,bodies)) continue;
        DeviceContactSupportState state=DeviceSupportContactStates[row];
        state.extraForceOnSecond*=DeviceContacts[row].warmStart!=0?ContactSupportSettings.retention:0;
        DeviceSupportContactStates[row]=state;
    }
    deviceSupportUpdateMotion(bodies,work); buildDeviceContactSupports(context,bodies,work);
}
bool deviceSupportUpdateMotion<T : IDeviceBodySelection>(T bodies)
{ DeviceAllContactSupportWork work; return deviceSupportUpdateMotion(bodies,work); }
void buildDeviceContactSupports<T : IDeviceBodySelection>(DeviceAvbdStep context,T bodies)
{ DeviceAllContactSupportWork work; buildDeviceContactSupports(context,bodies,work); }
void initializeDeviceContactSupports<T : IDeviceBodySelection>(DeviceAvbdStep context,T bodies)
{ DeviceAllContactSupportWork work; initializeDeviceContactSupports(context,bodies,work); }
void initializeDeviceContactSupports(DeviceAvbdStep context)
{ DeviceAllEquationBodies bodies; initializeDeviceContactSupports(context,bodies); }
#endif
