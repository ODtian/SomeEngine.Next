#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"

float3 MaterialCameraPosition(uint viewIndex) { return Views[viewIndex].CameraPos; }
#include "standard_material.slang"

struct ShadowReferenceVisibilityMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    typealias Data = StandardSurfaceData;
    Texture2D AlbedoMap;
    Texture2D NormalMap;
    Texture2D ARMMap;
    SamplerState MaterialSampler;

    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        StandardMaterial material;
        material.AlbedoMap = AlbedoMap;
        material.NormalMap = NormalMap;
        material.ARMMap = ARMMap;
        material.MaterialSampler = MaterialSampler;
        return material.evaluateSurface(pixel, surface, bary, triangle, receiver);
    }

    float4 shadeSurface(uint3 pixel, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        GPULight light = LightBuffer[0];
        float3 toLight = LightingViews[pixel.z].DirectionalCount > 0u
            ? -light.Direction : light.Position - receiver.Position;
        uint facesLight = dot(receiver.ShadingNormal, Views[pixel.z].CameraPos - receiver.Position) > 0.0 &&
            dot(receiver.ShadingNormal, normalize(toLight)) >= 0.15 ? 1u : 0u;
        float visibility = ReadVirtualShadowProjectedVisibility(pixel, 0u,
            uint3(Uniforms.ScreenWidth, Uniforms.ScreenHeight, Uniforms.ViewCount));
        return float4(visibility, 1.0, float(facesLight), 1.0);
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef ShadowReferenceVisibilityMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SURFACE_RECEIVERS 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
