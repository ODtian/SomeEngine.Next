#ifndef VSM_RECEIVER_PROJECTION_SLANG
#define VSM_RECEIVER_PROJECTION_SLANG

struct CameraDepthOfField
{
    float FocusDistance;
    float InfinityCircleRadiusPixels;
    float MinCircleRadiusPixels;
    float MaxCircleRadiusPixels;
    float DepthBlurRadiusPixels;
    float DepthBlurExponent;
    float ShadowResolutionBiasStrength;
    uint Padding;
};

struct VirtualShadowReceiverProjection
{
    float4 WorldToClipW;
    float ScreenPixelRadius;
    uint Pad0;
    uint Pad1;
    uint Pad2;
    float4 WorldToViewDepth;
    CameraDepthOfField DepthOfField;
    float3 CameraPosition;
    float CotanHalfFieldOfView;
};

float CalculateVirtualShadowNormalBiasLength(VirtualShadowReceiverProjection projection,
    float3 worldPosition, float normalBias, float worldUnitsPerCentimeter)
{
    float distanceToCamera = length(worldPosition - projection.CameraPosition);
    return max(0.02 * worldUnitsPerCentimeter,
        normalBias * distanceToCamera / projection.CotanHalfFieldOfView);
}

float CalculateVirtualShadowNominalWorldPixelRadius(
    VirtualShadowReceiverProjection projection, float3 worldPosition)
{
    return dot(float4(worldPosition, 1.0), projection.WorldToClipW) * projection.ScreenPixelRadius;
}

float CalculateVirtualShadowCircleOfConfusion(CameraDepthOfField optics, float sceneDepth)
{
    float radius = ((sceneDepth - optics.FocusDistance) / sceneDepth) * optics.InfinityCircleRadiusPixels;
    float depthBlur = (1.0 - exp2(-sceneDepth * optics.DepthBlurExponent)) * optics.DepthBlurRadiusPixels;
    return clamp(max(abs(radius), depthBlur) * sign(radius), optics.MinCircleRadiusPixels, optics.MaxCircleRadiusPixels);
}

float CalculateVirtualShadowDepthOfFieldBias(float sceneDepth, float circleRadiusPixels, float strength)
{
    if (sceneDepth < 0.0 || strength == 0.0)
        return 0.0;
    return min(strength, strength * (abs(circleRadiusPixels) / 8.0));
}

float CalculateVirtualShadowReceiverResolutionBias(VirtualShadowReceiverProjection projection, float3 worldPosition)
{
    if (projection.DepthOfField.ShadowResolutionBiasStrength == 0.0)
        return 0.0;
    float sceneDepth = dot(float4(worldPosition, 1.0), projection.WorldToViewDepth);
    if (sceneDepth < 0.0)
        return 0.0;
    return CalculateVirtualShadowDepthOfFieldBias(sceneDepth,
        CalculateVirtualShadowCircleOfConfusion(projection.DepthOfField, sceneDepth),
        projection.DepthOfField.ShadowResolutionBiasStrength);
}

#endif
