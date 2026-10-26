#ifndef PHYSICS_BODY_INPUTS
#define PHYSICS_BODY_INPUTS
#include "physics_body_input_data.slang"
#include "physics_body_input_math.slang"
[shader("compute")][numthreads(64,1,1)]
void ApplyDeviceBodyInputs(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= BodyInputParameters.count) return;
    DeviceBodyInput input = DeviceBodyInputs[id.x];
    DeviceBodyState body = DeviceBodyStates[input.bodyIndex];
    if (!deviceBodyInputMatches(body, input)) return;
    applyDeviceBodyInput(body, input);
    DeviceBodyStates[input.bodyIndex] = body;
}
[shader("compute")][numthreads(64,1,1)]
void ApplyDeviceKinematicTargets(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= BodyInputParameters.count) return;
    DeviceBodyInput input = DeviceBodyInputs[id.x];
    if ((input.kinds & 4u) == 0) return;
    DeviceBodyState body = DeviceBodyStates[input.bodyIndex];
    if (!deviceBodyInputMatches(body, input)) return;
    applyDeviceKinematicTarget(body, input.targetPosition, input.targetRotation);
    DeviceBodyStates[input.bodyIndex] = body;
}
#endif
