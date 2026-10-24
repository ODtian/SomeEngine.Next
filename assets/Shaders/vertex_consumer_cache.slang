#ifndef VERTEX_CONSUMER_CACHE_SLANG
#define VERTEX_CONSUMER_CACHE_SLANG

#include "vertex_evaluate_interfaces.slang"
#include "wave_aggregation.slang"

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
