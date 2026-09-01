#include "cluster_tessellation_types.slang"
#include "cluster_view.slang"
#include "visbuffer_encoding.slang"

struct TessellationVisibilityUniforms
{
    uint ScreenWidth;
    uint ScreenHeight;
    uint MaxDicedTriangles;
    uint Pad0;
};

ConstantBuffer<TessellationVisibilityUniforms> VisibilityUniforms;
StructuredBuffer<ClusterViewUniforms> VisibilityViews;
StructuredBuffer<DicedTriangle> VisibilityDicedTriangles;
ByteAddressBuffer VisibilityTessellationCounters;
Texture2DArray<float> VisibilityDepthTarget;
RWTexture2DArray<uint> VisibilityTarget;
RWTexture2DArray<uint> VisibilityDepthUav;

static const uint TESS_VIS_SUBPIXEL_BITS = 8u;
static const float TESS_VIS_SUBPIXEL_SCALE = 256.0;

struct TessellationRasterTriangle
{
    float2 Edge01;
    float2 Edge12;
    float2 Edge20;
    float C0;
    float C1;
    float C2;
    float3 DepthPlane;
    int2 MinPixel;
    int2 MaxPixel;
    uint Valid;
};

TessellationRasterTriangle SetupTessellationRasterTriangle(
    DicedTriangle diced,
    ClusterViewUniforms view)
{
    TessellationRasterTriangle triangle = {};
    float4 clip[3] =
    {
        mul(float4(diced.Position0, 1.0), view.ViewProj),
        mul(float4(diced.Position1, 1.0), view.ViewProj),
        mul(float4(diced.Position2, 1.0), view.ViewProj),
    };
    if (clip[0].w <= 0.0 || clip[1].w <= 0.0 || clip[2].w <= 0.0)
        return triangle;

    float3 inverseW = rcp(float3(clip[0].w, clip[1].w, clip[2].w));
    float2 scale = float2(0.5, -0.5) *
        float2(VisibilityUniforms.ScreenWidth, VisibilityUniforms.ScreenHeight);
    float2 bias = float2(0.5, 0.5) *
        float2(VisibilityUniforms.ScreenWidth, VisibilityUniforms.ScreenHeight);
    float2 vertices[3] =
    {
        (clip[0].xy * inverseW.x * scale + bias) * TESS_VIS_SUBPIXEL_SCALE,
        (clip[1].xy * inverseW.y * scale + bias) * TESS_VIS_SUBPIXEL_SCALE,
        (clip[2].xy * inverseW.z * scale + bias) * TESS_VIS_SUBPIXEL_SCALE,
    };

    triangle.Edge01 = vertices[0] - vertices[1];
    triangle.Edge12 = vertices[1] - vertices[2];
    triangle.Edge20 = vertices[2] - vertices[0];
    float determinant =
        triangle.Edge01.y * triangle.Edge20.x -
        triangle.Edge01.x * triangle.Edge20.y;
    if (determinant >= 0.0)
        return triangle;

    float2 minimum = min(vertices[0], min(vertices[1], vertices[2]));
    float2 maximum = max(vertices[0], max(vertices[1], vertices[2]));
    triangle.MinPixel = max(
        int2(0, 0),
        int2(floor((minimum + 128.0) / TESS_VIS_SUBPIXEL_SCALE)));
    triangle.MaxPixel = min(
        int2(VisibilityUniforms.ScreenWidth, VisibilityUniforms.ScreenHeight) - 1,
        int2(floor((maximum - 128.0) / TESS_VIS_SUBPIXEL_SCALE)));
    if (any(triangle.MinPixel > triangle.MaxPixel))
        return triangle;

    float2 base = float2(triangle.MinPixel) * TESS_VIS_SUBPIXEL_SCALE + 128.0;
    vertices[0] -= base;
    vertices[1] -= base;
    vertices[2] -= base;
    triangle.C0 = triangle.Edge12.y * vertices[1].x - triangle.Edge12.x * vertices[1].y;
    triangle.C1 = triangle.Edge20.y * vertices[2].x - triangle.Edge20.x * vertices[2].y;
    triangle.C2 = triangle.Edge01.y * vertices[0].x - triangle.Edge01.x * vertices[0].y;
    float barycentricScale = TESS_VIS_SUBPIXEL_SCALE /
        (triangle.C0 + triangle.C1 + triangle.C2);
    triangle.C0 -= saturate(triangle.Edge12.y + saturate(1.0 - triangle.Edge12.x));
    triangle.C1 -= saturate(triangle.Edge20.y + saturate(1.0 - triangle.Edge20.x));
    triangle.C2 -= saturate(triangle.Edge01.y + saturate(1.0 - triangle.Edge01.x));
    triangle.C0 /= TESS_VIS_SUBPIXEL_SCALE;
    triangle.C1 /= TESS_VIS_SUBPIXEL_SCALE;
    triangle.C2 /= TESS_VIS_SUBPIXEL_SCALE;
    float3 depth = float3(
        clip[0].z * inverseW.x,
        clip[1].z * inverseW.y,
        clip[2].z * inverseW.z);
    triangle.DepthPlane = float3(
        depth.x,
        (depth.y - depth.x) * barycentricScale,
        (depth.z - depth.x) * barycentricScale);
    triangle.Valid = 1u;
    return triangle;
}

[MaterialTarget("cluster.tess.visibility")]
[shader("compute")]
[numthreads(32, 1, 1)]
void RasterDicedTriangles(
    uint3 groupId : SV_GroupID,
    uint groupThreadIndex : SV_GroupThreadID)
{
    uint dicedIndex = groupId.x + groupId.y * 65535u;
    uint dicedCount = min(
        VisibilityTessellationCounters.Load(TESS_COUNTER_DICED_COUNT),
        VisibilityUniforms.MaxDicedTriangles);
    if (dicedIndex >= dicedCount)
        return;

    DicedTriangle diced = VisibilityDicedTriangles[dicedIndex];
    TessellationRasterTriangle triangle = SetupTessellationRasterTriangle(
        diced,
        VisibilityViews[diced.ViewIndex]);
    if (triangle.Valid == 0u)
        return;

    int width = triangle.MaxPixel.x - triangle.MinPixel.x + 1;
    int height = triangle.MaxPixel.y - triangle.MinPixel.y + 1;
    uint pixelCount = uint(width * height);
    uint visibility = EncodeVisBuffer(
        diced.VisibleClusterIndex,
        diced.OriginalTriangle);
    for (uint localPixel = groupThreadIndex;
         localPixel < pixelCount;
         localPixel += 32u)
    {
        int x = int(localPixel) % width;
        int y = int(localPixel) / width;
        float edge0 = triangle.C0 - triangle.Edge12.y * float(x) + triangle.Edge12.x * float(y);
        float edge1 = triangle.C1 - triangle.Edge20.y * float(x) + triangle.Edge20.x * float(y);
        float edge2 = triangle.C2 - triangle.Edge01.y * float(x) + triangle.Edge01.x * float(y);
        if (edge0 < 0.0 || edge1 < 0.0 || edge2 < 0.0)
            continue;

        int2 pixel = triangle.MinPixel + int2(x, y);
        float depth = saturate(
            triangle.DepthPlane.x +
            triangle.DepthPlane.y * edge1 +
            triangle.DepthPlane.z * edge2);
        float sceneDepth = VisibilityDepthTarget.Load(int4(pixel, diced.ViewIndex, 0));
        if (depth >= sceneDepth)
            continue;

        uint reversedDepth = asuint(1.0 - depth);
        uint oldDepth;
        InterlockedMax(
            VisibilityDepthUav[uint3(pixel, diced.ViewIndex)],
            reversedDepth,
            oldDepth);
        if (reversedDepth > oldDepth)
            VisibilityTarget[uint3(pixel, diced.ViewIndex)] = visibility;
    }
}
