#ifndef GI_MESH_TRIANGLES_SLANG
#define GI_MESH_TRIANGLES_SLANG

#include "gi_mesh_distance_field.slang"
#include "vertex_evaluate_interfaces.slang"

uint GiMeshVertexIndex(ByteAddressBuffer pages, uint address)
{
    return (pages.Load(address & ~3u) >> ((address & 3u) * 8u)) & 255u;
}

// Uses the selected material's vertex evaluator; the shared code reads only Cluster topology.
bool IntersectGiMeshTriangle<T : IVertexEvaluate>(T vertex, VertexEvalContext context,
    uint page, uint clusterIndex, uint triangleIndex, float3 origin, float3 direction,
    float start, float limit, out float distance, out float3 barycentric)
{
    ByteAddressBuffer pages = context.pageHeap;
    uint cluster = page + pages.Load(page + 16u) + clusterIndex * 96u;
    uint blob = page + pages.Load(page + 20u) + pages.Load(cluster);
    uint firstTriangle = pages.Load(cluster + 56u) >> 16u;
    uint firstIndex = page + pages.Load(page + 24u) + (firstTriangle + triangleIndex) * 3u;
    LoadVertexData(context, blob);
    context.localVertexIndex = GiMeshVertexIndex(pages, firstIndex);
    float3 first = vertex.getPosition(vertex.evaluate(context));
    context.localVertexIndex = GiMeshVertexIndex(pages, firstIndex + 1u);
    float3 second = vertex.getPosition(vertex.evaluate(context));
    context.localVertexIndex = GiMeshVertexIndex(pages, firstIndex + 2u);
    float3 third = vertex.getPosition(vertex.evaluate(context));
    return IntersectGiTriangle(first, second, third, origin, direction, start, limit, distance, barycentric);
}

#endif
