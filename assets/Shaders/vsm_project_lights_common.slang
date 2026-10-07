#include "vsm_material.slang"
#include "vsm_soft_sampling.slang"
#include "cluster_view.slang"
#include "render_lighting.slang"
#include "surface_receiver.slang"
#include "vsm_receiver_pixel.slang"
#include "vsm_projected_pixel.slang"
#include "resource_effects.slang"

struct VirtualShadowProjectionUniforms
{
    uint Width, Height, ViewCount, LightLayerCount;
    uint AdditionalReceiverLayerCount, DispatchDepth, LightLayerRowCount, Pad2;
};

ConstantBuffer<VirtualShadowProjectionUniforms> Projection;
StructuredBuffer<ClusterViewUniforms> ProjectionViews;
Texture2DArray<float> ProjectionDepth;
StructuredBuffer<SurfaceReceiver> ProjectionReceivers;
StructuredBuffer<SurfaceReceiver> ProjectionAdditionalReceivers;
StructuredBuffer<ClusteredLightGridParameters> LightingViews;
StructuredBuffer<GPULight> LightBuffer;
StructuredBuffer<uint2> ClusteredLightGrid;
StructuredBuffer<uint> LightIndexList;
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<float> ProjectionVisibility;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<float> ProjectionAdditionalVisibility;
#else
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> ProjectionVisibility;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<uint4> ProjectionAdditionalVisibility;
#endif

struct VirtualShadowProjectionReceiver
{
    bool Valid;
    float3 Position;
    float3 ShadowPosition;
    float3 Normal;
    float NominalPixelRadius;
    float ResolutionBias;
    uint Flags;
};

VirtualShadowProjectionReceiver ReadVirtualShadowProjectionReceiver(uint3 pixel, uint receiverLayer)
{
    VirtualShadowProjectionReceiver result = (VirtualShadowProjectionReceiver)0;
    uint index = (pixel.z * Projection.Height + pixel.y) * Projection.Width + pixel.x;
    SurfaceReceiver receiver = receiverLayer == 0u ? ProjectionReceivers[index] :
        ProjectionAdditionalReceivers[(receiverLayer - 1u) * Projection.ViewCount * Projection.Width * Projection.Height + index];
    if (!SurfaceReceiverHasLighting(receiver)) return result;
    float depth = receiverLayer == 0u ? ProjectionDepth.Load(int4(pixel, 0)) : receiver.Depth;
    if (depth < 0.0 || depth >= 1.0) return result;
    ClusterViewUniforms view = ProjectionViews[pixel.z];
    VirtualShadowReceiverPixel footprint = ReconstructVirtualShadowReceiverPixel(ProjectionDepth, pixel,
        uint2(Projection.Width, Projection.Height), depth, view.ClipToWorld);
    result.Position = footprint.Position;
    result.Normal = receiver.ShadingNormal;
    if (dot(result.Normal, view.CameraPos - result.Position) <= 0.0) return result;
    float bias = CalculateVirtualShadowNormalBiasLength(VirtualShadowSampling.ReceiverProjection,
        result.Position, VirtualShadowSampling.NormalBias, VirtualShadowViews[0].WorldUnitsPerCentimeter);
    result.ShadowPosition = result.Position + result.Normal * bias;
    result.NominalPixelRadius = CalculateVirtualShadowNominalWorldPixelRadius(
        VirtualShadowSampling.ReceiverProjection, result.Position);
    result.ResolutionBias = CalculateVirtualShadowReceiverResolutionBias(
        VirtualShadowSampling.ReceiverProjection, result.Position);
    result.Flags = receiver.Flags;
    result.Valid = true;
    return result;
}

#if VSM_PROJECT_DIRECTIONAL_LIGHTS
float ProjectVirtualShadowReceiver(uint3 pixel, uint light, VirtualShadowProjectionReceiver receiver)
{
    if (!receiver.Valid || light >= VirtualShadowSampling.DirectionalLightCount) return 1.0;
    GPULight source = LightBuffer[light];
    if (dot(receiver.Normal, -source.Direction) <= 0.0) return 1.0;
    uint firstView = light * VirtualShadowSampling.DirectionalClipmapLevels;
    uint levels = min(VirtualShadowSampling.DirectionalClipmapLevels, VirtualShadowSampling.ViewCount - firstView);
    VirtualShadowProjectionResult result = SampleVirtualShadowDirectionalSoft(VirtualShadowViews, VirtualShadowPageTable,
        VirtualShadowAtlas, firstView, levels, receiver.ShadowPosition, receiver.Normal,
        VirtualShadowDynamicResolutionBias[0].x, receiver.ResolutionBias,
        VirtualShadowSampling.BlockerSampleCount, VirtualShadowSampling.FilterSampleCount,
        VirtualShadowSampling.MaxFilterRadiusTexels, pixel.xy, VirtualShadowSampling.FrameIndex,
        (receiver.Flags & SURFACE_RECEIVER_FIRST_PERSON) != 0u,
        VirtualShadowSampling.FirstPersonViewOffset < VirtualShadowSampling.ViewCount
            ? VirtualShadowSampling.FirstPersonViewOffset + light * VirtualShadowViews[VirtualShadowSampling.FirstPersonViewOffset].ClipmapLevelCount
            : 0xffffffffu);
    return result.Visibility * SampleDirectionalVirtualShadowPrefiltered(VirtualShadowViews, VirtualShadowPageTable,
        VirtualShadowPrefilteredAtlas, firstView, levels, receiver.ShadowPosition);
}
#else
uint VirtualShadowLocalLightIndex(uint sourceLight)
{
    if (sourceLight >= VirtualShadowSampling.PointLightBufferOffset &&
        sourceLight < VirtualShadowSampling.PointLightBufferOffset + VirtualShadowSampling.PointLightCount)
        return sourceLight - VirtualShadowSampling.PointLightBufferOffset;
    if (sourceLight >= VirtualShadowSampling.SpotLightBufferOffset &&
        sourceLight < VirtualShadowSampling.SpotLightBufferOffset + VirtualShadowSampling.SpotLightCount)
        return VirtualShadowSampling.PointLightCount + sourceLight - VirtualShadowSampling.SpotLightBufferOffset;
    return 0xffffffffu;
}

float ProjectVirtualShadowLocalReceiver(uint3 pixel, uint light, uint sourceLight,
    VirtualShadowProjectionReceiver receiver)
{
    GPULight source = LightBuffer[sourceLight];
    float3 towardLight = source.Position - receiver.Position;
    float distanceSquared = dot(towardLight, towardLight);
    if (source.Range <= 0.0 || distanceSquared >= source.Range * source.Range || dot(receiver.Normal, towardLight) <= 0.0)
        return 1.0;
    uint points = VirtualShadowSampling.PointLightCount;
    if (light >= points && dot(source.Direction, source.Direction) > 1e-8 && source.OuterConeCos > 0.0 &&
        dot(-towardLight * rsqrt(max(distanceSquared, 1e-8)), source.Direction) <= source.OuterConeCos) return 1.0;
    uint firstView = light < points ? VirtualShadowSampling.PointViewOffset + light * 6u :
        VirtualShadowSampling.SpotViewOffset + light - points;
    return SampleVirtualShadowLocalSoft(VirtualShadowViews, VirtualShadowPageTable, VirtualShadowAtlas,
        firstView, receiver.ShadowPosition, receiver.Normal, receiver.NominalPixelRadius,
        VirtualShadowDynamicResolutionBias[0].y, VirtualShadowSampling.BlockerSampleCount,
        VirtualShadowSampling.FilterSampleCount, VirtualShadowSampling.MaxFilterRadiusTexels,
        pixel.xy, VirtualShadowSampling.FrameIndex).Visibility;
}

uint4 PackVirtualShadowLocalVisibility(uint4 mask, uint light, float visibility, float noise)
{
    const float noiseScale = 1.0 / 15.0;
    if (visibility > noiseScale * .25 && visibility < 1.0)
        visibility = saturate(visibility + (noise - .5) * noiseScale);
    uint value = uint(round(saturate(visibility) * 15.0));
    uint word = (light & 31u) >> 3u;
    uint shift = (light & 7u) * 4u;
    mask[word] = (mask[word] & ~(15u << shift)) | (value << shift);
    return mask;
}

void WriteVirtualShadowLocalMask(uint index, uint receiverLayer, uint layerValues, uint4 mask)
{
    if (receiverLayer == 0u) ProjectionVisibility[index] = mask;
    else ProjectionAdditionalVisibility[(receiverLayer - 1u) * layerValues + index] = mask;
}

uint4 ReadVirtualShadowLocalMask(uint index, uint receiverLayer, uint layerValues)
{
    return receiverLayer == 0u ? ProjectionVisibility[index] :
        ProjectionAdditionalVisibility[(receiverLayer - 1u) * layerValues + index];
}
#endif

[shader("compute")]
[numthreads(64, 1, 1)]
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
void ProjectVirtualShadowDirectionalLights(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
#else
void ProjectVirtualShadowLocalLights(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
#endif
{
    uint tileCountX = (Projection.Width + 7u) / 8u;
    uint tileCountY = (Projection.Height + 7u) / 8u;
    uint2 tile = group.xy;
    uint lightReceiver = group.z;
    if (Projection.LightLayerRowCount > 1u)
    {
        lightReceiver += (group.y / tileCountY) * Projection.DispatchDepth;
        tile.y = group.y % tileCountY;
    }
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
    uint layerSize = Projection.LightLayerCount * Projection.ViewCount;
#else
    uint layerSize = Projection.ViewCount;
#endif
    if (lightReceiver >= layerSize * (1u + Projection.AdditionalReceiverLayerCount)) return;
    uint receiverLayer = lightReceiver / layerSize;
    uint lightView = lightReceiver % layerSize;
    uint3 pixel = uint3(tile * 8u + DecodeVirtualShadowProjectionPixel(lane), lightView % Projection.ViewCount);
    uint tilePixels = tileCountX * tileCountY * 64u;
    uint pixelIndex = (tile.y * tileCountX + tile.x) * 64u + lane;
    VirtualShadowProjectionReceiver receiver = (VirtualShadowProjectionReceiver)0;
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
    bool hasLights = VirtualShadowSampling.DirectionalLightCount != 0u;
#else
    bool hasLights = VirtualShadowSampling.PointLightCount + VirtualShadowSampling.SpotLightCount != 0u;
#endif
    if (hasLights && pixel.x < Projection.Width && pixel.y < Projection.Height)
        receiver = ReadVirtualShadowProjectionReceiver(pixel, receiverLayer);
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
    float visibility = ProjectVirtualShadowReceiver(pixel, lightView / Projection.ViewCount, receiver);
    uint index = lightView * tilePixels + pixelIndex;
    if (receiverLayer == 0u) ProjectionVisibility[index] = visibility;
    else ProjectionAdditionalVisibility[(receiverLayer - 1u) * layerSize * tilePixels + index] = visibility;
    if (Projection.AdditionalReceiverLayerCount == 0u && lightView == 0u && pixelIndex == 0u)
        ProjectionAdditionalVisibility[0] = 1.0;
#else
    uint localCount = 0u, firstLight = 0u;
    if (receiver.Valid)
    {
        ClusteredLightGridParameters lighting = LightingViews[pixel.z];
        float viewDepth = max(-mul(float4(receiver.Position, 1.0), lighting.View).z, lighting.ZParams.x);
        uint cell = ClusteredLightCellIndex(lighting, pixel.z, pixel.xy, viewDepth);
        localCount = min(ClusteredLightGrid[cell].y, lighting.MaxLightsPerCluster);
        firstLight = cell * lighting.MaxLightsPerCluster;
    }
    float noise = VirtualShadowRayStepOffset(pixel.xy, VirtualShadowSampling.FrameIndex);
    uint packedGroups = (Projection.LightLayerCount + 31u) / 32u;
    uint layerValues = packedGroups * Projection.ViewCount * tilePixels;
    uint viewPixel = pixel.z * tilePixels + pixelIndex;
    if (packedGroups == 1u)
    {
        uint4 mask = 0xffffffffu.xxxx;
        for (uint index = 0u; index < localCount; index++)
        {
            uint sourceLight = LightIndexList[firstLight + index];
            uint light = VirtualShadowLocalLightIndex(sourceLight);
            if (light == 0xffffffffu) continue;
            float visibility = ProjectVirtualShadowLocalReceiver(pixel, light, sourceLight, receiver);
            mask = PackVirtualShadowLocalVisibility(mask, light, visibility, noise);
        }
        WriteVirtualShadowLocalMask(viewPixel, receiverLayer, layerValues, mask);
    }
    else
    {
        // Each pixel initializes the words it later updates in this same pass.
        for (uint groupIndex = 0u; groupIndex < packedGroups; groupIndex++)
            WriteVirtualShadowLocalMask(groupIndex * Projection.ViewCount * tilePixels + viewPixel,
                receiverLayer, layerValues, 0xffffffffu.xxxx);
        for (uint index = 0u; index < localCount; index++)
        {
            uint sourceLight = LightIndexList[firstLight + index];
            uint light = VirtualShadowLocalLightIndex(sourceLight);
            if (light == 0xffffffffu) continue;
            float visibility = ProjectVirtualShadowLocalReceiver(pixel, light, sourceLight, receiver);
            uint maskIndex = (light >> 5u) * Projection.ViewCount * tilePixels + viewPixel;
            uint4 mask = ReadVirtualShadowLocalMask(maskIndex, receiverLayer, layerValues);
            mask = PackVirtualShadowLocalVisibility(mask, light, visibility, noise);
            WriteVirtualShadowLocalMask(maskIndex, receiverLayer, layerValues, mask);
        }
    }
    if (Projection.AdditionalReceiverLayerCount == 0u && pixel.z == 0u && pixelIndex == 0u)
        ProjectionAdditionalVisibility[0] = 0xffffffffu.xxxx;
#endif
}
