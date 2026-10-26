#ifndef PHYSICS_XPBD_CONTACT
#define PHYSICS_XPBD_CONTACT
#include "physics_xpbd_types.slang"
#include "physics_contact_math.slang"
struct DeviceXpbdContactState
{
    float normalMultiplier;
    float3 tangentMultiplier;
};
RWStructuredBuffer<DeviceXpbdContactState> DeviceXpbdContactStates;
struct DeviceXpbdContactProjection<TSelection : IDeviceBodySelection> : IDeviceXpbdProjection
{
    bool active(uint row)
    {
        DeviceContactBodies bodies;
        return bodies.active(row) && deviceConstraintSelected(DeviceContactsMetadata[row], DeviceContactResponsesSelection[row]);
    }
    void initialize(uint row, DeviceXpbdStep context) { DeviceXpbdContactStates[row] = (DeviceXpbdContactState)0; }
    void project(uint row, DeviceXpbdStep context)
    {
        DeviceContactConstraint point = DeviceContacts[row];
        DeviceXpbdContactState state = DeviceXpbdContactStates[row];
        TSelection selection;
        float3 delta = deviceContactDelta(point, false), start = deviceContactDelta(point, true);
        float gap = point.separation + dot(point.normal, delta - point.bodyAnchorDelta);
        float startGap = point.separation + dot(point.normal, start - point.bodyAnchorDelta);
        float stiffness = point.normalStiffness * context.duration * context.duration;
        float damping = gap < min(startGap, 0) ? point.normalDamping * context.duration : 0;
        float weight = deviceContactWeight(point, point.normal, selection);
        float multiplier = max(0, state.normalMultiplier -
            (stiffness * gap + damping * (gap - startGap) + state.normalMultiplier) /
            (1 + (stiffness + damping) * weight));
        deviceContactApplyCorrection(point, point.normal, multiplier - state.normalMultiplier, selection);
        state.normalMultiplier = multiplier;
        float3 slip = deviceContactDelta(point, false) - start;
        slip -= point.normal * dot(point.normal, slip);
        float3 trial = state.tangentMultiplier;
        float travel = length(slip);
        if (travel > 0)
        {
            float3 direction = slip / travel;
            float tangentWeight = deviceContactWeight(point, direction, selection);
            if (tangentWeight > 0) trial -= direction * (travel / tangentWeight);
        }
        trial -= point.normal * dot(point.normal, trial);
        float magnitude = length(trial);
        if (magnitude > point.staticFriction * state.normalMultiplier)
            trial *= point.dynamicFriction * state.normalMultiplier / magnitude;
        float3 change = trial - state.tangentMultiplier;
        float correction = length(change);
        if (correction > 0) deviceContactApplyCorrection(point, change / correction, correction, selection);
        state.tangentMultiplier = trial;
        DeviceXpbdContactStates[row] = state;
        DeviceContactResponseState response = DeviceContactResponses[row];
        response.forceOnSecond = (point.normal * multiplier + state.tangentMultiplier) / (context.duration * context.duration);
        DeviceContactResponses[row] = response;
    }
};
#endif
