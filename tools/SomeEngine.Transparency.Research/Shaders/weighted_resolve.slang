#include "comparison_common.slang"
Texture2D<float4> Accumulation;
Texture2D<float> Revealage;
RWTexture2DArray<float4> SceneColor;

[shader("compute")]
[numthreads(8, 8, 1)]
void ResolveWeighted(uint3 threadId : SV_DispatchThreadID)
{
    uint2 pixel = threadId.xy;
    if (any(pixel >= uint2(Settings.Width, Settings.Height))) return;
    float4 accumulation = Accumulation.Load(int3(pixel, 0));
    if (accumulation.a == 0) return;
    float transmittance = Revealage.Load(int3(pixel, 0));
    float4 background = SceneColor[uint3(pixel, 0)];
    SceneColor[uint3(pixel, 0)] = float4(accumulation.rgb / accumulation.a * (1 - transmittance) +
        transmittance * background.rgb, background.a);
}
