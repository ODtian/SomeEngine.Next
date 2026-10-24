#include "vertex_evaluate.slang"
#include "phong_tessellation.slang"

ByteAddressBuffer PageHeap;

typedef StaticVertexEval VertexEvaluate;
typedef PhongTessellation DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;

#include "cluster_tessellation_entries.slang"

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("unbounded")]
[shader("compute")]
[numthreads(32, 1, 1)]
void SeedPhongTessellation(
    uint3 groupId : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    StaticVertexEval vertex;
    SeedTessellationRasterBin(vertex, groupId, groupThreadIndex);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds("unbounded")]
[shader("compute")]
[numthreads(32, 1, 1)]
void SplitPhongTessellation(
    uint3 groupId : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    StaticVertexEval vertex;
    PhongTessellation domain = MakePhongTessellation();
    SplitTessellationPatches(vertex, domain, groupId, groupThreadIndex);
}
