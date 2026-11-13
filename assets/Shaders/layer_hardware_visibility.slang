#ifndef LAYER_HARDWARE_VISIBILITY_SLANG
#define LAYER_HARDWARE_VISIBILITY_SLANG

__exported import layer_tile_raster;

void WriteLayerHardwareVisibility<TVertex, TVisibility : IVisibilityEvaluate<TVertex>, TRequests : ILayerPageRequests>(
    uniform TVisibility visibility, ClusterRasterPixelInput<TVisibility.Varying> input, bool frontFace)
{
    uint geometryView = input.ViewIndex;
    ClusterViewUniforms view = LayerRasterViews[geometryView];
    uint mainView = view.TargetViewIndex;
    uint2 pixel = uint2(input.Pos.xy);
    uint tileSize = LayerCompositionUniforms.TileSize;
    uint tile = (mainView * LayerCompositionUniforms.TileCountY + pixel.y / tileSize) *
        LayerCompositionUniforms.TileCountX + pixel.x / tileSize;
    uint identity = EncodeVisBuffer(input.VisibleClusterIndex, input.TriangleID);
    LayerTileRasterOutput<TRequests, LayerHardwareFragmentStore, false> output;
    output.Tile = tile;
    output.WaveIndex = 0u;
    ClusterRasterOutputPixel destination;
    if (!output.resolvePixel(int2(pixel), mainView, identity, destination)) return;
    destination.FrontFace = frontFace ? 1u : 0u;
    DrawRequest request = VisibleClusterRequest(LayerVisibleClusters[input.VisibleClusterIndex]);
    VisibilityPixelInput receiver;
    receiver.PixelCoord = uint3(pixel, mainView);
    receiver.Depth = input.Pos.z;
    receiver.EncodedDepth = asuint(1.0 - saturate(receiver.Depth));
    receiver.EncodedTargetDepth = output.readDepth(destination, mainView);
    receiver.Source = request;
    receiver.TriangleID = input.TriangleID;
    receiver.ViewIndex = geometryView;
    receiver.TargetViewIndex = mainView;
    VisibilityPixelResult result = visibility.evaluate(receiver, input.Varying);
    if (result.Visible == 0u) return;
    output.write(destination, geometryView, mainView, saturate(result.Depth), identity);
}

#endif
