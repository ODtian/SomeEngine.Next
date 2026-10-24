#include "fullscreen_triangle.slang"

struct DebugOverlayUniforms
{
    uint Mode;
    float DepthScale;
    uint2 Padding;
};

ConstantBuffer<DebugOverlayUniforms> DebugUniforms;
Texture2D<float4> InputColor;
Texture2DArray<float> InputDepth;

float3 DepthHeat(float depth)
{
    float value = saturate(depth * DebugUniforms.DepthScale);
    return saturate(float3(1.5 - abs(4.0 * value - 3.0),
                           1.5 - abs(4.0 * value - 2.0),
                           1.5 - abs(4.0 * value - 1.0)));
}

[shader("pixel")]
float4 PSMain(FullscreenVSOut input) : SV_Target
{
    int2 pixel = int2(input.Position.xy);
    float4 color = InputColor.Load(int3(pixel, 0));
    if (DebugUniforms.Mode == 0)
        return color;
    if (DebugUniforms.Mode == 1)
        return float4(DepthHeat(InputDepth.Load(int4(pixel, 0, 0))), 1.0);
    return float4(1.0, 0.0, 1.0, 1.0);
}
