#include "physics_grid_common.slang"

[shader("compute")]
[numthreads(256, 1, 1)]
void CountDevicePairs(uint3 id : SV_DispatchThreadID)
{
    DeviceCollisionRules rules;
    DeviceShapePairs types;
    countDevicePairs(id.x, rules, types);
}

[shader("compute")]
[numthreads(256, 1, 1)]
void WriteDevicePairs(uint3 id : SV_DispatchThreadID)
{
    DeviceCollisionRules rules;
    DeviceShapePairs types;
    writeDevicePairs(id.x, rules, types);
}
