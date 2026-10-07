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
    uint LightCapacity;
    uint AdditionalReceiverLayerCount;
    uint Pad0;
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
RWTexture2DArray<float> ProjectionVisibility;
[ResourceEffect(ResourceEffects.Write, ResourceQualifiers.None)]
RWTexture2DArray<float> ProjectionAdditionalVisibility;

uint2 VirtualShadowProjectionPixel(uint lane)
{
    return uint2((lane & 1u) | ((lane >> 1u) & 2u) | ((lane >> 2u) & 4u),
        ((lane >> 1u) & 1u) | ((lane >> 2u) & 2u) | ((lane >> 3u) & 4u));
}

float ProjectVirtualShadowReceiver(uint3 pixel, uint light, uint receiverLayer)
{
    uint directional = VirtualShadowSampling.DirectionalLightCount;
    uint points = VirtualShadowSampling.PointLightCount;
    uint spots = VirtualShadowSampling.SpotLightCount;
    if (light >= directional + points + spots) return 1.0;
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
    uint sourceLight = light < directional ? light : light < directional + points
        ? VirtualShadowSampling.PointLightBufferOffset + light - directional
        : VirtualShadowSampling.SpotLightBufferOffset + light - directional - points;
    GPULight source = LightBuffer[sourceLight];
    float3 towardLight = light < directional ? -source.Direction : source.Position - position;
    if (dot(normal, towardLight) <= 0.0) return 1.0;
    if (light >= directional)
    {
        float distanceSquared = dot(towardLight, towardLight);
        if (source.Range <= 0.0 || distanceSquared >= source.Range * source.Range) return 1.0;
        if (light >= directional + points && dot(source.Direction, source.Direction) > 1e-8 && source.OuterConeCos > 0.0 &&
            dot(-towardLight * rsqrt(max(distanceSquared, 1e-8)), source.Direction) <= source.OuterConeCos) return 1.0;
        ClusteredLightGridParameters lighting = LightingViews[pixel.z];
        float viewDepth = max(-mul(float4(position, 1.0), lighting.View).z, lighting.ZParams.x);
        uint cell = ClusteredLightCellIndex(lighting, pixel.z, pixel.xy, viewDepth);
        uint count = min(ClusteredLightGrid[cell].y, lighting.MaxLightsPerCluster);
        bool included = false;
        for (uint index = 0u; index < count; index++)
            included = included || LightIndexList[cell * lighting.MaxLightsPerCluster + index] == sourceLight;
        if (!included) return 1.0;
    }
    float bias = CalculateVirtualShadowNormalBiasLength(VirtualShadowSampling.ReceiverProjection,
        position, VirtualShadowSampling.NormalBias, VirtualShadowViews[0].WorldUnitsPerCentimeter);
    position += normal * bias;
    if (light < directional)
    {
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
    }
    uint firstView = light < directional + points
        ? VirtualShadowSampling.PointViewOffset + (light - directional) * 6u
        : VirtualShadowSampling.SpotViewOffset + light - directional - points;
    return TraceVirtualShadowLocal(VirtualShadowViews, VirtualShadowPageTable, VirtualShadowAtlas,
        firstView, position, normal, CalculateVirtualShadowNominalWorldPixelRadius(
            VirtualShadowSampling.ReceiverProjection, footprint.Position), VirtualShadowDynamicResolutionBias[0].y,
        VirtualShadowSampling.LocalRays, pixel.xy, VirtualShadowSampling.FrameIndex, 0.0).Visibility;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ProjectVirtualShadowLights(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint layerSize = Projection.LightCapacity * Projection.ViewCount;
    uint receiverLayer = group.z / layerSize;
    uint lightView = group.z % layerSize;
    uint3 pixel = uint3(group.xy * 8u + VirtualShadowProjectionPixel(lane), lightView % Projection.ViewCount);
    if (pixel.x >= Projection.Width || pixel.y >= Projection.Height) return;
    float visibility = ProjectVirtualShadowReceiver(pixel, lightView / Projection.ViewCount, receiverLayer);
    if (receiverLayer == 0u) ProjectionVisibility[uint3(pixel.xy, lightView)] = visibility;
    else ProjectionAdditionalVisibility[uint3(pixel.xy, (receiverLayer - 1u) * layerSize + lightView)] = visibility;
    if (Projection.AdditionalReceiverLayerCount == 0u && all(pixel == 0u.xxx) && lightView == 0u)
        ProjectionAdditionalVisibility[uint3(0,0,0)] = 1.0;
}
