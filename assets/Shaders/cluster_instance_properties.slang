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

uint2 LoadInstanceFirstPerson(ByteAddressBuffer data, RenderInstancePropertyMetadata metadata, uint instanceId)
{
    return RenderInstanceLoad<uint2>(data, LoadRenderInstanceMetadata(metadata, RENDER_FIRST_PERSON_METADATA_WORD),
        instanceId, 8u);
}

bool IsInstanceVisibleToView(uint2 firstPerson, ClusterViewUniforms view)
{
    uint kind = firstPerson.x & 3u;
    uint visibility = firstPerson.x >> 2u;
    bool player = firstPerson.y == view.PlayerId;
    if (view.Family == 2u) return kind == 2u && player;
    if (kind == 1u && view.Family != 0u) return false;
    if (visibility == 1u && !player) return false;
    if ((visibility == 2u || kind == 2u) && player) return false;
    return true;
}

float3 TransformFirstPersonWorldPosition(ByteAddressBuffer data, RenderInstancePropertyMetadata metadata,
    uint instanceId, ClusterViewUniforms view, float3 position)
{
    return (LoadInstanceFirstPerson(data, metadata, instanceId).x & 3u) == 1u && view.FirstPersonTransformEnabled != 0u
        ? mul(float4(position, 1.0), view.WorldToFirstPerson).xyz : position;
}

float3 TransformFirstPersonWorldNormal(ByteAddressBuffer data, RenderInstancePropertyMetadata metadata,
    uint instanceId, ClusterViewUniforms view, float3 normal)
{
    return (LoadInstanceFirstPerson(data, metadata, instanceId).x & 3u) == 1u && view.FirstPersonTransformEnabled != 0u
        ? normalize(mul(normal, transpose((float3x3)view.FirstPersonToWorld))) : normal;
}

ClusterViewUniforms ApplyFirstPersonViewTransform(ByteAddressBuffer data, RenderInstancePropertyMetadata metadata,
    uint instanceId, ClusterViewUniforms view)
{
    uint2 firstPerson = LoadInstanceFirstPerson(data, metadata, instanceId);
    if ((firstPerson.x & 3u) == 1u && view.FirstPersonTransformEnabled != 0u)
    {
        // Compose with the public position result so user vertex and domain types remain opaque.
        float4x4 nearTransform = view.WorldToFirstPerson;
        nearTransform[3].xyz += view.WorldTranslation - mul(view.WorldTranslation, (float3x3)nearTransform);
        float4x4 previousNearTransform = view.PreviousWorldToFirstPerson;
        previousNearTransform[3].xyz += view.PreviousWorldTranslation -
            mul(view.PreviousWorldTranslation, (float3x3)previousNearTransform);
        view.ViewProj = mul(nearTransform, view.ViewProj);
        view.PrevViewProj = mul(previousNearTransform, view.PrevViewProj);
        view.GeometryViewProj = mul(nearTransform, view.GeometryViewProj);
        view.View = mul(nearTransform, view.View);
        view.PrevView = mul(previousNearTransform, view.PrevView);
        view.ClipToWorld = mul(view.ClipToWorld, view.FirstPersonToWorld);
    }
    return view;
}

#endif
