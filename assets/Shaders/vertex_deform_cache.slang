#ifndef VERTEX_DEFORM_CACHE_SLANG
#define VERTEX_DEFORM_CACHE_SLANG

#include "vertex_evaluate_interfaces.slang"

// The user chooses both the vertex value and its byte representation. A region can
// contain a header, packed attributes, shared data, and current/previous results.
interface IVertexDeformCache : IVertexEvaluate
{
    uint getCacheByteSize(uint vertexCount);
    uint getCacheByteAlignment();
    void writeCache(RWByteAddressBuffer cache, uint address, uint vertexCount,
        uint localVertexIndex, Vertex current, Vertex previous);
    Vertex readCache(ByteAddressBuffer cache, uint address, uint vertexCount,
        VertexEvalContext context);
};

static const uint DEFORM_CACHE_OVERFLOW = 0xFFFFFFFDu;
ByteAddressBuffer VertexDeformCache;
StructuredBuffer<uint2> VertexDeformRegions;

struct CachedVertexEval<T : IVertexDeformCache> : IVertexEvaluate
{
    typedef T.Vertex Vertex;
    T vertex;

    Vertex evaluate(VertexEvalContext context)
    {
        uint2 region = VertexDeformRegions[context.visibleClusterIndex];
        if (region.x == DEFORM_CACHE_OVERFLOW)
        {
            Vertex empty = {};
            return empty;
        }
        return vertex.readCache(VertexDeformCache, region.x, region.y, context);
    }

    float3 getPosition(Vertex value) { return vertex.getPosition(value); }
    Vertex readLane(Vertex value, uint lane) { return vertex.readLane(value, lane); }
};

#endif
