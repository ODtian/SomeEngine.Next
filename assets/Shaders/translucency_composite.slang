#include "fullscreen_triangle.slang"

struct TranslucencyCompositionUniforms
{
    uint Enabled;
    uint3 Padding;
};

ConstantBuffer<TranslucencyCompositionUniforms> CompositionUniforms;
Texture2D<float4> OpaqueColor;
Texture2D<float4> TranslucencyAccumulation;
Texture2D<float> TranslucencyRevealage;

[shader("pixel")]
float4 PSMain(FullscreenVSOut input) : SV_Target
{
    int2 pixel = int2(input.Position.xy);
    float4 opaque = OpaqueColor.Load(int3(pixel, 0));
    if (CompositionUniforms.Enabled == 0)
        return opaque;

    float4 accumulation = TranslucencyAccumulation.Load(int3(pixel, 0));
    float revealage = saturate(TranslucencyRevealage.Load(int3(pixel, 0)));
    float alpha = 1.0 - revealage;
    float3 translucent = accumulation.rgb / max(accumulation.a, 1e-5);
    return float4(lerp(opaque.rgb, translucent, alpha), opaque.a);
}
