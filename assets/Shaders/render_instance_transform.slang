#ifndef RENDER_INSTANCE_TRANSFORM_SLANG
#define RENDER_INSTANCE_TRANSFORM_SLANG

#include "render_instance_properties.slang"

// Render-owned transform encoding. The generic instance-property transport does not know this
// structure; producers and shader consumers agree on it through their encoding fingerprint.
static const uint RENDER_INSTANCE_TRANSFORM_STRIDE = 48u;

struct RenderInstanceTransform
{
    float4 Rotation;
    float3 Position;
    float Scale;
    float3 Stretch;
    float Padding;
};

RenderInstanceTransform LoadRenderTransform(
    ByteAddressBuffer data,
    uint metadata,
    uint instanceSlot)
{
    return RenderInstanceLoad<RenderInstanceTransform>(
        data,
        metadata,
        instanceSlot,
        RENDER_INSTANCE_TRANSFORM_STRIDE);
}

float3 TransformInstanceLocalPosition(float3 position, RenderInstanceTransform transform)
{
    float3 scaled = position * transform.Scale * transform.Stretch;
    float3 vectorPart = transform.Rotation.xyz;
    float scalarPart = transform.Rotation.w;
    float3 uv = cross(vectorPart, scaled);
    float3 uuv = cross(vectorPart, uv);
    scaled = scaled + ((uv * scalarPart) + uuv) * 2.0;
    return scaled + transform.Position;
}

#endif
