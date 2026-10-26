#ifndef PHYSICS_BODY_ACTIVITY_DYNAMICS
#define PHYSICS_BODY_ACTIVITY_DYNAMICS
#include "physics_body_dynamics.slang"
#include "physics_body_activity_types.slang"
[shader("compute")][numthreads(64,1,1)]
void PredictDeviceActiveBodies(uint3 id:SV_DispatchThreadID)
{
    if(id.x>=BodyStep.count) return;
    uint index=DeviceBodyIndices[id.x];
    bool advance=DeviceBodyStates[index].motion!=2||deviceBodyActivityAwake(index);
    predictDeviceBody(index,advance,false);
    DeviceBodyActivityState activity=DeviceBodyActivities[index];
    activity.flags=advance?activity.flags|8:activity.flags&~8u;
    DeviceBodyActivities[index]=activity;
}
[shader("compute")][numthreads(64,1,1)]
void ResumeDeviceActiveBodies(uint3 id:SV_DispatchThreadID)
{
    if(id.x>=BodyStep.count) return;
    uint index=DeviceBodyIndices[id.x];
    DeviceBodyActivityState activity=DeviceBodyActivities[index];
    if((activity.flags&8)!=0||!deviceBodyActivityAwake(index)) return;
    predictDeviceBody(index,true,true);
    activity.flags|=8; DeviceBodyActivities[index]=activity;
}
#endif
