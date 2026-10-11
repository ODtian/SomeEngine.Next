#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "material_interfaces.slang"

[MaterialScalars]
struct RawMaterialScalars
{
#if RAW_SCALAR_REVERSE
    bool Enabled;
    float4 Tint;
    uint Unsigned;
    float3 Triple;
    float Trailing;
    int Signed;
    float2 Pair;
    float Leading;
#else
    float Leading;
    float3 Triple;
    float Trailing;
    float2 Pair;
    float4 Tint;
    int Signed;
    uint Unsigned;
    bool Enabled;
#endif
};

[MaterialSurfaceData]
struct RawMaterialSurface { float4 Color; };

struct RawMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    typealias Data = RawMaterialSurface;
    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        receiver = (SurfaceReceiver)0;
        receiver.Position = pixel.PositionFromDepth;
        receiver.Depth = pixel.Depth;
        receiver.ShadingNormal = surface.Normal;
        RawMaterialScalars values = LoadMaterialScalars<RawMaterialScalars>(pixel.MaterialIndex);
        float3 color = values.Tint.rgb * .25 + values.Triple * .125;
        color += float3(values.Leading, values.Trailing, values.Pair.x) * .0625;
        color += float3(values.Pair.y, float(values.Signed), float(values.Unsigned)) * .03125;
        color += values.Enabled ? float3(.125, .0625, .03125) : float3(0);
        Data data; data.Color = float4(color, values.Tint.a); return data;
    }
    float4 shadeSurface(uint3 pixelCoord, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        return data.Color;
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef RawMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
