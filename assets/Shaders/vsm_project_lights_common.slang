#include "vsm_material.slang"
#include "cluster_view.slang"
#include "render_lighting.slang"
#include "surface_receiver.slang"
#include "vsm_receiver_pixel.slang"
#include "resource_effects.slang"

struct VirtualShadowProjectionUniforms
{
    uint Width;
    uint Height;
    uint ViewCount;
    uint LightLayerCount;
    uint AdditionalReceiverLayerCount;
    uint DispatchWidth;
    uint Pad1;
    uint Pad2;
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
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<float> ProjectionVisibility;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWStructuredBuffer<float> ProjectionAdditionalVisibility;

uint2 VirtualShadowProjectionPixel(uint lane)
{
    return uint2((lane & 1u) | ((lane >> 1u) & 2u) | ((lane >> 2u) & 4u),
        ((lane >> 1u) & 1u) | ((lane >> 2u) & 2u) | ((lane >> 3u) & 4u));
}

float ProjectVirtualShadowReceiver(uint3 pixel, uint light, uint receiverLayer)
{
    uint points = VirtualShadowSampling.PointLightCount;
    uint spots = VirtualShadowSampling.SpotLightCount;
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
    if (light >= VirtualShadowSampling.DirectionalLightCount) return 1.0;
#else
    if (light >= points + spots) return 1.0;
#endif
    uint receiverIndex = (pixel.z * Projection.Height + pixel.y) * Projection.Width + pixel.x;
    SurfaceReceiver receiver = receiverLayer == 0u ? ProjectionReceivers[receiverIndex] :
        ProjectionAdditionalReceivers[(receiverLayer - 1u) * Projection.ViewCount * Projection.Width * Projection.Height + receiverIndex];
    if (!SurfaceReceiverHasLighting(receiver)) return 1.0;
    float depth = receiverLayer == 0u && (receiver.Flags & SURFACE_RECEIVER_HAIR) == 0u
        ? ProjectionDepth.Load(int4(pixel, 0)) : receiver.Depth;
    if (depth < 0.0 || depth >= 1.0) return 1.0;
    ClusterViewUniforms view = ProjectionViews[pixel.z];
    VirtualShadowReceiverPixel footprint = ReconstructVirtualShadowReceiverPixel(ProjectionDepth, pixel,
        uint2(Projection.Width, Projection.Height), depth, view.ClipToWorld);
    float3 position = footprint.Position;
    float3 normal = receiver.ShadingNormal;
    float3 towardCamera = view.CameraPos - position;
    if (dot(normal, towardCamera) <= 0.0) return 1.0;
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
    uint sourceLight = light;
#else
    uint sourceLight = light < points ? VirtualShadowSampling.PointLightBufferOffset + light
        : VirtualShadowSampling.SpotLightBufferOffset + light - points;
#endif
    GPULight source = LightBuffer[sourceLight];
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
    float3 towardLight = -source.Direction;
#else
    float3 towardLight = source.Position - position;
#endif
    if (dot(normal, towardLight) <= 0.0) return 1.0;
#if !VSM_PROJECT_DIRECTIONAL_LIGHTS
        float distanceSquared = dot(towardLight, towardLight);
        if (source.Range <= 0.0 || distanceSquared >= source.Range * source.Range) return 1.0;
        if (light >= points && dot(source.Direction, source.Direction) > 1e-8 && source.OuterConeCos > 0.0 &&
            dot(-towardLight * rsqrt(max(distanceSquared, 1e-8)), source.Direction) <= source.OuterConeCos) return 1.0;
        ClusteredLightGridParameters lighting = LightingViews[pixel.z];
        float viewDepth = max(-mul(float4(position, 1.0), lighting.View).z, lighting.ZParams.x);
        uint cell = ClusteredLightCellIndex(lighting, pixel.z, pixel.xy, viewDepth);
        uint count = min(ClusteredLightGrid[cell].y, lighting.MaxLightsPerCluster);
        bool included = false;
        for (uint index = 0u; index < count; index++)
            included = included || LightIndexList[cell * lighting.MaxLightsPerCluster + index] == sourceLight;
        if (!included) return 1.0;
#endif
    float bias = CalculateVirtualShadowNormalBiasLength(VirtualShadowSampling.ReceiverProjection,
        position, VirtualShadowSampling.NormalBias, VirtualShadowViews[0].WorldUnitsPerCentimeter);
    position += normal * bias;
#if VSM_PROJECT_DIRECTIONAL_LIGHTS
        uint firstView = light * VirtualShadowSampling.DirectionalClipmapLevels;
        uint levels = min(VirtualShadowSampling.DirectionalClipmapLevels, VirtualShadowSampling.ViewCount - firstView);
        VirtualShadowProjectionResult result = TraceVirtualShadowDirectional(VirtualShadowViews, VirtualShadowPageTable,
            VirtualShadowAtlas, firstView, levels, position, normal, VirtualShadowDynamicResolutionBias[0].x,
            CalculateVirtualShadowReceiverResolutionBias(VirtualShadowSampling.ReceiverProjection, footprint.Position),
            VirtualShadowSampling.DirectionalRays, pixel.xy, VirtualShadowSampling.FrameIndex,
            length(position - VirtualShadowSampling.ReceiverProjection.CameraPosition), 0.0,
            (receiver.Flags & SURFACE_RECEIVER_FIRST_PERSON) != 0u,
            VirtualShadowSampling.FirstPersonViewOffset < VirtualShadowSampling.ViewCount
                ? VirtualShadowSampling.FirstPersonViewOffset + light * VirtualShadowViews[VirtualShadowSampling.FirstPersonViewOffset].ClipmapLevelCount
                : 0xffffffffu);
        return result.Visibility * SampleDirectionalVirtualShadowPrefiltered(VirtualShadowViews, VirtualShadowPageTable,
            VirtualShadowPrefilteredAtlas, firstView, levels, position);
#else
    uint firstView = light < points ? VirtualShadowSampling.PointViewOffset + light * 6u
        : VirtualShadowSampling.SpotViewOffset + light - points;
    return TraceVirtualShadowLocal(VirtualShadowViews, VirtualShadowPageTable, VirtualShadowAtlas,
        firstView, position, normal, CalculateVirtualShadowNominalWorldPixelRadius(
            VirtualShadowSampling.ReceiverProjection, footprint.Position), VirtualShadowDynamicResolutionBias[0].y,
        VirtualShadowSampling.LocalRays, pixel.xy, VirtualShadowSampling.FrameIndex, 0.0).Visibility;
#endif
}

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
    uint groupIndex = group.y * Projection.DispatchWidth + group.x;
    uint tile = groupIndex % (tileCountX * tileCountY);
    uint lightReceiver = groupIndex / (tileCountX * tileCountY);
    uint layerSize = Projection.LightLayerCount * Projection.ViewCount;
    if (lightReceiver >= layerSize * (1u + Projection.AdditionalReceiverLayerCount)) return;
    uint receiverLayer = lightReceiver / layerSize;
    uint lightView = lightReceiver % layerSize;
    uint3 pixel = uint3(uint2(tile % tileCountX, tile / tileCountX) * 8u + VirtualShadowProjectionPixel(lane),
        lightView % Projection.ViewCount);
    if (pixel.x >= Projection.Width || pixel.y >= Projection.Height) return;
    float visibility = ProjectVirtualShadowReceiver(pixel, lightView / Projection.ViewCount, receiverLayer);
    uint pixelIndex = (lightView * Projection.Height + pixel.y) * Projection.Width + pixel.x;
    if (receiverLayer == 0u) ProjectionVisibility[pixelIndex] = visibility;
    else ProjectionAdditionalVisibility[(receiverLayer - 1u) * layerSize * Projection.Width * Projection.Height + pixelIndex] = visibility;
    if (Projection.AdditionalReceiverLayerCount == 0u && all(pixel == 0u.xxx) && lightView == 0u)
        ProjectionAdditionalVisibility[0] = 1.0;
}
