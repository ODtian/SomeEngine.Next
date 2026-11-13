#ifndef VSM_VISIBILITY_REQUESTS_SLANG
#define VSM_VISIBILITY_REQUESTS_SLANG

__exported import vsm_page_requests;
__exported import vsm_receiver_pixel;
__exported import surface_receiver;

float3 ReconstructVirtualShadowVisibilityPosition(uint3 pixel, uint2 screenSize,
    float depth, float4x4 clipToWorld)
{
    float2 uv = (float2(pixel.xy) + 0.5) / float2(screenSize);
    float4 world = mul(float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), depth, 1.0), clipToWorld);
    return world.xyz / world.w;
}

SurfaceReceiver ReconstructVirtualShadowOpaqueReceiver(Texture2DArray<float> depthImage,
    uint3 pixel, uint2 screenSize, float4x4 clipToWorld, bool firstPerson)
{
    SurfaceReceiver receiver = {};
    float depth = depthImage.Load(int4(pixel, 0));
    receiver.Depth = depth;
    if (depth >= 1.0) return receiver;
    float depthDx = VirtualShadowReceiverDepthStep(depthImage, pixel, screenSize, depth, uint2(1u, 0u));
    float depthDy = VirtualShadowReceiverDepthStep(depthImage, pixel, screenSize, depth, uint2(0u, 1u));
    receiver.Position = ReconstructVirtualShadowVisibilityPosition(pixel, screenSize, depth, clipToWorld);
    float3 positionDx = ReconstructVirtualShadowVisibilityPosition(pixel + uint3(1u, 0u, 0u),
        screenSize, depth + depthDx, clipToWorld);
    float3 positionDy = ReconstructVirtualShadowVisibilityPosition(pixel + uint3(0u, 1u, 0u),
        screenSize, depth + depthDy, clipToWorld);
    receiver.GeometricNormal = normalize(cross(positionDy - receiver.Position, positionDx - receiver.Position));
    receiver.ShadingNormal = receiver.GeometricNormal;
    receiver.Flags = SURFACE_RECEIVER_PRESENT | SURFACE_RECEIVER_LIGHTING |
        (firstPerson ? SURFACE_RECEIVER_FIRST_PERSON : 0u);
    return receiver;
}

void RequestVirtualShadowOpaqueVisibility(Texture2DArray<float> depthImage, uint3 pixel,
    uint2 screenSize, float4x4 clipToWorld, bool firstPerson, StructuredBuffer<uint> firstShadowViewIndices,
    float3 worldTranslation = float3(0))
{
    SurfaceReceiver receiver = ReconstructVirtualShadowOpaqueReceiver(depthImage, pixel, screenSize, clipToWorld, firstPerson);
    receiver.Position -= worldTranslation;
    MarkVirtualShadowPages<VirtualShadowGlobalPageRequests>(receiver, pixel.z, firstShadowViewIndices, PageDemand.CullBackfacingPixels != 0u);
}

#endif
