#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "material_interfaces.slang"

[MaterialScalars]
struct EmissiveScalars
{
    float4 BaseColorTint;
    float MetallicFactor;
    float Roughness;
    float3 EmissiveFactor;
};

struct EmissiveMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    void evaluateSurface(uint3 pixelCoord, uint instanceID, uint materialIndex,
        SurfaceEval surface, float3 bary, Triangle triangle, RWTexture2DArray<float4> outputColor)
    {
        EmissiveScalars values = LoadMaterialScalars<EmissiveScalars>(materialIndex);
        outputColor[pixelCoord] = float4(values.EmissiveFactor, 1.0);
    }

    void evaluateDebug(uint debugMode, uint3 pixelCoord, uint instanceID, uint materialIndex,
        SurfaceEval surface, float3 bary, Triangle triangle, RWTexture2DArray<float4> outputColor)
    {
        evaluateSurface(pixelCoord, instanceID, materialIndex, surface, bary, triangle, outputColor);
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef EmissiveMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
