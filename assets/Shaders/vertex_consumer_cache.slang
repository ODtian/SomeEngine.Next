#ifndef VERTEX_CONSUMER_CACHE_SLANG
#define VERTEX_CONSUMER_CACHE_SLANG

#include "vertex_evaluate_interfaces.slang"
#include "wave_aggregation.slang"

struct TriangleVertices<TVertex>
{
    TVertex First;
    TVertex Second;
    TVertex Third;
};

TriangleVertices<TVE.Vertex> EvaluateTriangleVertices<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    VertexEvalContext context,
    uint3 indices)
{
    TriangleVertices<TVE.Vertex> vertices = {};
    // One decoder body shares the material's format selection code across all three corners.
    [loop]
    for (uint corner = 0u; corner < 3u; corner++)
    {
        context.localVertexIndex = corner == 0u ? indices.x : corner == 1u ? indices.y : indices.z;
        TVE.Vertex vertex = evaluator.evaluate(context);
        if (corner == 0u) vertices.First = vertex;
        else if (corner == 1u) vertices.Second = vertex;
        else vertices.Third = vertex;
    }
    return vertices;
}

// Reuse the complete user-defined vertex in coherent cluster waves. The renderer
// never enumerates its fields; the material evaluator owns cross-lane transport.
TVE.Vertex EvaluateVertexWave<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    VertexEvalContext context,
    uint visibleClusterIndex)
{
    context.visibleClusterIndex = visibleClusterIndex;
    uint4 matching = WaveMatchingLanes(visibleClusterIndex) &
        WaveMatchingLanes(context.localVertexIndex) & WaveMatchingLanes(context.IsPreviousFrame);
    uint leader = WaveMaskFirstLane(matching);
    uint lane = WaveGetLaneIndex();

    TVE.Vertex value;
    if (lane == leader)
        value = evaluator.evaluate(context);
    return evaluator.readLane(value, leader);
}

#endif
