#ifndef PHYSICS_XPBD_BEND_MATH
#define PHYSICS_XPBD_BEND_MATH
#include "physics_xpbd_types.slang"
#include "physics_body_math.slang"
#include "physics_bend_data.slang"
RWStructuredBuffer<float> DeviceBendMultipliers;
float deviceBendGradients(float3 first,float3 middle,float3 last,
    out float3 firstGradient,out float3 middleGradient,out float3 lastGradient)
{
    float3 firstEdge=middle-first,secondEdge=last-middle;
    float firstLength=length(firstEdge),secondLength=length(secondEdge);
    float3 firstDirection=firstEdge/firstLength,secondDirection=secondEdge/secondLength;
    float cosine=dot(firstDirection,secondDirection);
    float3 firstDerivative=(secondDirection-cosine*firstDirection)/firstLength;
    float3 secondDerivative=(firstDirection-cosine*secondDirection)/secondLength;
    firstGradient=-firstDerivative; middleGradient=firstDerivative-secondDerivative; lastGradient=secondDerivative;
    return cosine;
}
struct DeviceXpbdBendProjection<TSelection : IDeviceBodySelection> : IDeviceXpbdProjection
{
    bool active(uint row) { return DeviceBendsMetadata[row].active != 0; }
    void initialize(uint row, DeviceXpbdStep context) { DeviceBendMultipliers[row] = 0; }
    void project(uint row, DeviceXpbdStep context)
    {
        DeviceBendConstraint constraint = DeviceBends[row];
        TSelection selection;
        DeviceBodyState first = DeviceBodyStates[constraint.firstBody];
        DeviceBodyState middle = DeviceBodyStates[constraint.middleBody];
        DeviceBodyState last = DeviceBodyStates[constraint.lastBody];
        float3 firstGradient,middleGradient,lastGradient;
        float cosine=deviceBendGradients(first.position,middle.position,last.position,firstGradient,middleGradient,lastGradient);
        float residual = cosine - constraint.minimumCosine;
        float multiplier = DeviceBendMultipliers[row];
        if (residual >= 0 && multiplier == 0) return;
        float firstWeight = first.motion == 2 && selection.contains(constraint.firstBody) ? first.inverseMass : 0;
        float middleWeight = middle.motion == 2 && selection.contains(constraint.middleBody) ? middle.inverseMass : 0;
        float lastWeight = last.motion == 2 && selection.contains(constraint.lastBody) ? last.inverseMass : 0;
        float alpha = constraint.compliance / (context.duration * context.duration);
        float change = (-residual - alpha * multiplier) /
            (firstWeight * dot(firstGradient, firstGradient) + middleWeight * dot(middleGradient, middleGradient) +
                lastWeight * dot(lastGradient, lastGradient) + alpha);
        change = max(0, multiplier + change) - multiplier;
        DeviceBendMultipliers[row] = multiplier + change;
        if (firstWeight > 0)
        {
            first.position += firstWeight * firstGradient * change;
            DeviceBodyStates[constraint.firstBody] = first;
        }
        if (middleWeight > 0)
        {
            middle.position += middleWeight * middleGradient * change;
            DeviceBodyStates[constraint.middleBody] = middle;
        }
        if (lastWeight > 0)
        {
            last.position += lastWeight * lastGradient * change;
            DeviceBodyStates[constraint.lastBody] = last;
        }
    }
};
#endif
