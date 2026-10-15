#include "comparison_vertex.slang"
#include "moment_reconstruction.slang"
Texture2D<float> Absorbance;
Texture2D<float4> Moments;
#if SIX_POWER_MOMENTS
Texture2D<float2> ExtraMoments;
#endif

[shader("fragment")]
float4 ShadeMoments(VertexOutput input) : SV_Target0
{
    ClipOpaque(input);
    uint2 pixel = uint2(input.Position.xy);
    float absorbance = Absorbance.Load(int3(pixel, 0));
    float4 moments = Moments.Load(int3(pixel, 0)) / absorbance;
#if SIX_POWER_MOMENTS
    float2 extraMoments = ExtraMoments.Load(int3(pixel, 0)) / absorbance;
    const float biasVector[6] = { 0, .48, 0, .451, 0, .45 };
    float transmittance = ReconstructTransmittance6(absorbance, float3(moments.yw, extraMoments.y),
        float3(moments.xz, extraMoments.x), input.Position.z * 2 - 1, Settings.MomentBias, Settings.Overestimation, biasVector);
#else
    float transmittance = ReconstructTransmittance(absorbance, moments.yw, moments.xz,
        input.Position.z * 2 - 1, Settings.MomentBias, Settings.Overestimation, float4(0, .375, 0, .375));
#endif
    float4 color = Colors[input.ColorIndex];
    return float4(color.rgb, 1) * (color.a * transmittance);
}
