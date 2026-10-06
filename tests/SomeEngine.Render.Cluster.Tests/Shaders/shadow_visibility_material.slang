#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"
#include "render_lighting.slang"
#include "vsm_material.slang"
#include "vsm_receiver_pixel.slang"

StructuredBuffer<GPULight> LightBuffer;

struct ShadowVisibilityVarying { float2 UV : TEXCOORD0; };
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

struct ShadowVisibilityMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>
{
    Texture2D<float4> NormalMap;
    SamplerState MaterialSampler;

    void evaluateDebug(uint debugMode, uint3 pixel, uint instance, uint material, SurfaceEval surface,
        float3 bary, Triangle triangle, RWTexture2DArray<float4> color)
    {
        evaluateSurface(pixel, instance, material, surface, bary, triangle, color);
    }

    void evaluateSurface(uint3 pixel, uint instance, uint material, SurfaceEval surface,
        float3 bary, Triangle triangle, RWTexture2DArray<float4> color)
    {
        float3 normal = normalize(surface.Normal);
        float3 fallbackTangent = normalize(cross(normal, abs(normal.y) < 0.99 ? float3(0,1,0) : float3(1,0,0)));
        float3 tangent = SafeNormalize(surface.Tangent.xyz - normal * dot(normal, surface.Tangent.xyz), fallbackTangent);
        float3 tangentNormal = NormalMap.SampleLevel(MaterialSampler, surface.TexCoord, 0.0).xyz * 2.0 - 1.0;
        tangentNormal.xy *= SHADOW_NORMAL_SCALE;
        float3 mappedNormal = normalize(tangent * tangentNormal.x +
            cross(normal, tangent) * surface.Tangent.w * tangentNormal.y + normal * tangentNormal.z);
        ClusterViewUniforms view = Views[pixel.z];
        VirtualShadowReceiverPixel receiver = ReconstructVirtualShadowReceiverPixel(
            DepthBuffer, pixel, uint2(view.ScreenWidth, view.ScreenHeight),
            DepthBuffer.Load(int4(pixel, 0)), view.ClipToWorld);
        float3 bias = mappedNormal * VirtualShadowSampling.NormalBias;
        float3 position = surface.Position + bias;
        float visibility = SampleDirectionalVirtualShadowMap(VirtualShadowViews,
            VirtualShadowPageTable, VirtualShadowAtlas, 0u, VirtualShadowSampling.DirectionalClipmapLevels,
            VirtualShadowDynamicResolutionBias[0].x, position,
            position + receiver.PositionDx - receiver.Position,
            position + receiver.PositionDy - receiver.Position,
            VirtualShadowSampling.BlockerSampleCount, VirtualShadowSampling.FilterSampleCount,
            VirtualShadowSampling.BlockerSearchRadiusTexels, VirtualShadowSampling.MaxFilterRadiusTexels);
        bool shadingFacesLight = dot(mappedNormal, view.CameraPos - surface.Position) > 0.0 &&
            dot(mappedNormal, -LightBuffer[0].Direction) >= 0.15;
        color[pixel] = float4(visibility, 1.0, shadingFacesLight ? 1.0 : 0.0, 1.0);
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef ShadowVisibilityMask VisibilityEvaluate;
typedef ShadowVisibilityMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#include "cluster_material_entries.slang"
