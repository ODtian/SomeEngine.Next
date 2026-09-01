#ifndef VERTEX_EVALUATE_SLANG
#define VERTEX_EVALUATE_SLANG

#include "common.slang"
#include "cluster_common.slang"
#include "material_vertex_interfaces.slang"

struct VertexEvalContext
{
    ByteAddressBuffer pageHeap;
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint localVertexIndex;
    uint instanceID;
    RenderInstanceTransform transform;
    uint IsPreviousFrame;
};

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
    attrs.worldNormal = normalize(RotateByTransform(normal, ctx.transform));
    attrs.worldTangent = float4(normalize(RotateByTransform(tangent.xyz, ctx.transform)), tangent.w);
    attrs.uv0 = uvOrigin + float2(u, v) * uvStep;
    return attrs;
}

interface IVertexEvaluate : IMaterialVertex
{
    // Geometry implementations may be invoked zero, one, or multiple times for any logical
    // vertex. Correctness must not depend on call count, order, or cross-call side effects.
    DeformedVertex evaluate(VertexEvalContext ctx);
    float3 getPosition(DeformedVertex v);
    float3 getNormal(DeformedVertex v);
    float4 getTangent(DeformedVertex v);
    float2 getTexCoord0(DeformedVertex v);

    uint getCacheByteSize(uint vertexCount);
    uint getCacheByteAlignment();
    void writeCache(
        RWByteAddressBuffer cache,
        uint cacheBaseByte,
        uint vertexCount,
        uint localVertIdx,
        DeformedVertex current,
        DeformedVertex previous);
    DeformedVertex readCache(
        ByteAddressBuffer cache,
        uint cacheBaseByte,
        uint vertexCount,
        uint localVertIdx);
    DeformedVertex readPreviousCache(
        ByteAddressBuffer cache,
        uint cacheBaseByte,
        uint vertexCount,
        uint localVertIdx);
};

struct StaticDeformedVertex
{
    float3 position;
    float3 basePosition;
    float3 worldNormal;
    float4 worldTangent;
    float2 uv0;
};

struct StaticVertexEval : IVertexEvaluate
{
    typedef StaticDeformedVertex DeformedVertex;

    DeformedVertex evaluate(VertexEvalContext ctx)
    {
        float3 worldPosition = EvalWorldPosition(ctx);
        StaticVertexAttributes attrs = EvalStaticVertexAttributes(ctx);

        DeformedVertex v;
        v.position = worldPosition;
        v.basePosition = worldPosition;
        v.worldNormal = attrs.worldNormal;
        v.worldTangent = attrs.worldTangent;
        v.uv0 = attrs.uv0;
        return v;
    }

    float3 getPosition(DeformedVertex v)
    {
        return v.position;
    }

    float3 getNormal(DeformedVertex v)
    {
        return v.worldNormal;
    }

    float4 getTangent(DeformedVertex v)
    {
        return v.worldTangent;
    }

    float2 getTexCoord0(DeformedVertex v)
    {
        return v.uv0;
    }

    uint getCacheByteSize(uint vertexCount)
    {
        return vertexCount * 72u;
    }

    uint getCacheByteAlignment()
    {
        return 16u;
    }

    void writeCache(
        RWByteAddressBuffer cache,
        uint cacheBaseByte,
        uint vertexCount,
        uint localVertIdx,
        DeformedVertex current,
        DeformedVertex previous)
    {
        uint byteAddr = cacheBaseByte + localVertIdx * 72u;
        cache.Store3(byteAddr + 0u, asuint(current.position));
        cache.Store3(byteAddr + 12u, asuint(current.basePosition));
        cache.Store3(byteAddr + 24u, asuint(current.worldNormal));
        cache.Store4(byteAddr + 36u, asuint(current.worldTangent));
        cache.Store2(byteAddr + 52u, asuint(current.uv0));
        cache.Store3(byteAddr + 60u, asuint(previous.position));
    }

    DeformedVertex readCache(
        ByteAddressBuffer cache,
        uint cacheBaseByte,
        uint vertexCount,
        uint localVertIdx)
    {
        uint byteAddr = cacheBaseByte + localVertIdx * 72u;
        DeformedVertex v;
        v.position = asfloat(cache.Load3(byteAddr + 0u));
        v.basePosition = asfloat(cache.Load3(byteAddr + 12u));
        v.worldNormal = asfloat(cache.Load3(byteAddr + 24u));
        v.worldTangent = asfloat(cache.Load4(byteAddr + 36u));
        v.uv0 = asfloat(cache.Load2(byteAddr + 52u));
        return v;
    }

    DeformedVertex readPreviousCache(
        ByteAddressBuffer cache,
        uint cacheBaseByte,
        uint vertexCount,
        uint localVertIdx)
    {
        uint byteAddr = cacheBaseByte + localVertIdx * 72u;
        DeformedVertex v;
        v.position = asfloat(cache.Load3(byteAddr + 60u));
        v.basePosition = v.position;
        v.worldNormal = float3(0.0, 1.0, 0.0);
        v.worldTangent = float4(1.0, 0.0, 0.0, 1.0);
        v.uv0 = float2(0.0);
        return v;
    }
};

struct WaveDeformedVertex
{
    float3 position;
    float3 basePosition;
    float3 worldNormal;
    float4 worldTangent;
    float2 uv0;
};

struct WaveVertexEval : IVertexEvaluate
{
    typedef WaveDeformedVertex DeformedVertex;

    DeformedVertex evaluate(VertexEvalContext ctx)
    {
        float3 basePosition = EvalWorldPosition(ctx);
        StaticVertexAttributes attrs = EvalStaticVertexAttributes(ctx);

        float freq = 3.0;
        float amplitude = 0.3;
        float offset = sin(basePosition.x * freq) * cos(basePosition.z * freq) * amplitude;

        DeformedVertex v;
        v.position = basePosition + float3(0.0, offset, 0.0);
        v.basePosition = basePosition;
        v.worldNormal = attrs.worldNormal;
        v.worldTangent = attrs.worldTangent;
        v.uv0 = attrs.uv0;
        return v;
    }

    float3 getPosition(DeformedVertex v)
    {
        return v.position;
    }

    float3 getNormal(DeformedVertex v)
    {
        return v.worldNormal;
    }

    float4 getTangent(DeformedVertex v)
    {
        return v.worldTangent;
    }

    float2 getTexCoord0(DeformedVertex v)
    {
        return v.uv0;
    }

    uint getCacheByteSize(uint vertexCount)
    {
        return vertexCount * 72u;
    }

    uint getCacheByteAlignment()
    {
        return 16u;
    }

    void writeCache(
        RWByteAddressBuffer cache,
        uint cacheBaseByte,
        uint vertexCount,
        uint localVertIdx,
        DeformedVertex current,
        DeformedVertex previous)
    {
        uint byteAddr = cacheBaseByte + localVertIdx * 72u;
        cache.Store3(byteAddr + 0u, asuint(current.position));
        cache.Store3(byteAddr + 12u, asuint(current.basePosition));
        cache.Store3(byteAddr + 24u, asuint(current.worldNormal));
        cache.Store4(byteAddr + 36u, asuint(current.worldTangent));
        cache.Store2(byteAddr + 52u, asuint(current.uv0));
        cache.Store3(byteAddr + 60u, asuint(previous.position));
    }

    DeformedVertex readCache(
        ByteAddressBuffer cache,
        uint cacheBaseByte,
        uint vertexCount,
        uint localVertIdx)
    {
        uint byteAddr = cacheBaseByte + localVertIdx * 72u;
        DeformedVertex v;
        v.position = asfloat(cache.Load3(byteAddr + 0u));
        v.basePosition = asfloat(cache.Load3(byteAddr + 12u));
        v.worldNormal = asfloat(cache.Load3(byteAddr + 24u));
        v.worldTangent = asfloat(cache.Load4(byteAddr + 36u));
        v.uv0 = asfloat(cache.Load2(byteAddr + 52u));
        return v;
    }

    DeformedVertex readPreviousCache(
        ByteAddressBuffer cache,
        uint cacheBaseByte,
        uint vertexCount,
        uint localVertIdx)
    {
        uint byteAddr = cacheBaseByte + localVertIdx * 72u;
        DeformedVertex v;
        v.position = asfloat(cache.Load3(byteAddr + 60u));
        v.basePosition = v.position;
        v.worldNormal = float3(0.0, 1.0, 0.0);
        v.worldTangent = float4(1.0, 0.0, 0.0, 1.0);
        v.uv0 = float2(0.0);
        return v;
    }
};

#endif // VERTEX_EVALUATE_SLANG
