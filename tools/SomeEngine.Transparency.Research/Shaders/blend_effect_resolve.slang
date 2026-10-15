#define BLEND_RESOLVE 1
#include "blend_effect_common.slang"
RWTexture2DArray<float4> SceneColor;
#ifdef BLEND_CLUSTERED
StructuredBuffer<uint> CellSlices;
uint DepthMask(uint lower, uint upper)
{
    return (0xffffffffu << lower) & (0xffffffffu >> (31 - upper));
}
#endif

[shader("compute")]
[numthreads(16, 16, 1)]
[WaveSize(32)]
void ResolveBlendEffect(uint3 group : SV_GroupID, uint3 thread : SV_GroupThreadID)
{
    uint2 tile = TileWords[group.y * Settings.TileColumns + group.x];
    if (tile.y == 0) return;
    uint localIndex = thread.y * 16 + thread.x;
    uint2 localPixel = uint2((localIndex & 7) + ((localIndex >> 5) & 1) * 8,
        ((localIndex >> 3) & 3) + (localIndex >> 6) * 4);
    uint2 pixel = group.xy * 16 + localPixel;
    bool valid = all(pixel < uint2(Settings.Width, Settings.Height));
    float opaque = valid ? ViewDepth(OpaqueDepth.Load(int4(pixel, 0, 0))) : 0;
#ifdef BLEND_CLUSTERED
    uint lower = DepthSlice(opaque);
#ifdef BLEND_XRAY
    uint upper = DepthSlice(opaque + Settings.XrayDistance);
#else
    uint upper = lower;
#endif
    uint occupiedDepths = WaveActiveBitOr(valid ? DepthMask(lower, upper) : 0);
#endif
    float2 point = BlendPixelPoint(pixel);
#ifdef BLEND_XRAY
    float2 waveMinimum = WaveActiveMin(point);
    float2 waveMaximum = WaveActiveMax(point);
#endif
#ifdef BLEND_XRAY
    float4 color = valid ? SceneColor[uint3(pixel, 0)] : 0;
#else
    float4 color = 0;
#endif
    bool changed = false;
    uint sourceLane = WaveGetLaneIndex();
    for (uint wordIndex = tile.x; wordIndex < tile.x + tile.y; wordIndex++)
    {
        uint2 word = Words[wordIndex];
        uint sourceItem = sourceLane < word.y ? References[word.x + sourceLane] : 0;
#if defined(BLEND_XRAY) || defined(BLEND_CLUSTERED)
        bool accepts = false;
        if (sourceLane < word.y)
        {
            accepts = true;
#ifdef BLEND_XRAY
            float4 rectangle = Bounds[sourceItem].Rectangle;
            accepts = all(rectangle.xy <= waveMaximum) && all(rectangle.zw >= waveMinimum);
#endif
#ifdef BLEND_CLUSTERED
            accepts = accepts && (occupiedDepths & CellSlices[sourceItem]) != 0;
#endif
        }
        uint bits = WaveActiveBallot(accepts).x;
#else
        uint bits = 0xffffffffu >> (32 - word.y);
#endif
        while (bits != 0)
        {
            uint bit = firstbitlow(bits);
            bits &= bits - 1;
#ifdef BLEND_XRAY
            uint index = References[word.x + bit];
#else
            uint index = WaveReadLaneAt(sourceItem, bit);
#endif
            bool acceptsPixel = valid;
            float4 surface = 0;
#ifdef BLEND_XRAY
            if (acceptsPixel)
            {
                ProjectedTriangle coverage = Coverage[index];
                float depth;
                if (Covers(coverage, point, depth) && depth > opaque && depth <= opaque + Settings.XrayDistance)
                {
                    float3 bary = float3(dot(coverage.EdgeA.xyz, float3(point, 1)),
                        dot(coverage.EdgeB.xyz, float3(point, 1)), dot(coverage.EdgeC.xyz, float3(point, 1)));
                    BlendTriangle triangle = Triangles[index];
                    float3 normal = triangle.A.Normal * bary.x + triangle.B.Normal * bary.y + triangle.C.Normal * bary.z;
                    float2 uv = float2(triangle.A.U, triangle.A.V) * bary.x + float2(triangle.B.U, triangle.B.V) * bary.y +
                        float2(triangle.C.U, triangle.C.V) * bary.z;
                    surface = ShadeXray(normal, uv, Bounds[index].MaterialIndex, depth, opaque);
                }
            }
#else
            if (acceptsPixel) surface = ShadeDecal(index, point, opaque);
#endif
            if (surface.a > 0)
            {
#ifndef BLEND_XRAY
                if (!changed) color = SceneColor[uint3(pixel, 0)];
#endif
                color.rgb = surface.rgb + (1 - surface.a) * color.rgb;
                color.rgb = f16tof32(f32tof16(color.rgb));
                changed = true;
            }
        }
    }
    if (changed) SceneColor[uint3(pixel, 0)] = color;
}
