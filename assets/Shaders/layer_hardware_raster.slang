#ifndef LAYER_HARDWARE_RASTER_SLANG
#define LAYER_HARDWARE_RASTER_SLANG

__exported import layer_hardware_visibility;

StructuredBuffer<uint> LayerHardwareClusters;
StructuredBuffer<uint2> LayerHardwareRanges;

ClusterRasterPixelInput<TVisibility.Varying> BuildLayerHardwareVertex<
    TVertex : IVertexEvaluate, TVisibility : IVisibilityEvaluate<TVertex.Vertex>>(
    uniform TVertex vertex, uniform TVisibility visibility, uint vertexID, uint instanceID)
{
    ClusterRasterPixelInput<TVisibility.Varying> output = {};
    output.Pos = asfloat(uint4(0x7fc00000u));
    uint2 range = LayerHardwareRanges[LayerRasterStage.RasterStage];
    uint visible = LayerHardwareClusters[range.x + instanceID];
    DrawRequest request = VisibleClusterRequest(LayerVisibleClusters[visible]);
    uint vertexAddress, vertexBytes, triangleStart, vertexCount, triangleCount, indexOffset;
    LoadClusterGeometry(request.PageOffset, DrawRequestClusterID(request), vertexAddress,
        vertexBytes, triangleStart, vertexCount, triangleCount, indexOffset);
    uint triangle = vertexID / 3u;
    if (triangle >= triangleCount) return output;
    uint address = ReadLayerRasterMaterialAddress(EncodeVisBuffer(visible, triangle));
    if (LayerRasterMaterials[address].x == 0xffffffffu ||
        DecodeStageBin(LayerMaterialRasterStages[address]) != LayerRasterStage.RasterStage) return output;
    uint index = FetchVertexIndex(request.PageOffset, indexOffset, triangleStart, triangle, vertexID % 3u);
    TVertex.Vertex value = vertex.evaluate(MakeSWRasterVertexEvalContext(vertexAddress, vertexBytes,
        index, request.InstanceID, GetCurrentInstanceTransform(request.InstanceID), visible));
    uint viewIndex = DrawRequestViewIndex(request);
    ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties,
        request.InstanceID, LayerRasterViews[viewIndex]);
    float3 position = vertex.getPosition(value);
    output.Pos = mul(float4(ClusterPositionForView(position, view), 1.0), view.ViewProj);
    output.ClipDepth = CalculateClusterRasterDepthInput(position, output.Pos, view);
    VisibilityVertexInput input = { request, index };
    output.Varying = visibility.makeVarying(value, input);
    output.VisibleClusterIndex = visible;
    output.TriangleID = triangle;
    output.ViewIndex = viewIndex;
    output.RenderTargetIndex = view.TargetViewIndex;
    return output;
}

#endif
