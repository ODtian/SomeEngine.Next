#ifndef VERTEX_EVALUATE_INTERFACES_SLANG
#define VERTEX_EVALUATE_INTERFACES_SLANG

#include "common.slang"
#include "cluster_common.slang"

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

// The evaluated vertex is owned entirely by the user shader. Cluster code may ask
// only for the geometric position needed by its algorithms and for a wave-lane copy;
// it never enumerates or assigns fields of Vertex.
interface IVertexEvaluate
{
    associatedtype Vertex;

    // Geometry implementations may be invoked zero, one, or multiple times for any logical
    // vertex. Correctness must not depend on call count, order, or cross-call side effects.
    Vertex evaluate(VertexEvalContext ctx);
    float3 getPosition(Vertex value);
    Vertex readLane(Vertex value, uint lane);
};

#endif
