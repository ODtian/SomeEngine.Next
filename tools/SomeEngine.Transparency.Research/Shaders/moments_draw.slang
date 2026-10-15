#include "comparison_vertex.slang"
struct MomentOutput
{
    float Absorbance : SV_Target0;
    float4 Moments : SV_Target1;
#if SIX_POWER_MOMENTS
    float2 ExtraMoments : SV_Target2;
#endif
};

[shader("fragment")]
MomentOutput DrawMoments(VertexOutput input)
{
    ClipOpaque(input);
    float depth = input.Position.z * 2 - 1;
    float squaredDepth = depth * depth;
    float absorbance = -log(1 - Colors[input.ColorIndex].a);
    MomentOutput result;
    result.Absorbance = absorbance;
    result.Moments = float4(depth, squaredDepth, depth * squaredDepth, squaredDepth * squaredDepth) * absorbance;
#if SIX_POWER_MOMENTS
    result.ExtraMoments = float2(squaredDepth * squaredDepth * depth, squaredDepth * squaredDepth * squaredDepth) * absorbance;
#endif
    return result;
}
