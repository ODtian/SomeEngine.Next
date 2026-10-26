#ifndef PHYSICS_XPBD_LENGTH_MATH
#define PHYSICS_XPBD_LENGTH_MATH
#include "physics_xpbd_types.slang"
#include "physics_body_math.slang"
#include "physics_length_data.slang"
RWStructuredBuffer<float> DeviceLengthMultipliers;
float deviceLengthGradient(float3 first,float3 second,out float3 gradient)
{
    float3 difference=second-first;
    float distance=length(difference);
    gradient=difference/distance;
    return distance;
}
struct DeviceXpbdLengthProjection<TSelection : IDeviceBodySelection> : IDeviceXpbdProjection
{
    bool active(uint row) { return DeviceLengthsMetadata[row].active != 0; }
    void initialize(uint row, DeviceXpbdStep context) { DeviceLengthMultipliers[row] = 0; }
    void project(uint row, DeviceXpbdStep context)
    {
        DeviceLengthConstraint constraint = DeviceLengths[row];
        TSelection selection;
        DeviceBodyState first = DeviceBodyStates[constraint.firstBody];
        DeviceBodyState second = DeviceBodyStates[constraint.secondBody];
        float3 gradient;
        float distance = deviceLengthGradient(first.position, second.position, gradient);
        float firstWeight = first.motion == 2 && selection.contains(constraint.firstBody) ? first.inverseMass : 0;
        float secondWeight = second.motion == 2 && selection.contains(constraint.secondBody) ? second.inverseMass : 0;
        float alpha = constraint.compliance / (context.duration * context.duration);
        float multiplier = DeviceLengthMultipliers[row];
        float change = (-(distance - constraint.restLength) - alpha * multiplier) /
            (firstWeight + secondWeight + alpha);
        DeviceLengthMultipliers[row] = multiplier + change;
        if (firstWeight > 0)
        {
            first.position -= firstWeight * gradient * change;
            DeviceBodyStates[constraint.firstBody] = first;
        }
        if (secondWeight > 0)
        {
            second.position += secondWeight * gradient * change;
            DeviceBodyStates[constraint.secondBody] = second;
        }
    }
};
#endif
