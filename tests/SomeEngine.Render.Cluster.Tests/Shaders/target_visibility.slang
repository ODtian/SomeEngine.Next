#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"

float3 MaterialCameraPosition(uint viewIndex) { return Views[viewIndex].CameraPos; }
#include "standard_material.slang"

struct TargetVisibilityVarying { float2 UV : TEXCOORD0; };

[VisibilityDepth(TARGET_VISIBILITY_DEPTH)]
struct TargetVisibility : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = TargetVisibilityVarying;
    Varying makeVarying(StandardVertex vertex, VisibilityVertexInput input)
    { Varying value; value.UV = vertex.uv0; return value; }
    Varying readLane(Varying value, uint lane)
    { Varying result; result.UV = WaveReadLaneAt(value.UV, lane); return result; }
    Varying interpolateVertex(Varying a, Varying b, Varying c, float3 bary)
    { Varying result; result.UV = a.UV * bary.x + b.UV * bary.y + c.UV * bary.z; return result; }
    Varying interpolatePixel(Varying a, Varying b, Varying c, VisibilityInterpolationInput input)
    { return interpolateVertex(a, b, c, input.PerspectiveBarycentric); }

    VisibilityPixelResult evaluate(VisibilityPixelInput input, Varying varying)
    {
        // Quad neighbours remain available while only part of a quad survives
        // the target's page, receiver and early-depth tests.
        float2 footprint = abs(QuadReadAcrossX(varying.UV) - varying.UV) +
            abs(QuadReadAcrossY(varying.UV) - varying.UV);
        float stripe = abs(frac(varying.UV.x * 8.0) - 0.5);
        VisibilityPixelResult result;
        result.Visible = input.TargetPolicy == 0u ||
            stripe > min(0.2 + footprint.x * 8.0, 0.45) ? 1u : 0u;
        result.Depth = input.Depth;
#if TARGET_VISIBILITY_OFFSET
        if (input.TargetPolicy != 0u)
            result.Depth = saturate(input.Depth + (varying.UV.y - 0.5) * 0.004);
#endif
        return result;
    }
};

struct TargetVisibilityMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    typealias Data = StandardSurfaceData;
    SamplerState MaterialSampler;

    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        StandardMaterial material;
        material.MaterialSampler = MaterialSampler;
        return material.evaluateSurface(pixel, surface, bary, triangle, receiver);
    }

    float4 shadeSurface(uint3 pixel, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        float visibility = ReadVirtualShadowProjectedVisibility(pixel, 0u,
            uint3(Uniforms.ScreenWidth, Uniforms.ScreenHeight, Uniforms.ViewCount));
        return float4(visibility, 1.0, 0.0, 1.0);
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef TargetVisibility VisibilityEvaluate;
typedef TargetVisibilityMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SURFACE_RECEIVERS 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
