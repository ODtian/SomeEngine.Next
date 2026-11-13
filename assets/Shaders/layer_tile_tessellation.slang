#ifndef LAYER_TILE_TESSELLATION_SLANG
#define LAYER_TILE_TESSELLATION_SLANG

__exported import layer_tile_raster;
__exported import layer_tessellation_parameters;
__exported import cluster_tessellation_geometry;
__exported import vertex_consumer_cache;

void RasterizeLayerTileSurface<TVertex : IVertexEvaluate, TDomain : IDomainEvaluate<TVertex.Vertex>,
    TVisibility : IVisibilityEvaluate<TVertex.Vertex>, TRequests : ILayerPageRequests, let twoSided : bool = false>(
    uniform TVertex vertex, uniform TDomain domain, uniform TVisibility visibility, uint3 group, uint lane)
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
        int4 bounds = output.scissor(visible, view.TargetViewIndex, view.ScreenWidth, view.ScreenHeight);
        for (uint baseTriangle = 0u; baseTriangle < triangleCount; baseTriangle += 32u)
        {
            uint triangleIndex = baseTriangle + lane;
            bool sourceActive = triangleIndex < triangleCount;
            uint address = 0u;
            if (sourceActive)
            {
                address = ReadLayerRasterMaterialAddress(EncodeVisBuffer(visible, triangleIndex));
                sourceActive = LayerRasterMaterials[address].x != 0xffffffffu &&
                    DecodeStageBin(LayerMaterialRasterStages[address]) == LayerRasterStage.RasterStage;
            }
            TVertex.Vertex vertices[3];
            TVisibility.Varying sourceVaryings[3];
            TDomain.TriangleData triangle = {};
            [unroll]
            for (uint corner = 0u; corner < 3u; corner++)
            {
                sourceVaryings[corner] = {};
                if (!sourceActive) continue;
                uint index = FetchVertexIndex(request.PageOffset, indexOffset, triangleStart, triangleIndex, corner);
                VertexEvalContext context = MakeSWRasterVertexEvalContext(vertexAddress, vertexBytes,
                    index, request.InstanceID, transform, visible);
                vertices[corner] = EvaluateVertexWave(vertex, context, visible);
                VisibilityVertexInput input = { request, index };
                sourceVaryings[corner] = visibility.makeVarying(vertices[corner], input);
            }
            if (sourceActive)
            {
                triangle = domain.makeTriangle(vertices[0], vertices[1], vertices[2], address);
                float3 minimum, maximum;
                domain.bounds(triangle, minimum, maximum);
                sourceActive = sourceActive && TessellationBoundsIntersectScreen(minimum, maximum, view, bounds, 0.5 / CLUSTER_RASTER_SUBPIXEL_SCALE_F);
            }
            TessellationTraversal traversal = BeginTessellationTraversal();
            TessellationPatch patch = {};
            uint microTriangle = 0u, microTriangleCount = 0u, factor = 1u;
            uint3 edgeFactors = uint3(1u);
            bool active = sourceActive;
            while (true)
            {
                while (active && microTriangle == microTriangleCount)
                {
                    active = NextTessellationPatch<TVertex.Vertex, TDomain>(domain, triangle, view,
                        LayerTessellation.MaxFactor, LayerTessellation.TargetEdgePixels, traversal, patch);
                    if (!active) break;
                    float3 minimum, maximum;
                    domain.patchBounds(triangle, DecodeTessellationPatchBarycentric(patch, 0u),
                        DecodeTessellationPatchBarycentric(patch, 1u),
                        DecodeTessellationPatchBarycentric(patch, 2u), minimum, maximum);
                    if (!TessellationBoundsIntersectScreen(minimum, maximum, view, bounds, 0.5 / CLUSTER_RASTER_SUBPIXEL_SCALE_F)) continue;
                    edgeFactors = TessellationPatchEdgeFactors(patch);
                    factor = max(edgeFactors.x, max(edgeFactors.y, edgeFactors.z));
                    microTriangleCount = TessellationGridTriangleCount(factor);
                    microTriangle = 0u;
                }
                if (!WaveActiveAnyTrue(active)) break;
                float4 clips[3];
                TVisibility.Varying varyings[3];
                uint3 indices = active ? TessellationGridTriangleVertexIndices(factor, microTriangle) : uint3(0u);
                [unroll]
                for (uint corner = 0u; corner < 3u; corner++)
                {
                    clips[corner] = float4(0.0, 0.0, 0.0, 1.0);
                    varyings[corner] = {};
                    if (!active) continue;
                    float3 bary = TransformPatchBarycentric(DecodeTessellationPatchBarycentric(patch, 0u),
                        DecodeTessellationPatchBarycentric(patch, 1u), DecodeTessellationPatchBarycentric(patch, 2u),
                        TessellationGridBarycentric(factor, edgeFactors, indices[corner]));
                    float3 position = domain.position(triangle, bary);
                    clips[corner] = mul(float4(ClusterPositionForView(position, view), 1.0), view.ViewProj);
                    varyings[corner] = visibility.interpolateVertex(sourceVaryings[0], sourceVaryings[1],
                        sourceVaryings[2], bary);
                }
                RasterizeClusterTriangleWave<TVertex.Vertex, TVisibility, LayerTileRasterOutput<TRequests>>(
                    visibility, output, clips, varyings, bounds, visible, request, triangleIndex, viewIndex,
                    work.Tile / (LayerCompositionUniforms.TileCountX * LayerCompositionUniforms.TileCountY),
                    active, view.ScreenWidth, view.ScreenHeight, true, 0u, !twoSided,
                    work.Tile / (LayerCompositionUniforms.TileCountX * LayerCompositionUniforms.TileCountY));
                if (active) microTriangle++;
            }
        }
    }
    TRequests.End(work.Tile, lane);
    LayerSoftwareFragmentStore.End(lane);
}

#endif
