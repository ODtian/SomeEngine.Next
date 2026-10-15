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
    uint2 pixel = group.xy * 16 + thread.xy;
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
    float4 color = valid ? SceneColor[uint3(pixel, 0)] : 0;
    uint2 tile = TileWords[group.y * Settings.TileColumns + group.x];
    for (uint wordIndex = tile.x; wordIndex < tile.x + tile.y; wordIndex++)
    {
        uint2 word = Words[wordIndex];
#ifdef BLEND_CLUSTERED
        uint sourceLane = WaveGetLaneIndex();
        bool accepts = false;
        if (sourceLane < word.y)
        {
            uint slices = CellSlices[References[word.x + sourceLane]];
            accepts = (occupiedDepths & DepthMask(slices & 255, slices >> 8)) != 0;
        }
        uint bits = WaveActiveBallot(accepts).x;
#else
        uint bits = 0xffffffffu >> (32 - word.y);
#endif
        while (bits != 0)
        {
            uint bit = firstbitlow(bits);
            bits &= bits - 1;
            uint index = References[word.x + bit];
            float4 surface = 0;
#ifdef BLEND_XRAY
            ProjectedTriangle coverage = Coverage[index];
            float depth;
            if (valid && Covers(coverage, point, depth) && depth > opaque && depth <= opaque + Settings.XrayDistance)
            {
                float3 bary = float3(dot(coverage.EdgeA.xyz, float3(point, 1)),
                    dot(coverage.EdgeB.xyz, float3(point, 1)), dot(coverage.EdgeC.xyz, float3(point, 1)));
                BlendTriangle triangle = Triangles[index];
                float3 normal = triangle.A.Normal * bary.x + triangle.B.Normal * bary.y + triangle.C.Normal * bary.z;
                float2 uv = float2(triangle.A.U, triangle.A.V) * bary.x + float2(triangle.B.U, triangle.B.V) * bary.y +
                    float2(triangle.C.U, triangle.C.V) * bary.z;
                surface = ShadeXray(normal, uv, Bounds[index].MaterialIndex, depth, opaque);
            }
#else
            if (valid) surface = ShadeDecal(index, point, opaque);
#endif
            color.rgb = surface.rgb + (1 - surface.a) * color.rgb;
            color.rgb = f16tof32(f32tof16(color.rgb));
        }
    }
    if (valid) SceneColor[uint3(pixel, 0)] = color;
}
