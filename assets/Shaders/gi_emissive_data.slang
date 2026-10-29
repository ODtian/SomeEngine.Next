#ifndef GI_EMISSIVE_DATA_SLANG
#define GI_EMISSIVE_DATA_SLANG

struct GiEmissiveSettings
{
    uint Width;
    uint Height;
    uint ViewCount;
    uint Frame;
    uint EmitterCapacity;
    uint InstanceCount;
    uint MaterialCount;
    uint UseHistory;
};

struct GiEmissiveTriangle
{
    float4 PositionArea;
    float4 EdgeUFlux;
    float4 EdgeV;
    float4 Emission;
    float4 SamplePositionRadius;
    uint4 TriangleInstance;
    uint4 Material;
};

struct GiDirectLightProposal
{
    uint Emitter;
    float Probability;
};

struct GiDirectLightGuide
{
    uint Emitter;
    float Brightness;
    uint2 Padding;
    uint4 TriangleInstance;
};

ConstantBuffer<GiEmissiveSettings> GiEmissive;
RWStructuredBuffer<float4> GiEmissionBounds;
RWStructuredBuffer<GiEmissiveTriangle> GiEmissiveTriangles;

float4 GiMaterialEmissionBound(uint materialIndex)
{
    return GiEmissionBounds[(GiEmissive.Frame & 1u) * GiEmissive.MaterialCount + materialIndex];
}

float GiEmissionBrightness(float3 value)
{
    return dot(value, float3(0.2126, 0.7152, 0.0722));
}

#endif
