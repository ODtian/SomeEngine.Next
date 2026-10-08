#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "unlit_material.slang"

struct VertexUv { float2 UV : TEXCOORD0; };
[VisibilityDepth("offset")]
struct VertexUvDepth : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = VertexUv;
    Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input) { Varying result; result.UV = vertex.uv0; return result; }
    Varying readLane(Varying value, uint lane) { Varying result; result.UV = WaveReadLaneAt(value.UV, lane); return result; }
    Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary)
    { Varying result; result.UV = a.UV * bary.x + b.UV * bary.y + c.UV * bary.z; return result; }
    Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input)
    { return interpolateVertex(a,b,c,input.PerspectiveBarycentric); }
    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        VisibilityPixelResult result;
        result.Visible = 1u;
        result.Depth = varying.UV.x * .5 + varying.UV.y * .25;
        return result;
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef VertexUvDepth VisibilityEvaluate;
typedef UnlitMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
