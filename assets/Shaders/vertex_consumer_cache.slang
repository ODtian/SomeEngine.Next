#ifndef VERTEX_CONSUMER_CACHE_SLANG
#define VERTEX_CONSUMER_CACHE_SLANG

#include "vertex_evaluate.slang"
#include "triangle_surface.slang"
#include "wave_aggregation.slang"

// Reuse vertices in coherent cluster waves. Searching all equivalence classes
// in mixed-cluster waves is more expensive than evaluating their vertices.
SurfaceVertex EvaluateSurfaceVertexWave<TVE : IVertexEvaluate>(
    uniform TVE evaluator,
    VertexEvalContext context,
    uint visibleClusterIndex)
{
    if (!WaveActiveAllEqual(visibleClusterIndex))
    {
        TVE.Vertex value = evaluator.evaluate(context);
        SurfaceVertex result;
        result.Position = evaluator.getPosition(value);
        result.Normal = evaluator.getNormal(value);
        result.Tangent = evaluator.getTangent(value);
        result.TexCoord = evaluator.getTexCoord0(value);
        return result;
    }
    uint4 matching = WaveMatchingLanes(context.localVertexIndex);
    uint leader = WaveMaskFirstLane(matching);
    uint lane = WaveGetLaneIndex();

    float3 position = 0.0.xxx;
    float3 normal = float3(0.0, 0.0, 1.0);
    float4 tangent = float4(1.0, 0.0, 0.0, 1.0);
    float2 texCoord = 0.0.xx;
    if (lane == leader)
    {
        TVE.Vertex value = evaluator.evaluate(context);
        position = evaluator.getPosition(value);
        normal = evaluator.getNormal(value);
        tangent = evaluator.getTangent(value);
        texCoord = evaluator.getTexCoord0(value);
    }

    SurfaceVertex result;
    result.Position = WaveReadLaneAt(position, leader);
    result.Normal = WaveReadLaneAt(normal, leader);
    result.Tangent = WaveReadLaneAt(tangent, leader);
    result.TexCoord = WaveReadLaneAt(texCoord, leader);
    return result;
}

#endif
