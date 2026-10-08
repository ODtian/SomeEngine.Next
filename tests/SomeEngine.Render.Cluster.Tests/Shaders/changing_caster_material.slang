#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "unlit_material.slang"

// Fixture-owned single-material mesh interpretation. BoundsExpansion=.5 covers
// the authored world-space shift; positions stay inside that finite bound.
struct OffsetVertex : IVertexEvaluate
{
    typealias Vertex = StandardVertex;
    Vertex evaluate(VertexEvalContext context)
    {
        StaticVertexEval source;
        Vertex result = source.evaluate(context);
        uint material = GetClusterMaterialSlotOffset(context.instanceID);
        UnlitScalars values = LoadMaterialScalars<UnlitScalars>(material);
        result.position.x += values.BaseColorTint.w * .4;
        return result;
    }
    float3 getPosition(Vertex value) { return value.position; }
    Vertex readLane(Vertex value, uint lane)
    {
        StaticVertexEval source;
        return source.readLane(value, lane);
    }
};

struct MaskUv { float2 UV : TEXCOORD0; };
[VisibilityDepth("masked")]
struct PatternMask : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = MaskUv;
    Texture2D<float4> AlbedoMap;
    SamplerState MaterialSampler;
    Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input) { Varying value; value.UV = vertex.uv0; return value; }
    Varying readLane(Varying value, uint lane) { Varying result; result.UV = WaveReadLaneAt(value.UV, lane); return result; }
    Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary)
    { Varying result; result.UV = a.UV * bary.x + b.UV * bary.y + c.UV * bary.z; return result; }
    Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input)
    { return interpolateVertex(a,b,c,input.PerspectiveBarycentric); }
    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        result.Visible = AlbedoMap.SampleLevel(MaterialSampler, varying.UV, 0).a >= .5 ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
};

typedef OffsetVertex VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef PatternMask VisibilityEvaluate;
typedef UnlitMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
