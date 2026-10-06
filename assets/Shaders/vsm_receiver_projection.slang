#ifndef VSM_RECEIVER_PROJECTION_SLANG
#define VSM_RECEIVER_PROJECTION_SLANG

struct VirtualShadowReceiverProjection
{
    float4 WorldToClipW;
    float ScreenPixelRadius;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

float CalculateVirtualShadowNominalWorldPixelRadius(
    VirtualShadowReceiverProjection projection, float3 worldPosition)
{
    return dot(float4(worldPosition, 1.0), projection.WorldToClipW) * projection.ScreenPixelRadius;
}

#endif
