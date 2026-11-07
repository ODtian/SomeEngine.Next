#ifndef SOMEENGINE_CLUSTER_INSTANCE_PROPERTIES_INCLUDED
#define SOMEENGINE_CLUSTER_INSTANCE_PROPERTIES_INCLUDED

#include "render_instance_transform.slang"
#include "render_instance_properties.slang"
#include "generated/render_instance_layout.slang"
#include "cluster_view.slang"

static const uint CLUSTER_UINT32_PROPERTY_STRIDE = 4u;
static const uint CLUSTER_FLOAT32_PROPERTY_STRIDE = 4u;

uint LoadClusterBvhRoot(ByteAddressBuffer data, uint metadata, uint instanceSlot)
{
    return RenderInstanceLoad<uint>(
        data,
        metadata,
        instanceSlot,
        CLUSTER_UINT32_PROPERTY_STRIDE);
}

uint LoadClusterMaterialSlotOffset(ByteAddressBuffer data, uint metadata, uint instanceSlot)
{
    return RenderInstanceLoad<uint>(
        data,
        metadata,
        instanceSlot,
        CLUSTER_UINT32_PROPERTY_STRIDE);
}

bool HasFiniteClusterInstanceBounds(StructuredBuffer<uint> bounds, ByteAddressBuffer data,
    RenderInstancePropertyMetadata metadata, uint instanceID)
{
    uint offset = LoadClusterMaterialSlotOffset(data,
        LoadRenderInstanceMetadata(metadata, CLUSTER_MATERIAL_SLOT_OFFSET_METADATA_WORD), instanceID);
    return bounds[offset] != 0u;
}

ClusterViewUniforms ApplyClusterInstanceBounds(StructuredBuffer<uint> bounds, ByteAddressBuffer data,
    RenderInstancePropertyMetadata metadata, uint instanceID, ClusterViewUniforms view)
{
    view.CullingEnabled = view.CullingEnabled != 0u &&
        HasFiniteClusterInstanceBounds(bounds, data, metadata, instanceID) ? 1u : 0u;
    return view;
}

float LoadClusterBoundsExpansion(ByteAddressBuffer data, uint metadata, uint instanceSlot)
{
    return RenderInstanceLoad<float>(
        data,
        metadata,
        instanceSlot,
        CLUSTER_FLOAT32_PROPERTY_STRIDE);
}

uint LoadInstanceDepthCacheClass(ByteAddressBuffer data, RenderInstancePropertyMetadata metadata, uint instanceId)
{
    return RenderInstanceLoad<uint>(data, LoadRenderInstanceMetadata(metadata, RENDER_DEPTH_CACHE_CLASS_METADATA_WORD),
        instanceId, CLUSTER_UINT32_PROPERTY_STRIDE);
}

struct InstanceXray
{
    float4 Visible;
    float4 Occluded;
    float MinimumOcclusion, MaximumOcclusion;
    uint Enabled, Padding;
};

InstanceXray LoadInstanceXray(ByteAddressBuffer data, RenderInstancePropertyMetadata metadata, uint instanceId)
{
    return RenderInstanceLoad<InstanceXray>(data, LoadRenderInstanceMetadata(metadata, RENDER_XRAY_METADATA_WORD),
        instanceId, 48u);
}

bool KeepBlendOcclusion(ByteAddressBuffer data, RenderInstancePropertyMetadata metadata,
    uint instanceId, ClusterViewUniforms view)
{
    uint ordered = RenderInstanceLoad<uint>(data,
        LoadRenderInstanceMetadata(metadata, CLUSTER_BLEND_OCCLUSION_METADATA_WORD), instanceId, 4u);
    return view.TargetPolicy == 0u &&
        (ordered != 0u || LoadInstanceXray(data, metadata, instanceId).Enabled != 0u);
}

#endif
