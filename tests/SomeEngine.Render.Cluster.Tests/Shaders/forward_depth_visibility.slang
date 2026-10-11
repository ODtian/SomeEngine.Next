#include "visibility_evaluate.slang"

// Positive public depth moves shadow geometry away from the light.
[VisibilityDepth("offset")]
struct ForwardDepthOffsetVisibility<TVertex> : IVisibilityEvaluate<TVertex>
{
    typealias Varying = EmptyVisibilityVarying;
    Varying makeVarying(TVertex vertex, VisibilityVertexInput input) { return {}; }
    Varying readLane(Varying value, uint lane) { return {}; }
    Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary) { return {}; }
    Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input) { return {}; }
    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        result.Visible = 1u;
        result.Depth = input.Depth + (input.TargetPolicy != 0u ? 0.001 : 0.0);
        return result;
    }
};

#define OpaqueVisibilityEvaluate ForwardDepthOffsetVisibility
#include "shadow_reference_visibility.slang"
