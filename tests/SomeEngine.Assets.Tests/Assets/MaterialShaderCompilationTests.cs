using System.Text.RegularExpressions;
using SomeEngine.Tests;

namespace SomeEngine.Assets.Tests.Assets;

public class MaterialShaderCompilationTests
{
    [Fact]
    public void Brdf_DoesNotAmplifyBackFacingViewVectors()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("brdf.slang"));

        Assert.Contains("float rawNdotV = dot(N, V);", source);
        Assert.Contains("if (rawNdotV <= 0.0)", source);
        Assert.Contains("return float3(0.0);", source);
        Assert.Contains("static const float MIN_N_DOT_V = 0.05;", source);
        Assert.Contains("float NdotV = max(rawNdotV, MIN_N_DOT_V);", source);
        Assert.Contains("float3 EvaluatePointLight(", source);
        Assert.Contains("float3 EvaluateSpotLight(", source);
        Assert.Contains("float EvaluateSpotCone(", source);
    }

    [Fact]
    public void StandardPBR_UsesClusterLightIndicesWithoutDefaultAmbient()
    {
        string source = File.ReadAllText(TestProjectPaths.ShaderPath("standard_pbr.slang"));
        string materialInterfaces = File.ReadAllText(TestProjectPaths.ShaderPath("material_interfaces.slang"));

        Assert.Contains("ClusterLightGrid", source);
        Assert.Contains("LightIndexList", source);
        Assert.Contains("GetClusterIndex(", source);
        Assert.Contains("LIGHT_GRID_INVALID_CLUSTER", source);
        Assert.Contains("LightIndexList[", source);
        Assert.Contains("static const uint DEFAULT_LIGHT_LAYER_MASK = 0xFFFFFFFFu;", source);
        Assert.Contains("static const int NO_COOKIE = -1;", source);
        Assert.Contains("uint surfaceLightLayerMask = Uniforms.LightLayerMask != 0u ? Uniforms.LightLayerMask : DEFAULT_LIGHT_LAYER_MASK;", source);
        Assert.Contains("if ((light.LayerMask & surfaceLightLayerMask) == 0u)", source);
        Assert.Contains("light.CookieIndex", source);
        Assert.Contains("light.CookieStrength", source);
        Assert.Contains("float SampleLightCookie(GPULight light, float3 surfacePosition)", source);
        Assert.Contains("LightCookieAtlas.SampleLevel", source);
        Assert.Contains("LightCookieSampler", source);
        Assert.Contains("light.WorldToLightCookie", source);
        Assert.Contains("light.CookieScaleOffset", source);
        Assert.Contains("float cookieAttenuation = SampleLightCookie(light, ctx.surfacePosition);", source);
        Assert.DoesNotContain("light.CookieIndex == NO_COOKIE ? 1.0 : saturate(light.CookieStrength)", source);
        Assert.DoesNotContain("TileLightGrid", source);
        Assert.Contains("for (uint lightIndex = 0u; lightIndex < directionalEnd; lightIndex++)", source);
        Assert.Contains("if (lightIndex < directionalEnd || lightIndex >= spotEnd)", source);
        Assert.False(
            Regex.IsMatch(source, @"for\s*\([^)]*LightCounts\.(PointCount|SpotCount)", RegexOptions.Multiline),
            "Standard PBR final shading must use cluster light indices for point and spot lights.");
        Assert.Contains("EvaluateDirectionalLight(", source);
        Assert.Contains("-normalize(light.Direction)", source);
        Assert.Contains("EvaluatePointLight(", source);
        Assert.Contains("EvaluateSpotLight(", source);
        Assert.Contains("light.Color * light.Intensity * cookieAttenuation", source);
        Assert.DoesNotContain("light.Kind", source);
        Assert.Contains("float3 finalColor = directColor + emissive;", source);
        Assert.DoesNotContain("Uniforms.LightDir", source);
        Assert.DoesNotContain("Uniforms.LightIntensity", source);
        Assert.DoesNotContain("Uniforms.AmbientColor", source);
        Assert.DoesNotContain("groundColor", source);
        Assert.DoesNotContain("skyColor", source);
        Assert.DoesNotContain("GetDefaultStandardPBRScalars", source);
        Assert.DoesNotMatch(
            @"LoadMaterialScalars<StandardPBRScalars>\s*\(\s*[^)]",
            source);
        Assert.Contains("LoadMaterialScalars<StandardPBRScalars>()", source);
        Assert.Contains("T LoadMaterialScalars<T>()", materialInterfaces);
        Assert.DoesNotContain("GetMaterialScalarPayloadByteSize", materialInterfaces);
        Assert.DoesNotContain("LoadMaterialScalarLayoutHash", materialInterfaces);
        Assert.DoesNotContain("T LoadMaterialScalars<T>(T defaultValue)", materialInterfaces);
        Assert.DoesNotContain("LoadMaterialScalarBytesRaw", materialInterfaces);
        Assert.DoesNotContain("LoadMaterialScalarBytes<T>", materialInterfaces);
    }

}
