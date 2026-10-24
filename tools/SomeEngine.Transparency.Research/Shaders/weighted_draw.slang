#include "comparison_vertex.slang"

struct WeightedOutput
{
    float4 Accumulation : SV_Target0;
    float Revealage : SV_Target1;
};

[shader("fragment")]
WeightedOutput DrawWeighted(VertexOutput input)
{
    ClipOpaque(input);
    float4 color = Colors[input.ColorIndex];
#if CUBIC_DEPTH_WEIGHT
    float weight = max(.00001, pow(1 - input.Position.z, 3));
#else
    // McGuire 2015 weight, globally scaled to preserve FP16 accumulation range.
    float weight = clamp(pow(min(1, color.a * 10) + .01, 3) * 1e8 *
        pow(1 - input.Position.z * .9, 3), .01, 3000) / 3000;
#endif
    WeightedOutput result;
    result.Accumulation = float4(color.rgb * color.a, color.a) * weight;
    result.Revealage = color.a;
    return result;
}
