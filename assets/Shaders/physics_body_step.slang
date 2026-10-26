#ifndef PHYSICS_BODY_STEP
#define PHYSICS_BODY_STEP
#include "physics_body_math.slang"
struct DeviceBodyStep
{
    uint count; float duration; uint substep; uint padding0;
    float3 acceleration; uint padding1;
    uint2 number; uint2 time;
};
#endif
