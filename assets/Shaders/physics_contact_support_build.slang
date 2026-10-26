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
bool deviceSupportUpdateMotion()
{
    bool changed=false;
    for(uint body=0;body<ContactSupportSettings.bodies;body++)
    {
        if(!deviceSupportBodySelected(body)) continue;
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
bool deviceSupportWeights(uint body,float2 point,out uint3 contacts,out float3 weights,out uint count)
{
    contacts=uint3(0); weights=float3(0); count=0;
    uint length=DeviceSupportBodyStates[body].pointCount,start=body*ContactSupportSettings.contactsPerBody;
    float tolerance=ContactSupportSettings.distanceTolerance,squaredTolerance=tolerance*tolerance;
    for(uint first=0;first<length;first++)
    {
        uint a=DeviceSupportBodyContacts[start+first]; float2 pa=deviceSupportWitness(a);
        if(dot(point-pa,point-pa)<=squaredTolerance) { contacts.x=a; weights.x=1; count=1; return true; }
    }
    for(uint first=0;first<length;first++)
        for(uint second=first+1;second<length;second++)
        {
            uint a=DeviceSupportBodyContacts[start+first],b=DeviceSupportBodyContacts[start+second];
            float2 pa=deviceSupportWitness(a),pb=deviceSupportWitness(b),line=pb-pa;
            float squared=dot(line,line); if(squared==0) continue;
            float fraction=dot(point-pa,line)/squared;
            if(fraction < -tolerance||fraction>1+tolerance) continue;
            fraction=clamp(fraction,0,1); float2 delta=pa+line*fraction-point;
            if(dot(delta,delta)>squaredTolerance) continue;
            contacts=uint3(a,b,0); weights=float3(1-fraction,fraction,0); count=2; return true;
        }
    for(uint first=0;first<length;first++)
        for(uint second=first+1;second<length;second++)
            for(uint third=second+1;third<length;third++)
            {
                uint a=DeviceSupportBodyContacts[start+first],b=DeviceSupportBodyContacts[start+second],c=DeviceSupportBodyContacts[start+third];
                float2 pa=deviceSupportWitness(a),pb=deviceSupportWitness(b),pc=deviceSupportWitness(c);
                double area=deviceSupportOrientation(pa,pb,pc); if(area==0) continue;
                double wb=deviceSupportOrientation(pa,point,pc)/area,wc=deviceSupportOrientation(pa,pb,point)/area,wa=1-wb-wc;
                if(min(wa,min(wb,wc)) < -tolerance) continue;
                wa=max(0.0,wa); wb=max(0.0,wb); wc=max(0.0,wc); double total=wa+wb+wc;
                contacts=uint3(a,b,c); weights=float3(float(wa/total),float(wb/total),float(wc/total)); count=3; return true;
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
void buildDeviceContactSupports(DeviceAvbdStep context)
{
    for(uint body=0;body<ContactSupportSettings.bodies;body++)
    {
        if(!deviceSupportBodySelected(body)) continue;
        DeviceBodySupportState state=DeviceSupportBodyStates[body];
        state.enabled=0; state.reaction=0; state.pointCount=state.loadCount=state.equationCount=0;
        DeviceSupportBodyStates[body]=state;
    }
    uint candidateCount=0;
    for(uint row=0;row<ContactSupportSettings.contacts;row++)
    {
        if(!deviceSupportContactSelected(row)) continue;
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
            DeviceSupportCandidate previous=DeviceSupportCandidates[position-1];
            DeviceConstraintMetadata a=DeviceContactsMetadata[previous.contact],b=DeviceContactsMetadata[row];
            if(previous.height<candidate.height||(previous.height==candidate.height
                &&(a.entityIndex<b.entityIndex||(a.entityIndex==b.entityIndex&&a.entityGeneration<=b.entityGeneration)))) break;
            DeviceSupportCandidates[position]=previous; position--;
        }
        DeviceSupportCandidates[position]=candidate;
    }
    uint order=0;
    for(uint index=0;index<candidateCount;index++)
    {
        DeviceSupportCandidate candidate=DeviceSupportCandidates[index];
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
        DeviceSupportBodyStates[candidate.upper]=body; DeviceSupportOrder[1+order++]=candidate.contact;
    }
    DeviceSupportOrder[0]=order;
    for(uint body=0;body<ContactSupportSettings.bodies;body++)
    {
        if(!deviceSupportBodySelected(body)) continue;
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
    for(uint row=0;row<ContactSupportSettings.contacts;row++)
        if(deviceSupportContactSelected(row)&&DeviceSupportContactStates[row].enabled==0) deviceReleaseContactSupport(row);
}
void initializeDeviceContactSupports(DeviceAvbdStep context)
{
    for(uint row=0;row<ContactSupportSettings.contacts;row++)
    {
        if(!deviceSupportContactSelected(row)) continue;
        DeviceContactSupportState state=DeviceSupportContactStates[row];
        state.extraForceOnSecond*=DeviceContacts[row].warmStart!=0?ContactSupportSettings.retention:0;
        DeviceSupportContactStates[row]=state;
    }
    deviceSupportUpdateMotion(); buildDeviceContactSupports(context);
}
#endif
