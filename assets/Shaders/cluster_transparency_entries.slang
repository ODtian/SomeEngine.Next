import "cluster_transparency";

struct SingleTransparencyMaterials<let visibilityDepth : uint, let tessellated : bool> : ITransparencyMaterials
{
    TRANSPARENCY_MATERIAL_TYPE Material0;
    typealias Projection = TRANSPARENCY_MATERIAL_TYPE.Projection;

    Projection beginProjection(uint bin, uint source, uint view, float4 screenBounds)
    { return Material0.beginProjection(source, view, tessellated, screenBounds); }
    bool nextProjection(uint bin, uint source, uint view, inout Projection value,
        out uint primitive, out float4 first, out float4 second, out float4 third)
    { return Material0.nextProjection(source, view, tessellated, value, primitive, first, second, third); }
    bool cover(uint bin, uint source, uint primitive, uint3 pixel, float depth, out float resultDepth)
    { return Material0.cover(source, primitive, tessellated, pixel, depth, resultDepth); }
    float4 shade(uint bin, uint source, uint primitive, uint3 pixel, float depth)
    { return Material0.shade<visibilityDepth>(source, primitive, pixel, depth); }
    void receiver(uint bin, uint source, uint primitive, uint3 pixel, float depth,
        out float3 position, out float3 normal, out uint flags)
    { Material0.receiver<visibilityDepth>(source, primitive, pixel, depth, position, normal, flags); }
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
void ReserveTransparentCells(uint3 thread : SV_DispatchThreadID) { ReserveTransparency(thread); }

[shader("compute")] [numthreads(64,1,1)]
void ScatterTransparentCells(uint3 thread : SV_DispatchThreadID) { ScatterTransparency(thread); }

[shader("compute")] [numthreads(16,16,1)]
void RequestTransparentShadows<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 pixel : SV_DispatchThreadID)
{ RequestTransparencyShadows(materials, pixel); }

[shader("compute")] [numthreads(16,16,1)]
void ResolveTransparentColor<let visibilityDepth : uint, let tessellated : bool>(
    uniform SingleTransparencyMaterials<visibilityDepth, tessellated> materials, uint3 pixel : SV_DispatchThreadID)
{ ResolveTransparency(materials, pixel); }
