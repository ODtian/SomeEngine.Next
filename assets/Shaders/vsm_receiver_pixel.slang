#ifndef VSM_RECEIVER_PIXEL_SLANG
#define VSM_RECEIVER_PIXEL_SLANG

struct VirtualShadowReceiverPixel
{
    float3 Position;
    float3 PositionDx;
    float3 PositionDy;
};

float VirtualShadowReceiverDepthStep(
    Texture2DArray<float> depthImage,
    uint3 pixel,
    uint2 screenSize,
    float depth,
    uint2 offset)
{
    float positiveDepth = 1.0;
    float negativeDepth = 1.0;
    if (all(pixel.xy + offset < screenSize))
        positiveDepth = depthImage.Load(int4(pixel.xy + offset, pixel.z, 0));
    if (all(pixel.xy >= offset))
        negativeDepth = depthImage.Load(int4(pixel.xy - offset, pixel.z, 0));
    if (positiveDepth >= 1.0 && negativeDepth >= 1.0)
        return 0.0;
    // The closer foreground neighbor supplies the local plane at silhouettes
    // and depth discontinuities. An edge pixel extends that plane past the viewport.
    if (negativeDepth < 1.0 &&
        (positiveDepth >= 1.0 || abs(depth - negativeDepth) < abs(positiveDepth - depth)))
        return depth - negativeDepth;
    return positiveDepth - depth;
}

VirtualShadowReceiverPixel ReconstructVirtualShadowReceiverPixel(
    Texture2DArray<float> depthImage,
    uint3 pixel,
    uint2 screenSize,
    float depth,
    float4x4 clipToWorld)
{
    float depthDx = VirtualShadowReceiverDepthStep(depthImage, pixel, screenSize, depth, uint2(1u, 0u));
    float depthDy = VirtualShadowReceiverDepthStep(depthImage, pixel, screenSize, depth, uint2(0u, 1u));
    float2 uv = (float2(pixel.xy) + 0.5) / float2(screenSize);
    float4 world = mul(float4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, depth, 1.0), clipToWorld);
    float4 worldDx = world + mul(float4(2.0 / float(screenSize.x), 0.0, depthDx, 0.0), clipToWorld);
    float4 worldDy = world + mul(float4(0.0, -2.0 / float(screenSize.y), depthDy, 0.0), clipToWorld);
    VirtualShadowReceiverPixel result;
    result.Position = world.xyz / max(abs(world.w), 1e-8);
    result.PositionDx = worldDx.xyz / max(abs(worldDx.w), 1e-8);
    result.PositionDy = worldDy.xyz / max(abs(worldDy.w), 1e-8);
    return result;
}

#endif
