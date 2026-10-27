#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "material_interfaces.slang"
#include "cluster_instance_properties.slang"
#include "vsm_receiver_requests.slang"

StructuredBuffer<ClusterViewUniforms> MaterialViews;
ByteAddressBuffer MaterialInstanceData;
ConstantBuffer<RenderInstancePropertyMetadata> MaterialInstanceProperties;

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

struct RawMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>, ITransparentSurfaceEvaluate<SurfaceEval, Triangle>
{
    float4 evaluateColor(SurfacePixelInput input)
    {
        RawMaterialScalars values = LoadMaterialScalars<RawMaterialScalars>(input.MaterialIndex);
        float3 color = values.Tint.rgb * .25 + values.Triple * .125;
        color += float3(values.Leading, values.Trailing, values.Pair.x) * .0625;
        color += float3(values.Pair.y, float(values.Signed), float(values.Unsigned)) * .03125;
        color += values.Enabled ? float3(.125, .0625, .03125) : float3(0);
        return float4(color, values.Tint.a);
    }
    void evaluateSurface(SurfacePixelInput input, SurfaceEval surface, float3 bary,
        Triangle triangle, RWTexture2DArray<float4> outputColor)
    {
        outputColor[input.PixelCoord] = evaluateColor(input);
    }
    void evaluateDebug(uint debugMode, SurfacePixelInput input, SurfaceEval surface, float3 bary,
        Triangle triangle, RWTexture2DArray<float4> outputColor)
    {
        evaluateSurface(input, surface, bary, triangle, outputColor);
    }
    float4 evaluateTransparency(uint debugMode, SurfacePixelInput input,
        SurfaceEval surface, float3 bary, Triangle triangle)
    {
        return evaluateColor(input);
    }
    void markShadowPages(SurfacePixelInput input, SurfaceEval surface, float3 bary, Triangle triangle)
    {
        float3 position = input.VisibilityDepth == 2u ? input.PositionFromDepth : surface.Position;
        position = TransformFirstPersonWorldPosition(MaterialInstanceData, MaterialInstanceProperties, input.InstanceID,
            MaterialViews[input.PixelCoord.z], position);
        bool firstPerson = (LoadInstanceFirstPerson(MaterialInstanceData, MaterialInstanceProperties, input.InstanceID).x & 3u) == 1u;
        MarkVirtualShadowPages(position, input.PixelCoord.z, firstPerson);
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef RawMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_FORWARD_SHADING 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
