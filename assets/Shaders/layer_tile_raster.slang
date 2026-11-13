#ifndef LAYER_TILE_RASTER_SLANG
#define LAYER_TILE_RASTER_SLANG

ByteAddressBuffer PageHeap;
#include "sw_raster_core.slang"
__exported import layer_tile_binning;
__exported import layer_visibility_requests;
__exported import layer_composition_parameters;
__exported import fragment_material;

StructuredBuffer<ClusterViewUniforms> LayerRasterViews;
StructuredBuffer<VisibleCluster> LayerVisibleClusters;
StructuredBuffer<uint2> LayerRasterMaterials;
StructuredBuffer<uint> LayerMaterialRasterStages;
Texture2DArray<float> LayerOpaqueDepth;

struct LayerRasterStageParameters
{
    uint RasterStage, Padding0, Padding1, Padding2;
};
ConstantBuffer<LayerRasterStageParameters> LayerRasterStage;

struct LayerTileVertexCache<TVarying>
{
    // A cluster's vertices are shared by every wave in its 32-thread workgroup.
    static groupshared float4 Clips[64];
    static groupshared TVarying Varyings[64];
};

uint ReadLayerRasterMaterialAddress(uint identity)
{
    return ReadFragmentMaterialAddress(PageHeap, LayerVisibleClusters, InstanceData,
        LoadRenderInstanceMetadata(InstanceProperties, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD), identity);
}

struct LayerTileRasterOutput<TRequests : ILayerPageRequests, TStore : ILayerFragmentStore = LayerSoftwareFragmentStore,
    let checkRasterStage : bool = true> : IClusterRasterOutput
{
    static bool UsesReverseDepth() { return false; }
    uint Tile;
    uint WaveIndex;

    int4 scissor(uint visibleCluster, uint outputView, uint width, uint height)
    {
        uint tilesPerView = LayerCompositionUniforms.TileCountX * LayerCompositionUniforms.TileCountY;
        uint tile = Tile % tilesPerView;
        uint2 start = uint2(tile % LayerCompositionUniforms.TileCountX,
            tile / LayerCompositionUniforms.TileCountX) * LayerCompositionUniforms.TileSize;
        return int4(start, min(start + LayerCompositionUniforms.TileSize, uint2(width, height)));
    }

    bool resolvePixel(int2 pixel, uint outputView, uint identity,
        out ClusterRasterOutputPixel result)
    {
        result = {};
        result.Position = pixel;
        result.MaterialAddress = ReadLayerRasterMaterialAddress(identity);
        uint2 material = LayerRasterMaterials[result.MaterialAddress];
        return material.x != 0xffffffffu && (!checkRasterStage ||
            DecodeStageBin(LayerMaterialRasterStages[result.MaterialAddress]) == LayerRasterStage.RasterStage);
    }

    uint readDepth(ClusterRasterOutputPixel pixel, uint outputView)
    {
        uint tilesPerView = LayerCompositionUniforms.TileCountX * LayerCompositionUniforms.TileCountY;
        float depth = (LayerRasterMaterials[pixel.MaterialAddress].y & 256u) != 0u
            ? LayerOpaqueDepth[uint3(pixel.Position, Tile / tilesPerView)] : 1.0;
        return asuint(1.0 - depth);
    }

    void write(ClusterRasterOutputPixel pixel, uint viewIndex, uint outputView,
        float depth, uint identity)
    {
        DrawRequest request = VisibleClusterRequest(LayerVisibleClusters[DecodeVisBufferVisibleClusterIndex(identity)]);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties,
            request.InstanceID, LayerRasterViews[viewIndex]);
        float2 uv = (float2(pixel.Position) + 0.5) /
            float2(LayerCompositionUniforms.Width, LayerCompositionUniforms.Height);
        float4 world = mul(float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), depth, 1.0), view.ClipToWorld);
        uint tilesPerView = LayerCompositionUniforms.TileCountX * LayerCompositionUniforms.TileCountY;
        uint3 coord = uint3(pixel.Position, Tile / tilesPerView);
        uint size = LayerCompositionUniforms.TileSize;
        uint localPixel = (uint(pixel.Position.y) % size) * size + uint(pixel.Position.x) % size;
        bool firstPerson = (LoadInstanceFirstPerson(InstanceData, InstanceProperties, request.InstanceID).x & 3u) == 1u;
        LayerVisibilityOutput<TStore, TRequests> output;
        uint2 material = LayerRasterMaterials[pixel.MaterialAddress];
        output.Tile = Tile;
        output.WaveIndex = WaveIndex;
        output.PassIndex = material.x;
        output.DrawOrder = material.y & 255u;
        output.ClipByOpaqueDepth = (material.y & 256u) != 0u;
        output.Write(true, coord, localPixel, depth, identity, LayerOpaqueDepth[coord],
            world.xyz / world.w - view.WorldTranslation, firstPerson, pixel.FrontFace != 0u);
    }
};

void RasterizeLayerTile<TVertex : IVertexEvaluate, TVisibility : IVisibilityEvaluate<TVertex.Vertex>,
    TRequests : ILayerPageRequests, let twoSided : bool = false>(
    uniform TVertex vertex, uniform TVisibility visibility, uint3 group, uint lane)
{
    uint workIndex = group.y * 65535u + group.x;
    uint2 works = LayerStageWorkRanges[LayerRasterStage.RasterStage];
    if (workIndex >= works.y) return;
    LayerStageWork work = LayerStageWorks[works.x + workIndex];
    LayerSoftwareFragmentStore.Begin(lane);
    TRequests.Begin(work.Tile, lane);
    LayerTileRasterOutput<TRequests> output;
    output.Tile = work.Tile;
    output.WaveIndex = WaveReadLaneFirst(lane);
    for (uint item = 0u; item < work.ClusterCount; item++)
    {
        uint visible = LayerStageClusters[work.ClusterOffset + item];
        DrawRequest request = VisibleClusterRequest(LayerVisibleClusters[visible]);
        uint viewIndex = DrawRequestViewIndex(request);
        ClusterViewUniforms view = ApplyFirstPersonViewTransform(InstanceData, InstanceProperties,
            request.InstanceID, LayerRasterViews[viewIndex]);
        uint vertexAddress, vertexBytes, triangleStart, vertexCount, triangleCount, indexOffset;
        LoadClusterGeometry(request.PageOffset, DrawRequestClusterID(request), vertexAddress,
            vertexBytes, triangleStart, vertexCount, triangleCount, indexOffset);
        RenderInstanceTransform transform = GetCurrentInstanceTransform(request.InstanceID);
        for (uint half = 0u; half < 2u; half++)
        {
            uint localVertex = lane + half * 32u;
            if (localVertex >= vertexCount) continue;
            TVertex.Vertex value = vertex.evaluate(MakeSWRasterVertexEvalContext(vertexAddress,
                vertexBytes, localVertex, request.InstanceID, transform, visible));
            float4 clip = mul(float4(ClusterPositionForView(vertex.getPosition(value), view), 1.0), view.ViewProj);
            VisibilityVertexInput input = { request, localVertex };
            TVisibility.Varying varying = visibility.makeVarying(value, input);
            LayerTileVertexCache<TVisibility.Varying>.Clips[localVertex] = clip;
            LayerTileVertexCache<TVisibility.Varying>.Varyings[localVertex] = varying;
        }
        GroupMemoryBarrierWithGroupSync();
        int4 bounds = output.scissor(visible, view.TargetViewIndex, view.ScreenWidth, view.ScreenHeight);
        for (uint baseTriangle = 0u; baseTriangle < triangleCount; baseTriangle += 32u)
        {
            uint triangle = baseTriangle + lane;
            bool active = triangle < triangleCount;
            if (active)
            {
                uint address = ReadLayerRasterMaterialAddress(EncodeVisBuffer(visible, triangle));
                active = LayerRasterMaterials[address].x != 0xffffffffu &&
                    DecodeStageBin(LayerMaterialRasterStages[address]) == LayerRasterStage.RasterStage;
            }
            float4 clips[3];
            TVisibility.Varying varyings[3];
            [unroll]
            for (uint corner = 0u; corner < 3u; corner++)
            {
                uint index = active ? FetchVertexIndex(request.PageOffset, indexOffset, triangleStart, triangle, corner) : 0u;
                clips[corner] = LayerTileVertexCache<TVisibility.Varying>.Clips[index];
                varyings[corner] = LayerTileVertexCache<TVisibility.Varying>.Varyings[index];
            }
            RasterizeClusterTriangleWave<TVertex.Vertex, TVisibility, LayerTileRasterOutput<TRequests>>(
                visibility, output, clips, varyings, bounds, visible, request, triangle, viewIndex,
                work.Tile / (LayerCompositionUniforms.TileCountX * LayerCompositionUniforms.TileCountY),
                active, view.ScreenWidth, view.ScreenHeight, true,
                0u, !twoSided,
                work.Tile / (LayerCompositionUniforms.TileCountX * LayerCompositionUniforms.TileCountY));
        }
        GroupMemoryBarrierWithGroupSync();
    }
    TRequests.End(work.Tile, lane);
    LayerSoftwareFragmentStore.End(lane);
}

#endif
