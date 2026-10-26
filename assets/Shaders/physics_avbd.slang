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
    void adjust(uint body,DeviceAvbdStep context,DeviceAvbdEquation equation,inout float3 translation,inout float3 rotation)
    { first.adjust(body,context,equation,translation,rotation); second.adjust(body,context,equation,translation,rotation); }
};
void initializeDeviceAvbd<T : IDeviceAvbdConstraints>(uint index,T constraints)
{
    uint equations,stride; DeviceAvbdEquationMetadata.GetDimensions(equations,stride);
    if(index==0)
    {
        uint bodies; DeviceAvbdEquationIndices.GetDimensions(bodies,stride);
        for(uint body=0;body<bodies;body++) DeviceAvbdEquationIndices[body]=0xffffffff;
        for(uint row=0;row<equations;row++)
        {
            uint4 metadata=DeviceAvbdEquationMetadata[row];
            if(metadata.w==0) continue;
            DeviceBodyState body=DeviceBodyStates[metadata.x];
            if(body.entityIndex==metadata.y&&body.entityGeneration==metadata.z&&body.motion==2)
                DeviceAvbdEquationIndices[metadata.x]=row;
        }
    }
    if(index<equations) deviceAvbdInitializeEquation(index);
    if(index<constraints.count()&&constraints.active(index)) constraints.initialize(index,AvbdStep);
}
void updateDeviceAvbdBody<T : IDeviceAvbdConstraints>(uint index,T constraints)
{
    uint2 range=ConstraintGraphBodyRanges[AvbdStep.color];
    if(index>=range.y) return;
    uint bodyIndex=ConstraintGraphBodyOrder[range.x+index], equationIndex=DeviceAvbdEquationIndices[bodyIndex];
    if(equationIndex==0xffffffff) return;
    DeviceBodyState body=DeviceBodyStates[bodyIndex];
    DeviceAvbdEquation equation=DeviceAvbdBodyEquations[equationIndex];
    for(uint local=0;local<AvbdStep.bodyIterations;local++)
    {
        deviceAvbdReset(equation,body,AvbdStep.duration);
        for(uint entry=ConstraintGraphBodyOffsets[bodyIndex];entry<ConstraintGraphBodyOffsets[bodyIndex+1];entry++)
        {
            uint row=ConstraintGraphAdjacency[entry];
            if(constraints.active(row)&&deviceGraphRowWrites(ConstraintGraphRows[row],bodyIndex))
                constraints.accumulate(row,bodyIndex,AvbdStep,equation);
        }
        deviceAvbdFactorize(equation);
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
{ if(row<constraints.count()&&constraints.active(row)) constraints.updateDuals(row,AvbdStep); }
#endif
