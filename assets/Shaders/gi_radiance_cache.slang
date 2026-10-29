#ifndef GI_RADIANCE_CACHE_SLANG
#define GI_RADIANCE_CACHE_SLANG

struct GiRadianceSample
{
    float3 Position;
    uint Valid;
    float3 Normal;
    float Weight;
    float3 Radiance;
    uint Padding;
};

struct GiCacheParameters
{
    uint Frame;
    uint TrainingCount;
    uint HashCount;
    uint CacheMode;
    float CellSize;
    float LearningRate;
    uint Reset;
    uint Padding;
};

ConstantBuffer<GiCacheParameters> GiCache;
RWStructuredBuffer<uint> GiRadianceKeys;
RWStructuredBuffer<uint4> GiRadianceSums;
RWStructuredBuffer<float4> GiRadianceValues;
RWStructuredBuffer<GiRadianceSample> GiRadianceTraining;
RWStructuredBuffer<float> GiNeuralWeights;
RWStructuredBuffer<float2> GiNeuralMoments;
RWStructuredBuffer<float> GiNeuralActivations;
RWStructuredBuffer<float> GiNeuralGradients;
RWStructuredBuffer<uint> GiNeuralUpdates;

uint GiMixBits(uint value)
{
    value ^= value >> 16u;
    value *= 0x7feb352du;
    value ^= value >> 15u;
    value *= 0x846ca68bu;
    return value ^ (value >> 16u);
}

uint GiRadianceKey(float3 position, float3 normal, uint level)
{
    int3 cell = int3(floor(position / (GiCache.CellSize * float(1u << level))));
    float3 absolute = abs(normal);
    uint axis = absolute.x >= absolute.y && absolute.x >= absolute.z ? 0u : absolute.y >= absolute.z ? 1u : 2u;
    uint side = axis * 2u + uint(normal[axis] > 0.0) + level * 8u;
    uint key = GiMixBits(asuint(cell.x) * 2654435761u ^ asuint(cell.y) * 2246822519u ^
        asuint(cell.z) * 3266489917u ^ side * 668265263u);
    return max(1u, key);
}

float4 QueryGiHashedRadiance(float3 position, float3 normal, float footprint)
{
    uint first = uint(clamp(ceil(log2(max(1.0, footprint / GiCache.CellSize))), 0.0, 7.0));
    for (uint level = first; level < 8u; level++)
    {
        uint key = GiRadianceKey(position, normal, level);
        uint address = GiMixBits(key) & (GiCache.HashCount - 1u);
        for (uint probe = 0u; probe < 16u; probe++)
        {
            uint at = (address + probe) & (GiCache.HashCount - 1u);
            uint stored = GiRadianceKeys[at];
            if (stored == 0u) break;
            if (stored == key)
            {
                float4 value = GiRadianceValues[at];
                if (value.w >= 1.0) return value;
                break;
            }
        }
    }
    return 0.0;
}

void GiNeuralFeatures(float3 position, float3 normal, out float values[24])
{
    float3 point = position / 32.0;
    values[0] = point.x; values[1] = point.y; values[2] = point.z;
    values[3] = normal.x; values[4] = normal.y; values[5] = normal.z;
    for (uint band = 0u; band < 3u; band++)
    {
        float3 angle = point * (3.14159265359 * float(1u << (band + 1u)));
        for (uint axis = 0u; axis < 3u; axis++)
        {
            values[6u + band * 6u + axis] = sin(angle[axis]);
            values[9u + band * 6u + axis] = cos(angle[axis]);
        }
    }
}

float3 QueryGiNeuralRadiance(float3 position, float3 normal)
{
    float input[24], hidden[32];
    GiNeuralFeatures(position, normal, input);
    for (uint neuron = 0u; neuron < 32u; neuron++)
    {
        float sum = GiNeuralWeights[768u + neuron];
        for (uint feature = 0u; feature < 24u; feature++) sum += input[feature] * GiNeuralWeights[neuron * 24u + feature];
        hidden[neuron] = max(0.0, sum);
    }
    float3 result = 0.0;
    for (uint channel = 0u; channel < 3u; channel++)
    {
        float sum = GiNeuralWeights[896u + channel];
        for (uint neuron = 0u; neuron < 32u; neuron++) sum += hidden[neuron] * GiNeuralWeights[800u + channel * 32u + neuron];
        result[channel] = exp(clamp(sum, 0.0, 6.0)) - 1.0;
    }
    return result;
}

float4 QueryGiRadianceValue(float3 position, float3 normal, float footprint)
{
    if ((GiCache.CacheMode & 1u) != 0u)
    {
        float4 value = QueryGiHashedRadiance(position, normal, footprint);
        if (value.w > 0.0) return value;
    }
    if ((GiCache.CacheMode & 2u) != 0u && GiNeuralUpdates[0] >= 4u)
        return float4(QueryGiNeuralRadiance(position, normal), 1.0);
    return 0.0;
}

float3 QueryGiReflectedRadiance(float3 position, float3 normal, float footprint)
{
    return QueryGiRadianceValue(position, normal, footprint).rgb;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void InitializeGiRadianceCache(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index < GiCache.HashCount)
    {
        GiRadianceKeys[index] = 0u;
        GiRadianceSums[index] = 0u;
        GiRadianceValues[index] = 0.0;
    }
    if (index < GiCache.TrainingCount) GiRadianceTraining[index] = (GiRadianceSample)0;
    if (index < 899u)
    {
        GiNeuralWeights[index] = index < 896u ? (float(GiMixBits(index + 1u) >> 8u) / 16777216.0 - 0.5) * 0.25 : 0.05;
        GiNeuralMoments[index] = 0.0;
    }
    if (index == 0u) GiNeuralUpdates[0] = 0u;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void UpdateGiHashedRadiance(uint3 thread : SV_DispatchThreadID)
{
    if (thread.x >= GiCache.TrainingCount) return;
    GiRadianceSample sample = GiRadianceTraining[thread.x];
    if (sample.Valid == 0u) return;
    uint3 value = uint3(clamp(sample.Radiance, 0.0.xxx, 256.0.xxx) * 4096.0);
    for (uint level = 0u; level < 8u; level++)
    {
        uint key = GiRadianceKey(sample.Position, sample.Normal, level);
        uint address = GiMixBits(key) & (GiCache.HashCount - 1u);
        for (uint probe = 0u; probe < 16u; probe++)
        {
            uint at = (address + probe) & (GiCache.HashCount - 1u), previous;
            InterlockedCompareExchange(GiRadianceKeys[at], 0u, key, previous);
            if (previous == 0u || previous == key)
            {
                InterlockedAdd(GiRadianceSums[at].x, value.x);
                InterlockedAdd(GiRadianceSums[at].y, value.y);
                InterlockedAdd(GiRadianceSums[at].z, value.z);
                InterlockedAdd(GiRadianceSums[at].w, 1u);
                break;
            }
        }
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void ResolveGiHashedRadiance(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiCache.HashCount) return;
    uint4 sum = GiRadianceSums[index];
    if (sum.w == 0u) return;
    float4 old = GiRadianceValues[index];
    float history = min(old.w, 4.0);
    GiRadianceValues[index] = float4((old.rgb * history + float3(sum.xyz) / 4096.0) / (history + float(sum.w)),
        min(32.0, old.w + float(sum.w)));
    GiRadianceSums[index] = 0u;
}

[shader("compute")]
[numthreads(32, 1, 1)]
void EvaluateGiNeuralTraining(uint3 thread : SV_DispatchThreadID)
{
    uint index = thread.x;
    if (index >= GiCache.TrainingCount) return;
    GiRadianceSample sample = GiRadianceTraining[index];
    uint first = index * 96u;
    float input[24], hidden[32];
    GiNeuralFeatures(sample.Position, sample.Normal, input);
    for (uint feature = 0u; feature < 24u; feature++) GiNeuralActivations[first + feature] = input[feature];
    for (uint neuron = 0u; neuron < 32u; neuron++)
    {
        float sum = GiNeuralWeights[768u + neuron];
        for (uint feature = 0u; feature < 24u; feature++) sum += input[feature] * GiNeuralWeights[neuron * 24u + feature];
        hidden[neuron] = max(0.0, sum);
        GiNeuralActivations[first + 24u + neuron] = hidden[neuron];
    }
    float3 errors = 0.0;
    for (uint channel = 0u; channel < 3u; channel++)
    {
        float sum = GiNeuralWeights[896u + channel];
        for (uint neuron = 0u; neuron < 32u; neuron++) sum += hidden[neuron] * GiNeuralWeights[800u + channel * 32u + neuron];
        float prediction = exp(clamp(sum, 0.0, 6.0)) - 1.0;
        errors[channel] = sample.Valid != 0u ? (prediction - sample.Radiance[channel]) / (1.0 + prediction) : 0.0;
        GiNeuralActivations[first + 56u + channel] = errors[channel];
    }
    for (uint neuron = 0u; neuron < 32u; neuron++)
    {
        float derivative = dot(errors, float3(GiNeuralWeights[800u + neuron], GiNeuralWeights[832u + neuron], GiNeuralWeights[864u + neuron]));
        GiNeuralActivations[first + 60u + neuron] = hidden[neuron] > 0.0 ? derivative : 0.0;
    }
}

[shader("compute")]
[numthreads(64, 1, 1)]
void DifferentiateGiNeuralTraining(uint3 thread : SV_DispatchThreadID)
{
    uint weight = thread.x / 8u, section = thread.x % 8u;
    if (weight >= 899u) return;
    float gradient = 0.0;
    for (uint index = section; index < GiCache.TrainingCount; index += 8u)
    {
        uint first = index * 96u;
        if (weight < 768u) gradient += GiNeuralActivations[first + 60u + weight / 24u] * GiNeuralActivations[first + weight % 24u];
        else if (weight < 800u) gradient += GiNeuralActivations[first + 60u + weight - 768u];
        else if (weight < 896u) gradient += GiNeuralActivations[first + 56u + (weight - 800u) / 32u] * GiNeuralActivations[first + 24u + (weight - 800u) % 32u];
        else gradient += GiNeuralActivations[first + 56u + weight - 896u];
    }
    GiNeuralGradients[weight * 8u + section] = 2.0 * gradient / float(GiCache.TrainingCount);
}

[shader("compute")]
[numthreads(1, 1, 1)]
void BeginGiNeuralUpdate()
{
    GiNeuralUpdates[0]++;
}

[shader("compute")]
[numthreads(64, 1, 1)]
void UpdateGiNeuralRadiance(uint3 thread : SV_DispatchThreadID)
{
    uint weight = thread.x;
    if (weight >= 899u) return;
    float gradient = 0.0;
    for (uint section = 0u; section < 8u; section++) gradient += GiNeuralGradients[weight * 8u + section];
    float2 old = GiNeuralMoments[weight];
    float2 moments = float2(0.9 * old.x + 0.1 * gradient, 0.999 * old.y + 0.001 * gradient * gradient);
    float iteration = float(GiNeuralUpdates[0]);
    GiNeuralWeights[weight] -= GiCache.LearningRate * (moments.x / (1.0 - pow(0.9, iteration))) /
        (sqrt(moments.y / (1.0 - pow(0.999, iteration))) + 1e-7);
    GiNeuralMoments[weight] = moments;
}

#endif
