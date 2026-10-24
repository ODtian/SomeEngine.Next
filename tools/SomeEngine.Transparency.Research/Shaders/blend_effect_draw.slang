#define BLEND_DRAW 1
#include "blend_effect_common.slang"

struct BlendVarying
{
    float4 Position : SV_Position;
    float3 Normal : TEXCOORD0;
    float2 UV : TEXCOORD1;
    float ViewZ : TEXCOORD2;
    nointerpolation uint Item : TEXCOORD3;
};
[shader("vertex")]
BlendVarying DrawBlendEffect(uint vertex : SV_VertexID)
{
    BlendVarying result = {};
#ifdef BLEND_XRAY
    uint index = vertex / 3;
    BlendTriangle triangle = Triangles[index];
    BlendVertex value = vertex % 3 == 0 ? triangle.A : vertex % 3 == 1 ? triangle.B : triangle.C;
    // The far triangle bound conservatively rejects visible-side coverage before material work.
    result.Position = float4(value.Position.xy, DeviceDepth(Bounds[index].DepthRange.y), 1);
    result.Normal = value.Normal;
    result.UV = float2(value.U, value.V);
    result.ViewZ = value.Position.z;
    result.Item = index;
#else
    uint index = vertex / 6, corner = vertex % 6;
    float2 uv = corner == 0 ? float2(0, 0) : corner == 1 ? float2(1, 0) :
        corner == 2 || corner == 4 ? float2(1, 1) : corner == 3 ? float2(0, 0) : float2(0, 1);
    result.Position = float4(lerp(Bounds[index].Rectangle.xy, Bounds[index].Rectangle.zw, uv),
        DeviceDepth(Bounds[index].DepthRange.y), 1);
    result.Item = index;
#endif
    return result;
}
[shader("fragment")]
[earlydepthstencil]
float4 BlendEffectPixel(BlendVarying input) : SV_Target
{
    uint2 pixel = uint2(input.Position.xy);
    float opaque = ViewDepth(OpaqueDepth.Load(int4(pixel, 0, 0)));
#ifdef BLEND_XRAY
    float4 surface = ShadeXray(input.Normal, input.UV, Bounds[input.Item].MaterialIndex, input.ViewZ, opaque);
#else
    float4 surface = ShadeDecal(input.Item, BlendPixelPoint(pixel), opaque);
#endif
    if (surface.a == 0) discard;
    return surface;
}
