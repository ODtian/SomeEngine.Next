#ifndef SOMEENGINE_RENDER_INSTANCE_PROPERTIES_INCLUDED
#define SOMEENGINE_RENDER_INSTANCE_PROPERTIES_INCLUDED

// Generic one-word linear-storage convention. Property modules may ignore this convention and
// interpret their declared metadata words themselves.
static const uint RENDER_INSTANCE_LINEAR_PER_INSTANCE = 0x80000000u;
static const uint RENDER_INSTANCE_LINEAR_ADDRESS_MASK = 0x7FFFFFFFu;

uint ResolveRenderInstanceLinearAddress(uint metadata, uint instanceSlot, uint stride)
{
    uint element = ((metadata & RENDER_INSTANCE_LINEAR_PER_INSTANCE) != 0u) ? instanceSlot : 0u;
    return (metadata & RENDER_INSTANCE_LINEAR_ADDRESS_MASK) + element * stride;
}

T RenderInstanceLoad<T>(
    ByteAddressBuffer data,
    uint metadata,
    uint instanceSlot,
    uint stride)
{
    uint address = ResolveRenderInstanceLinearAddress(metadata, instanceSlot, stride);
    return data.Load<T>(address);
}

#endif
