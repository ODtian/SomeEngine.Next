import "cluster_transparency";

struct TransparencyShadingData { TRANSPARENCY_MATERIAL_TYPE.SourceData Material0; };
RWStructuredBuffer<TransparencyShadingData> TransparencyShadingTriangles;
StructuredBuffer<TransparencyShadingData> TransparencyShadingInputs;
groupshared TransparencyShadingData TransparencyTileShading[16];

struct SingleTransparencyMaterials<let visibilityDepth : uint, let tessellated : bool> : ITransparencyMaterials
{
    TRANSPARENCY_MATERIAL_TYPE Material0;
    typealias Projection = TRANSPARENCY_MATERIAL_TYPE.Projection;
    typealias Shading = TransparencyShadingData;

    Shading loadShading(uint index) { return TransparencyShadingInputs[index]; }
    void cacheTileShading(uint packet, uint index) { TransparencyTileShading[packet] = TransparencyShadingInputs[index]; }
    Shading loadTileShading(uint packet) { return TransparencyTileShading[packet]; }
    Shading readShadingLane(Shading value, uint lane)
    { value.Material0 = Material0.readSourceData(value.Material0,lane); return value; }
    bool coverShading(uint bin, uint source, Shading value, float3x2 domain, uint3 pixel, float depth, out float resultDepth)
    {
        if (visibilityDepth == 0u) { resultDepth = depth; return true; }
        return Material0.coverData<visibilityDepth>(value.Material0,domain,tessellated,pixel,depth,resultDepth);
    }
    float4 shadeShading(uint bin, uint source, Shading value, uint3 pixel, float depth)
    { return Material0.shadeData<visibilityDepth,0u>(value.Material0,pixel,depth); }

    Projection beginProjection(uint bin, uint source, uint view, float4 screenBounds)
    { return Material0.beginProjection(source, view, tessellated, screenBounds); }
    void storeProjection(uint bin, uint source, uint index, Projection value)
    { TransparencyShadingTriangles[index].Material0 = Material0.sourceData(value); }
    bool nextProjection(uint bin, uint source, uint view, inout Projection value,
        out uint primitive, out float3x2 domain, out float4 first, out float4 second, out float4 third)
    { return Material0.nextProjection(source, view, tessellated, value, primitive, domain, first, second, third); }
    bool cover(uint bin, uint source, float3x2 domain, uint3 pixel, float depth, out float resultDepth)
    { return Material0.cover(source, domain, tessellated, pixel, depth, resultDepth); }
    float4 shade(uint bin, uint source, uint primitive, uint3 pixel, float depth)
    { return Material0.shade<visibilityDepth>(source, primitive, pixel, depth); }
    TRANSPARENCY_MATERIAL_TYPE.SourceData readSource(uint index)
    { return TransparencyShadingInputs[index].Material0; }
    bool coverCached(uint bin, uint index, float3x2 domain, uint3 pixel, float depth, out float resultDepth)
    {
        if (visibilityDepth == 0u) { resultDepth = depth; return true; }
        return Material0.coverData<visibilityDepth>(readSource(index), domain, tessellated, pixel, depth, resultDepth);
    }
    float4 shadeCached(uint bin, uint index, uint3 pixel, float depth)
    { return Material0.shadeData<visibilityDepth,0u>(readSource(index), pixel, depth); }
    void markShadowPages(uint bin, uint source, uint primitive, uint3 pixel, float depth)
    { Material0.markShadowPages<visibilityDepth>(source, primitive, pixel, depth); }
    void markShadowPagesShading(uint bin, uint source, Shading value, uint3 pixel, float depth)
    { Material0.markShadowPagesData<visibilityDepth>(value.Material0,pixel,depth); }
};

[shader("compute")] [numthreads(64,1,1)]
void ClearTransparentTiles(uint3 thread : SV_DispatchThreadID) { ClearTransparency(thread); }

[shader("compute")] [numthreads(64,1,1)]
void CopyTransparentSources(uint3 thread : SV_DispatchThreadID) { CopyTransparencySources(thread); }

[shader("compute")] [numthreads(64,1,1)]
void BuildTransparentGeometry<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 thread : SV_DispatchThreadID)
{ BuildTransparency(materials, thread); }

[shader("compute")] [numthreads(64,1,1)]
void CountTransparentCells(uint3 thread : SV_DispatchThreadID) { CountTransparencyCells(thread); }

[shader("compute")] [numthreads(64,1,1)]
void ReserveTransparentCells(uint3 thread : SV_DispatchThreadID) { ReserveTransparency(thread); }

[shader("compute")] [numthreads(64,1,1)]
void ScatterTransparentCells(uint3 thread : SV_DispatchThreadID) { ScatterTransparency(thread); }

[shader("compute")] [numthreads(128,1,1)] [WaveSize(32)]
void SortTransparentCells(uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { SortTransparencyCells(group, thread); }

[shader("compute")] [numthreads(128,1,1)] [WaveSize(32)]
void PrepareTransparentCells(uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { PrepareTransparencyCells(group, thread); }

[shader("compute")] [numthreads(1,1,1)]
void FinalizeTransparentSort() { FinalizeTransparencySort(); }

[shader("compute")] [numthreads(128,1,1)] [WaveSize(32)]
void SortTransparentRanges(uint3 group : SV_GroupID, uint thread : SV_GroupIndex) { SortTransparencyRanges(group, thread); }

[shader("compute")] [numthreads(64,1,1)]
void ClassifyTransparentTiles(uint3 thread : SV_DispatchThreadID) { ClassifyTransparencySortedTiles(thread); }

[shader("compute")] [numthreads(64,1,1)]
void ScatterTransparentHardwareCells(uint3 thread : SV_DispatchThreadID) { ScatterTransparencyHardwareCells(thread); }

[shader("compute")] [numthreads(64,1,1)]
void CountTransparentHardwareCells(uint3 thread : SV_DispatchThreadID) { CountTransparencyHardwareCells(thread); }

[shader("compute")] [numthreads(1,1,1)]
void FinalizeTransparentTiles() { FinalizeTransparencyTiles(); }

[shader("compute")] [numthreads(64,1,1)]
void ClearTransparentHardwareMasks(uint3 thread : SV_DispatchThreadID) { ClearTransparencyHardwareMasks(thread); }

[shader("vertex")]
TransparencyHardwareVertex DrawTransparentCoverage(uint vertex : SV_VertexID) { return MakeTransparencyHardwareVertex(vertex); }

[shader("fragment")] [earlydepthstencil]
void StoreTransparentCoverage(TransparencyHardwareVertex input) { StoreTransparencyHardwareCoverage(input); }

[shader("compute")] [numthreads(64,1,1)] [WaveSize(32)]
void RequestTransparentShadows<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{
    RequestTransparencyShadowJobs(materials,group,thread);
}

[shader("compute")] [numthreads(16,16,1)]
void RequestProjectedTransparentShadows<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 pixel : SV_DispatchThreadID)
{ RequestTransparencyShadows<SingleTransparencyMaterials<visibilityDepth, tessellated>, true>(materials, pixel); }

[shader("compute")] [numthreads(16,16,1)]
void ResolveTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 pixel : SV_DispatchThreadID)
{ ResolveTransparency<SingleTransparencyMaterials<visibilityDepth, tessellated>, false>(materials, pixel); }

[shader("compute")] [numthreads(64,1,1)] [WaveSize(32)]
void ResolveTileTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{ ResolveTransparencyTiles(materials, group, thread); }

[shader("compute")] [numthreads(128,1,1)] [WaveSize(32)]
void ResolveLocalTileTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth,tessellated> materials, uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{ ResolveTransparencyLocalTileJobs(materials,group,thread); }

[shader("compute")] [numthreads(16,16,1)]
void ResolveProjectedTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 pixel : SV_DispatchThreadID)
{ ResolveTransparency<SingleTransparencyMaterials<visibilityDepth, tessellated>, true>(materials, pixel); }

[shader("compute")] [numthreads(256,1,1)] [WaveSize(32)]
void ResolveDepthTileTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth,tessellated> materials, uint3 group : SV_GroupID, uint thread : SV_GroupIndex)
{ ResolveTransparencyDepthTileJobs(materials,group,thread); }

[shader("compute")] [numthreads(64,1,1)]
void RequestTransparentPages<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 thread : SV_DispatchThreadID)
{ RequestTransparencyPages(materials, thread); }


[shader("compute")] [numthreads(16,16,1)]
void ResolveUnorderedTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 pixel : SV_DispatchThreadID)
{ ResolveUnorderedTransparency<SingleTransparencyMaterials<visibilityDepth, tessellated>, false>(materials, pixel); }

[shader("compute")] [numthreads(16,16,1)]
void ResolveProjectedUnorderedTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 pixel : SV_DispatchThreadID)
{ ResolveUnorderedTransparency<SingleTransparencyMaterials<visibilityDepth, tessellated>, true>(materials, pixel); }

[shader("compute")] [numthreads(16,16,1)]
void ResolveOrderedTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 pixel : SV_DispatchThreadID)
{ ResolveOrderedTransparency<SingleTransparencyMaterials<visibilityDepth, tessellated>, false>(materials, pixel); }

[shader("compute")] [numthreads(16,16,1)]
void ResolveProjectedOrderedTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 pixel : SV_DispatchThreadID)
{ ResolveOrderedTransparency<SingleTransparencyMaterials<visibilityDepth, tessellated>, true>(materials, pixel); }
