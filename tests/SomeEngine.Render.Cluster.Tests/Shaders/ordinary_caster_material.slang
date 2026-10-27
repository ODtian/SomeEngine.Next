#include "triangle_surface.slang"
#include "unlit_material.slang"

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
[VisibilityDepth("masked")]
struct TextureVisibility : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = EmptyVisibilityVarying;
    Texture2D<float4> AlbedoMap;
    SamplerState MaterialSampler;
    Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input) { return {}; }
    Varying readLane(Varying value, uint lane) { return {}; }
    Varying interpolateVertex(Varying first, Varying second, Varying third, float3 bary) { return {}; }
    Varying interpolatePixel(Varying first, Varying second, Varying third, VisibilityInterpolationInput input) { return {}; }
    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        result.Visible = AlbedoMap.SampleLevel(MaterialSampler, float2(0.5, 0.5), 0).a >= 0.5 ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
}
typedef TextureVisibility VisibilityEvaluate;
typedef UnlitMaterial SurfaceEvaluate;

#define CLUSTER_FORWARD_SHADING 1
#include "Pipeline/cluster_material.slang"
