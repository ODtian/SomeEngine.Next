#ifndef CLUSTER_MATERIAL_SPILL_ENTRIES_SLANG
#define CLUSTER_MATERIAL_SPILL_ENTRIES_SLANG

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds(CLUSTER_RASTER_BOUNDS)]
[shader("vertex")]
[RasterMaterialInputs("evaluators")]
ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> ClusterRasterSpilledVertex(
    uniform RasterVertexEvaluate vertex, uniform RasterVisibilityEvaluate visibility,
    uint vertexID : SV_VertexID, uint instanceID : SV_InstanceID)
{
    return BuildVisBufferVertex<RasterVertexEvaluate, RasterVisibilityEvaluate, RasterSpillInput>(
        vertex, visibility, vertexID, instanceID);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds(CLUSTER_RASTER_BOUNDS)]
[shader("compute")][numthreads(32,1,1)][WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareSpilledImageAtomic64(uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility, uint3 group:SV_GroupID,uint lane:SV_GroupIndex)
{
    ClusterRasterOutputImageAtomic64 output;
    SWRasterKernel<RasterVertexEvaluate,RasterVisibilityEvaluate,ClusterRasterOutputImageAtomic64,RasterSpillInput>(
        vertex,visibility,output,group,lane,true);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds(CLUSTER_RASTER_BOUNDS)]
[shader("compute")][numthreads(32,1,1)][WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareSpilledBufferAtomic64(uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility, uint3 group:SV_GroupID,uint lane:SV_GroupIndex)
{
    ClusterRasterOutputBufferAtomic64 output;
    output.Width=ClusterSoftwareRasterUniformData.ScreenWidth;
    output.Height=ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel<RasterVertexEvaluate,RasterVisibilityEvaluate,ClusterRasterOutputBufferAtomic64,RasterSpillInput>(
        vertex,visibility,output,group,lane,true);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds(CLUSTER_RASTER_BOUNDS)]
[shader("compute")][numthreads(32,1,1)][WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareSpilledNonAtomic(uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility, uint3 group:SV_GroupID,uint lane:SV_GroupIndex)
{
    ClusterRasterOutputNonAtomic output;
    output.Width=ClusterSoftwareRasterUniformData.ScreenWidth;
    output.Height=ClusterSoftwareRasterUniformData.ScreenHeight;
    SWRasterKernel<RasterVertexEvaluate,RasterVisibilityEvaluate,ClusterRasterOutputNonAtomic,RasterSpillInput>(
        vertex,visibility,output,group,lane,true);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds(CLUSTER_RASTER_BOUNDS)]
[shader("compute")][numthreads(32,1,1)][WaveSize(32)]
[RasterMaterialInputs("evaluators")]
void ClusterRasterSoftwareSpilledTarget(uniform RasterVertexEvaluate vertex,
    uniform RasterVisibilityEvaluate visibility, uint3 group:SV_GroupID,uint lane:SV_GroupIndex)
{
    ClusterRasterOutputTarget output;
    SWRasterKernel<RasterVertexEvaluate,RasterVisibilityEvaluate,ClusterRasterOutputTarget,RasterSpillInput>(
        vertex,visibility,output,group,lane,true);
}

[VertexEncoding(CLUSTER_VERTEX_ENCODING)]
[ConservativeBounds(CLUSTER_RASTER_BOUNDS)]
[shader("vertex")][RasterMaterialInputs("evaluators")]
ClusterRasterPixelInput<RasterVisibilityEvaluate.Varying> ClusterRasterSpilledVertexTarget(
    uniform RasterVertexEvaluate vertex,uniform RasterVisibilityEvaluate visibility,
    uint vertexID:SV_VertexID,uint instanceID:SV_InstanceID)
{ return BuildVisBufferVertex<RasterVertexEvaluate,RasterVisibilityEvaluate,RasterSpillInput,ShadowPageOutput>(vertex,visibility,vertexID,instanceID); }

#endif
