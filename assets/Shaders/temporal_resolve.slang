#include "fullscreen_triangle.slang"

Texture2DArray<float4> SceneColor;
Texture2DArray<float2> MotionVectors;
Texture2DArray<float> SceneDepth;
// Two generations share fixed bindings. Preserve the original half color/motion and float depth bits.
RWTexture2DArray<uint4> TemporalHistory;

struct TemporalResolveUniformData
{
    float HistoryWeight;
    float NeighborhoodClampScale;
    float NeighborhoodClampMin;
    float MotionRejectionScale;
    uint HistoryLayer;
};

ConstantBuffer<TemporalResolveUniformData> TemporalResolveUniforms;

float3 LoadCurrentClamped(int2 pixel, int2 maxPixel)
{
    return SceneColor.Load(int4(clamp(pixel, int2(0, 0), maxPixel), 0, 0)).rgb;
}

float3 RgbToYCoCg(float3 color)
{
    return float3(
        dot(color, float3(0.25, 0.5, 0.25)),
        color.r - color.b,
        color.g - 0.5 * (color.r + color.b));
}

float3 YCoCgToRgb(float3 color)
{
    float rbAverage = color.x - color.z * 0.5;
    return float3(
        rbAverage + color.y * 0.5,
        color.x + color.z * 0.5,
        rbAverage - color.y * 0.5);
}

uint4 LoadPreviousHistory(int2 pixel)
{
    return TemporalHistory[uint3(pixel, TemporalResolveUniforms.HistoryLayer ^ 1u)];
}

float2 UnpackHistoryPair(uint value)
{
    return float2(f16tof32(value & 0xffffu), f16tof32(value >> 16u));
}

uint PackHistoryPair(float2 value)
{
    return f32tof16(value.x) | (f32tof16(value.y) << 16u);
}

float4 StoreTemporalHistory(int2 pixel, float4 color)
{
    float2 motion = MotionVectors.Load(int4(pixel, 0, 0));
    float depth = SceneDepth.Load(int4(pixel, 0, 0));
    TemporalHistory[uint3(pixel, TemporalResolveUniforms.HistoryLayer)] = uint4(
        PackHistoryPair(color.xy), PackHistoryPair(color.zw), PackHistoryPair(motion), asuint(depth));
    return color;
}

float LoadCurrentDepthClamped(int2 pixel, int2 maxPixel)
{
    return SceneDepth.Load(int4(clamp(pixel, int2(0, 0), maxPixel), 0, 0));
}

float Max3(float3 value)
{
    return max(value.x, max(value.y, value.z));
}

float3 ClipAabb(float3 aabbMin, float3 aabbMax, float3 value)
{
    float3 center = 0.5 * (aabbMin + aabbMax);
    float3 extents = max(0.5 * (aabbMax - aabbMin), float3(1e-5, 1e-5, 1e-5));
    float3 offset = value - center;
    float3 unit = abs(offset / extents);
    float maxUnit = Max3(unit);
    return maxUnit > 1.0 ? center + offset / maxUnit : value;
}

void ComputeCurrentNeighborhoodYCoCg(
    int2 pixel,
    int2 maxPixel,
    out float3 minColor,
    out float3 maxColor,
    out float3 meanColor,
    out float3 sigmaColor)
{
    minColor = float3(1e20, 1e20, 1e20);
    maxColor = float3(-1e20, -1e20, -1e20);
    float3 m1 = float3(0.0, 0.0, 0.0);
    float3 m2 = float3(0.0, 0.0, 0.0);

    [unroll]
    for (int y = -1; y <= 1; y++)
    {
        [unroll]
        for (int x = -1; x <= 1; x++)
        {
            float3 color = RgbToYCoCg(LoadCurrentClamped(pixel + int2(x, y), maxPixel));
            minColor = min(minColor, color);
            maxColor = max(maxColor, color);
            m1 += color;
            m2 += color * color;
        }
    }

    meanColor = m1 / 9.0;
    sigmaColor = sqrt(max(m2 / 9.0 - meanColor * meanColor, float3(0.0, 0.0, 0.0)));
}

float3 ClipHistoryToCurrentNeighborhood(int2 pixel, int2 maxPixel, float3 currentColor, float3 previousColor)
{
    float3 minColor;
    float3 maxColor;
    float3 meanColor;
    float3 sigmaColor;
    ComputeCurrentNeighborhoodYCoCg(pixel, maxPixel, minColor, maxColor, meanColor, sigmaColor);

    float3 currentYCoCg = RgbToYCoCg(currentColor);
    float3 previousYCoCg = RgbToYCoCg(previousColor);
    float3 radius = max(abs(currentYCoCg) * TemporalResolveUniforms.NeighborhoodClampScale, TemporalResolveUniforms.NeighborhoodClampMin.xxx);
    float3 varianceMin = meanColor - sigmaColor * 1.25;
    float3 varianceMax = meanColor + sigmaColor * 1.25;
    float3 strictMin = max(minColor, varianceMin);
    float3 strictMax = min(maxColor, varianceMax);
    float3 aabbMin = min(strictMin, strictMax) - radius;
    float3 aabbMax = max(strictMin, strictMax) + radius;
    float3 clippedYCoCg = ClipAabb(aabbMin, aabbMax, previousYCoCg);
    return YCoCgToRgb(clippedYCoCg);
}

float MotionRejection(float2 motion, float2 size)
{
    float motionPixels = length(motion * size);
    return saturate((motionPixels - 0.25) * TemporalResolveUniforms.MotionRejectionScale);
}

float HistoryColorRejection(float3 currentColor, float3 previousColor, float3 boundedPreviousColor)
{
    float3 currentYCoCg = RgbToYCoCg(currentColor);
    float3 previousYCoCg = RgbToYCoCg(previousColor);
    float3 boundedYCoCg = RgbToYCoCg(boundedPreviousColor);
    float currentMagnitude = max(abs(currentYCoCg.x), 1e-3);
    float tolerance = max(
        currentMagnitude * TemporalResolveUniforms.NeighborhoodClampScale,
        TemporalResolveUniforms.NeighborhoodClampMin) * 2.0;

    float clampDelta = Max3(abs(previousYCoCg - boundedYCoCg));
    float currentDelta = abs(currentYCoCg.x - boundedYCoCg.x);
    float clampRejection = saturate((clampDelta - tolerance) / max(tolerance, 1e-4));
    float colorRejection = saturate((currentDelta - tolerance * 2.0) / max(tolerance * 4.0, 1e-4));

    return max(clampRejection, colorRejection * 0.75);
}

float ResolveHistoryWeight(
    float baseWeight,
    float2 motion,
    float2 size,
    float3 currentColor,
    float3 previousColor,
    float3 boundedPreviousColor)
{
    float rejection = max(
        MotionRejection(motion, size),
        HistoryColorRejection(currentColor, previousColor, boundedPreviousColor) * 0.20);
    return saturate(baseWeight) * (1.0 - rejection);
}

struct TemporalHistorySample
{
    float4 Color;
    float2 Motion;
    float Depth;
};

TemporalHistorySample DecodeTemporalHistory(uint4 value)
{
    TemporalHistorySample result;
    result.Color = float4(UnpackHistoryPair(value.x), UnpackHistoryPair(value.y));
    result.Motion = UnpackHistoryPair(value.z);
    result.Depth = asfloat(value.w);
    return result;
}

TemporalHistorySample MixTemporalHistory(
    TemporalHistorySample a, TemporalHistorySample b, float weight)
{
    TemporalHistorySample result;
    result.Color = lerp(a.Color, b.Color, weight);
    result.Motion = lerp(a.Motion, b.Motion, weight);
    result.Depth = lerp(a.Depth, b.Depth, weight);
    return result;
}

TemporalHistorySample LoadPreviousHistoryBilinear(float2 uv, float2 size, int2 maxPixel)
{
    float2 pixel = uv * size - 0.5;
    int2 p0 = int2(floor(pixel));
    int2 p1 = p0 + int2(1, 1);
    float2 f = frac(pixel);
    TemporalHistorySample h00 = DecodeTemporalHistory(LoadPreviousHistory(clamp(p0, int2(0, 0), maxPixel)));
    TemporalHistorySample h10 = DecodeTemporalHistory(LoadPreviousHistory(clamp(int2(p1.x, p0.y), int2(0, 0), maxPixel)));
    TemporalHistorySample h01 = DecodeTemporalHistory(LoadPreviousHistory(clamp(int2(p0.x, p1.y), int2(0, 0), maxPixel)));
    TemporalHistorySample h11 = DecodeTemporalHistory(LoadPreviousHistory(clamp(p1, int2(0, 0), maxPixel)));
    return MixTemporalHistory(MixTemporalHistory(h00, h10, f.x), MixTemporalHistory(h01, h11, f.x), f.y);
}

float2 LoadDilatedMotionByClosestDepth(int2 pixel, int2 maxPixel, out float closestDepth)
{
    closestDepth = LoadCurrentDepthClamped(pixel, maxPixel);
    float2 closestMotion = MotionVectors.Load(int4(clamp(pixel, int2(0, 0), maxPixel), 0, 0));

    for (int y = -1; y <= 1; y++)
    {
        for (int x = -1; x <= 1; x++)
        {
            int2 samplePixel = pixel + int2(x, y);
            float sampleDepth = LoadCurrentDepthClamped(samplePixel, maxPixel);
            if (sampleDepth < closestDepth)
            {
                closestDepth = sampleDepth;
                closestMotion = MotionVectors.Load(int4(clamp(samplePixel, int2(0, 0), maxPixel), 0, 0));
            }
        }
    }

    return closestMotion;
}

float VelocityDisocclusionRejection(float2 previousMotion, float2 motion, float2 size)
{
    float velocityDeltaPixels = length((previousMotion - motion) * size);
    return saturate((velocityDeltaPixels - 0.25) * 0.08);
}

float DepthDisocclusionRejection(float currentDepth, float previousDepth)
{
    float depthThreshold = max(0.0005, previousDepth * 0.002);
    float disocclusion = currentDepth - previousDepth;
    return saturate((disocclusion - depthThreshold) * 96.0);
}

[shader("pixel")]
float4 PSMain(FullscreenVSOut input) : SV_Target
{
    int2 pixel = int2(input.Position.xy);
    float4 current = SceneColor.Load(int4(pixel, 0, 0));
    if (TemporalResolveUniforms.HistoryWeight <= 0.0)
        return current;

    uint width;
    uint height;
    uint layers;
    SceneColor.GetDimensions(width, height, layers);
    width = max(width, 1u);
    height = max(height, 1u);
    int2 maxPixel = int2((int)width - 1, (int)height - 1);
    float2 size = float2((float)width, (float)height);
    float2 uv = (float2(pixel) + 0.5) / size;

    // MotionVectors store current output UV minus previous history UV.
    // Jitter is applied by the projection that produced SceneColor; history reprojection
    // is owned by motion vectors and must not apply current jitter again.
    // No-history frames return the current color before sampling history.
    float closestDepth;
    float2 motion = LoadDilatedMotionByClosestDepth(pixel, maxPixel, closestDepth);
    // Retain subpixel silhouette coverage, but do not accumulate an old surface in empty background.
    if (closestDepth >= 1.0)
        return current;
    float2 previousUv = uv - motion;
    if (any(previousUv < float2(0.0, 0.0)) || any(previousUv > float2(1.0, 1.0)))
        return StoreTemporalHistory(pixel, current);

    TemporalHistorySample history = LoadPreviousHistoryBilinear(previousUv, size, maxPixel);
    float4 previous = history.Color;
    float3 boundedPrevious = ClipHistoryToCurrentNeighborhood(pixel, maxPixel, current.rgb, previous.rgb);
    float currentDepth = LoadCurrentDepthClamped(pixel, maxPixel);
    float previousDepth = history.Depth;
    float depthRejection = DepthDisocclusionRejection(currentDepth, previousDepth);
    float velocityRejection = VelocityDisocclusionRejection(history.Motion, motion, size);
    float weight = ResolveHistoryWeight(
        TemporalResolveUniforms.HistoryWeight,
        motion,
        size,
        current.rgb,
        previous.rgb,
        boundedPrevious);
    weight *= 1.0 - max(depthRejection, velocityRejection);
    float3 resolved = lerp(current.rgb, boundedPrevious, weight);

    return StoreTemporalHistory(pixel, float4(resolved, current.a));
}
