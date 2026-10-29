#ifndef PHYSICS_AVBD
#define PHYSICS_AVBD
#include "physics_constraint_graph.slang"
#include "physics_avbd_body.slang"
struct DeviceAvbdStep
{
    float duration; float retention; float initialErrorFraction; uint bodyIterations;
    uint color; uint substep; uint2 number;
    uint2 startTime; uint2 endTime;
};
uniform DeviceAvbdStep AvbdStep;
interface IDeviceAvbdConstraints : IDeviceConstraintBodies
{
    void initialize(uint row,DeviceAvbdStep context);
    void accumulate(uint row,uint body,DeviceAvbdStep context,inout DeviceAvbdEquation equation);
    void updateDuals(uint row,DeviceAvbdStep context);
    bool adjustsBody() { return true; }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation);
};
struct DeviceAvbdConstraintGroups<TFirst : IDeviceAvbdConstraints,TSecond : IDeviceAvbdConstraints> : IDeviceAvbdConstraints
{
    TFirst first; TSecond second;
    uint count() { return first.count()+second.count(); }
    bool active(uint row) { return row<first.count()?first.active(row):second.active(row-first.count()); }
    uint bodyCount(uint row) { return row<first.count()?first.bodyCount(row):second.bodyCount(row-first.count()); }
    uint body(uint row,uint end) { return row<first.count()?first.body(row,end):second.body(row-first.count(),end); }
    bool writes(uint row,uint end) { return row<first.count()?first.writes(row,end):second.writes(row-first.count(),end); }
    uint groupCount() { return first.groupCount()+second.groupCount(); }
    uint group(uint row) { return row<first.count()?first.group(row):first.groupCount()+second.group(row-first.count()); }
    void initialize(uint row,DeviceAvbdStep context)
    { if(row<first.count()) first.initialize(row,context); else second.initialize(row-first.count(),context); }
    void accumulate(uint row,uint body,DeviceAvbdStep context,inout DeviceAvbdEquation equation)
    { if(row<first.count()) first.accumulate(row,body,context,equation); else second.accumulate(row-first.count(),body,context,equation); }
    void updateDuals(uint row,DeviceAvbdStep context)
    { if(row<first.count()) first.updateDuals(row,context); else second.updateDuals(row-first.count(),context); }
    override bool adjustsBody() { return first.adjustsBody()||second.adjustsBody(); }
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation)
    { first.adjust(body,context,equation,translation,rotation); second.adjust(body,context,equation,translation,rotation); }
};
void clearDeviceAvbdBodyIndices(uint body)
{
    uint bodies,stride; DeviceAvbdEquationIndices.GetDimensions(bodies,stride);
    if(body<bodies) DeviceAvbdEquationIndices[body]=0xffffffff;
}
void deviceAvbdWriteEquationIndex(uint row)
{
    uint4 metadata=DeviceAvbdEquationMetadata[row];
    if(metadata.w==0) return;
    DeviceBodyState body=DeviceBodyStates[metadata.x];
    if(body.entityIndex==metadata.y&&body.entityGeneration==metadata.z&&body.motion==2)
        DeviceAvbdEquationIndices[metadata.x]=row;
}
struct DeviceAllEquationBodies : IDeviceBodySelection
{
    bool contains(uint body) { return true; }
};
void initializeDeviceAvbdEquation<TSelection : IDeviceBodySelection>(uint row,TSelection bodies)
{
    uint4 metadata=DeviceAvbdEquationMetadata[row];
    if(metadata.w!=0&&bodies.contains(metadata.x)) deviceAvbdInitializeEquation(row);
}
void initializeDeviceAvbdRows<T : IDeviceAvbdConstraints,TSelection : IDeviceBodySelection>(uint index,T constraints,TSelection bodies)
{
    uint equations,stride; DeviceAvbdEquationMetadata.GetDimensions(equations,stride);
    if(index<equations)
    {
        deviceAvbdWriteEquationIndex(index);
        initializeDeviceAvbdEquation(index,bodies);
    }
    if(index<constraints.count()&&constraints.active(index)) constraints.initialize(index,AvbdStep);
}
void initializeDeviceAvbdRows<T : IDeviceAvbdConstraints>(uint index,T constraints)
{ DeviceAllEquationBodies bodies; initializeDeviceAvbdRows(index,constraints,bodies); }
void initializeDeviceAvbd<T : IDeviceAvbdConstraints,TSelection : IDeviceBodySelection>(uint index,T constraints,TSelection bodies)
{
    uint equations,stride; DeviceAvbdEquationMetadata.GetDimensions(equations,stride);
    if(index==0)
    {
        uint bodyCount; DeviceAvbdEquationIndices.GetDimensions(bodyCount,stride);
        for(uint body=0;body<bodyCount;body++) clearDeviceAvbdBodyIndices(body);
        for(uint row=0;row<equations;row++)
            deviceAvbdWriteEquationIndex(row);
    }
    if(index<equations) initializeDeviceAvbdEquation(index,bodies);
    if(index<constraints.count()&&constraints.active(index)) constraints.initialize(index,AvbdStep);
}
void initializeDeviceAvbd<T : IDeviceAvbdConstraints>(uint index,T constraints)
{ DeviceAllEquationBodies bodies; initializeDeviceAvbd(index,constraints,bodies); }
// The first primal iterate uses the completed start pose; the inertial target stays predicted.
void restoreDeviceAvbdBodyStartPose(uint index)
{
    if(index>=BodyStep.count) return;
    uint bodyIndex=DeviceBodyIndices[index];
    if(ConstraintGraphActiveBodies[bodyIndex]==0||DeviceAvbdEquationIndices[bodyIndex]==0xffffffff) return;
    DeviceBodyState body=DeviceBodyStates[bodyIndex];
    if(body.motion!=2) return;
    body.rotation=body.startRotation;
    bodySetCenterOfMass(body,body.startCenterOfMass);
    DeviceBodyStates[bodyIndex]=body;
}

void updateDeviceAvbdBody<T : IDeviceAvbdConstraints>(uint index,T constraints)
{
    uint2 range=ConstraintGraphBodyRanges[AvbdStep.color];
    if(index>=range.y) return;
    uint bodyIndex=ConstraintGraphBodyOrder[range.x+index], equationIndex=DeviceAvbdEquationIndices[bodyIndex];
    if(equationIndex==0xffffffff) return;
    DeviceBodyState body=DeviceBodyStates[bodyIndex];
    uint first=ConstraintGraphBodyOffsets[bodyIndex],end=ConstraintGraphBodyOffsets[bodyIndex+1];
    uint constraintRows=end-first;
    if(constraintRows==0&&!constraints.adjustsBody()&&
        all(bodyCenterOfMass(body)==body.predictedCenterOfMass)&&all(body.rotation==body.predictedRotation)) return;
    DeviceAvbdEquation equation=DeviceAvbdBodyEquations[equationIndex];
    for(uint local=0;local<AvbdStep.bodyIterations;local++)
    {
        if(deviceAvbdCanReuseInertia(equation,body,AvbdStep.duration,constraintRows))
            deviceAvbdUpdateGradients(equation,body,AvbdStep.duration);
        else
        {
            deviceAvbdReset(equation,body,AvbdStep.duration);
            for(uint entry=first;entry<end;entry++)
            {
                uint row=ConstraintGraphAdjacency[entry];
                if(constraints.active(row)&&deviceGraphRowWrites(ConstraintGraphRows[row],bodyIndex))
                    constraints.accumulate(row,bodyIndex,AvbdStep,equation);
            }
            deviceAvbdFactorize(equation);
            equation.constraintRows=constraintRows; equation.duration=AvbdStep.duration;
        }
        float3 translation,rotation;
        deviceAvbdResponse(equation,-equation.linearGradient,-equation.angularGradient,translation,rotation);
        constraints.adjust(bodyIndex,AvbdStep,equation,translation,rotation);
        float3 center=bodyCenterOfMass(body)+translation;
        body.rotation=bodyApplyRotationChange(body.rotation,rotation); bodySetCenterOfMass(body,center);
        DeviceBodyStates[bodyIndex]=body;
    }
    DeviceAvbdBodyEquations[equationIndex]=equation;
}
void updateDeviceAvbdDuals<T : IDeviceAvbdConstraints>(uint row,T constraints)
{ if(row<constraints.count()&&constraints.active(row)&&deviceGraphRowActive(ConstraintGraphRows[row])) constraints.updateDuals(row,AvbdStep); }
#endif
