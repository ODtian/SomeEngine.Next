#include "vertex_evaluate.slang"

ByteAddressBuffer PageHeap;

typedef StaticVertexEval VertexEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;

#include "sw_raster_core.slang"

#include "cluster_policy_raster_output.slang"

[VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void CSSWRasterImageAtomic64(uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    VertexEvaluate eval;
    VisibilityEvaluate visibility;
    ClusterRasterOutputImageAtomic64 rasterOutput;
    SWRasterKernel(eval, visibility, rasterOutput, groupID, groupThreadIndex);
}

[VertexEncoding("someengine.cluster.positions.uint16.v1+someengine.cluster.attributes.standard.v1")]
[ConservativeBounds("finite")]
[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void CSSWRasterBufferAtomic64(uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    VertexEvaluate eval;
    VisibilityEvaluate visibility;
    ClusterRasterOutputBufferAtomic64 rasterOutput;
    rasterOutput.Width = ClusterSoftwareRasterUniformData.ScreenWidth;
    rasterOutput.Height = ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel(eval, visibility, rasterOutput, groupID, groupThreadIndex);
}
