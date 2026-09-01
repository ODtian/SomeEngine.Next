#include "cluster_shade_pipeline.slang"
#include "phong_tessellation.slang"
#include "standard_pbr.slang"

StandardPBRMaterial Material;

[MaterialTarget("cluster.shade")]
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSMaterialShade(uint3 tid : SV_DispatchThreadID)
{
    StaticVertexEval vertexEval;
    PhongTessellation surface = MakePhongTessellation();
    CSShade(vertexEval, surface, Material, tid);
}

[MaterialTarget("cluster.shade.cached")]
[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSMaterialShadeCached(uint3 tid : SV_DispatchThreadID)
{
    StaticVertexEval vertexEval;
    PhongTessellation surface = MakePhongTessellation();
    CSShadeFromCache(vertexEval, surface, Material, tid);
}
