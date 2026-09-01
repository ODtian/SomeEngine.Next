#include "cluster_tessellation_core.slang"
#include "phong_tessellation.slang"

[MaterialTarget("cluster.tess.factor")]
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(1, 1, 1)]
void FactorPhongTessellation(uint3 threadId : SV_DispatchThreadID)
{
    StaticVertexEval evaluator;
    PhongTessellation surface = MakePhongTessellation();
    FactorRasterBin(evaluator, surface, threadId);
}

[MaterialTarget("cluster.tess.dice")]
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(64, 1, 1)]
void DicePhongTessellation(uint3 threadId : SV_DispatchThreadID)
{
    StaticVertexEval evaluator;
    PhongTessellation surface = MakePhongTessellation();
    DiceSplitWork(evaluator, surface, threadId);
}
