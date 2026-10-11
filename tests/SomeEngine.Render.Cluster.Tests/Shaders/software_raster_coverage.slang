// A public shader-library caller: real clip-space vertices and a legal scissor.
// Only visibility is used; unrelated surface-material tables are not parameters.
#define MATERIAL_INTERFACES_SLANG
#include "visibility_evaluate.slang"
#include "visbuffer_encoding.slang"
#define CLUSTER_RASTER_TWO_SIDED 0
#define CLUSTER_TARGET_RASTER_TWO_SIDED 0
#include "cluster_raster_shared.slang"

struct CoverageInputs
{
    float4 Vertex0;
    float4 Vertex1;
    float4 Vertex2;
    uint ViewWidth;
    uint ViewHeight;
    uint ImageWidth;
    uint ImageHeight;
    int4 Scissor;
};
ConstantBuffer<CoverageInputs> Inputs;
RWStructuredBuffer<uint> Pixels;

struct CoverageOutput : IClusterRasterOutput
{
    uint ImageIndex;

    int4 scissor(uint cluster, uint policy, uint target, uint width, uint height)
    { return Inputs.Scissor; }

    bool resolvePixel(int2 pixel, uint policy, uint target, uint visibility,
        out ClusterRasterOutputPixel result)
    {
        result.Position = pixel - Inputs.Scissor.xy;
        result.DepthCacheClass = 0u;
        return all(result.Position >= 0) &&
            result.Position.x < int(Inputs.ImageWidth) && result.Position.y < int(Inputs.ImageHeight);
    }

    uint readDepth(ClusterRasterOutputPixel pixel, uint policy, uint target)
    { return 0u; }

    void write(ClusterRasterOutputPixel pixel, uint view, uint policy, uint target,
        float depth, uint visibility)
    {
        uint index = ImageIndex * Inputs.ImageWidth * Inputs.ImageHeight +
            uint(pixel.Position.y) * Inputs.ImageWidth + uint(pixel.Position.x);
        InterlockedOr(Pixels[index], 0xFFFFFFFFu);
    }
};

[shader("compute")]
[numthreads(64, 1, 1)]
void ClearCoverage(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x < Inputs.ImageWidth * Inputs.ImageHeight * 2u)
        Pixels[thread.x] = 0u;
}

void DrawCoverage(uint thread)
{
    float4 vertices[3] = { Inputs.Vertex0, Inputs.Vertex1, Inputs.Vertex2 };
    EmptyVisibilityVarying varyings[3] = { {}, {}, {} };
    OpaqueVisibilityEvaluate<float4> visibility;
    CoverageOutput output;
    output.ImageIndex = 0u;
    RasterizeClusterTriangleWave<float4, OpaqueVisibilityEvaluate<float4>, CoverageOutput>(
        visibility, output, vertices, varyings, Inputs.Scissor,
        0u, 0u, 0u, 0u, 0u, thread == 0u, Inputs.ViewWidth, Inputs.ViewHeight);
}

void DrawRectangleCoverage(uint thread, uniform uint threadCount)
{
    // Independent pre-change narrow-rectangle visit order. Triangle setup and
    // per-pixel callbacks receive real geometry; no edge coefficients are injected.
    float4 vertices[3] = { Inputs.Vertex0, Inputs.Vertex1, Inputs.Vertex2 };
    ClusterRasterTriangle triangle = SetupClusterRasterTriangle(
        vertices, Inputs.Scissor, Inputs.ViewWidth, Inputs.ViewHeight, true);
    if (triangle.Valid == 0u) return;
    // Retain the archived float pixel predicate for the diagnostic comparison.
    // Acceptance is always the independent Int64 geometry oracle in the test.
    triangle.CoverageMode = CLUSTER_RASTER_COVERAGE_FLOAT_PIXELS;
    ClusterPixelRasterTask<float4, OpaqueVisibilityEvaluate<float4>, CoverageOutput> task;
    task.Triangle = triangle;
    task.SourceBarycentric0 = float3(1.0, 0.0, 0.0);
    task.SourceBarycentric1 = float3(0.0, 1.0, 0.0);
    task.SourceBarycentric2 = float3(0.0, 0.0, 1.0);
    task.Varying0 = {};
    task.Varying1 = {};
    task.Varying2 = {};
    task.VisibilityValue = EncodeVisBuffer(0u, 0u);
    task.VisibleClusterIndex = 0u;
    task.TriangleID = 0u;
    task.ViewIndex = 0u;
    task.TargetPolicy = 0u;
    task.TargetViewIndex = 0u;
    task.Scanline = 0u;
    task.EarlyDepthEnabled = 0u;
    task.DepthCenter = 0.0;
    CoverageOutput output;
    output.ImageIndex = 1u;
    task.Output = output;
    OpaqueVisibilityEvaluate<float4> visibility;
    uint width = uint(triangle.MaxPixel.x - triangle.MinPixel.x + 1);
    uint height = uint(triangle.MaxPixel.y - triangle.MinPixel.y + 1);
    for (uint index = thread; index < width * height; index += threadCount)
        task.RasterizePixels(visibility, index);
}

[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void RasterCoverage32(uint thread : SV_GroupIndex) { DrawCoverage(thread); }

[shader("compute")]
[numthreads(64, 1, 1)]
[WaveSize(32)]
void RasterCoverage64(uint thread : SV_GroupIndex) { DrawCoverage(thread); }

[shader("compute")]
[numthreads(32, 1, 1)]
[WaveSize(32)]
void RasterCoverageRectangle32(uint thread : SV_GroupIndex) { DrawRectangleCoverage(thread, 32u); }

[shader("compute")]
[numthreads(64, 1, 1)]
[WaveSize(32)]
void RasterCoverageRectangle64(uint thread : SV_GroupIndex) { DrawRectangleCoverage(thread, 64u); }
