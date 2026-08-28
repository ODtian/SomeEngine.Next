#ifndef RENDER_LIGHTING_SLANG
#define RENDER_LIGHTING_SLANG

struct GPULight
{
    float3 Position;
    float Range;
    float3 Direction;
    float InnerConeCos;
    float3 Color;
    float Intensity;
    float OuterConeCos;
    uint LayerMask;
    int CookieIndex;
    float CookieStrength;
    float4x4 WorldToLightCookie;
    float4 CookieScaleOffset;
};

struct ClusteredLightGridParameters
{
    float4x4 View;
    uint TileSizeX;
    uint TileSizeY;
    uint TileCountX;
    uint TileCountY;
    float4 ZParams;
    uint DepthSliceCount;
    uint MaxLightsPerCluster;
    uint DirectionalCount;
    uint NonDirectionalCount;
    float P00;
    float P11;
    uint ScreenWidth;
    uint ScreenHeight;
};

uint ClusteredLightDepthSlice(
    ClusteredLightGridParameters parameters,
    float viewDepth)
{
    float slice = log2(max(viewDepth, parameters.ZParams.x)) *
        parameters.ZParams.z + parameters.ZParams.w;
    return (uint)clamp(
        floor(slice),
        0.0,
        float(parameters.DepthSliceCount - 1u));
}

uint ClusteredLightCellIndex(
    ClusteredLightGridParameters parameters,
    uint2 pixelCoord,
    float viewDepth)
{
    uint2 tileCoord = pixelCoord / uint2(parameters.TileSizeX, parameters.TileSizeY);
    uint sliceIndex = ClusteredLightDepthSlice(parameters, viewDepth);
    return (sliceIndex * parameters.TileCountY + tileCoord.y) *
        parameters.TileCountX + tileCoord.x;
}

#endif
