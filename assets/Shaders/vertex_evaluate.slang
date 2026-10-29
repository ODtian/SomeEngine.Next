#ifndef VERTEX_EVALUATE_SLANG
#define VERTEX_EVALUATE_SLANG

#include "vertex_evaluate_interfaces.slang"

float3 EvalObjectPosition(VertexEvalContext ctx)
{
    uint address = ctx.vertexBlobAddress + 16u + ctx.localVertexIndex * 6u;
    uint aligned = address & ~3u;
    uint2 words = ctx.pageHeap.Load2(aligned);
    uint3 position;
    if ((address & 2u) == 0u)
        position = uint3(words.x & 65535u, words.x >> 16u, words.y & 65535u);
    else
        position = uint3(words.x >> 16u, words.y & 65535u, words.y >> 16u);
    float3 origin = asfloat(ctx.pageHeap.Load3(ctx.vertexBlobAddress));
    float step = asfloat(ctx.pageHeap.Load(ctx.vertexBlobAddress + 12u));
    return origin + float3(position) * step;
}

// Interpret the default material's attribute formats using integer/float32 operations.
// Source double components are converted in the material's float32 precision.
float DecodeStandardDouble(uint2 bits)
{
    uint exponent = (bits.y >> 20u) & 2047u;
    if (exponent == 0u) return asfloat(bits.y & 0x80000000u);
    float fraction = 1.0 + float(bits.y & 1048575u) * (1.0 / 1048576.0) + float(bits.x) * (1.0 / 4503599627370496.0);
    float value = ldexp(fraction, int(exponent) - 1023);
    return (bits.y & 0x80000000u) == 0u ? value : -value;
}

float4 ReadStandardAttribute(VertexEvalContext ctx, uint stream, uint componentCount, float4 fallback)
{
    if (stream == 0u) return fallback;
    uint descriptor = ctx.attributeBlobAddress + 16u + (stream - 1u) * 28u;
    uint3 descriptorValues = ctx.pageHeap.Load3(descriptor + 4u);
    uint components = descriptorValues.x;
    uint values = descriptorValues.y;
    uint counts = descriptorValues.z;
    uint count = counts == 0xFFFFFFFFu ? components : ctx.pageHeap.Load(ctx.attributeBlobAddress + counts + ctx.localVertexIndex * 4u);
    count = min(count, componentCount);
    if (count == 0u) return fallback;
    uint address = ctx.attributeBlobAddress + values;
    uint format = ctx.pageHeap.Load(descriptor + 24u);
    if (format == 1u || format == 2u)
    {
        uint packed = ctx.pageHeap.Load(address + ctx.localVertexIndex * 4u);
        int4 signedComponents = int4(packed << uint4(24u, 16u, 8u, 0u)) >> 24;
        float4 decoded = max(float4(signedComponents) / 127.0, -1.0);
        return select(uint4(0u, 1u, 2u, 3u) < count, decoded, fallback);
    }
    if (format == 3u)
    {
        float3 originStep = asfloat(ctx.pageHeap.Load3(address));
        uint packed = ctx.pageHeap.Load(address + 12u + ctx.localVertexIndex * 4u);
        float2 decoded = originStep.xy + float2(packed & 65535u, packed >> 16u) * originStep.z;
        return float4(select(uint2(0u, 1u) < count, decoded, fallback.xy), fallback.zw);
    }
    float4 result = fallback;
    if (format == 4u)
    {
        uint vertexAddress = address + ctx.localVertexIndex * components * 4u;
        [unroll]
        for (uint component = 0u; component < 4u; component++)
            if (component < count)
                result[component] = asfloat(ctx.pageHeap.Load(vertexAddress + component * 4u));
    }
    else
    {
        uint vertexAddress = address + ctx.localVertexIndex * components * 8u;
        [unroll]
        for (uint component = 0u; component < 4u; component++)
            if (component < count)
                result[component] = DecodeStandardDouble(ctx.pageHeap.Load2(vertexAddress + component * 8u));
    }
    return result;
}

float3 EvalWorldPosition(VertexEvalContext ctx)
{
    return TransformInstanceLocalPosition(EvalObjectPosition(ctx), ctx.transform);
}

float3 RotateByTransform(float3 v, RenderInstanceTransform transform)
{
    float3 qvec = transform.Rotation.xyz;
    float qw = transform.Rotation.w;
    float3 uv = cross(qvec, v);
    float3 uuv = cross(qvec, uv);
    return v + ((uv * qw) + uuv) * 2.0;
}

struct StaticVertexAttributes
{
    float3 worldNormal;
    float4 worldTangent;
    float2 uv0;
};

StaticVertexAttributes EvalStaticVertexAttributes(VertexEvalContext ctx)
{
    uint3 streams = ctx.pageHeap.Load3(ctx.attributeBlobAddress + 4u);
    float3 normal = ReadStandardAttribute(ctx, streams.x, 3u, float4(0.0, 1.0, 0.0, 0.0)).xyz;
    float4 tangent = ReadStandardAttribute(ctx, streams.y, 4u, float4(1.0, 0.0, 0.0, 1.0));
    float2 uv = ReadStandardAttribute(ctx, streams.z, 2u, 0.0.xxxx).xy;

    StaticVertexAttributes attrs;
    float3 scale = ctx.transform.Scale * ctx.transform.Stretch;
    float3 inverseScale = float3(
        scale.x == 0.0 ? 0.0 : 1.0 / scale.x,
        scale.y == 0.0 ? 0.0 : 1.0 / scale.y,
        scale.z == 0.0 ? 0.0 : 1.0 / scale.z);
    float3 transformedNormal = RotateByTransform(normal * inverseScale, ctx.transform);
    attrs.worldNormal = dot(transformedNormal, transformedNormal) > 0.0
        ? normalize(transformedNormal) : normalize(RotateByTransform(normal, ctx.transform));
    float3 transformedTangent = RotateByTransform(tangent.xyz * scale, ctx.transform);
    transformedTangent -= attrs.worldNormal * dot(attrs.worldNormal, transformedTangent);
    float handedness = scale.x * scale.y * scale.z < 0.0 ? -tangent.w : tangent.w;
    attrs.worldTangent = float4(
        dot(transformedTangent, transformedTangent) > 0.0
            ? normalize(transformedTangent) : normalize(RotateByTransform(tangent.xyz, ctx.transform)),
        handedness);
    attrs.uv0 = uv;
    return attrs;
}

struct StandardVertex
{
    float3 position;
    float3 basePosition;
    float3 worldNormal;
    float4 worldTangent;
    float2 uv0;
};

struct StaticPositionEval : IVertexEvaluate
{
    typedef float3 Vertex;
    Vertex evaluate(VertexEvalContext ctx) { return EvalWorldPosition(ctx); }
    float3 getPosition(Vertex value) { return value; }
    Vertex readLane(Vertex value, uint lane) { return WaveReadLaneAt(value, lane); }
};

// Provides positions to the default linear surface's shadow page requests.
struct StaticSurfacePositionEval : IVertexEvaluate
{
    typedef StandardVertex Vertex;

    Vertex evaluate(VertexEvalContext ctx)
    {
        Vertex value = {};
        value.position = EvalWorldPosition(ctx);
        value.basePosition = value.position;
        return value;
    }

    float3 getPosition(Vertex value) { return value.position; }

    Vertex readLane(Vertex value, uint lane)
    {
        Vertex result = {};
        result.position = WaveReadLaneAt(value.position, lane);
        result.basePosition = result.position;
        return result;
    }
};

[MaterialDepthInputs("")]
struct StaticVertexEval : IVertexEvaluate
{
    typedef StandardVertex Vertex;

    Vertex evaluate(VertexEvalContext ctx)
    {
        float3 worldPosition = EvalWorldPosition(ctx);
        StaticVertexAttributes attrs = EvalStaticVertexAttributes(ctx);

        Vertex v;
        v.position = worldPosition;
        v.basePosition = worldPosition;
        v.worldNormal = attrs.worldNormal;
        v.worldTangent = attrs.worldTangent;
        v.uv0 = attrs.uv0;
        return v;
    }

    float3 getPosition(Vertex v)
    {
        return v.position;
    }

    Vertex readLane(Vertex value, uint lane)
    {
        Vertex result;
        result.position = WaveReadLaneAt(value.position, lane);
        result.basePosition = WaveReadLaneAt(value.basePosition, lane);
        result.worldNormal = WaveReadLaneAt(value.worldNormal, lane);
        result.worldTangent = WaveReadLaneAt(value.worldTangent, lane);
        result.uv0 = WaveReadLaneAt(value.uv0, lane);
        return result;
    }
};

struct WaveVertexEval : IVertexEvaluate
{
    typedef StandardVertex Vertex;

    Vertex evaluate(VertexEvalContext ctx)
    {
        float3 basePosition = EvalWorldPosition(ctx);
        StaticVertexAttributes attrs = EvalStaticVertexAttributes(ctx);

        float freq = 3.0;
        float amplitude = 0.3;
        float offset = sin(basePosition.x * freq) * cos(basePosition.z * freq) * amplitude;

        Vertex v;
        v.position = basePosition + float3(0.0, offset, 0.0);
        v.basePosition = basePosition;
        v.worldNormal = attrs.worldNormal;
        v.worldTangent = attrs.worldTangent;
        v.uv0 = attrs.uv0;
        return v;
    }

    float3 getPosition(Vertex v)
    {
        return v.position;
    }

    Vertex readLane(Vertex value, uint lane)
    {
        Vertex result;
        result.position = WaveReadLaneAt(value.position, lane);
        result.basePosition = WaveReadLaneAt(value.basePosition, lane);
        result.worldNormal = WaveReadLaneAt(value.worldNormal, lane);
        result.worldTangent = WaveReadLaneAt(value.worldTangent, lane);
        result.uv0 = WaveReadLaneAt(value.uv0, lane);
        return result;
    }
};

#endif // VERTEX_EVALUATE_SLANG
