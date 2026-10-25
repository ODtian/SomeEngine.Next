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
    uint address = ResolveRenderInstanceLinearAddress(metadata, instanceSlot, RENDER_INSTANCE_TRANSFORM_STRIDE);
    // The render-owned encoding occupies three complete 16-byte groups, including padding.
    float4 positionScale = asfloat(data.Load4(address + 16u));
    float4 stretchPadding = asfloat(data.Load4(address + 32u));
    RenderInstanceTransform transform;
    transform.Rotation = asfloat(data.Load4(address));
    transform.Position = positionScale.xyz;
    transform.Scale = positionScale.w;
    transform.Stretch = stretchPadding.xyz;
    transform.Padding = stretchPadding.w;
    return transform;
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
