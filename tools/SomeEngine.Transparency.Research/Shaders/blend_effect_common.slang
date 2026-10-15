#include "triangles.slang"

struct BlendSettings
{
    uint Width, Height, TileColumns, TileRows;
    uint ItemCount, WordCount, DepthSlices, Padding;
    float4 DepthProjection;
    float NearDepth, FarDepth, XrayDistance, Alpha;
};
struct BlendVertex { float3 Position; float U; float3 Normal; float V; };
struct BlendTriangle { BlendVertex A, B, C; };
struct BlendBounds { float4 Rectangle; float2 DepthRange; uint MaterialIndex, Padding; };
ConstantBuffer<BlendSettings> Settings;
#if defined(BLEND_DRAW) || defined(BLEND_RESOLVE)
#ifdef BLEND_XRAY
StructuredBuffer<BlendTriangle> Triangles;
#ifdef BLEND_RESOLVE
StructuredBuffer<ProjectedTriangle> Coverage;
#endif
#endif
StructuredBuffer<float4> Pattern;
Texture2DArray<float> OpaqueDepth;
#endif
#ifndef BLEND_BACKGROUND
StructuredBuffer<BlendBounds> Bounds;
#endif
#if defined(BLEND_CELLS) || defined(BLEND_RESOLVE)
StructuredBuffer<uint> References;
StructuredBuffer<uint2> Words;
#endif
#ifdef BLEND_RESOLVE
StructuredBuffer<uint2> TileWords;
#endif

float2 BlendPixelPoint(uint2 pixel)
{
    return float2((pixel.x + .5) * (2.0 / Settings.Width) - 1,
        1 - (pixel.y + .5) * (2.0 / Settings.Height));
}
float ViewDepth(float depth)
{
    float4 p = Settings.DepthProjection;
    return -(p.z - depth * p.w) / (depth * p.y - p.x);
}
float DeviceDepth(float viewDepth)
{
    float4 p = Settings.DepthProjection;
    return (-viewDepth * p.x + p.z) / (-viewDepth * p.y + p.w);
}
uint DepthSlice(float depth)
{
    float value = log2(max(depth, Settings.NearDepth) / Settings.NearDepth) *
        (Settings.DepthSlices / log2(Settings.FarDepth / Settings.NearDepth));
    return (uint)clamp(floor(value), 0, float(Settings.DepthSlices - 1));
}
float3 MaterialTint(uint index)
{
    return .3 + .7 * frac(float3(.37, .61, .83) * (index + 1));
}
#if defined(BLEND_DRAW) || defined(BLEND_RESOLVE)
float4 SamplePattern(float2 uv)
{
    uint2 texel = uint2(frac(uv) * 64);
    return Pattern[texel.y * 64 + texel.x];
}
float4 ShadeXray(float3 normal, float2 uv, uint material, float depth, float opaque)
{
    if (depth <= opaque || depth > opaque + Settings.XrayDistance) return 0;
    float4 texel = SamplePattern(uv * 3);
    normal = normalize(normal + float3((texel.rg - .5) * .18, 0));
    float3 light = normalize(float3(.4, .7, -1));
    float diffuse = .15 + .85 * abs(dot(normal, light));
    float specular = pow(saturate(abs(dot(normal, normalize(light + float3(0, 0, -1))))), 24) * .25;
    float3 color = MaterialTint(material) * texel.rgb * diffuse + specular;
    float alpha = Settings.Alpha * (.6 + .4 * texel.a);
    return float4(color * alpha, alpha);
}
float4 ShadeDecal(uint index, float2 point, float depth)
{
    BlendBounds item = Bounds[index];
    if (depth < item.DepthRange.x || depth > item.DepthRange.y) return 0;
    float2 uv = (point - item.Rectangle.xy) / (item.Rectangle.zw - item.Rectangle.xy);
    if (any(uv < 0) || any(uv >= 1)) return 0;
    float4 texel = SamplePattern(uv * 2);
    float edge = saturate((.5 - length(uv - .5)) * 16);
    float alpha = Settings.Alpha * edge * texel.a;
    return float4(MaterialTint(item.MaterialIndex) * texel.rgb * alpha, alpha);
}
#endif
