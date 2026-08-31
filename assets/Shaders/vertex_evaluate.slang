#ifndef VERTEX_EVALUATE_SLANG
#define VERTEX_EVALUATE_SLANG

#include "common.slang"
#include "cluster_common.slang"
#include "material_vertex_interfaces.slang"

struct VertexEvalContext
{
    ByteAddressBuffer pageHeap;
    uint pageOffset;
    uint posOffset;
    uint attrOffset;
    uint vertexStride;
    uint idxOffset;
    uint vStart;
    uint totalVertexCount;
    uint localVertIdx;
    int3 intBase;
    uint instanceID;
    RenderInstanceTransform transform;
    uint IsPreviousFrame;

    uint getVertexByteAddress()
    {
        return pageOffset + attrOffset + (vStart + localVertIdx) * vertexStride;
    }
};

float3 EvalObjectPosition(VertexEvalContext ctx)
{
    return FetchVertexPosition(
        ctx.pageHeap,
        ctx.pageOffset,
        ctx.posOffset,
        ctx.vStart,
        ctx.localVertIdx,
        ctx.intBase);
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
    uint vertexBase = ctx.getVertexByteAddress();
    float3 normal = DecodeSnorm8x3(LoadStreamBytes(ctx.pageHeap, vertexBase + 0u, 3u));
    float4 tangent = DecodeSnorm8x4(LoadStreamBytes(ctx.pageHeap, vertexBase + 3u, 4u));

    StaticVertexAttributes attrs;
    attrs.worldNormal = normalize(RotateByTransform(normal, ctx.transform));
    attrs.worldTangent = float4(normalize(RotateByTransform(tangent.xyz, ctx.transform)), tangent.w);
    attrs.uv0 = DecodeFloat16x2(LoadStreamBytes(ctx.pageHeap, vertexBase + 7u, 4u));
    return attrs;
}

interface IVertexEvaluate : IMaterialVertex
{
    DeformedVertex evaluate(VertexEvalContext ctx);
    float3 getPosition(DeformedVertex v);
    float3 getNormal(DeformedVertex v);
    float4 getTangent(DeformedVertex v);
    float2 getTexCoord0(DeformedVertex v);

    uint getCacheByteSize(uint vertexCount);
    uint getCacheByteAlignment();
    bool tryGetConservativeObjectBounds(
        VertexEvalContext context,
        out float3 minimum,
        out float3 maximum);
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

    bool tryGetConservativeObjectBounds(
        VertexEvalContext context,
        out float3 minimum,
        out float3 maximum)
    {
        float3 position = EvalObjectPosition(context);
        minimum = position;
        maximum = position;
        return true;
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

    bool tryGetConservativeObjectBounds(
        VertexEvalContext context,
        out float3 minimum,
        out float3 maximum)
    {
        float3 position = EvalObjectPosition(context);
        minimum = position - float3(0.0, 0.3, 0.0);
        maximum = position + float3(0.0, 0.3, 0.0);
        return true;
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
