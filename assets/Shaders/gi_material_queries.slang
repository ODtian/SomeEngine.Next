#ifndef GI_MATERIAL_QUERIES_SLANG
#define GI_MATERIAL_QUERIES_SLANG

#include "material_interfaces.slang"

struct GiTraceRay
{
    float3 Origin;
    float Start;
    float3 Direction;
    float Limit;
};

struct GiTriangleCandidate
{
    uint RayIndex;
    uint InstanceIndex;
    uint3 Triangle;
    uint MaterialIndex;
    float Start;
    float Limit;
};

struct GiSurfaceSample
{
    float3 Position;
    float Distance;
    float3 Normal;
    float Coverage;
    float3 Diffuse;
    float Occlusion;
    float3 Emission;
    float Padding1;
    float3 GeometricNormal;
    float Padding2;
};

struct GiMaterialWorkSettings
{
    uint MaterialIndex;
    uint3 Padding;
};

ConstantBuffer<GiMaterialWorkSettings> GiMaterialWork;
RWStructuredBuffer<uint2> GiMaterialWorkRanges;
RWStructuredBuffer<uint> GiMaterialWorkIndices;
RWStructuredBuffer<GiTraceRay> GiRays;
RWStructuredBuffer<GiTriangleCandidate> GiCandidates;
RWStructuredBuffer<float4> GiCandidateHits;
RWStructuredBuffer<GiSurfaceSample> GiSurfaceSamples;

#endif
