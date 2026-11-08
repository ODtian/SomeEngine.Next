#include "physics_dispatch.slang"
struct ContactStateInitializationSettings { uint inputs; uint words; };
ConstantBuffer<ContactStateInitializationSettings, ScalarDataLayout> ContactStateInitializationParameters;
RWStructuredBuffer<uint, ScalarDataLayout> DeviceContactGeometryRows;
StructuredBuffer<uint, ScalarDataLayout> ContactStateDefaults;
RWStructuredBuffer<uint, ScalarDataLayout> ContactStateValues;

[shader("compute")][numthreads(64,1,1)]
void InitializeContactStateRows(uint3 id : SV_DispatchThreadID)
{
    uint input = devicePhysicsThreadIndex(id,64);
    if(input >= ContactStateInitializationParameters.inputs) return;
    uint reference = DeviceContactGeometryRows[input];
    if((reference & 0x80000000u) == 0) return;
    uint row = reference & 0x7fffffffu;
    for(uint word=0; word<ContactStateInitializationParameters.words; word++)
        ContactStateValues[row*ContactStateInitializationParameters.words+word] = ContactStateDefaults[word];
}
