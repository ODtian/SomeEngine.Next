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

// Convert the persisted double components using integer/float32 operations. This material
// interprets normal/tangent/UV values without requiring shader float64 device features.
float ReadStandardComponent(VertexEvalContext ctx, uint stream, uint component, float fallback)
{
    if (stream == 0u) return fallback;
    uint descriptor = ctx.attributeBlobAddress + 16u + (stream - 1u) * 28u;
    uint components = ctx.pageHeap.Load(descriptor + 4u);
    uint counts = ctx.pageHeap.Load(descriptor + 12u);
    uint count = counts == 0xFFFFFFFFu ? components : ctx.pageHeap.Load(ctx.attributeBlobAddress + counts + ctx.localVertexIndex * 4u);
    if (component >= count) return fallback;
    uint values = ctx.pageHeap.Load(descriptor + 8u);
    uint format = ctx.pageHeap.Load(descriptor + 24u);
    if (format == 1u || format == 2u)
    {
        uint raw = ctx.pageHeap.Load(ctx.attributeBlobAddress + values + ctx.localVertexIndex * 4u);
        int value = (int((raw >> (component * 8u)) & 255u) << 24) >> 24;
        return max(float(value) / 127.0, -1.0);
    }
    if (format == 3u)
    {
        if (component >= 2u) return fallback;
        uint address = ctx.attributeBlobAddress + values;
        float2 origin = asfloat(ctx.pageHeap.Load2(address));
        float step = asfloat(ctx.pageHeap.Load(address + 8u));
        uint packed = ctx.pageHeap.Load(address + 12u + ctx.localVertexIndex * 4u);
        return (component == 0u ? origin.x : origin.y) + float((packed >> (component * 16u)) & 65535u) * step;
    }
    uint2 bits = ctx.pageHeap.Load2(ctx.attributeBlobAddress + values + (ctx.localVertexIndex * components + component) * 8u);
    uint exponent = (bits.y >> 20u) & 2047u;
    if (exponent == 0u) return asfloat(bits.y & 0x80000000u);
    float fraction = 1.0 + float(bits.y & 1048575u) * (1.0 / 1048576.0) + float(bits.x) * (1.0 / 4503599627370496.0);
    float value = ldexp(fraction, int(exponent) - 1023);
    return (bits.y & 0x80000000u) == 0u ? value : -value;
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
    uint normalStream = ctx.pageHeap.Load(ctx.attributeBlobAddress + 4u);
    uint tangentStream = ctx.pageHeap.Load(ctx.attributeBlobAddress + 8u);
    uint uvStream = ctx.pageHeap.Load(ctx.attributeBlobAddress + 12u);
    float3 normal = float3(ReadStandardComponent(ctx, normalStream, 0u, 0.0),
        ReadStandardComponent(ctx, normalStream, 1u, 1.0), ReadStandardComponent(ctx, normalStream, 2u, 0.0));
    float4 tangent = float4(ReadStandardComponent(ctx, tangentStream, 0u, 1.0),
        ReadStandardComponent(ctx, tangentStream, 1u, 0.0), ReadStandardComponent(ctx, tangentStream, 2u, 0.0),
        ReadStandardComponent(ctx, tangentStream, 3u, 1.0));
    float2 uv = float2(ReadStandardComponent(ctx, uvStream, 0u, 0.0), ReadStandardComponent(ctx, uvStream, 1u, 0.0));

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
