#include "generated/render_instance_layout.slang"
#include "cluster_shade_pipeline.slang"
#include "triangle_surface.slang"

float3 MaterialCameraPosition(uint viewIndex) { return Views[viewIndex].CameraPos; }
#include "standard_material.slang"
#include "vsm_projected_sampling.slang"

struct ShadowReferenceVisibilityMaterial : ISurfaceEvaluate<SurfaceEval, Triangle>,
    ITransparentSurfaceEvaluate<SurfaceEval, Triangle>
{
    Texture2D NormalMap;
    SamplerState MaterialSampler;

    SurfaceShadowReceiver evaluateShadowReceiver(SurfacePixelInput input, SurfaceEval surface,
        float3 bary, Triangle triangle)
    {
        StandardMaterial material;
        return material.evaluateShadowReceiver(input, surface, bary, triangle);
    }

    float4 evaluateVisibility(SurfacePixelInput input, SurfaceEval surface, float3 bary, Triangle triangle)
    {
        SurfaceShadowReceiver receiver = evaluateShadowReceiver(input, surface, bary, triangle);
        float2 uvDx = triangle.TexCoord0 * input.BarycentricDx.x + triangle.TexCoord1 * input.BarycentricDx.y + triangle.TexCoord2 * input.BarycentricDx.z;
        float2 uvDy = triangle.TexCoord0 * input.BarycentricDy.x + triangle.TexCoord1 * input.BarycentricDy.y + triangle.TexCoord2 * input.BarycentricDy.z;
        float3 tangentNormal = NormalMap.SampleGrad(MaterialSampler, surface.TexCoord, uvDx, uvDy).rgb * 2.0 - 1.0;
        tangentNormal.xy *= LoadMaterialScalars<StandardScalars>(input.MaterialIndex).NormalScale;
        float3 normal = normalize(surface.Normal);
        float3 tangent = normalize(surface.Tangent.xyz - normal * dot(normal, surface.Tangent.xyz));
        float3 shadingNormal = normalize(tangent * tangentNormal.x +
            cross(normal, tangent) * surface.Tangent.w * tangentNormal.y + normal * tangentNormal.z);
        GPULight light = LightBuffer[0];
        float3 toLight = LightingViews[input.PixelCoord.z].DirectionalCount > 0u
            ? -light.Direction : light.Position - receiver.Position;
        uint facesLight = dot(shadingNormal, input.CameraPosition - receiver.Position) > 0.0 &&
            dot(shadingNormal, normalize(toLight)) >= 0.15 ? 1u : 0u;
        float visibility = ReadVirtualShadowProjectedVisibility(input.PixelCoord, 0u,
            uint3(Uniforms.ScreenWidth, Uniforms.ScreenHeight, Uniforms.ViewCount));
        return float4(visibility, 1.0, float(facesLight), 1.0);
    }

    void evaluateSurface(SurfacePixelInput input, SurfaceEval surface, float3 bary,
        Triangle triangle, RWTexture2DArray<float4> outputColor)
    {
        outputColor[input.PixelCoord] = evaluateVisibility(input, surface, bary, triangle);
    }

    void evaluateDebug(uint debugMode, SurfacePixelInput input, SurfaceEval surface, float3 bary,
        Triangle triangle, RWTexture2DArray<float4> outputColor)
    {
        evaluateSurface(input, surface, bary, triangle, outputColor);
    }

    float4 evaluateTransparency(uint debugMode, SurfacePixelInput input, SurfaceEval surface,
        float3 bary, Triangle triangle)
    {
        return evaluateVisibility(input, surface, bary, triangle);
    }
};

typedef StaticVertexEval VertexEvaluate;
typedef LinearSurface DomainEvaluate;
typedef OpaqueVisibilityEvaluate<VertexEvaluate.Vertex> VisibilityEvaluate;
typedef ShadowReferenceVisibilityMaterial SurfaceEvaluate;
#define CLUSTER_MESHLET_RASTER 1
#define CLUSTER_SOFTWARE_MESHLET_RASTER 1
#define CLUSTER_FORWARD_SHADING 1
#include "cluster_material_entries.slang"
