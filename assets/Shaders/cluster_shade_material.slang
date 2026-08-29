#include "cluster_shade_pipeline.slang"
#include "standard_pbr.slang"

StandardPBRMaterial Material;

[MaterialTarget("cluster.shade")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSMaterialShade(uint3 tid : SV_DispatchThreadID)
{
    StaticVertexEval vertexEval;
    LinearSurface surface = MakeLinearSurface();
    CSShade(vertexEval, surface, Material, tid);
}

[MaterialTarget("cluster.shade.cached")]
[shader("compute")]
[numthreads(64, 1, 1)]
void CSMaterialShadeCached(uint3 tid : SV_DispatchThreadID)
{
    StaticVertexEval vertexEval;
    LinearSurface surface = MakeLinearSurface();
    CSShadeFromCache(vertexEval, surface, Material, tid);
}
