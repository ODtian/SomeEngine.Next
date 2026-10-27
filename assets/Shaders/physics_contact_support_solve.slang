// SPDX-License-Identifier: Apache-2.0
// Local support responses and reverse load transfer follow ContactSupportData.Solve.
#ifndef PHYSICS_CONTACT_SUPPORT_SOLVE
#define PHYSICS_CONTACT_SUPPORT_SOLVE
#include "physics_contact_support_build.slang"
float deviceSupportResponse(DeviceSupportEquation height,DeviceSupportEquation force)
{ return dot(height.linear,force.translationResponse)+dot(height.angular,force.rotationResponse); }
bool deviceSupportSolveForces(uint start,uint equationCount,uint3 active,uint count)
{
    double coefficients[12]; double scale=0;
    for(uint row=0;row<count;row++)
    {
        for(uint column=0;column<count;column++)
            coefficients[row*4+column]=deviceSupportResponse(DeviceSupportEquations[start+active[row]],DeviceSupportEquations[start+active[column]]);
        coefficients[row*4+count]=DeviceSupportEquations[start+active[row]].heightError;
        scale=max(scale,coefficients[row*4+row]);
    }
    for(uint column=0;column<count;column++)
    {
        double diagonal=coefficients[column*4+column];
        if(diagonal<=scale*ContactSupportSettings.relativePivotTolerance) return false;
        for(uint row=column+1;row<count;row++)
        {
            double ratio=coefficients[row*4+column]/diagonal;
            for(uint component=column+1;component<=count;component++) coefficients[row*4+component]-=ratio*coefficients[column*4+component];
        }
    }
    double forces[3];
    for(int row=int(count)-1;row>=0;row--)
    {
        double value=coefficients[row*4+count];
        for(uint column=uint(row)+1;column<count;column++) value-=coefficients[row*4+column]*forces[column];
        forces[row]=value/coefficients[row*4+row]; if(forces[row]<0) return false;
    }
    for(uint row=0;row<equationCount;row++)
    {
        DeviceSupportEquation equation=DeviceSupportEquations[start+row]; double height=0;
        for(uint index=0;index<count;index++) height+=deviceSupportResponse(equation,DeviceSupportEquations[start+active[index]])*forces[index];
        if(equation.heightError>height+ContactSupportSettings.distanceTolerance) return false;
    }
    for(uint index=0;index<count;index++)
    {
        DeviceSupportEquation equation=DeviceSupportEquations[start+active[index]];
        equation.reaction=float(forces[index]); DeviceSupportEquations[start+active[index]]=equation;
    }
    return true;
}
void deviceSupportFindForces(uint start,uint count)
{
    for(uint first=0;first<count;first++)
        if(deviceSupportSolveForces(start,count,uint3(first,0,0),1)) return;
    for(uint first=0;first<count;first++)
        for(uint second=first+1;second<count;second++)
            if(deviceSupportSolveForces(start,count,uint3(first,second,0),2)) return;
    for(uint first=0;first<count;first++)
        for(uint second=first+1;second<count;second++)
            for(uint third=second+1;third<count;third++)
                if(deviceSupportSolveForces(start,count,uint3(first,second,third),3)) return;
}
void correctDeviceContactSupport(uint body,DeviceAvbdStep context,DeviceAvbdEquation factor,inout float3 translation,inout float3 rotation)
{
    if(!deviceSupportBodySelected(body)) return;
    DeviceBodySupportState center=DeviceSupportBodyStates[body]; center.reaction=0;
    uint start=body*(ContactSupportSettings.contactsPerBody+1),count=0;
    float3 up=ContactSupportSettings.up;
    if(center.enabled!=0)
    {
        DeviceSupportEquation equation=(DeviceSupportEquation)0; equation.contact=0xffffffff; equation.linear=up;
        float height=dot(bodyCenterOfMass(DeviceBodyStates[body]),up);
        equation.heightError=center.minimumHeight-ContactSupportSettings.initialErrorFraction*center.initialError-height-dot(up,translation);
        DeviceSupportEquations[start+count++]=equation;
    }
    for(uint index=0;index<center.pointCount;index++)
    {
        uint pointRow=DeviceSupportBodyContacts[body*ContactSupportSettings.contactsPerBody+index];
        DeviceContactSupportState state=DeviceSupportContactStates[pointRow]; state.reaction=0; DeviceSupportContactStates[pointRow]=state;
        DeviceContactConstraint point=DeviceContacts[pointRow];
        DeviceSupportEquation equation=(DeviceSupportEquation)0; equation.contact=pointRow;
        deviceContactJacobian(point,body,up,equation.linear,equation.angular);
        float side=state.upperIsSecond!=0?1:-1; equation.linear*=side; equation.angular*=side;
        float height=dot(deviceSupportSidePoint(point,state.upperIsSecond!=0,false),up);
        equation.heightError=state.minimumHeight-ContactSupportSettings.initialErrorFraction*state.initialError-height
            -dot(equation.linear,translation)-dot(equation.angular,rotation);
        DeviceSupportEquations[start+count++]=equation;
    }
    center.equationCount=count; DeviceSupportBodyStates[body]=center;
    bool hasError=false;
    for(uint index=0;index<count;index++) hasError=hasError||DeviceSupportEquations[start+index].heightError>0;
    if(!hasError) return;
    float3 heightLinear,heightAngular;
    deviceAvbdResponse(factor,up,float3(0),heightLinear,heightAngular);
    if(center.enabled!=0)
    {
        DeviceSupportEquation equation=DeviceSupportEquations[start];
        equation.translationResponse=heightLinear; equation.rotationResponse=heightAngular; DeviceSupportEquations[start]=equation;
    }
    if(center.enabled==0||!deviceSupportSolveForces(start,count,uint3(0),1))
    {
        float3 firstLinear,firstAngular,secondLinear,secondAngular;
        float3 horizontalX=deviceSupportHorizontalX(),horizontalY=deviceSupportHorizontalY();
        deviceAvbdResponse(factor,float3(0),horizontalX,firstLinear,firstAngular);
        deviceAvbdResponse(factor,float3(0),horizontalY,secondLinear,secondAngular);
        for(uint index=0;index<count;index++)
        {
            DeviceSupportEquation equation=DeviceSupportEquations[start+index];
            float first=dot(equation.angular,horizontalX),second=dot(equation.angular,horizontalY);
            equation.translationResponse=heightLinear+firstLinear*first+secondLinear*second;
            equation.rotationResponse=heightAngular+firstAngular*first+secondAngular*second;
            DeviceSupportEquations[start+index]=equation;
        }
        deviceSupportFindForces(start,count);
    }
    for(uint index=0;index<count;index++)
    {
        DeviceSupportEquation equation=DeviceSupportEquations[start+index];
        translation+=equation.translationResponse*equation.reaction; rotation+=equation.rotationResponse*equation.reaction;
        if(equation.contact==0xffffffff) center.reaction=equation.reaction;
        else
        {
            DeviceContactSupportState state=DeviceSupportContactStates[equation.contact];
            state.reaction=equation.reaction; DeviceSupportContactStates[equation.contact]=state;
        }
    }
    DeviceSupportBodyStates[body]=center;
}
void updateDeviceContactSupports<T : IDeviceBodySelection,TWork : IDeviceContactSupportWork>(DeviceAvbdStep context,T bodies,TWork work)
{
    if(deviceSupportUpdateMotion(bodies,work)) buildDeviceContactSupports(context,bodies,work);
    for(uint contactOffset=0;contactOffset<work.contactCount();contactOffset++)
    {
        uint row=work.contact(contactOffset);
        if(!deviceSupportContactRuns(row,bodies)) continue;
        DeviceContactSupportState state=DeviceSupportContactStates[row]; state.load=state.reaction; DeviceSupportContactStates[row]=state;
    }
    for(uint bodyOffset=0;bodyOffset<work.bodyCount();bodyOffset++)
    {
        uint body=work.body(bodyOffset);
        if(!deviceSupportBodySelected(body)||!bodies.contains(body)) continue;
        DeviceBodySupportState state=DeviceSupportBodyStates[body]; if(state.enabled==0) continue;
        for(uint index=0;index<state.loadCount;index++)
        {
            DeviceSupportLoad load=DeviceSupportBodyLoads[body*3+index];
            DeviceContactSupportState contact=DeviceSupportContactStates[load.contact]; contact.load+=state.reaction*load.weight;
            DeviceSupportContactStates[load.contact]=contact;
        }
    }
    for(uint index=work.orderCount();index>0;index--)
    {
        uint row=DeviceSupportOrder[work.orderStart()+index-1];
        if(!deviceSupportContactRuns(row,bodies)) continue;
        DeviceContactSupportState state=DeviceSupportContactStates[row];
        DeviceContactResponseState response=DeviceContactResponses[row];
        DeviceContactConstraint point=DeviceContacts[row];
        float3 original=response.forceOnSecond-state.extraForceOnSecond;
        float3 total=original+ContactSupportSettings.up*(state.load*(state.upperIsSecond!=0?1:-1));
        float normal=dot(total,point.normal),pressure=max(0,normal);
        float3 tangent=total-point.normal*normal; float magnitude=length(tangent),bound=point.staticFriction*pressure;
        if(magnitude>bound) tangent*=bound/magnitude;
        total=point.normal*pressure+tangent; state.extraForceOnSecond=total-original;
        response.forceOnSecond=total; DeviceSupportContactStates[row]=state; DeviceContactResponses[row]=response;
        for(uint loadIndex=0;loadIndex<state.loadCount;loadIndex++)
        {
            DeviceSupportLoad load=DeviceSupportContactLoads[row*3+loadIndex];
            DeviceContactSupportState below=DeviceSupportContactStates[load.contact]; below.load+=state.load*load.weight;
            DeviceSupportContactStates[load.contact]=below;
        }
    }
}
void updateDeviceContactSupports<T : IDeviceBodySelection>(DeviceAvbdStep context,T bodies)
{ DeviceAllContactSupportWork work; updateDeviceContactSupports(context,bodies,work); }
void updateDeviceContactSupports(DeviceAvbdStep context)
{ DeviceAllEquationBodies bodies; updateDeviceContactSupports(context,bodies); }
#endif
