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
    int4 scissor(
        uint visibleClusterIndex,
        uint targetPolicy,
        uint targetViewIndex,
        uint screenWidth,
        uint screenHeight);

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
    float2 Screen0;
    float2 Screen1;
    float2 Screen2;
    float3 InvW;
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

uint ClusterRasterClipMask(float4 value, bool clipDepth = true)
{
    return (value.w < 1e-6 ? 1u : 0u) |
        (clipDepth && value.z < 0.0 ? 2u : 0u) |
        (clipDepth && value.z > value.w ? 4u : 0u) |
        (value.x < -value.w ? 8u : 0u) |
        (value.x > value.w ? 16u : 0u) |
        (value.y < -value.w ? 32u : 0u) |
        (value.y > value.w ? 64u : 0u);
}

uint ClusterRasterClipTriangle(
    float4 source[3],
    uint clipPlanes,
    out float4 clipped[CLUSTER_RASTER_MAX_CLIPPED_VERTICES],
    out float3 clippedSourceBarycentric[CLUSTER_RASTER_MAX_CLIPPED_VERTICES],
    bool clipDepth = true)
{
    float4 first[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
    float4 second[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
    float3 firstSourceBarycentric[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
    float3 secondSourceBarycentric[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
    first[0] = source[0];
    first[1] = source[1];
    first[2] = source[2];
    firstSourceBarycentric[0] = float3(1.0, 0.0, 0.0);
    firstSourceBarycentric[1] = float3(0.0, 1.0, 0.0);
    firstSourceBarycentric[2] = float3(0.0, 0.0, 1.0);
    uint count = 3u;
    if (!all(isfinite(source[0])) || !all(isfinite(source[1])) || !all(isfinite(source[2])))
        clipPlanes = 127u;

    [loop]
    for (uint plane = 0u; plane < 7u; plane++)
    {
        // A plane whose vertices are all inside cannot change this polygon.
        if ((clipPlanes & (1u << plane)) == 0u)
            continue;
        uint outputCount = 0u;
        if (count == 0u)
            break;

        float4 previous = first[count - 1u];
        float3 previousSourceBarycentric = firstSourceBarycentric[count - 1u];
        float previousDistance = ClusterRasterClipDistance(previous, plane);
        bool previousInside = previousDistance >= 0.0;
        [loop]
        for (uint index = 0u; index < count; index++)
        {
            float4 current = first[index];
            float3 currentSourceBarycentric = firstSourceBarycentric[index];
            float currentDistance = ClusterRasterClipDistance(current, plane);
            bool currentInside = currentDistance >= 0.0;

            if (currentInside != previousInside)
            {
                float denominator = previousDistance - currentDistance;
                float amount = abs(denominator) > 1e-12
                    ? saturate(previousDistance / denominator)
                    : 0.0;
                float4 intersection = lerp(previous, current, amount);
                float3 sourceBarycentric = lerp(
                    previousSourceBarycentric,
                    currentSourceBarycentric,
                    amount);
                // Include newly rounded intersections, so later planes still handle
                // a point that crosses a boundary through floating-point arithmetic.
                clipPlanes |= all(isfinite(intersection)) ? ClusterRasterClipMask(intersection, clipDepth) : 127u;
                if (outputCount < CLUSTER_RASTER_MAX_CLIPPED_VERTICES)
                {
                    second[outputCount++] = intersection;
                    secondSourceBarycentric[outputCount - 1u] = sourceBarycentric;
                }
            }
            if (currentInside && outputCount < CLUSTER_RASTER_MAX_CLIPPED_VERTICES)
            {
                second[outputCount++] = current;
                secondSourceBarycentric[outputCount - 1u] = currentSourceBarycentric;
            }

            previous = current;
            previousSourceBarycentric = currentSourceBarycentric;
            previousDistance = currentDistance;
            previousInside = currentInside;
        }

        count = outputCount;
        [unroll]
        for (uint index = 0u; index < CLUSTER_RASTER_MAX_CLIPPED_VERTICES; index++)
        {
            first[index] = index < count ? second[index] : 0.0.xxxx;
            firstSourceBarycentric[index] = index < count
                ? secondSourceBarycentric[index]
                : 0.0.xxx;
        }
    }

    [unroll]
    for (uint index = 0u; index < CLUSTER_RASTER_MAX_CLIPPED_VERTICES; index++)
    {
        clipped[index] = index < count ? first[index] : 0.0.xxxx;
        clippedSourceBarycentric[index] = index < count
            ? firstSourceBarycentric[index]
            : 0.0.xxx;
    }
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
    float2 v0 = floor((clipVerts[0].xy * inverseW.x * screenScale + screenBias) *
        CLUSTER_RASTER_SUBPIXEL_SCALE_F + 0.5);
    float2 v1 = floor((clipVerts[1].xy * inverseW.y * screenScale + screenBias) *
        CLUSTER_RASTER_SUBPIXEL_SCALE_F + 0.5);
    float2 v2 = floor((clipVerts[2].xy * inverseW.z * screenScale + screenBias) *
        CLUSTER_RASTER_SUBPIXEL_SCALE_F + 0.5);

    tri.Screen0 = v0 / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    tri.Screen1 = v1 / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    tri.Screen2 = v2 / CLUSTER_RASTER_SUBPIXEL_SCALE_F;
    tri.InvW = inverseW;

    tri.Edge01 = v0 - v1;
    tri.Edge12 = v1 - v2;
    tri.Edge20 = v2 - v0;
    float determinant = tri.Edge01.y * tri.Edge20.x - tri.Edge01.x * tri.Edge20.y;
    if (determinant >= 0.0)
        return tri;

    float2 minimum = min(v0, min(v1, v2));
    float2 maximum = max(v0, max(v1, v2));
    tri.MinPixel = int2(floor(
        (minimum + (CLUSTER_RASTER_SUBPIXEL_SCALE / 2u) - 1.0) /
        CLUSTER_RASTER_SUBPIXEL_SCALE_F));
    tri.MaxPixel = int2(floor(
        (maximum - (CLUSTER_RASTER_SUBPIXEL_SCALE / 2u) - 1.0) /
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

float3 ClusterRasterLinearBarycentric(
    ClusterRasterTriangle triangle,
    float2 pixelCenter)
{
    float2 edge0 = triangle.Screen1 - triangle.Screen0;
    float2 edge1 = triangle.Screen2 - triangle.Screen0;
    float2 point = pixelCenter - triangle.Screen0;
    float determinant = edge0.x * edge1.y - edge0.y * edge1.x;
    if (abs(determinant) <= 1e-12)
        return float3(1.0, 0.0, 0.0);
    float inverse = rcp(determinant);
    float u = (point.x * edge1.y - point.y * edge1.x) * inverse;
    float v = (edge0.x * point.y - edge0.y * point.x) * inverse;
    return float3(1.0 - u - v, u, v);
}

float3 ClusterRasterPerspectiveBarycentric(
    ClusterRasterTriangle triangle,
    float3 linearBarycentric)
{
    float3 weighted = linearBarycentric * triangle.InvW;
    float sum = weighted.x + weighted.y + weighted.z;
    return abs(sum) > 1e-12
        ? weighted / sum
        : float3(1.0, 0.0, 0.0);
}

struct ClusterPixelRasterTask<
    TVertex,
    TVisibility : IVisibilityEvaluate<TVertex>,
    TOutput : IClusterRasterOutput> : IWaveUniformTask<TVisibility>
{
    ClusterRasterTriangle Triangle;
    float3 SourceBarycentric0;
    float3 SourceBarycentric1;
    float3 SourceBarycentric2;
    TVisibility.Varying Varying0;
    TVisibility.Varying Varying1;
    TVisibility.Varying Varying2;
    uint VisibilityValue;
    uint VisibleClusterIndex;
    uint TriangleID;
    uint ViewIndex;
    uint TargetPolicy;
    uint TargetViewIndex;
    uint PixelCount;
    uint Scanline;
    TOutput Output;

    [mutating] uint GetTaskCount()
    {
        return PixelCount;
    }

    ClusterPixelRasterTask<TVertex, TVisibility, TOutput> ReadLane(
        uniform TVisibility visibility,
        uint sourceLane)
    {
        ClusterPixelRasterTask<TVertex, TVisibility, TOutput> result;
        result.Triangle.Edge01 = float2(
            WaveReadLaneAt(Triangle.Edge01.x, sourceLane),
            WaveReadLaneAt(Triangle.Edge01.y, sourceLane));
        result.Triangle.Edge12 = float2(
            WaveReadLaneAt(Triangle.Edge12.x, sourceLane),
            WaveReadLaneAt(Triangle.Edge12.y, sourceLane));
        result.Triangle.Edge20 = float2(
            WaveReadLaneAt(Triangle.Edge20.x, sourceLane),
            WaveReadLaneAt(Triangle.Edge20.y, sourceLane));
        result.Triangle.C0 = WaveReadLaneAt(Triangle.C0, sourceLane);
        result.Triangle.C1 = WaveReadLaneAt(Triangle.C1, sourceLane);
        result.Triangle.C2 = WaveReadLaneAt(Triangle.C2, sourceLane);
        result.Triangle.MinPixel = int2(
            WaveReadLaneAt(Triangle.MinPixel.x, sourceLane),
            WaveReadLaneAt(Triangle.MinPixel.y, sourceLane));
        result.Triangle.MaxPixel = int2(
            WaveReadLaneAt(Triangle.MaxPixel.x, sourceLane),
            WaveReadLaneAt(Triangle.MaxPixel.y, sourceLane));
        result.Triangle.DepthPlane = float3(
            WaveReadLaneAt(Triangle.DepthPlane.x, sourceLane),
            WaveReadLaneAt(Triangle.DepthPlane.y, sourceLane),
            WaveReadLaneAt(Triangle.DepthPlane.z, sourceLane));
        result.Triangle.Screen0 = float2(
            WaveReadLaneAt(Triangle.Screen0.x, sourceLane),
            WaveReadLaneAt(Triangle.Screen0.y, sourceLane));
        result.Triangle.Screen1 = float2(
            WaveReadLaneAt(Triangle.Screen1.x, sourceLane),
            WaveReadLaneAt(Triangle.Screen1.y, sourceLane));
        result.Triangle.Screen2 = float2(
            WaveReadLaneAt(Triangle.Screen2.x, sourceLane),
            WaveReadLaneAt(Triangle.Screen2.y, sourceLane));
        result.Triangle.InvW = float3(
            WaveReadLaneAt(Triangle.InvW.x, sourceLane),
            WaveReadLaneAt(Triangle.InvW.y, sourceLane),
            WaveReadLaneAt(Triangle.InvW.z, sourceLane));
        result.SourceBarycentric0 = float3(
            WaveReadLaneAt(SourceBarycentric0.x, sourceLane),
            WaveReadLaneAt(SourceBarycentric0.y, sourceLane),
            WaveReadLaneAt(SourceBarycentric0.z, sourceLane));
        result.SourceBarycentric1 = float3(
            WaveReadLaneAt(SourceBarycentric1.x, sourceLane),
            WaveReadLaneAt(SourceBarycentric1.y, sourceLane),
            WaveReadLaneAt(SourceBarycentric1.z, sourceLane));
        result.SourceBarycentric2 = float3(
            WaveReadLaneAt(SourceBarycentric2.x, sourceLane),
            WaveReadLaneAt(SourceBarycentric2.y, sourceLane),
            WaveReadLaneAt(SourceBarycentric2.z, sourceLane));
        result.VisibilityValue = WaveReadLaneAt(VisibilityValue, sourceLane);
        result.VisibleClusterIndex = WaveReadLaneAt(VisibleClusterIndex, sourceLane);
        result.TriangleID = WaveReadLaneAt(TriangleID, sourceLane);
        result.ViewIndex = WaveReadLaneAt(ViewIndex, sourceLane);
        result.TargetPolicy = WaveReadLaneAt(TargetPolicy, sourceLane);
        result.TargetViewIndex = WaveReadLaneAt(TargetViewIndex, sourceLane);
        result.Scanline = WaveReadLaneAt(Scanline, sourceLane);
        result.Varying0 = visibility.readLane(Varying0, sourceLane);
        result.Varying1 = visibility.readLane(Varying1, sourceLane);
        result.Varying2 = visibility.readLane(Varying2, sourceLane);
        result.PixelCount = WaveReadLaneAt(PixelCount, sourceLane);
        result.Output = Output;
        return result;
    }

    [mutating] void ExecuteTask(
        uniform TVisibility visibility,
        uint sourceLane,
        uint localIndex,
        bool active)
    {
        ClusterPixelRasterTask<TVertex, TVisibility, TOutput> source = ReadLane(visibility, sourceLane);
        if (active)
            source.RasterizePixels(visibility, localIndex);
    }

    void RasterizePixels(uniform TVisibility visibility, uint localIndex)
    {
        ClusterRasterTriangle triangle = Triangle;
        float3 sourceBarycentric0 = SourceBarycentric0;
        float3 sourceBarycentric1 = SourceBarycentric1;
        float3 sourceBarycentric2 = SourceBarycentric2;
        uint visibilityValue = VisibilityValue;
        uint visibleClusterIndex = VisibleClusterIndex;
        uint triangleID = TriangleID;
        uint viewIndex = ViewIndex;
        uint targetPolicy = TargetPolicy;
        uint targetViewIndex = TargetViewIndex;
        uint scanline = Scanline;
        TVisibility.Varying varying0 = Varying0;
        TVisibility.Varying varying1 = Varying1;
        TVisibility.Varying varying2 = Varying2;
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
            float3 fanLinear = ClusterRasterLinearBarycentric(
                triangle,
                float2(pixel) + 0.5);
            float3 fanPerspective = ClusterRasterPerspectiveBarycentric(
                triangle,
                fanLinear);
            float3 fanLinearX = ClusterRasterLinearBarycentric(
                triangle,
                float2(pixel) + float2(1.5, 0.5));
            float3 fanLinearY = ClusterRasterLinearBarycentric(
                triangle,
                float2(pixel) + float2(0.5, 1.5));
            float3 fanPerspectiveX = ClusterRasterPerspectiveBarycentric(
                triangle,
                fanLinearX);
            float3 fanPerspectiveY = ClusterRasterPerspectiveBarycentric(
                triangle,
                fanLinearY);
            VisibilityInterpolationInput interpolation;
            interpolation.LinearBarycentric =
                sourceBarycentric0 * fanLinear.x +
                sourceBarycentric1 * fanLinear.y +
                sourceBarycentric2 * fanLinear.z;
            interpolation.PerspectiveBarycentric =
                sourceBarycentric0 * fanPerspective.x +
                sourceBarycentric1 * fanPerspective.y +
                sourceBarycentric2 * fanPerspective.z;
            float3 linearX =
                sourceBarycentric0 * fanLinearX.x +
                sourceBarycentric1 * fanLinearX.y +
                sourceBarycentric2 * fanLinearX.z;
            float3 linearY =
                sourceBarycentric0 * fanLinearY.x +
                sourceBarycentric1 * fanLinearY.y +
                sourceBarycentric2 * fanLinearY.z;
            float3 perspectiveX =
                sourceBarycentric0 * fanPerspectiveX.x +
                sourceBarycentric1 * fanPerspectiveX.y +
                sourceBarycentric2 * fanPerspectiveX.z;
            float3 perspectiveY =
                sourceBarycentric0 * fanPerspectiveY.x +
                sourceBarycentric1 * fanPerspectiveY.y +
                sourceBarycentric2 * fanPerspectiveY.z;
            interpolation.LinearBarycentricDx =
                linearX - interpolation.LinearBarycentric;
            interpolation.LinearBarycentricDy =
                linearY - interpolation.LinearBarycentric;
            interpolation.PerspectiveBarycentricDx =
                perspectiveX - interpolation.PerspectiveBarycentric;
            interpolation.PerspectiveBarycentricDy =
                perspectiveY - interpolation.PerspectiveBarycentric;
            TVisibility.Varying varying = visibility.interpolatePixel(
                varying0,
                varying1,
                varying2,
                interpolation);
            VisibilityPixelInput pixelInput;
            pixelInput.PixelCoord = uint3(pixel, viewIndex);
            pixelInput.Depth = depth;
            pixelInput.VisibleClusterIndex = visibleClusterIndex;
            pixelInput.TriangleID = triangleID;
            pixelInput.ViewIndex = viewIndex;
            pixelInput.TargetPolicy = targetPolicy;
            pixelInput.TargetViewIndex = targetViewIndex;
            VisibilityPixelResult pixelResult = visibility.evaluate(pixelInput, varying);
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
    TVertex,
    TVisibility : IVisibilityEvaluate<TVertex>,
    TOutput : IClusterRasterOutput>(
    uniform TVisibility visibility,
    uniform TOutput output,
    float4 clipVerts[3],
    TVisibility.Varying varyings[3],
    int4 scissorRect,
    uint visibleClusterIndex,
    uint triangleID,
    uint viewIndex,
    uint targetPolicy,
    uint targetViewIndex,
    bool sourceActive,
    uint screenWidth,
    uint screenHeight,
    bool clipDepth = true)
{
    uint mask0 = ClusterRasterClipMask(clipVerts[0], clipDepth);
    uint mask1 = ClusterRasterClipMask(clipVerts[1], clipDepth);
    uint mask2 = ClusterRasterClipMask(clipVerts[2], clipDepth);
    sourceActive = sourceActive && ((mask0 & mask1 & mask2) == 0u);
    bool needsClipping = sourceActive && ((mask0 | mask1 | mask2) != 0u);
    float4 clipped[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
    float3 clippedSourceBarycentric[CLUSTER_RASTER_MAX_CLIPPED_VERTICES];
    uint clippedCount = sourceActive ? 3u : 0u;
    if (needsClipping)
        clippedCount = ClusterRasterClipTriangle(
            clipVerts,
            mask0 | mask1 | mask2,
            clipped,
            clippedSourceBarycentric, clipDepth);
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
        float3 fanSourceBarycentric[3] =
        {
            active
                ? (needsClipping
                    ? clippedSourceBarycentric[0]
                    : float3(1.0, 0.0, 0.0))
                : 0.0.xxx,
            active
                ? (needsClipping
                    ? clippedSourceBarycentric[fanIndex + 1u]
                    : float3(0.0, 1.0, 0.0))
                : 0.0.xxx,
            active
                ? (needsClipping
                    ? clippedSourceBarycentric[fanIndex + 2u]
                    : float3(0.0, 0.0, 1.0))
                : 0.0.xxx,
        };
        ClusterRasterTriangle rasterTriangle = active
            ? SetupClusterRasterTriangle(fan, scissorRect, screenWidth, screenHeight)
            : (ClusterRasterTriangle)0;

        ClusterPixelRasterTask<TVertex, TVisibility, TOutput> task;
        task.Triangle = rasterTriangle;
        task.SourceBarycentric0 = fanSourceBarycentric[0];
        task.SourceBarycentric1 = fanSourceBarycentric[1];
        task.SourceBarycentric2 = fanSourceBarycentric[2];
        task.Varying0 = varyings[0];
        task.Varying1 = varyings[1];
        task.Varying2 = varyings[2];
        task.VisibilityValue = EncodeVisBuffer(visibleClusterIndex, triangleID);
        task.VisibleClusterIndex = visibleClusterIndex;
        task.TriangleID = triangleID;
        task.ViewIndex = viewIndex;
        task.TargetPolicy = targetPolicy;
        task.TargetViewIndex = targetViewIndex;
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
        WaveQueue::DistributeWithUniform<TVisibility,
            ClusterPixelRasterTask<TVertex, TVisibility, TOutput>>(task, visibility);
    }
}

#endif
