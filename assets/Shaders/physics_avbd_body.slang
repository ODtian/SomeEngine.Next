// SPDX-License-Identifier: Apache-2.0
// Effective translation/angular equations follow AvbdBodyEquation and Newton v1.7.0.
#ifndef PHYSICS_AVBD_BODY
#define PHYSICS_AVBD_BODY
#include "physics_body_math.slang"
struct DeviceAvbdEquation
{
    float values[21]; float factors[21];
    float3 inertiaX; float3 inertiaY; float3 inertiaZ;
    float3 linearGradient; float3 angularGradient; uint dimensions;
    // Pose of the angular basis used by the cached factorization.
    float4 rotationFrame;
};
RWStructuredBuffer<DeviceAvbdEquation> DeviceAvbdBodyEquations;
// Metadata = body index, actual Entity index/generation, active.
RWStructuredBuffer<uint4> DeviceAvbdEquationMetadata;
RWStructuredBuffer<uint> DeviceAvbdEquationIndices;
uint deviceAvbdMatrixIndex(uint row,uint column) { return row * (row+1) / 2 + column; }
float3 deviceAvbdInertia(DeviceAvbdEquation equation,float3 value)
{ return value.x * equation.inertiaX + value.y * equation.inertiaY + value.z * equation.inertiaZ; }
float3 deviceAvbdRotationAxis(DeviceAvbdEquation equation,uint activeAxis)
{
    uint mask=(equation.inertiaX.x!=0?1u:0u)|(equation.inertiaY.y!=0?2u:0u)|(equation.inertiaZ.z!=0?4u:0u);
    if(mask==7) return activeAxis==0?float3(1,0,0):activeAxis==1?float3(0,1,0):float3(0,0,1);
    uint index=0;
    for(uint axis=0;axis<3;axis++)
    {
        if((mask&(1u<<axis))==0) continue;
        if(index++==activeAxis)
        {
            float3 direction=axis==0?float3(1,0,0):axis==1?float3(0,1,0):float3(0,0,1);
            return bodyRotate(equation.rotationFrame,direction);
        }
    }
    return float3(0);
}
void deviceAvbdInitializeEquation(uint row)
{
    uint4 metadata = DeviceAvbdEquationMetadata[row];
    if (metadata.w == 0) return;
    DeviceBodyState body = DeviceBodyStates[metadata.x];
    if (body.entityIndex != metadata.y || body.entityGeneration != metadata.z || body.motion != 2) return;
    DeviceAvbdEquation equation;
    equation.dimensions = 3 + countbits(bodyRotationMask(body));
    equation.rotationFrame = body.rotation;
    equation.inertiaX = equation.inertiaY = equation.inertiaZ = float3(0);
    if (equation.dimensions > 3)
    {
        equation.inertiaX = bodyLocalInertia(body,float3(1,0,0));
        equation.inertiaY = bodyLocalInertia(body,float3(0,1,0));
        equation.inertiaZ = bodyLocalInertia(body,float3(0,0,1));
    }
    equation.linearGradient = equation.angularGradient = float3(0);
    for (uint index=0;index<21;index++) equation.values[index]=equation.factors[index]=0;
    DeviceAvbdBodyEquations[row] = equation;
}
void deviceAvbdReset(inout DeviceAvbdEquation equation,DeviceBodyState body,float duration)
{
    for (uint index=0;index<21;index++) equation.values[index]=0;
    equation.rotationFrame=body.rotation;
    float inverseSquaredStep = 1 / (duration*duration), linearWeight = inverseSquaredStep / body.inverseMass;
    equation.linearGradient = (bodyCenterOfMass(body)-body.predictedCenterOfMass)*linearWeight;
    equation.angularGradient = float3(0);
    if (equation.dimensions > 3)
    {
        float3 error = bodyRotationVector(bodyRotationMultiply(bodyRotationConjugate(body.rotation),body.predictedRotation));
        equation.angularGradient = -bodyRotate(body.rotation,deviceAvbdInertia(equation,error))*inverseSquaredStep;
    }
    for (uint axis=0;axis<3;axis++)
        equation.values[deviceAvbdMatrixIndex(axis,axis)] = linearWeight;
    for (uint axis=0;axis<equation.dimensions-3;axis++)
    {
        float3 direction=deviceAvbdRotationAxis(equation,axis);
        float3 column=bodyRotate(body.rotation,deviceAvbdInertia(equation,bodyRotate(bodyRotationConjugate(body.rotation),direction)))*inverseSquaredStep;
        for (uint row=axis;row<equation.dimensions-3;row++)
            equation.values[deviceAvbdMatrixIndex(row+3,axis+3)]=dot(deviceAvbdRotationAxis(equation,row),column);
    }
}
void deviceAvbdAddRow(inout DeviceAvbdEquation equation,float3 linear,float3 angular,float force,float stiffness)
{
    if (equation.dimensions==3) angular=float3(0);
    equation.linearGradient+=linear*force; equation.angularGradient+=angular*force;
    float jacobian[6]={linear.x,linear.y,linear.z,0,0,0};
    for(uint axis=0;axis<equation.dimensions-3;axis++) jacobian[axis+3]=dot(angular,deviceAvbdRotationAxis(equation,axis));
    for (uint row=0;row<equation.dimensions;row++)
        for (uint column=0;column<=row;column++)
            equation.values[deviceAvbdMatrixIndex(row,column)]+=jacobian[row]*jacobian[column]*stiffness;
}
void deviceAvbdFactorize(inout DeviceAvbdEquation equation)
{
    for (uint row=0;row<equation.dimensions;row++)
    {
        for (uint column=0;column<row;column++)
        {
            float value=equation.values[deviceAvbdMatrixIndex(row,column)];
            for(uint previous=0;previous<column;previous++)
                value-=equation.factors[deviceAvbdMatrixIndex(row,previous)]*equation.factors[deviceAvbdMatrixIndex(column,previous)]*
                    equation.factors[deviceAvbdMatrixIndex(previous,previous)];
            equation.factors[deviceAvbdMatrixIndex(row,column)]=value/equation.factors[deviceAvbdMatrixIndex(column,column)];
        }
        float diagonal=equation.values[deviceAvbdMatrixIndex(row,row)];
        for(uint previous=0;previous<row;previous++)
        {
            float coefficient=equation.factors[deviceAvbdMatrixIndex(row,previous)];
            diagonal-=coefficient*coefficient*equation.factors[deviceAvbdMatrixIndex(previous,previous)];
        }
        equation.factors[deviceAvbdMatrixIndex(row,row)]=diagonal;
    }
}
void deviceAvbdResponse(DeviceAvbdEquation equation,float3 force,float3 torque,out float3 translation,out float3 rotation)
{
    float response[6]={force.x,force.y,force.z,0,0,0};
    for(uint axis=0;axis<equation.dimensions-3;axis++) response[axis+3]=dot(torque,deviceAvbdRotationAxis(equation,axis));
    for(uint row=0;row<equation.dimensions;row++)
        for(uint column=0;column<row;column++) response[row]-=equation.factors[deviceAvbdMatrixIndex(row,column)]*response[column];
    for(uint row=0;row<equation.dimensions;row++) response[row]/=equation.factors[deviceAvbdMatrixIndex(row,row)];
    for(int row=int(equation.dimensions)-1;row>=0;row--)
        for(uint column=uint(row)+1;column<equation.dimensions;column++) response[row]-=equation.factors[deviceAvbdMatrixIndex(column,uint(row))]*response[column];
    translation=float3(response[0],response[1],response[2]);
    rotation=float3(0);
    for(uint axis=0;axis<equation.dimensions-3;axis++) rotation+=deviceAvbdRotationAxis(equation,axis)*response[axis+3];
}
float2 deviceAvbdMaterial(float stiffness,float penalty)
{
    if(stiffness<=0||penalty<=0) return float2(0);
    if(stiffness>=penalty) { float scale=1/(1+penalty/stiffness); return float2(scale,penalty*scale); }
    float ratio=stiffness/penalty, complement=1/(1+ratio);
    return float2(ratio*complement,stiffness*complement);
}
float3 deviceAvbdForce(float2 terms,float3 error,float3 multiplier) { return terms.x*multiplier+terms.y*error; }
#endif
