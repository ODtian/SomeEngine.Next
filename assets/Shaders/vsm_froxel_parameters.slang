#ifndef VSM_FROXEL_PARAMETERS_SLANG
#define VSM_FROXEL_PARAMETERS_SLANG

struct VirtualShadowFroxelParameters
{
    float4x4 ViewToWorld;
    float4 ViewTileScaleBias;
    float Near;
    float Radius;
    float DepthScale1;
    float ClipToViewScale;
    float ClipToViewBias;
    uint Orthographic;
    uint Width;
    uint Height;
    uint AdditionalReceiverLayerCount;
    uint Enabled;
    uint HighQualityFrontLayerTranslucency;
    uint Water;
};

static const int VSM_FROXEL_INVALID_SLICE = 1 << 28;

int VirtualShadowFroxelSlice(VirtualShadowFroxelParameters parameters, float depth)
{
    if (depth >= 1.0) return VSM_FROXEL_INVALID_SLICE;
    float viewDepth = depth * parameters.ClipToViewScale + parameters.ClipToViewBias;
    if (parameters.Orthographic != 0u)
        return int(floor((viewDepth - parameters.Near) / parameters.Radius));
    return int(floor(log2(1.0 / (viewDepth * parameters.Near)) / log2(parameters.DepthScale1)));
}

float VirtualShadowFroxelDepth(VirtualShadowFroxelParameters parameters, float slice)
{
    return parameters.Orthographic != 0u ? parameters.Near + slice * parameters.Radius
        : parameters.Near * pow(parameters.DepthScale1, slice);
}

float3 VirtualShadowFroxelCenter(VirtualShadowFroxelParameters parameters, uint2 tile, int slice)
{
    float depth = VirtualShadowFroxelDepth(parameters, float(slice) + .5);
    float2 xy = (float2(tile) + .5) * parameters.ViewTileScaleBias.xy + parameters.ViewTileScaleBias.zw;
    return float3(parameters.Orthographic != 0u ? xy : xy * depth, -depth);
}

void VirtualShadowFroxelViewBounds(VirtualShadowFroxelParameters parameters, uint2 tile, int slice,
    out float3 minimum, out float3 maximum)
{
    float2 nearXY = float2(tile) * parameters.ViewTileScaleBias.xy + parameters.ViewTileScaleBias.zw;
    float2 farXY = nearXY + parameters.ViewTileScaleBias.xy;
    float nearDepth = VirtualShadowFroxelDepth(parameters, float(slice));
    if (parameters.Orthographic != 0u)
    {
        minimum = float3(min(nearXY, farXY), -nearDepth - parameters.Radius);
        maximum = float3(max(nearXY, farXY), -nearDepth);
        return;
    }
    if (abs(nearXY.x) > abs(farXY.x)) { float value = nearXY.x; nearXY.x = farXY.x; farXY.x = value; }
    if (abs(nearXY.y) > abs(farXY.y)) { float value = nearXY.y; nearXY.y = farXY.y; farXY.y = value; }
    float farDepth = nearDepth * parameters.DepthScale1;
    float2 a = nearXY * nearDepth;
    float2 b = farXY * farDepth;
    minimum = float3(min(a, b), -farDepth);
    maximum = float3(max(a, b), -nearDepth);
}

#endif
