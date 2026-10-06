#ifndef SURFACE_RECEIVER_DEPTH_SLANG
#define SURFACE_RECEIVER_DEPTH_SLANG

bool SurfaceReceiverDepthHasCoverage(float depth, float coverage)
{
    return coverage > 0.0 && depth >= 0.0 && depth < 1.0;
}

// The current-to-previous matrix includes both frames' actual AA offsets.
bool TrySurfaceReceiverPreviousPixel(uint2 pixel, uint2 currentSize, float currentDepth,
    float4x4 currentClipToPreviousClip, uint2 previousSize, float4 previousUVBounds, out uint2 previousPixel)
{
    previousPixel = 0u.xx;
    float2 uv = (float2(pixel) + .5) / float2(currentSize);
    float4 clip = mul(float4(uv * float2(2.0,-2.0) + float2(-1.0,1.0), currentDepth, 1.0),
        currentClipToPreviousClip);
    float2 previousUV = clip.xy / clip.w * float2(.5,-.5) + .5;
    if (any(previousUV < previousUVBounds.xy) || any(previousUV > previousUVBounds.zw)) return false;
    previousPixel = min(uint2(previousUV * float2(previousSize)), previousSize - 1u);
    return true;
}

// Users interpret their normal/coverage encoding and supply the decoded result alongside this depth.
bool TryReadSurfaceReceiverPreviousDepth(Texture2DArray<float> previousDepth, uint2 previousPixel, uint layer,
    float coverage, out float depth)
{
    depth = previousDepth.Load(int4(previousPixel, layer, 0));
    return SurfaceReceiverDepthHasCoverage(depth, coverage);
}

#endif
