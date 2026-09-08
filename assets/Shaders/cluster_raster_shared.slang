#ifndef CLUSTER_RASTER_SHARED_SLANG
#define CLUSTER_RASTER_SHARED_SLANG

#include "wave_queue.slang"
#include "material_interfaces.slang"

static const uint CLUSTER_RASTER_SUBPIXEL_BITS = 8u;
static const uint CLUSTER_RASTER_SUBPIXEL_SCALE = 1u << CLUSTER_RASTER_SUBPIXEL_BITS;
static const float CLUSTER_RASTER_SUBPIXEL_SCALE_F = float(CLUSTER_RASTER_SUBPIXEL_SCALE);
static const uint CLUSTER_RASTER_MAX_CLIPPED_VERTICES = 16u;

interface IClusterRasterOutput
{
    void write(
        int2 pixel,
        uint viewIndex,
        uint targetPolicy,
        uint targetViewIndex,
        float depth,
        uint visibilityValue);
};

struct ClusterRasterTriangle
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

float ClusterRasterClipDistance(float4 value, uint plane)
{
    if (plane == 0u) return value.w - 1e-6;
    if (plane == 1u) return value.z;
    if (plane == 2u) return value.w - value.z;
    if (plane == 3u) return value.x + value.w;
    if (plane == 4u) return value.w - value.x;
    if (plane == 5u) return value.y + value.w;
    return value.w - value.y;
}

uint ClusterRasterClipMask(float4 value)
{
    return (value.w < 1e-6 ? 1u : 0u) |
        (value.z < 0.0 ? 2u : 0u) |
        (value.z > value.w ? 4u : 0u) |
        (value.x < -value.w ? 8u : 0u) |
        (value.x > value.w ? 16u : 0u) |
        (value.y < -value.w ? 32u : 0u) |
        (value.y > value.w ? 64u : 0u);
}

uint ClusterRasterClipTriangle(
    float4 source[3],
    out float4 clipped[CLUSTER_RASTER_MAX_CLIPPED_VERTICES])
{
    float4 first[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
    float4 second[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
    first[0] = source[0];
    first[1] = source[1];
    first[2] = source[2];
    uint count = 3u;

    [loop]
    for (uint plane = 0u; plane < 7u; plane++)
    {
        uint outputCount = 0u;
        if (count == 0u)
            break;

        float4 previous = first[count - 1u];
        float previousDistance = ClusterRasterClipDistance(previous, plane);
        bool previousInside = previousDistance >= 0.0;
        [loop]
        for (uint index = 0u; index < count; index++)
        {
            float4 current = first[index];
            float currentDistance = ClusterRasterClipDistance(current, plane);
            bool currentInside = currentDistance >= 0.0;

            if (currentInside != previousInside)
            {
                float denominator = previousDistance - currentDistance;
                float amount = abs(denominator) > 1e-12
                    ? saturate(previousDistance / denominator)
                    : 0.0;
                if (outputCount < CLUSTER_RASTER_MAX_CLIPPED_VERTICES)
                    second[outputCount++] = lerp(previous, current, amount);
            }
            if (currentInside && outputCount < CLUSTER_RASTER_MAX_CLIPPED_VERTICES)
                second[outputCount++] = current;

            previous = current;
            previousDistance = currentDistance;
            previousInside = currentInside;
        }

        count = outputCount;
        [unroll]
        for (uint index = 0u; index < CLUSTER_RASTER_MAX_CLIPPED_VERTICES; index++)
            first[index] = index < count ? second[index] : 0.0.xxxx;
    }

    [unroll]
    for (uint index = 0u; index < CLUSTER_RASTER_MAX_CLIPPED_VERTICES; index++)
        clipped[index] = index < count ? first[index] : 0.0.xxxx;
    return count;
}

ClusterRasterTriangle SetupClusterRasterTriangle(
    float4 clipVerts[3],
    int4 scissorRect,
    uint screenWidth,
    uint screenHeight)
{
    ClusterRasterTriangle tri = {};
    float3 inverseW = rcp(float3(clipVerts[0].w, clipVerts[1].w, clipVerts[2].w));
    float2 screenScale = float2(0.5, -0.5) * float2(screenWidth, screenHeight);
    float2 screenBias = float2(0.5, 0.5) * float2(screenWidth, screenHeight);
    float2 v0 = (clipVerts[0].xy * inverseW.x * screenScale + screenBias) *
        CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    float2 v1 = (clipVerts[1].xy * inverseW.y * screenScale + screenBias) *
        CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    float2 v2 = (clipVerts[2].xy * inverseW.z * screenScale + screenBias) *
        CLUSTER_RASTER_SUBPIXEL_SCALE_F;

    tri.Edge01 = v0 - v1;
    tri.Edge12 = v1 - v2;
    tri.Edge20 = v2 - v0;
    float determinant = tri.Edge01.y * tri.Edge20.x - tri.Edge01.x * tri.Edge20.y;
    if (determinant >= 0.0)
        return tri;

    float2 minimum = min(v0, min(v1, v2));
    float2 maximum = max(v0, max(v1, v2));
    tri.MinPixel = int2(floor(
        (minimum + (CLUSTER_RASTER_SUBPIXEL_SCALE / 2u)) /
        CLUSTER_RASTER_SUBPIXEL_SCALE_F));
    tri.MaxPixel = int2(floor(
        (maximum - (CLUSTER_RASTER_SUBPIXEL_SCALE / 2u)) /
        CLUSTER_RASTER_SUBPIXEL_SCALE_F));
    tri.MinPixel = max(tri.MinPixel, scissorRect.xy);
    tri.MaxPixel = min(tri.MaxPixel, scissorRect.zw - 1);
    if (any(tri.MinPixel > tri.MaxPixel))
        return tri;

    float2 base = float2(tri.MinPixel) * CLUSTER_RASTER_SUBPIXEL_SCALE_F +
        (CLUSTER_RASTER_SUBPIXEL_SCALE / 2u);
    v0 -= base;
    v1 -= base;
    v2 -= base;
    tri.C0 = tri.Edge12.y * v1.x - tri.Edge12.x * v1.y;
    tri.C1 = tri.Edge20.y * v2.x - tri.Edge20.x * v2.y;
    tri.C2 = tri.Edge01.y * v0.x - tri.Edge01.x * v0.y;
    float unitScale = CLUSTER_RASTER_SUBPIXEL_SCALE_F / (tri.C0 + tri.C1 + tri.C2);

    tri.C0 -= saturate(tri.Edge12.y + saturate(1.0 - tri.Edge12.x));
    tri.C1 -= saturate(tri.Edge20.y + saturate(1.0 - tri.Edge20.x));
    tri.C2 -= saturate(tri.Edge01.y + saturate(1.0 - tri.Edge01.x));
    tri.C0 /= CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    tri.C1 /= CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    tri.C2 /= CLUSTER_RASTER_SUBPIXEL_SCALE_F;

    float3 depth = float3(
        clipVerts[0].z * inverseW.x,
        clipVerts[1].z * inverseW.y,
        clipVerts[2].z * inverseW.z);
    tri.DepthPlane = float3(
        depth.x,
        (depth.y - depth.x) * unitScale,
        (depth.z - depth.x) * unitScale);
    tri.Valid = 1u;
    return tri;
}

struct ClusterPixelRasterTask<
    TPixel : IPixelEvaluate,
    TOutput : IClusterRasterOutput> : IWaveTask
{
    ClusterRasterTriangle Triangle;
    uint VisibilityValue;
    uint ViewIndex;
    uint TargetPolicy;
    uint TargetViewIndex;
    uint PixelCount;
    uint Scanline;
    TPixel Pixel;
    TOutput Output;

    [mutating] uint GetTaskCount()
    {
        return PixelCount;
    }

    [mutating] void ExecuteTask(uint sourceLane, uint localIndex, bool active)
    {
        ClusterRasterTriangle triangle;
        triangle.Edge01 = float2(
            WaveReadLaneAt(Triangle.Edge01.x, sourceLane),
            WaveReadLaneAt(Triangle.Edge01.y, sourceLane));
        triangle.Edge12 = float2(
            WaveReadLaneAt(Triangle.Edge12.x, sourceLane),
            WaveReadLaneAt(Triangle.Edge12.y, sourceLane));
        triangle.Edge20 = float2(
            WaveReadLaneAt(Triangle.Edge20.x, sourceLane),
            WaveReadLaneAt(Triangle.Edge20.y, sourceLane));
        triangle.C0 = WaveReadLaneAt(Triangle.C0, sourceLane);
        triangle.C1 = WaveReadLaneAt(Triangle.C1, sourceLane);
        triangle.C2 = WaveReadLaneAt(Triangle.C2, sourceLane);
        triangle.MinPixel = int2(
            WaveReadLaneAt(Triangle.MinPixel.x, sourceLane),
            WaveReadLaneAt(Triangle.MinPixel.y, sourceLane));
        triangle.MaxPixel = int2(
            WaveReadLaneAt(Triangle.MaxPixel.x, sourceLane),
            WaveReadLaneAt(Triangle.MaxPixel.y, sourceLane));
        triangle.DepthPlane = float3(
            WaveReadLaneAt(Triangle.DepthPlane.x, sourceLane),
            WaveReadLaneAt(Triangle.DepthPlane.y, sourceLane),
            WaveReadLaneAt(Triangle.DepthPlane.z, sourceLane));
        uint visibilityValue = WaveReadLaneAt(VisibilityValue, sourceLane);
        uint viewIndex = WaveReadLaneAt(ViewIndex, sourceLane);
        uint targetPolicy = WaveReadLaneAt(TargetPolicy, sourceLane);
        uint targetViewIndex = WaveReadLaneAt(TargetViewIndex, sourceLane);
        uint scanline = WaveReadLaneAt(Scanline, sourceLane);
        if (!active)
            return;

        int width = triangle.MaxPixel.x - triangle.MinPixel.x + 1;
        int firstX = scanline != 0u ? 0 : int(localIndex) % width;
        int lastX = scanline != 0u ? width : firstX + 1;
        int y = scanline != 0u ? int(localIndex) : int(localIndex) / width;
        if (scanline != 0u)
        {
            float3 rowEdges = float3(triangle.C0, triangle.C1, triangle.C2) +
                float(y) * float3(triangle.Edge12.x, triangle.Edge20.x, triangle.Edge01.x);
            float3 edgeSteps = float3(triangle.Edge12.y, triangle.Edge20.y, triangle.Edge01.y);
            float lower = 0.0;
            float upper = float(width - 1);
            [unroll]
            for (uint edge = 0u; edge < 3u; edge++)
            {
                if (edgeSteps[edge] < 0.0)
                    lower = max(lower, rowEdges[edge] / edgeSteps[edge]);
                else if (edgeSteps[edge] > 0.0)
                    upper = min(upper, rowEdges[edge] / edgeSteps[edge]);
                else if (rowEdges[edge] < 0.0)
                    return;
            }
            // Round outwards and retain the edge test at both ends. This keeps
            // floating-point division from dropping a covered boundary pixel.
            firstX = max(firstX, int(floor(min(lower, float(width)))));
            lastX = min(lastX, int(ceil(max(upper, -1.0))) + 1);
        }
        [loop]
        for (int x = firstX; x < lastX; x++)
        {
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
            PixelEvaluateInput pixelInput;
            pixelInput.PixelCoord = uint3(pixel, viewIndex);
            pixelInput.Depth = depth;
            PixelEvaluateResult pixelResult = Pixel.evaluate(pixelInput);
            if (pixelResult.Visible == 0u)
                continue;
            Output.write(
                pixel,
                viewIndex,
                targetPolicy,
                targetViewIndex,
                saturate(pixelResult.Depth),
                visibilityValue);
        }
    }
};

void RasterizeClusterTriangleWave<
    TPixel : IPixelEvaluate,
    TOutput : IClusterRasterOutput>(
    uniform TPixel pixel,
    uniform TOutput output,
    float4 clipVerts[3],
    int4 scissorRect,
    uint visibilityValue,
    uint viewIndex,
    uint targetPolicy,
    uint targetViewIndex,
    bool sourceActive,
    uint screenWidth,
    uint screenHeight)
{
    uint mask0 = ClusterRasterClipMask(clipVerts[0]);
    uint mask1 = ClusterRasterClipMask(clipVerts[1]);
    uint mask2 = ClusterRasterClipMask(clipVerts[2]);
    sourceActive = sourceActive && ((mask0 & mask1 & mask2) == 0u);
    bool needsClipping = sourceActive && ((mask0 | mask1 | mask2) != 0u);
    float4 clipped[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
    uint clippedCount = sourceActive ? 3u : 0u;
    if (needsClipping)
        clippedCount = ClusterRasterClipTriangle(clipVerts, clipped);
    uint fanTriangleCount = clippedCount >= 3u ? clippedCount - 2u : 0u;
    uint waveTriangleCount = WaveActiveMax(fanTriangleCount);

    [loop]
    for (uint fanIndex = 0u; fanIndex < waveTriangleCount; fanIndex++)
    {
        bool active = fanIndex < fanTriangleCount;
        float4 fan[3] =
        {
            active ? (needsClipping ? clipped[0] : clipVerts[0]) : float4(0.0, 0.0, 0.0, 1.0),
            active ? (needsClipping ? clipped[fanIndex + 1u] : clipVerts[1]) : float4(0.0, 0.0, 0.0, 1.0),
            active ? (needsClipping ? clipped[fanIndex + 2u] : clipVerts[2]) : float4(0.0, 0.0, 0.0, 1.0),
        };
        ClusterRasterTriangle rasterTriangle = active
            ? SetupClusterRasterTriangle(fan, scissorRect, screenWidth, screenHeight)
            : (ClusterRasterTriangle)0;

        ClusterPixelRasterTask<TPixel, TOutput> task;
        task.Triangle = rasterTriangle;
        task.VisibilityValue = visibilityValue;
        task.ViewIndex = viewIndex;
        task.TargetPolicy = targetPolicy;
        task.TargetViewIndex = targetViewIndex;
        task.Pixel = pixel;
        task.Output = output;
        if (active && rasterTriangle.Valid != 0u)
        {
            int width = rasterTriangle.MaxPixel.x - rasterTriangle.MinPixel.x + 1;
            int height = rasterTriangle.MaxPixel.y - rasterTriangle.MinPixel.y + 1;
            // Narrow triangles are cheaper as rectangle tasks.  Wider triangles
            // use one wave task per scanline, bounding queue pressure while the
            // Cluster classifier guarantees the enclosing box is at most 64x64.
            task.Scanline = width > 4 ? 1u : 0u;
            task.PixelCount = task.Scanline != 0u
                ? uint(height)
                : uint(width * height);
        }
        else
        {
            task.PixelCount = 0u;
            task.Scanline = 0u;
        }
        WaveQueue::Distribute(task);
    }
}

#endif
