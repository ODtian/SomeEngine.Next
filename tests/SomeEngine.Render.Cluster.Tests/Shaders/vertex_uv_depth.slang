#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"


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
[MaterialSurfaceData]
struct VertexUvSurfaceData { float4 Color; };

struct VertexUvSurface : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    typealias Data = VertexUvSurfaceData;
    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        receiver = (SurfaceReceiver)0;
        receiver.Position = surface.Position;
        receiver.Depth = pixel.Depth;
        receiver.ShadingNormal = surface.Normal;
        receiver.GeometricNormal = surface.Normal;
        Data data;
        data.Color = float4(surface.TexCoord, surface.TexCoord.x * .5 + surface.TexCoord.y * .25, 1);
        return data;
    }
    float4 shadeSurface(uint3 pixel, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        return data.Color;
    }
};

typedef VertexUvSurface SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
