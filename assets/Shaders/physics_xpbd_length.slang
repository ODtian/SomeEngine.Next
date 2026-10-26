#include "physics_xpbd.slang"
#include "physics_constraint_data.slang"
RWStructuredBuffer<float> DeviceLengthMultipliers;
struct DeviceXpbdLength : IDeviceXpbdProjection
{
    bool active(uint row) { return DeviceLengthsMetadata[row].active != 0; }
    void initialize(uint row, DeviceXpbdStep context) { DeviceLengthMultipliers[row] = 0; }
    void project(uint row, DeviceXpbdStep context)
    {
        DeviceLengthConstraint constraint = DeviceLengths[row];
        DeviceBodyState first = DeviceBodyStates[constraint.firstBody];
        DeviceBodyState second = DeviceBodyStates[constraint.secondBody];
        float3 difference = second.position - first.position;
        float distance = length(difference);
        float3 gradient = difference / distance;
        float firstWeight = first.motion == 2 ? first.inverseMass : 0;
        float secondWeight = second.motion == 2 ? second.inverseMass : 0;
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
