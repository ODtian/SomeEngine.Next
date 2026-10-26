// SPDX-License-Identifier: Apache-2.0
// Radial bounds and positive curvature follow DistanceRangeMath and the project's LongRange.cpp.
#ifndef PHYSICS_AVBD_DISTANCE_MATH
#define PHYSICS_AVBD_DISTANCE_MATH
#include "physics_avbd.slang"
void deviceEvaluateDistanceRange(float minimum,float maximum,float stiffness,float penalty,
    float2 previous,float2 initialError,float initialFraction,float distance,
    out float force,out float curvature,out float2 multiplier)
{
    float2 material=deviceAvbdMaterial(stiffness,penalty);
    if(minimum==maximum)
    {
        force=material.x*previous.x+material.y*(distance-maximum-initialFraction*(initialError.x-initialError.y));
        curvature=material.y; multiplier=float2(force,0); return;
    }
    float upper=max(0,material.x*previous.x+material.y*(distance-maximum-initialFraction*initialError.x));
    float lower=max(0,material.x*previous.y+material.y*(minimum-distance-initialFraction*initialError.y));
    force=upper-lower; curvature=material.y*((upper>0?1:0)+(lower>0?1:0)); multiplier=float2(upper,lower);
}
void deviceAccumulateDistanceRange(float3 delta,float distance,float3 offset,float side,
    float force,float stiffness,inout DeviceAvbdEquation equation)
{
    if(stiffness==0) return;
    float3 direction=distance>0?delta/distance:float3(0);
    float3 linear=direction*side,angular=cross(offset,linear);
    deviceAvbdAddRow(equation,linear,angular,force,stiffness);
    if(force==0||distance==0) return;
    float magnitude=abs(force),radiusSquared=dot(offset,offset),radial=dot(offset,direction);
    for(uint axis=0;axis<3;axis++)
    {
        float3 unit=axis==0?float3(1,0,0):axis==1?float3(0,1,0):float3(0,0,1);
        float linearCurvature=magnitude/distance*max(0,1-direction[axis]*direction[axis]);
        float angularCurvature=max(0,(radiusSquared-offset[axis]*offset[axis]-angular[axis]*angular[axis])/distance);
        float3 column=offset*direction[axis]-unit*radial;
        deviceAvbdAddRow(equation,unit,float3(0),0,linearCurvature);
        deviceAvbdAddRow(equation,float3(0),unit,0,magnitude*(angularCurvature+length(column)));
    }
}
#endif
