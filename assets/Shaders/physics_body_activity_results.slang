#include "physics_body_activity.slang"

struct DeviceBodyActivityResult { uint row; DeviceBodyActivityState state; };
RWStructuredBuffer<DeviceBodyActivityState, ScalarDataLayout> DeviceBodyActivityPreviousResults;
RWStructuredBuffer<DeviceBodyActivityResult, ScalarDataLayout> DeviceBodyActivityResults;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceBodyActivityResultCount;
StructuredBuffer<uint, ScalarDataLayout> DeviceBodyActivityCpuResults;
[shader("compute")][numthreads(64,1,1)]
void CollectDeviceBodyActivityResults(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=ActivityStep.bodies) return;
    DeviceBodyActivityState state=DeviceBodyActivities[row];
    DeviceBodyActivityState previous=ActivityStep.phase==4?(DeviceBodyActivityState)0:DeviceBodyActivityPreviousResults[row];
    bool changed=state.entityIndex!=previous.entityIndex||state.entityGeneration!=previous.entityGeneration
        ||(state.flags&15)!=(previous.flags&15)||state.quietSeconds!=previous.quietSeconds||state.radius!=previous.radius
        ||(state.flags&48)!=0;
    uint selected=DeviceBodyActivityCpuResults[row];
    bool event=(ActivityStep.flags&2)!=0&&(state.flags&48)!=0;
    if(selected==2||(selected!=0&&changed)||event)
    {
        uint output; InterlockedAdd(DeviceBodyActivityResultCount[0],1,output);
        DeviceBodyActivityResult result; result.row=row; result.state=state;
        DeviceBodyActivityResults[output]=result;
    }
    DeviceBodyActivityPreviousResults[row]=state;
}
