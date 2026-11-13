#include "physics_dispatch.slang"
struct ContactStateInitializationSettings { uint words; };
ConstantBuffer<ContactStateInitializationSettings, ScalarDataLayout> ContactStateInitializationParameters;
StructuredBuffer<uint, ScalarDataLayout> ContactStateCounts;
StructuredBuffer<uint2, ScalarDataLayout> ContactStateMatches;
StructuredBuffer<uint, ScalarDataLayout> ContactStateDefaults;
StructuredBuffer<uint, ScalarDataLayout> PreviousContactStateValues;
RWStructuredBuffer<uint, ScalarDataLayout> ContactStateValues;
[shader("compute")][numthreads(64,1,1)]
void InitializeContactStateRows(uint3 id:SV_DispatchThreadID)
{
    uint row=devicePhysicsThreadIndex(id,64);
    if(row>=ContactStateCounts[1]) return;
    uint previous=ContactStateMatches[row].x;
    for(uint word=0;word<ContactStateInitializationParameters.words;word++)
        ContactStateValues[row*ContactStateInitializationParameters.words+word]=previous==0xffffffff?
            ContactStateDefaults[word]:PreviousContactStateValues[previous*ContactStateInitializationParameters.words+word];
}
