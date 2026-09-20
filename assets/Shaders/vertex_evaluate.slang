#ifndef VERTEX_EVALUATE_SLANG
#define VERTEX_EVALUATE_SLANG

#include "vertex_evaluate_interfaces.slang"

uint LoadStandardVertexBits(VertexEvalContext ctx, uint bitOffset, uint bitCount)
{
    if (bitCount == 0u)
        return 0u;

    uint wordAddress = ctx.vertexBlobAddress + ((bitOffset >> 5u) * 4u);
    uint shift = bitOffset & 31u;
    uint value = ctx.pageHeap.Load(wordAddress) >> shift;
    if (shift + bitCount > 32u)
        value |= ctx.pageHeap.Load(wordAddress + 4u) << (32u - shift);
    return bitCount == 32u ? value : value & ((1u << bitCount) - 1u);
}

struct StandardVertexWidths
{
    uint x;
    uint y;
    uint z;
    uint u;
    uint v;
};

StandardVertexWidths LoadStandardVertexWidths(VertexEvalContext ctx)
{
    uint packed = ctx.pageHeap.Load(ctx.vertexBlobAddress + 12u);
    StandardVertexWidths widths;
    widths.x = packed & 31u;
    widths.y = (packed >> 5u) & 31u;
    widths.z = (packed >> 10u) & 31u;
    widths.u = (packed >> 15u) & 31u;
    widths.v = (packed >> 20u) & 31u;
    return widths;
}

uint StandardVertexRecordBitAddress(VertexEvalContext ctx)
{
    uint recordBitStride = ctx.pageHeap.Load(ctx.vertexBlobAddress + 16u);
    uint dataBitOffset = ctx.pageHeap.Load(ctx.vertexBlobAddress + 20u);
    return dataBitOffset + ctx.localVertexIndex * recordBitStride;
}

float3 DecodeStandardSnorm3(uint raw)
{
    int3 value;
    value.x = (int(raw & 0xFFu) << 24) >> 24;
    value.y = (int((raw >> 8u) & 0xFFu) << 24) >> 24;
    value.z = (int((raw >> 16u) & 0xFFu) << 24) >> 24;
    return max(float3(value) / 127.0, float3(-1.0));
}

float4 DecodeStandardSnorm4(uint raw)
{
    int4 value;
    value.x = (int(raw & 0xFFu) << 24) >> 24;
    value.y = (int((raw >> 8u) & 0xFFu) << 24) >> 24;
    value.z = (int((raw >> 16u) & 0xFFu) << 24) >> 24;
    value.w = (int((raw >> 24u) & 0xFFu) << 24) >> 24;
    return max(float4(value) / 127.0, float4(-1.0));
}

float3 EvalObjectPosition(VertexEvalContext ctx)
{
    StandardVertexWidths widths = LoadStandardVertexWidths(ctx);
    uint bit = StandardVertexRecordBitAddress(ctx);
    uint x = LoadStandardVertexBits(ctx, bit, widths.x); bit += widths.x;
    uint y = LoadStandardVertexBits(ctx, bit, widths.y); bit += widths.y;
    uint z = LoadStandardVertexBits(ctx, bit, widths.z);
    float3 origin = asfloat(ctx.pageHeap.Load3(ctx.vertexBlobAddress + 24u));
    float step = asfloat(ctx.pageHeap.Load(ctx.vertexBlobAddress + 36u));
    return origin + float3(x, y, z) * step;
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
    StandardVertexWidths widths = LoadStandardVertexWidths(ctx);
    uint bit = StandardVertexRecordBitAddress(ctx) + widths.x + widths.y + widths.z;
    float3 normal = DecodeStandardSnorm3(LoadStandardVertexBits(ctx, bit, 24u)); bit += 24u;
    float4 tangent = DecodeStandardSnorm4(LoadStandardVertexBits(ctx, bit, 32u)); bit += 32u;
    uint u = LoadStandardVertexBits(ctx, bit, widths.u); bit += widths.u;
    uint v = LoadStandardVertexBits(ctx, bit, widths.v);
    float2 uvOrigin = asfloat(ctx.pageHeap.Load2(ctx.vertexBlobAddress + 40u));
    float uvStep = asfloat(ctx.pageHeap.Load(ctx.vertexBlobAddress + 48u));

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
    attrs.uv0 = uvOrigin + float2(u, v) * uvStep;
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
