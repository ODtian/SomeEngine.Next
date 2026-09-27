using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Tests;
using System.Runtime.InteropServices;
using SlangShaderSharp;
using SomeEngine.Render.Shadows;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class CookedClusterShaderContractTests
{
    [Theory]
    [InlineData("seed")]
    [InlineData("split")]
    public async Task TessellationSurfaceEntriesFuseVertexEvaluationWithoutGlobalCaches(
        string stage)
    {
        Shader shader = await ReadCookedShader("cluster_tessellation_phong.shader.asset");
        string entryPoint = stage switch
        {
            "seed" => "SeedPhongTessellation",
            "split" => "SplitPhongTessellation",
            _ => throw new ArgumentOutOfRangeException(nameof(stage)),
        };

        string[] resources = ResourceNames(shader, entryPoint);
        Assert.Contains("PageHeap", resources);
        Assert.Contains("TessellationCounters", resources);
        Assert.Contains("TessellationUniformData", resources);
        Assert.Contains("TessellationVisibleClusters", resources);
        Assert.DoesNotContain("TessellationDeformCache", resources);
        Assert.DoesNotContain("TessellationCacheOffsets", resources);
        Assert.DoesNotContain("TessellationQueueStates", resources);
        Assert.DoesNotContain("TessellationWorkNext", resources);
        if (stage == "split")
        {
            Assert.Contains("InstanceData", resources);
            Assert.Contains("InstanceProperties", resources);
            Assert.Contains("TessellationFinalPatches", resources);
            Assert.Contains("TessellationRasterSlots", resources);
            Assert.Contains("TessellationHiZTexture", resources);
            Assert.Contains("TessellationLevelCounts", resources);
            Assert.Contains("TessellationNextLevelArgs", resources);
            Assert.Contains("TessellationPolicyViews", resources);
            Assert.Contains("TessellationViews", resources);
            Assert.Contains("TessellationWorkPatches0", resources);
            Assert.Contains("TessellationWorkPatches1", resources);
        }
    }

    [Fact]
    public async Task TessellationTemplateBuildsAllClusterEntriesFromOneSurfaceType()
    {
        Shader shader = await ReadCookedShader("cluster_tessellation_phong.shader.asset");
        (string Entry, ShaderStage Stage)[] entries =
        [
            ("ClusterTessellationSeed", ShaderStage.Compute),
            ("ClusterTessellationSplit", ShaderStage.Compute),
            ("ClusterTessellationDiceImageAtomic64", ShaderStage.Compute),
            ("ClusterTessellationDiceBufferAtomic64", ShaderStage.Compute),
            ("ClusterTessellationHardwareVertex", ShaderStage.Vertex),
            ("ClusterTessellationHardwarePixel", ShaderStage.Pixel),
        ];

        foreach ((string entry, ShaderStage stage) in entries)
        {
            _ = shader.RequireEntry(
                SlangShaderSharp.SlangCompileTarget.Spirv,
                entry,
                stage);
        }
    }

    [Fact]
    public async Task TessellationRasterEntriesConsumeGroupedPatchesWithoutIntermediateGeometry()
    {
        Shader shader = await ReadCookedShader("cluster_tessellation_phong.shader.asset");
        (string Entry, ShaderStage Stage)[] entries =
        [
            ("ClusterTessellationDiceImageAtomic64", ShaderStage.Compute),
            ("ClusterTessellationDiceBufferAtomic64", ShaderStage.Compute),
            ("ClusterTessellationHardwareVertex", ShaderStage.Vertex),
        ];

        foreach ((string entry, ShaderStage stage) in entries)
        {
            _ = shader.RequireEntry(SlangShaderSharp.SlangCompileTarget.Spirv, entry, stage);
            string[] resources = ResourceNames(shader, entry);
            Assert.Contains("TessellationRasterPatches", resources);
            Assert.DoesNotContain("GroupedPatches", resources);
            if (stage == ShaderStage.Compute)
            {
                Assert.Contains("GroupedPatchIndices", resources);
                Assert.Contains("TessellationBinOffsets", resources);
            }
            Assert.Contains("InstanceData", resources);
            Assert.Contains("InstanceProperties", resources);
            Assert.DoesNotContain("DicedTriangles", resources);
            Assert.DoesNotContain("TessellationDeformCache", resources);
            Assert.DoesNotContain("TessellationCacheOffsets", resources);
        }
    }

    [Fact]
    public async Task TessellationBinningEntriesContainNoDicedTrianglePayload()
    {
        ClusterShaderOperationRole[] roles =
        [
            ClusterShaderOperationRole.TessellationBinningReset,
            ClusterShaderOperationRole.TessellationBinningCount,
            ClusterShaderOperationRole.TessellationBinningReserve,
            ClusterShaderOperationRole.TessellationBinningScatter,
        ];
        foreach (ClusterShaderOperationRole role in roles)
        {
            (Shader shader, string entryPoint) = await ConfiguredComputeOperation(role);
            string[] resources = ResourceNames(shader, entryPoint);
            Assert.DoesNotContain("DicedTriangles", resources);
            Assert.DoesNotContain("VisibilityDicedTriangles", resources);
        }
    }

    [Fact]
    public async Task StandardShadeEntryConsumesMaterialLightingAndVirtualShadowResources()
    {
        Shader shader = await ReadTemplateShader("standard.shader.asset");
        string[] resources = ResourceNames(shader, "ClusterShade");

        Assert.Contains("AlbedoMap", resources);
        Assert.Contains("NormalMap", resources);
        Assert.Contains("ARMMap", resources);
        Assert.Contains("MaterialSampler", resources);
        Assert.Contains("LightingViews", resources);
        Assert.Contains("LightBuffer", resources);
        Assert.Contains("ClusteredLightGrid", resources);
        Assert.Contains("LightIndexList", resources);
        Assert.Contains("LightingCookieAtlas", resources);
        Assert.Contains("VirtualShadowViews", resources);
        Assert.Contains("VirtualShadowPageTable", resources);
        Assert.Contains("VirtualShadowAtlas", resources);
        Assert.Contains("VirtualShadowSampling", resources);
    }

    [Fact]
    public async Task ProjectMaterialShaderExposesItsSupportedClusterEntries()
    {
        Shader shader = await ReadTemplateShader("standard.shader.asset");
        (string Entry, ShaderStage Stage)[] entries =
        [
            ("ClusterRasterSoftwareImageAtomic64", ShaderStage.Compute),
            ("ClusterRasterSoftwareBufferAtomic64", ShaderStage.Compute),
            ("ClusterRasterVertex", ShaderStage.Vertex),
            ("ClusterRasterPixelImageAtomic64", ShaderStage.Pixel),
            ("ClusterRasterPixelBufferAtomic64", ShaderStage.Pixel),
            ("ClusterRasterPixelHardwareOnly", ShaderStage.Pixel),
            ("ClusterShade", ShaderStage.Compute),
        ];

        foreach ((string entry, ShaderStage stage) in entries)
        {
            _ = shader.RequireEntry(
                SlangShaderSharp.SlangCompileTarget.Spirv,
                entry,
                stage);
        }
    }

    [Fact]
    public void GlobalDeformationAndMotionPrepassAssetsAreNotCooked()
    {
        string shaderDirectory = TestProjectPaths.ShaderDirectory();
        Assert.False(File.Exists(Path.Combine(shaderDirectory, "cluster_deform.shader.asset")));
        Assert.False(File.Exists(Path.Combine(shaderDirectory, "cluster_deform_prefix.shader.asset")));
        Assert.False(File.Exists(Path.Combine(shaderDirectory, "render_motion.shader.asset")));
    }

    [Theory]
    [InlineData("ClusterRasterSoftwareTarget", ShaderStage.Compute)]
    [InlineData("ClusterRasterSoftwareMeshletTarget", ShaderStage.Compute)]
    [InlineData("ClusterRasterPixelTarget", ShaderStage.Pixel)]
    [InlineData("ClusterRasterMeshPixelTarget", ShaderStage.Pixel)]
    public async Task DepthTargetEntriesDoNotRequirePrimaryVisibilityOutputs(
        string entryPoint,
        ShaderStage stage)
    {
        Shader shader = await ReadTemplateShader("standard.shader.asset");
        foreach (SlangShaderSharp.SlangCompileTarget target in new[]
        {
            SlangShaderSharp.SlangCompileTarget.Dxil,
            SlangShaderSharp.SlangCompileTarget.Spirv,
        })
        {
            _ = shader.RequireEntry(target, entryPoint, stage);
            string[] resources = ResourceNames(shader, entryPoint,
                target == SlangShaderSharp.SlangCompileTarget.Dxil ? "dxil" : "spirv");
            Assert.Contains("RasterTargetOutput", resources);
            Assert.DoesNotContain("VisibilityWinnerImage", resources);
            Assert.DoesNotContain("VisibilityWinnerBuffer", resources);
        }
    }

    [Theory]
    [InlineData(
        "clustered_light_grid.shader.asset",
        "BuildClusteredLightGrid",
        "ClusteredLightGrid|LightBuffer|LightIndexList|LightingViews")]
    [InlineData(
        "temporal_resolve.shader.asset",
        "PSMain",
        "MotionVectors|SceneColor|SceneDepth|TemporalHistory|TemporalResolveUniforms")]
    [InlineData(
        "render_debug_overlay.shader.asset",
        "PSMain",
        "DebugUniforms|InputColor|InputDepth")]
    [InlineData(
        "translucency_composite.shader.asset",
        "PSMain",
        "CompositionUniforms|OpaqueColor|TranslucencyAccumulation|TranslucencyRevealage")]
    [InlineData(
        "stream_output_geometry.shader.asset",
        "BuildStreamOutput",
        "ExportUniforms|PhaseOneCounts|PhaseTwoCounts|StreamOutputClusters|StreamOutputCount|VisibleClusters")]
    [InlineData(
        "ray_tracing_geometry.shader.asset",
        "BuildRayTracingGeometry",
        "ExportUniforms|PageHeap|PhaseOneCounts|PhaseTwoCounts|RayTracingClusterCount|RayTracingClusters|VisibleClusters")]
    public async Task IndependentFeatureEntriesExposeExactOwnedResourceContracts(
        string assetName,
        string entryPoint,
        string expectedResources)
    {
        Shader shader = await ReadCookedShader(assetName);

        Assert.Equal(
            Sorted(expectedResources.Split('|', StringSplitOptions.RemoveEmptyEntries)),
            ResourceNames(shader, entryPoint));
    }

    private static async Task<(Shader Shader, string EntryPoint)> ConfiguredComputeOperation(
        ClusterShaderOperationRole role)
    {
        string root = TestProjectPaths.DefaultProjectRoot();
        string manifestDirectory = Path.Combine(root, "Library", "AssetManifest");
        AssetManifest manifest = AssetManifest.Load(manifestDirectory);
        AssetEntry configurationRecord = Assert.Single(
            manifest.List(ClusterShadersAsset.FileType));
        using ClusterShadersAsset configuration = await ClusterShadersAsset.OpenAsync(
            Path.Combine(root, configurationRecord.Path));
        ClusterShaderOperation operation = Assert.Single(
            configuration.GetBlock<ClusterShadersBlock>().Operations,
            candidate => candidate.Role == role);
        ShaderRef shaderRef = Assert.Single(operation.Shaders!);
        Assert.Equal(ShaderStage.Compute, shaderRef.Stage);
        Assert.False(string.IsNullOrWhiteSpace(shaderRef.EntryPoint));
        Guid shaderGuid = shaderRef.Asset.Value;
        Assert.True(manifest.TryGetAsset(shaderGuid, out AssetEntry shaderRecord));
        using ShaderAsset file = await ShaderAsset.OpenAsync(Path.Combine(root, shaderRecord.Path));
        Shader shader = await file.GetBlock<ShaderBlock>().CreateAsync(file.Name);
        return (shader, shaderRef.EntryPoint!);
    }

    private static string[] ResourceNames(Shader shader, string entryPoint, string target = "dxil")
    {
        Assert.True(shader.TryReflection(target, entryPoint, out ShaderEntryPointReflection reflection));
        IList<ShaderResourceReflection> resources = Assert.IsAssignableFrom<IList<ShaderResourceReflection>>(
            reflection.Reflection?.Resources);
        var names = new string[resources.Count];
        for (int index = 0; index < resources.Count; index++)
            names[index] = Assert.IsType<string>(resources[index].Name);
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    [Theory]
    [InlineData("standard.shader.asset", "VirtualShadowViews")]
    [InlineData("vsm_view_compact.shader.asset", "ShadowViews")]
    public async Task ShadowViewBuffersAcceptTheSameCpuDataOnBothTargets(string asset, string resource)
    {
        using Shader shader = await ReadTemplateShader(asset);
        foreach (string target in new[] { "dxil", "spirv" })
        {
            ShaderTypeLayout element = StructuredElement(shader, target, resource);
            Assert.Equal((nuint)Marshal.SizeOf<VirtualShadowView>(), element.GetStride(SlangParameterCategory.Uniform));
            for (uint index = 0; index < element.FieldCount; index++)
            {
                ShaderVariableLayout field = element.GetFieldByIndex(index);
                Assert.Equal((nuint)Marshal.OffsetOf<VirtualShadowView>(field.Name), field.GetOffset());
            }
        }
    }

    [Theory]
    [InlineData("standard.shader.asset")]
    [InlineData("vsm_view_compact.shader.asset")]
    public async Task RasterTargetViewsUseTheSameFormatOnBothTargets(string asset)
    {
        using Shader shader = await ReadTemplateShader(asset);
        ShaderTypeLayout first = StructuredElement(shader, "dxil", "RasterTargetViews");
        ShaderTypeLayout second = StructuredElement(shader, "spirv", "RasterTargetViews");
        Assert.Equal(first.GetStride(SlangParameterCategory.Uniform), second.GetStride(SlangParameterCategory.Uniform));
        Assert.Equal(first.FieldCount, second.FieldCount);
        for (uint index = 0; index < first.FieldCount; index++)
        {
            Assert.Equal(first.GetFieldByIndex(index).Name, second.GetFieldByIndex(index).Name);
            Assert.Equal(first.GetFieldByIndex(index).GetOffset(), second.GetFieldByIndex(index).GetOffset());
        }
    }

    private static ShaderTypeLayout StructuredElement(Shader shader, string target, string resource)
    {
        Assert.True(shader.TryProgram(target, out ShaderProgram code));
        ShaderTypeLayout data = code.Layout.GetGlobalParamsVarLayout().TypeLayout;
        if (data.Kind is SlangTypeKind.ConstantBuffer or SlangTypeKind.ParameterBlock)
            data = data.ElementTypeLayout;
        for (uint index = 0; index < data.FieldCount; index++)
        {
            ShaderVariableLayout field = data.GetFieldByIndex(index);
            if (field.Name == resource) return field.TypeLayout.ElementTypeLayout;
        }
        throw new InvalidDataException($"Shader {shader.Name} has no {resource} buffer for {target}.");
    }

    private static async ValueTask<Shader> ReadCookedShader(string assetName)
    {
        await using ShaderAsset file = await ShaderAsset.OpenAsync(TestProjectPaths.BuiltShaderPath(assetName));
        return await file.GetBlock<ShaderBlock>().CreateAsync(file.Name);
    }

    private static async ValueTask<Shader> ReadTemplateShader(string assetName)
    {
        await using ShaderAsset file = await ShaderAsset.OpenAsync(Path.Combine(
            TestProjectPaths.ProjectRoot(), "templates", "DefaultProject", "assets", "Shaders", assetName));
        return await file.GetBlock<ShaderBlock>().CreateAsync(file.Name);
    }

    private static string[] Sorted(params string[] names)
    {
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }
}
