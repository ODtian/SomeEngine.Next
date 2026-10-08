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

[MaterialSurfaceData]
struct EmissiveSurfaceData { float3 Color; };

struct EmissiveMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    typealias Data = EmissiveSurfaceData;
    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        receiver = (SurfaceReceiver)0;
        receiver.Position = pixel.PositionFromDepth;
        receiver.Depth = pixel.Depth;
        receiver.ShadingNormal = surface.Normal;
        EmissiveScalars values = LoadMaterialScalars<EmissiveScalars>(pixel.MaterialIndex);
        Data data; data.Color = values.EmissiveFactor; return data;
    }
    void shadeSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver,
        RWTexture2DArray<float4> outputColor)
    {
        outputColor[pixelCoord] = float4(data.Color, 1.0);
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef EmissiveMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
