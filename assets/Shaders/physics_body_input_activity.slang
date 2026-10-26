#ifndef PHYSICS_BODY_INPUT_ACTIVITY
#define PHYSICS_BODY_INPUT_ACTIVITY
#include "physics_body_activity_types.slang"
#include "physics_body_input_data.slang"
#include "physics_body_input_math.slang"

[shader("compute")][numthreads(64,1,1)]
void ReadDeviceBodyInputActivity(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= BodyInputParameters.count) return;
    DeviceBodyInput input = DeviceBodyInputs[id.x];
    DeviceBodyState body = DeviceBodyStates[input.bodyIndex];
    if (!deviceBodyInputMatches(body, input)) return;
    DeviceBodyState next = body;
    applyDeviceBodyInput(next, input);
    if ((input.kinds & 4u) != 0) applyDeviceKinematicTarget(next, input.targetPosition, input.targetRotation);
    bool rotationChanged = any(next.rotation != body.rotation) && any(next.rotation != -body.rotation);
    if (any(next.position != body.position) || rotationChanged ||
        any(next.linearVelocity != body.linearVelocity) || any(next.angularVelocity != body.angularVelocity))
        InterlockedOr(DeviceBodyActivities[input.bodyIndex].flags, 128u);
}
#endif
