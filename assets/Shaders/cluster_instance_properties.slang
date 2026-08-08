#ifndef SOMEENGINE_CLUSTER_INSTANCE_PROPERTIES_INCLUDED
#define SOMEENGINE_CLUSTER_INSTANCE_PROPERTIES_INCLUDED

#include "render_instance_transform.slang"
#include "render_instance_properties.slang"
#include "generated/render_instance_layout.slang"

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

#endif
