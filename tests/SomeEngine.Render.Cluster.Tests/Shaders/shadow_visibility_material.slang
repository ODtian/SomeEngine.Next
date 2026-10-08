#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "render_lighting.slang"
#include "vsm_material.slang"
#include "vsm_soft_sampling.slang"
#include "vsm_receiver_pixel.slang"

StructuredBuffer<GPULight> LightBuffer;

struct ShadowVisibilityVarying { float2 UV : TEXCOORD0; };
[VisibilityDepth("masked")]
struct ShadowVisibilityMask : IVisibilityEvaluate<StandardVertex>
{
    typealias Varying = ShadowVisibilityVarying;
    Texture2D<float4> AlbedoMap;
    SamplerState MaterialSampler;
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
        VisibilityPixelResult result;
        result.Visible = SHADOW_ALPHA_CUTOFF < 0.0 ||
            AlbedoMap.SampleLevel(MaterialSampler, varying.UV, 0.0).a >= SHADOW_ALPHA_CUTOFF ? 1u : 0u;
        result.Depth = input.Depth;
        return result;
    }
};

[MaterialSurfaceData]
struct ShadowVisibilityData { uint ShadingFacesLight; };

struct ShadowVisibilityMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    typealias Data = ShadowVisibilityData;
    Texture2D<float4> NormalMap;
    SamplerState MaterialSampler;

    float3 CalculateShadingNormal(SurfaceEval surface)
    {
        float3 normal = normalize(surface.Normal);
        float3 fallbackTangent = normalize(cross(normal, abs(normal.y) < 0.99 ? float3(0,1,0) : float3(1,0,0)));
        float3 tangent = SafeNormalize(surface.Tangent.xyz - normal * dot(normal, surface.Tangent.xyz), fallbackTangent);
        float3 tangentNormal = NormalMap.SampleLevel(MaterialSampler, surface.TexCoord, 0.0).xyz * 2.0 - 1.0;
        tangentNormal.xy *= SHADOW_NORMAL_SCALE;
        return normalize(tangent * tangentNormal.x +
            cross(normal, tangent) * surface.Tangent.w * tangentNormal.y + normal * tangentNormal.z);
    }

    Data evaluateSurface(SurfacePixel pixel, SurfaceEval surface, float3 bary,
        Triangle triangle, out SurfaceReceiver receiver)
    {
        receiver = (SurfaceReceiver)0;
        receiver.Position = pixel.PositionFromDepth;
        receiver.Depth = pixel.Depth;
        receiver.ShadingNormal = CalculateShadingNormal(surface);
        receiver.GeometricNormal = SafeNormalize(cross(triangle.Position1 - triangle.Position0,
            triangle.Position2 - triangle.Position0), normalize(surface.Normal));
        if (dot(receiver.GeometricNormal, surface.Normal) < 0.0) receiver.GeometricNormal = -receiver.GeometricNormal;
        receiver.Flags = SURFACE_RECEIVER_LIGHTING;
        Data data;
        data.ShadingFacesLight = dot(receiver.ShadingNormal, Views[pixel.PixelCoord.z].CameraPos - receiver.Position) > 0.0 &&
            dot(receiver.ShadingNormal, -LightBuffer[0].Direction) >= 0.15 ? 1u : 0u;
        return data;
    }
    float4 shadeSurface(uint3 pixel, uint debugMode, Data data, SurfaceReceiver receiver)
    {
        uint levelCount = min(VirtualShadowSampling.DirectionalClipmapLevels, VirtualShadowSampling.ViewCount);
        float visibility = 1.0;
        if (SurfaceReceiverReceivesDirectionalShadow(receiver, -LightBuffer[0].Direction,
            VirtualShadowViews[0].SourceAngleRadians, VirtualShadowSampling.CullBackfacingPixels != 0u))
        {
            float biasLength = CalculateVirtualShadowNormalBiasLength(VirtualShadowSampling.ReceiverProjection,
                receiver.Position, VirtualShadowSampling.NormalBias, VirtualShadowViews[0].WorldUnitsPerCentimeter);
            float3 position = receiver.Position + receiver.ShadingNormal * biasLength;
            VirtualShadowProjectionResult projection = SampleVirtualShadowDirectionalSoft(VirtualShadowViews,
                VirtualShadowPageTable, VirtualShadowAtlas, 0u, levelCount, position, receiver.ShadingNormal,
                VirtualShadowDynamicResolutionBias[0].x,
                CalculateVirtualShadowReceiverResolutionBias(VirtualShadowSampling.ReceiverProjection, receiver.Position),
                VirtualShadowSampling.BlockerSampleCount, VirtualShadowSampling.FilterSampleCount,
                VirtualShadowSampling.MaxFilterRadiusTexels, pixel.xy, VirtualShadowSampling.FrameIndex);
            visibility = projection.Visibility * SampleDirectionalVirtualShadowPrefiltered(VirtualShadowViews,
                VirtualShadowPageTable, VirtualShadowPrefilteredAtlas, 0u, levelCount, position).Visibility;
        }
        return float4(visibility, 1.0, data.ShadingFacesLight != 0u ? 1.0 : 0.0, 1.0);
    }

};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef ShadowVisibilityMask VisibilityEvaluate;
typedef ShadowVisibilityMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
