#include "physics_xpbd_selection.slang"
#include "physics_length_data.slang"
RWStructuredBuffer<float> DeviceLengthMultipliers;
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
        float3 difference = second.position - first.position;
        float distance = length(difference);
        float3 gradient = difference / distance;
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
struct DeviceXpbdLength : IDeviceXpbdProjection
{
    DeviceXpbdLengthProjection<DeviceSelectedBodies> inner;
    bool active(uint row) { return inner.active(row); }
    void initialize(uint row, DeviceXpbdStep context) { inner.initialize(row, context); }
    void project(uint row, DeviceXpbdStep context) { inner.project(row, context); }
};
