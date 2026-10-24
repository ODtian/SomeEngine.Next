#define BLEND_BACKGROUND 1
#include "blend_effect_common.slang"
RWTexture2DArray<float4> SceneColor;
[shader("compute")]
[numthreads(8, 8, 1)]
void FillBlendBackground(uint3 thread : SV_DispatchThreadID)
{
    uint2 pixel = thread.xy;
    if (any(pixel >= uint2(Settings.Width, Settings.Height))) return;
    uint column = min(pixel.x * 4 / Settings.Width, 3u);
    uint row = min(pixel.y * 4 / Settings.Height, 3u);
    SceneColor[uint3(pixel, 0)] = float4(.08 + .2 * float3(column / 3.0, row / 3.0, .5), 1);
}
