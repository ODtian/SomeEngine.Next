#ifndef SURFACE_INPUTS_SLANG
#define SURFACE_INPUTS_SLANG

#include "cluster_shade_pipeline.slang"
__exported import shade_input_interfaces;
__exported import layer_fragment_store;
__exported import layer_composition_parameters;

struct VisibilityBufferInput<TVertex : IVertexEvaluate, TDomain : IDomainEvaluate<TVertex.Vertex>>
    : IShadeInput<TVertex, TDomain>
{
    bool Read(TVertex vertex, TDomain domain, uint3 thread,
        out SurfacePixelInput input, out TDomain.Surface surface,
        out float3 bary, out TDomain.TriangleData triangle, out uint outputIndex)
    {
        TriangleInfo info;
        ClusterViewUniforms view;
        outputIndex = 0u;
        if (!TryEvaluateSurfacePixel<TVertex, TDomain>(vertex, domain, thread, Uniforms.VisibilityDepth,
            info, view, triangle, bary, surface, input)) return false;
        WriteClusterMotion(vertex, domain, info, view, triangle, bary, input.PixelCoord);
        return true;
    }
};

struct LayerFragmentInput<TVertex : IVertexEvaluate, TDomain : IDomainEvaluate<TVertex.Vertex>>
    : IShadeInput<TVertex, TDomain>
{
    bool Read(TVertex vertex, TDomain domain, uint3 thread,
        out SurfacePixelInput input, out TDomain.Surface surface,
        out float3 bary, out TDomain.TriangleData triangle, out uint outputIndex)
    {
        input = {};
        surface = {};
        bary = 0.0.xxx;
        triangle = {};
        ShadePixelWork work;
        bool fullTile;
        uint lane;
        outputIndex = 0u;
        if (!TryReadShadeWork(thread, work, fullTile, lane)) return false;
        uint corner = lane & 3u;
        uint2 offset = uint2(corner & 1u, corner >> 1u);
        if (fullTile) offset += uint2((lane / 4u) % 4u, (lane / 4u) / 4u) * 2u;
        outputIndex = work.PixelIndex + offset.y * 8u + offset.x;
        LayerFragment fragment = LoadLayerFragment(outputIndex);
        LayerBlockHeader block = LoadLayerBlock(outputIndex / LayerFragmentStore.BlockSize);
        uint tilesPerView = LayerCompositionUniforms.TileCountX * LayerCompositionUniforms.TileCountY;
        uint tileInView = block.Tile % tilesPerView;
        uint size = LayerCompositionUniforms.TileSize;
        uint pixel = LayerFragmentPixel(fragment);
        uint3 coord = uint3(tileInView % LayerCompositionUniforms.TileCountX * size + pixel % size,
            tileInView / LayerCompositionUniforms.TileCountX * size + pixel / size, block.Tile / tilesPerView);
        TriangleInfo info;
        uint visible = DecodeVisBufferVisibleClusterIndex(fragment.Identity);
        uint geometryView = DrawRequestViewIndex(VisibleClusters[visible]);
        if (!DecodeSurfaceTriangle(fragment.Identity, geometryView, info)) return false;
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(
            InstanceData, InstanceProperties, info.instanceID, Views[info.viewIndex]);
        triangle = evaluateTriangle(vertex, domain, info);
        float3 positionFromDepth;
        SurfaceBarycentrics coordinates = ResolveBarycentricsAtDepth<TVertex.Vertex, TDomain>(
            domain, coord, view, triangle, info, fragment.Depth, positionFromDepth);
        if (coordinates.Valid == 0u) return false;
        bary = coordinates.Value;
        surface = domain.eval(triangle, bary);
        input = BuildSurfacePixelInput(info, view, coord, coordinates,
            fragment.Depth, Uniforms.VisibilityDepth, positionFromDepth);
        return true;
    }
};

#endif
