using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Tests;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class CookedClusterShaderContractTests
{
    [Fact]
    public async Task TraversalEntryExposesThreadGroupHierarchyResourcesOnly()
    {
        (Shader traversal, string entryPoint) = await ConfiguredComputeOperation(
            ClusterShaderOperationRole.BvhTraversal);

        Assert.Equal(
            Sorted(
                "Views",
                "GlobalBVH",
                "InstanceData",
                "InstanceProperties",
                "CandidateArgs",
                "CandidateClusters",
                "CandidateCount",
                "CurrentNodeIndirectArgs",
                "DeferredWork",
                "DeferredWorkCount",
                "HierarchyHiZ",
                "NextNodeIndirectArgs",
                "PageFaultBuffer",
                "PolicyClusterViews",
                "RasterRegionGrid",
                "RasterTargetCurrentHiZ",
                "RasterTargetInvocations",
                "RasterTargetPreviousHiZ",
                "TraversalLevel",
                "TraversalNodes"),
            ResourceNames(traversal, entryPoint));
    }

    [Fact]
    public async Task CullEntryExposesCompactViewIndexedResourcesOnly()
    {
        (Shader cull, string entryPoint) = await ConfiguredComputeOperation(
            ClusterShaderOperationRole.CullPhaseOne);

        Assert.Equal(
            Sorted(
                "Views",
                "PageHeap",
                "InstanceData",
                "InstanceProperties",
                "PolicyClusterViews",
                "CandidateClusters",
                "CandidateCount",
                "DrawArgs",
                "BaseDrawArgs0",
                "BaseDrawArgs1",
                "VisibleClusters",
                "HiZTexture",
                "NextCandidates",
                "NextCandidateCount",
                "NextCandidateArgs",
                "RasterRegionGrid",
                "RasterTargetCurrentHiZ",
                "RasterTargetInvocations",
                "RasterTargetPreviousHiZ"),
            ResourceNames(cull, entryPoint));
    }

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
        Assert.Contains("InstanceData", resources);
        Assert.Contains("InstanceProperties", resources);
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
                SomeEngine.Graphics.ShaderTarget.Spirv,
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
            _ = shader.RequireEntry(SomeEngine.Graphics.ShaderTarget.Spirv, entry, stage);
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
        Shader shader = await ReadCookedShader("cluster_shade_material.shader.asset");
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
    public async Task BuiltInMaterialShaderExposesAllFixedClusterEntries()
    {
        Shader shader = await ReadCookedShader("cluster_shade_material.shader.asset");
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
                SomeEngine.Graphics.ShaderTarget.Spirv,
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
    [InlineData(
        "clustered_light_grid.shader.asset",
        "BuildClusteredLightGrid",
        "ClusteredLightGrid|LightBuffer|LightIndexList|LightingViews")]
    [InlineData(
        "temporal_resolve.shader.asset",
        "PSMain",
        "MotionVectors|PreviousMotionVectors|PreviousSceneColor|PreviousSceneDepth|SceneColor|SceneDepth|TemporalResolveUniforms")]
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
        string root = TestProjectPaths.ProjectRoot();
        string manifestDirectory = Path.Combine(root, "Library", "AssetManifest");
        AssetManifest manifest = AssetManifest.Load(manifestDirectory);
        AssetEntry configurationRecord = Assert.Single(
            manifest.List(ClusterShaders.Asset.FileType));
        using ClusterShaders.Asset configuration = await ClusterShaders.Asset.OpenAsync(
            Path.Combine(root, configurationRecord.Path));
        ClusterShaderOperation operation = Assert.Single(
            configuration.Operations!,
            candidate => candidate.Role == role);
        ShaderRef shaderRef = Assert.Single(operation.Shaders!);
        Assert.Equal(ShaderStage.Compute, shaderRef.Stage);
        Assert.False(string.IsNullOrWhiteSpace(shaderRef.EntryPoint));
        Assert.True(Guid.TryParse(shaderRef.AssetGuid, out Guid shaderGuid));
        Assert.True(manifest.TryGetAsset(shaderGuid, out AssetEntry shaderRecord));
        using Shader.Asset file = await Shader.Asset.OpenAsync(Path.Combine(root, shaderRecord.Path));
        Shader shader = await Shader.CreateAsync(file);
        return (shader, shaderRef.EntryPoint!);
    }

    private static string[] ResourceNames(Shader shader, string entryPoint)
    {
        Assert.True(shader.TryReflection("dxil", entryPoint, out ShaderEntryPointReflection reflection));
        IList<ShaderResourceReflection> resources = Assert.IsAssignableFrom<IList<ShaderResourceReflection>>(
            reflection.Reflection?.Resources);
        var names = new string[resources.Count];
        for (int index = 0; index < resources.Count; index++)
            names[index] = Assert.IsType<string>(resources[index].Name);
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }

    private static async ValueTask<Shader> ReadCookedShader(string assetName)
    {
        await using Shader.Asset file = await Shader.Asset.OpenAsync(Path.Combine(
            TestProjectPaths.ProjectRoot(), "assets", "Shaders", assetName));
        return await Shader.CreateAsync(file);
    }

    private static string[] Sorted(params string[] names)
    {
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }
}
