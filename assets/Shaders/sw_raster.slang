__exported import vertex_evaluate_interfaces;

ByteAddressBuffer PageHeap;

#include "sw_raster_core.slang"

#include "cluster_policy_raster_output.slang"

[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void CSSWRasterImageAtomic64<TVE : IVertexEvaluate, TVisibility : IVisibilityEvaluate<TVE.Vertex>>(
    uniform TVE eval, uniform TVisibility visibility, uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputImageAtomic64 rasterOutput;
    SWRasterKernel(eval, visibility, rasterOutput, groupID, groupThreadIndex);
}

[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void CSSWRasterBufferAtomic64<TVE : IVertexEvaluate, TVisibility : IVisibilityEvaluate<TVE.Vertex>>(
    uniform TVE eval, uniform TVisibility visibility, uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputBufferAtomic64 rasterOutput;
    rasterOutput.Width = ClusterSoftwareRasterUniformData.ScreenWidth;
    rasterOutput.Height = ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel(eval, visibility, rasterOutput, groupID, groupThreadIndex);
}

[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void CSSWRasterNonAtomic<TVE : IVertexEvaluate, TVisibility : IVisibilityEvaluate<TVE.Vertex>>(
    uniform TVE eval, uniform TVisibility visibility, uint3 groupID : SV_GroupID, uint groupThreadIndex : SV_GroupThreadID)
{
    ClusterRasterOutputNonAtomic rasterOutput;
    rasterOutput.Width = ClusterSoftwareRasterUniformData.ScreenWidth;
    rasterOutput.Height = ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel(eval, visibility, rasterOutput, groupID, groupThreadIndex);
}
