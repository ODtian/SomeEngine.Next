#ifndef GI_EMISSIVE_SAMPLING_SLANG
#define GI_EMISSIVE_SAMPLING_SLANG
#include "gi_emissive_data.slang"

struct GiEmitterBounds
{
    float3 Center;
    float Flux;
    float3 Extent;
    uint Emitter;
};

struct GiEmitterPointSample
{
    uint Emitter;
    float3 Barycentric;
    float AreaPdf;
};

RWStructuredBuffer<uint4> GiEmitterState;
RWStructuredBuffer<uint2> GiEmitterInstanceRanges;
RWStructuredBuffer<uint2> GiEmitterTreeRanges;
RWStructuredBuffer<uint> GiEmitterInstanceChanges;
RWStructuredBuffer<GiEmitterBounds> GiEmitterTriangleTree;
RWStructuredBuffer<GiEmitterBounds> GiEmitterObjectTree;

uint GiEmitterLeafCount(uint count) { return count <= 1u ? 1u : 1u << (firstbithigh(count - 1u) + 1u); }

GiEmitterBounds MergeGiEmitterBounds(GiEmitterBounds left, GiEmitterBounds right)
{
    if (left.Flux == 0.0) return right;
    if (right.Flux == 0.0) return left;
    float3 lower = min(left.Center - left.Extent, right.Center - right.Extent);
    float3 upper = max(left.Center + left.Extent, right.Center + right.Extent);
    GiEmitterBounds result = (GiEmitterBounds)0;
    result.Center = (lower + upper) * 0.5; result.Extent = (upper - lower) * 0.5;
    result.Flux = left.Flux + right.Flux;
    return result;
}

float GiLightBoundsMass(float3 position, float3 normal, GiEmitterBounds bounds)
{
    if (bounds.Flux == 0.0) return 0.0;
    float3 delta = bounds.Center - position;
    float radius = length(bounds.Extent), distance = length(delta);
    float cosine = saturate((dot(normal, delta) + radius) / distance);
    return bounds.Flux * max(0.05, cosine) / (distance * distance + radius * radius);
}

[shader("compute")]
[numthreads(64, 1, 1)]
void BuildGiEmitterTriangleTrees(uint3 group : SV_GroupID, uint lane : SV_GroupIndex)
{
    uint instance = group.x;
    if (instance >= GiEmissive.InstanceCount ||
        ((GiEmitterChanges[0] & 1u) == 0u && GiEmitterInstanceChanges[instance] == 0u)) return;
    uint2 tree = GiEmitterTreeRanges[instance], range = GiEmitterInstanceRanges[instance];
    if (range.y == 0u) return;
    for (uint leaf = lane; leaf < tree.y; leaf += 64u)
    {
        GiEmitterBounds bounds = (GiEmitterBounds)0;
        if (leaf < range.y)
        {
            uint emitter = range.x + leaf;
            GiEmissiveTriangle light = GiEmissiveTriangles[emitter];
            float3 a = light.PositionArea.xyz, b = a + light.EdgeUFlux.xyz, c = a + light.EdgeV.xyz;
            float3 lower = min(a, min(b, c)), upper = max(a, max(b, c));
            bounds.Center = (lower + upper) * 0.5; bounds.Extent = (upper - lower) * 0.5;
            bounds.Flux = light.EdgeUFlux.w; bounds.Emitter = emitter;
        }
        GiEmitterTriangleTree[tree.x + tree.y + leaf] = bounds;
    }
    DeviceMemoryBarrierWithGroupSync();
    for (uint level = tree.y / 2u; level > 0u; level /= 2u)
    {
        for (uint item = lane; item < level; item += 64u)
        {
            uint at = tree.x + level + item;
            GiEmitterTriangleTree[at] = MergeGiEmitterBounds(GiEmitterTriangleTree[tree.x + (level + item) * 2u],
                GiEmitterTriangleTree[tree.x + (level + item) * 2u + 1u]);
        }
        DeviceMemoryBarrierWithGroupSync();
    }
}

[shader("compute")]
[numthreads(128, 1, 1)]
void BuildGiEmitterObjectTree(uint lane : SV_GroupIndex)
{
    if (GiEmitterState[1].x == 0u) return;
    uint leaves = GiEmissive.ObjectLeafCount;
    for (uint instance = lane; instance < leaves; instance += 128u)
    {
        GiEmitterBounds bounds = (GiEmitterBounds)0;
        if (instance < GiEmissive.InstanceCount && GiEmitterInstanceRanges[instance].y > 0u)
            bounds = GiEmitterTriangleTree[GiEmitterTreeRanges[instance].x + 1u];
        bounds.Emitter = instance;
        GiEmitterObjectTree[leaves + instance] = bounds;
    }
    DeviceMemoryBarrierWithGroupSync();
    for (uint level = leaves / 2u; level > 0u; level /= 2u)
    {
        for (uint item = lane; item < level; item += 128u)
            GiEmitterObjectTree[level + item] = MergeGiEmitterBounds(GiEmitterObjectTree[(level + item) * 2u],
                GiEmitterObjectTree[(level + item) * 2u + 1u]);
        DeviceMemoryBarrierWithGroupSync();
    }
    if (lane == 0u) GiEmitterState[0].z = asuint(GiEmitterObjectTree[1].Flux);
}

float GiEmittingObjectMass(float3 position, float3 normal, uint instance)
{
    return GiLightBoundsMass(position, normal, GiEmitterObjectTree[GiEmissive.ObjectLeafCount + instance]);
}

GiDirectLightProposal SelectGiEmittingObject(float3 position, float3 normal, float variate)
{
    GiDirectLightProposal result = (GiDirectLightProposal)0;
    if (asfloat(GiEmitterState[0].z) <= 0.0) return result;
    uint node = 1u;
    float probability = 1.0;
    while (node < GiEmissive.ObjectLeafCount)
    {
        float left = GiLightBoundsMass(position, normal, GiEmitterObjectTree[node * 2u]);
        float right = GiLightBoundsMass(position, normal, GiEmitterObjectTree[node * 2u + 1u]);
        float fraction = left / (left + right);
        if (variate < fraction) { probability *= fraction; variate /= fraction; node *= 2u; }
        else { probability *= 1.0 - fraction; variate = (variate - fraction) / (1.0 - fraction); node = node * 2u + 1u; }
    }
    result.Instance = node - GiEmissive.ObjectLeafCount; result.Probability = probability;
    return result;
}

float GiEmittingObjectProbability(float3 position, float3 normal, uint instance)
{
    if (GiEmitterInstanceRanges[instance].y == 0u) return 0.0;
    uint node = GiEmissive.ObjectLeafCount + instance;
    float probability = 1.0;
    while (node > 1u)
    {
        uint parent = node / 2u;
        float left = GiLightBoundsMass(position, normal, GiEmitterObjectTree[parent * 2u]);
        float right = GiLightBoundsMass(position, normal, GiEmitterObjectTree[parent * 2u + 1u]);
        probability *= ((node & 1u) == 0u ? left : right) / (left + right);
        node = parent;
    }
    return probability;
}

float3 GiEmitterBarycentric(float2 variates)
{
    float root = sqrt(variates.x);
    return float3(1.0 - root, root * (1.0 - variates.y), root * variates.y);
}

float GiTriangleSolidAngle(float3 a, float3 b, float3 c)
{
    return 2.0 * atan2(abs(dot(a, cross(b, c))), 1.0 + dot(a, b) + dot(b, c) + dot(c, a));
}

float3 SampleGiTriangleDirection(float3 a, float3 b, float3 c, float area, float2 variates)
{
    float3 ab = normalize(cross(a, b)), ac = -normalize(cross(c, a));
    float cosAlpha = clamp(dot(ab, ac), -1.0, 1.0), sinAlpha = length(cross(ab, ac));
    float phi = 3.14159265359 + variates.x * area - atan2(sinAlpha, cosAlpha);
    float sine = sin(phi), cosine = cos(phi);
    float first = cosine + cosAlpha, second = sine - sinAlpha * dot(a, b);
    float q = clamp(fma(fma(second, cosine, -first * sine), cosAlpha, second) /
        (fma(second, sine, first * cosine) * sinAlpha), -1.0, 1.0);
    float3 corner = q * a + sqrt(max(0.0, 1.0 - q * q)) * normalize(c - a * dot(c, a));
    float z = 1.0 - variates.y * (1.0 - dot(corner, b));
    return z * b + sqrt(max(0.0, 1.0 - z * z)) * normalize(corner - b * dot(corner, b));
}

GiEmitterPointSample SampleGiEmitterTriangle(float3 position, float3 normal, uint instance, float3 variates)
{
    uint2 tree = GiEmitterTreeRanges[instance];
    uint node = 1u;
    float probability = 1.0, choice = variates.x;
    while (node < tree.y)
    {
        float left = GiLightBoundsMass(position, normal, GiEmitterTriangleTree[tree.x + node * 2u]);
        float right = GiLightBoundsMass(position, normal, GiEmitterTriangleTree[tree.x + node * 2u + 1u]);
        float fraction = left / (left + right);
        if (choice < fraction) { probability *= fraction; choice /= fraction; node *= 2u; }
        else { probability *= 1.0 - fraction; choice = (choice - fraction) / (1.0 - fraction); node = node * 2u + 1u; }
    }
    GiEmitterPointSample result;
    result.Emitter = GiEmitterTriangleTree[tree.x + node].Emitter;
    GiEmissiveTriangle light = GiEmissiveTriangles[result.Emitter];
    float3 a = normalize(light.PositionArea.xyz - position);
    float3 b = normalize(light.PositionArea.xyz + light.EdgeUFlux.xyz - position);
    float3 c = normalize(light.PositionArea.xyz + light.EdgeV.xyz - position);
    float solidAngle = GiTriangleSolidAngle(a, b, c);
    result.Barycentric = GiEmitterBarycentric(variates.yz);
    result.AreaPdf = probability / light.PositionArea.w;
    if (solidAngle >= 0.0003 && solidAngle <= 6.22)
    {
        float3 direction = SampleGiTriangleDirection(a, b, c, solidAngle, variates.yz);
        float3 delta = position - light.PositionArea.xyz;
        float3 perpendicular = cross(direction, light.EdgeV.xyz);
        float divisor = dot(perpendicular, light.EdgeUFlux.xyz);
        float2 barycentric = max(float2(dot(delta, perpendicular), dot(direction, cross(delta, light.EdgeUFlux.xyz))) / divisor, 0.0);
        barycentric /= max(1.0, barycentric.x + barycentric.y);
        result.Barycentric = float3(1.0 - barycentric.x - barycentric.y, barycentric);
        float3 point = light.PositionArea.xyz + light.EdgeUFlux.xyz * barycentric.x + light.EdgeV.xyz * barycentric.y;
        float3 toLight = point - position;
        float cosine = abs(dot(normalize(cross(light.EdgeUFlux.xyz, light.EdgeV.xyz)), normalize(toLight)));
        result.AreaPdf = probability * cosine / (solidAngle * dot(toLight, toLight));
    }
    return result;
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
