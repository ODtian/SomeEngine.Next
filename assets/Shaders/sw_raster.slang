#include "vertex_evaluate.slang"

ByteAddressBuffer PageHeap;

#include "sw_raster_core.slang"

#include "cluster_policy_raster_output.slang"

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
void CSSWRasterImageAtomic64(uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    StaticVertexEval eval;
    OpaquePixelEvaluate pixel;
    ClusterRasterOutputImageAtomic64 rasterOutput;
    SWRasterKernel(eval, pixel, rasterOutput, groupID, groupThreadIndex);
}

[VertexEncoding("someengine.vertex.standard.cluster.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
void CSSWRasterBufferAtomic64(uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    StaticVertexEval eval;
    OpaquePixelEvaluate pixel;
    ClusterRasterOutputBufferAtomic64 rasterOutput;
    rasterOutput.Width = ClusterSoftwareRasterUniformData.ScreenWidth;
    rasterOutput.Height = ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel(eval, pixel, rasterOutput, groupID, groupThreadIndex);
}
