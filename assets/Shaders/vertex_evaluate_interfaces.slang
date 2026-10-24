#ifndef VERTEX_EVALUATE_INTERFACES_SLANG
#define VERTEX_EVALUATE_INTERFACES_SLANG

#include "common.slang"
#include "render_instance_transform.slang"

struct VertexEvalContext
{
    ByteAddressBuffer pageHeap;
    uint vertexBlobAddress;
    uint vertexBlobSize;
    uint attributeBlobAddress;
    uint attributeBlobSize;
    uint localVertexIndex;
    uint visibleClusterIndex;
    uint instanceID;
    RenderInstanceTransform transform;
    uint IsPreviousFrame;
};

void LoadVertexData(inout VertexEvalContext context, uint descriptorAddress)
{
    uint4 data = context.pageHeap.Load4(descriptorAddress);
    context.vertexBlobAddress = descriptorAddress + data.x;
    context.vertexBlobSize = data.y;
    context.attributeBlobAddress = descriptorAddress + data.z;
    context.attributeBlobSize = data.w;
}

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
