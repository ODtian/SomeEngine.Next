__exported import vertex_evaluate_interfaces;

ByteAddressBuffer PageHeap;
#include "cluster_tessellation_core.slang"

[shader("compute")]
[numthreads(32, 1, 1)]
void SeedPhongTessellation<TVE : IVertexEvaluate>(
    uniform TVE vertex, uint3 groupId : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    SeedTessellationRasterBin(vertex, groupId, groupThreadIndex);
}

[shader("compute")]
[numthreads(32, 1, 1)]
void SplitPhongTessellation<TVE : IVertexEvaluate, TDomain : IDomainEvaluate<TVE.Vertex>>(
    uniform TVE vertex, uniform TDomain domain,
    uint3 groupId : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    SplitTessellationPatches(vertex, domain, groupId, groupThreadIndex);
}
