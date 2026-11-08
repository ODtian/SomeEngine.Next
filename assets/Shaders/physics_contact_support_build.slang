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
uint buildDeviceSupportHull(uint body)
{
    uint start=ConstraintGraphBodyOffsets[body],length=DeviceSupportBodyStates[body].pointCount;
    for(uint index=0;index<length;index++) DeviceSupportSortedContacts[start+index]=DeviceSupportBodyContacts[start+index];
    for(uint index=1;index<length;index++)
    {
        uint contact=DeviceSupportSortedContacts[start+index],target=index;
        float2 point=deviceSupportWitness(contact);
        while(target>0)
        {
            uint prior=DeviceSupportSortedContacts[start+target-1]; float2 previous=deviceSupportWitness(prior);
            bool before=point.x<previous.x||(point.x==previous.x&&(point.y<previous.y
                ||(point.y==previous.y&&deviceSupportContactBefore(contact,prior))));
            if(!before) break;
            DeviceSupportSortedContacts[start+target]=prior; target--;
        }
        DeviceSupportSortedContacts[start+target]=contact;
    }
    uint unique=0;
    for(uint index=0;index<length;index++)
    {
        uint contact=DeviceSupportSortedContacts[start+index];
        if(unique==0||any(deviceSupportWitness(contact)!=deviceSupportWitness(DeviceSupportSortedContacts[start+unique-1])))
            DeviceSupportSortedContacts[start+unique++]=contact;
    }
    if(unique<=2)
    {
        for(uint index=0;index<unique;index++) DeviceSupportHullContacts[start+index]=DeviceSupportSortedContacts[start+index];
        return unique;
    }
    uint count=0;
    for(uint index=0;index<unique;index++)
    {
        uint contact=DeviceSupportSortedContacts[start+index]; float2 point=deviceSupportWitness(contact);
        while(count>=2&&deviceSupportOrientation(deviceSupportWitness(DeviceSupportHullContacts[start+count-2]),
            deviceSupportWitness(DeviceSupportHullContacts[start+count-1]),point)<=0) count--;
        DeviceSupportHullContacts[start+count++]=contact;
    }
    uint lowerCount=count;
    for(int index=int(unique)-2;index>=0;index--)
    {
        uint contact=DeviceSupportSortedContacts[start+uint(index)]; float2 point=deviceSupportWitness(contact);
        while(count>lowerCount&&deviceSupportOrientation(deviceSupportWitness(DeviceSupportHullContacts[start+count-2]),
            deviceSupportWitness(DeviceSupportHullContacts[start+count-1]),point)<=0) count--;
        DeviceSupportHullContacts[start+count++]=contact;
    }
    return count-1;
}
// The sorted, unique monotone hull and its affine weights follow ContactSupportPolygon.
bool deviceSupportWeights(uint body,float2 point,out uint3 contacts,out float3 weights,out uint count)
{
    contacts=uint3(0); weights=float3(0); count=0;
    uint length=buildDeviceSupportHull(body),start=ConstraintGraphBodyOffsets[body];
    if(length==0) return false;
    uint first=DeviceSupportHullContacts[start]; float2 a=deviceSupportWitness(first);
    float tolerance=ContactSupportSettings.distanceTolerance;
    if(length<=2)
    {
        uint last=DeviceSupportHullContacts[start+length-1]; float2 b=deviceSupportWitness(last),line=b-a;
        float squared=dot(line,line);
        if(squared==0)
        {
            if(dot(point-a,point-a)>tolerance*tolerance) return false;
            contacts.x=first; weights.x=1; count=1; return true;
        }
        float fraction=dot(point-a,line)/squared;
        if(fraction < -tolerance||fraction>1+tolerance) return false;
        fraction=clamp(fraction,0,1); float2 delta=a+line*fraction-point;
        if(dot(delta,delta)>tolerance*tolerance) return false;
        contacts=uint3(first,last,0); weights=float3(1-fraction,fraction,0); count=2; return true;
    }
    for(uint index=1;index<length-1;index++)
    {
        uint second=DeviceSupportHullContacts[start+index],third=DeviceSupportHullContacts[start+index+1];
        float2 b=deviceSupportWitness(second),c=deviceSupportWitness(third);
        double area=deviceSupportOrientation(a,b,c);
        double wb=deviceSupportOrientation(a,point,c)/area,wc=deviceSupportOrientation(a,b,point)/area,wa=1-wb-wc;
        if(min(wa,min(wb,wc))>=-tolerance)
        {
            wa=max(0.0,wa); wb=max(0.0,wb); wc=max(0.0,wc); double total=wa+wb+wc;
            contacts=uint3(first,second,third); weights=float3(float(wa/total),float(wb/total),float(wc/total));
            count=3; return true;
        }
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
bool deviceSupportCandidate(uint row,DeviceAvbdStep context,out DeviceSupportCandidate candidate)
{
    candidate=(DeviceSupportCandidate)0;
    DeviceContactConstraint point=DeviceContacts[row];
    if(!isinf(point.normalStiffness)||point.normalStiffness<0) return false;
    uint first=DeviceContactBodyLinks[point.bodyOffset].stateIndex;
    uint second=DeviceContactBodyLinks[point.bodyOffset+point.firstBodyCount].stateIndex;
    bool rigid=first!=second;
    for(uint end=0;end<point.bodyCount;end++)
        rigid=rigid&&DeviceContactBodyLinks[point.bodyOffset+end].stateIndex==(end<point.firstBodyCount?first:second);
    if(!rigid) return false;
    float vertical=dot(point.normal,ContactSupportSettings.up),squared=vertical*vertical;
    if(vertical==0||1-squared>point.staticFriction*point.staticFriction*squared) return false;
    bool upperSecond=vertical>0;
    uint lower=upperSecond?first:second,upper=upperSecond?second:first;
    if(!deviceSupportBodySelected(upper)||DeviceSupportBodyStates[upper].moving!=0) return false;
    if(DeviceBodyStates[lower].motion==2)
    { if(!deviceSupportBodySelected(lower)||DeviceSupportBodyStates[lower].moving!=0) return false; }
    else if(deviceSupportHasMotion(lower)) return false;
    float3 delta=deviceContactNormalDelta(point,false);
    float3 gap=point.normal*point.separation+delta-point.bodyAnchorDelta;
    float pressure=dot(DeviceContactResponses[row].forceOnSecond,point.normal);
    if(dot(point.normal,gap)>ContactSupportSettings.distanceTolerance&&pressure<=0) return false;
    float3 slip=deviceContactDelta(point,false)-deviceContactDelta(point,true); slip-=point.normal*dot(point.normal,slip);
    float travel=length(slip),mobility=0;
    if(travel>0)
    {
        float3 linear,angular;
        deviceContactJacobian(point,first,slip/travel,linear,angular); mobility+=bodyConstraintWeight(DeviceBodyStates[first],linear,angular);
        deviceContactJacobian(point,second,slip/travel,linear,angular); mobility+=bodyConstraintWeight(DeviceBodyStates[second],linear,angular);
    }
    if(travel>ContactSupportSettings.stickDistance+point.staticFriction*abs(pressure)*mobility*context.duration*context.duration) return false;
    float3 firstWitness=point.worldPoint-point.normal*(point.separation*.5),secondWitness=point.worldPoint+point.normal*(point.separation*.5);
    candidate.contact=row; candidate.lower=lower; candidate.upper=upper; candidate.upperIsSecond=upperSecond?1:0;
    candidate.lowerPoint=deviceSupportProject(upperSecond?firstWitness:secondWitness);
    candidate.upperPoint=deviceSupportProject(upperSecond?secondWitness:firstWitness);
    candidate.height=dot(point.worldPoint,ContactSupportSettings.up); candidate.gapHeight=dot(gap,ContactSupportSettings.up)*(upperSecond?1:-1);
    return true;
}
bool deviceBuildSupportCandidate(DeviceSupportCandidate candidate,uint order)
{
    uint3 contacts; float3 weights; uint count=0;
    if(DeviceBodyStates[candidate.lower].motion==2
        &&!deviceSupportWeights(candidate.lower,candidate.lowerPoint,contacts,weights,count)) return false;
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
    DeviceSupportBodyContacts[ConstraintGraphBodyOffsets[candidate.upper]+body.pointCount++]=candidate.contact;
    DeviceSupportBodyStates[candidate.upper]=body;
    return true;
}
void deviceBuildSupportBody(uint body)
{
    DeviceBodySupportState state=DeviceSupportBodyStates[body]; if(state.moving!=0) return;
    float3 center=bodyCenterOfMass(DeviceBodyStates[body]); uint3 contacts; float3 weights; uint count;
    if(!deviceSupportWeights(body,deviceSupportProject(center),contacts,weights,count)) return;
    float height=dot(center,ContactSupportSettings.up);
    for(uint load=0;load<count;load++)
    {
        height+=weights[load]*DeviceSupportContactStates[contacts[load]].heightShift;
        DeviceSupportLoad value; value.contact=contacts[load]; value.weight=weights[load]; DeviceSupportBodyLoads[body*3+load]=value;
    }
    state.minimumHeight=height; state.initialError=max(0,height-dot(DeviceBodyStates[body].startCenterOfMass,ContactSupportSettings.up));
    state.enabled=1; state.loadCount=count; DeviceSupportBodyStates[body]=state;
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
        DeviceSupportCandidate candidate;
        if(!deviceSupportCandidate(row,context,candidate)) continue;
        uint position=candidateCount++;
        while(!work.contactsAreHeightOrdered()&&position>0)
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
        if(deviceBuildSupportCandidate(candidate,order)) DeviceSupportOrder[work.orderStart()+order++]=candidate.contact;
    }
    work.setOrderCount(order);
    for(uint bodyOffset=0;bodyOffset<work.bodyCount();bodyOffset++)
    {
        uint body=work.body(bodyOffset);
        if(!deviceSupportBodySelected(body)||!bodies.contains(body)) continue;
        deviceBuildSupportBody(body);
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
        state.extraForceOnSecond*=DeviceContacts[row].warmStart!=0?(context.firstIteration==0?ContactSupportSettings.retention:1):0;
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
