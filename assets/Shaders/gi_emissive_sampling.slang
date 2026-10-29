#ifndef GI_EMISSIVE_SAMPLING_SLANG
#define GI_EMISSIVE_SAMPLING_SLANG
#include "gi_emissive_data.slang"

RWStructuredBuffer<uint4> GiEmitterState;
RWStructuredBuffer<float> GiEmitterPrefix;
RWStructuredBuffer<float> GiEmitterGroupPrefix;

groupshared float GiEmitterFluxScan[64];
[shader("compute")]
[numthreads(64, 1, 1)]
void BuildGiEmitterDistribution(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint count = GiEmitterState[0].x;
    uint index = group.x * 64u + lane;
    if (group.x >= GiEmitterState[0].y) return;
    GiEmitterFluxScan[lane] = index < count ? GiEmissiveTriangles[index].EdgeUFlux.w : 0.0;
    GroupMemoryBarrierWithGroupSync();
    for (uint stride = 1u; stride < 64u; stride *= 2u)
    {
        float add = lane >= stride ? GiEmitterFluxScan[lane - stride] : 0.0;
        GroupMemoryBarrierWithGroupSync();
        GiEmitterFluxScan[lane] += add;
        GroupMemoryBarrierWithGroupSync();
    }
    if (index < count) GiEmitterPrefix[index] = GiEmitterFluxScan[lane];
    if (lane == 63u) GiEmitterGroupPrefix[group.x] = GiEmitterFluxScan[63];
}

[shader("compute")]
[numthreads(1, 1, 1)]
void BuildGiEmitterGroupDistribution()
{
    uint4 state = GiEmitterState[0];
    float total = 0.0;
    for (uint group = 0u; group < state.y; group++)
    {
        total += GiEmitterGroupPrefix[group];
        GiEmitterGroupPrefix[group] = total;
    }
    float old = asfloat(GiEmitterState[1].z);
    if (abs(total - old) > max(total * 0.01, 1e-5)) GiEmitterState[1].x = 1u;
    state.z = asuint(total);
    GiEmitterState[0] = state;
}

GiDirectLightProposal SelectGiEmitter(float variate)
{
    GiDirectLightProposal result = {0u, 0.0};
    uint4 state = GiEmitterState[0];
    float total = asfloat(state.z);
    if (state.x == 0u || total <= 0.0) return result;
    float choice = variate * total;
    uint low = 0u, high = state.y;
    while (low + 1u < high)
    {
        uint middle = (low + high) / 2u;
        if (GiEmitterGroupPrefix[middle - 1u] <= choice) low = middle;
        else high = middle;
    }
    uint group = low;
    choice -= group == 0u ? 0.0 : GiEmitterGroupPrefix[group - 1u];
    low = group * 64u;
    high = min(low + 64u, state.x);
    while (low + 1u < high)
    {
        uint middle = (low + high) / 2u;
        if (GiEmitterPrefix[middle - 1u] <= choice) low = middle;
        else high = middle;
    }
    result.Emitter = low;
    result.Probability = GiEmissiveTriangles[low].EdgeUFlux.w / total;
    return result;
}

float3 GiEmitterBarycentric(float2 variates)
{
    float root = sqrt(variates.x);
    return float3(1.0 - root, root * (1.0 - variates.y), root * variates.y);
}

float3 EvaluateGiEmissiveIrradiance(float3 emission, float coverage, float3 receiverNormal,
    float3 emitterNormal, float3 delta, float areaWeight)
{
    float square = dot(delta, delta);
    float3 direction = delta * rsqrt(square);
    float cosine = max(0.0, dot(receiverNormal, direction)) * abs(dot(emitterNormal, direction));
    return emission * (coverage * cosine * areaWeight / (3.14159265359 * square));
}

#endif