using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Tests;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class CookedClusterShaderContractTests
{
    [Fact]
    public async Task TraversalEntryExposesExactProductionResources()
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
                "PageFaultBuffer"),
            ResourceNames(traversal, entryPoint));
    }

    [Fact]
    public async Task CullEntryExposesExactProductionResources()
    {
        (Shader cull, string entryPoint) = await ConfiguredComputeOperation(
            ClusterShaderOperationRole.CullPhaseOne);

        Assert.Equal(
            Sorted(
                "Views",
                "PageHeap",
                "InstanceData",
                "InstanceProperties",
                "CandidateClusters",
                "CandidateCount",
                "DrawArgs",
                "VisibleClusters",
                "HiZTexture",
                "NextCandidates",
                "NextCandidateCount",
                "NextCandidateArgs"),
            ResourceNames(cull, entryPoint));
    }

    [Theory]
    [InlineData(ClusterShaderOperationRole.TessellationFactor)]
    [InlineData(ClusterShaderOperationRole.TessellationDice)]
    public async Task TessellationSurfaceEntriesExposeExactForwardResources(
        ClusterShaderOperationRole role)
    {
        (Shader shader, string entryPoint) = await ConfiguredComputeOperation(role);

        string[] expected = role == ClusterShaderOperationRole.TessellationFactor
            ? Sorted(
                "Uniforms",
                "Views",
                "PageHeap",
                "VisibleClusters",
                "BinnedClusterIndexBuffer",
                "RasterBinMeta",
                "DeformCache",
                "CacheOffsets",
                "TessellationCounters",
                "SplitWorkItems",
                "DiceDispatchArgs")
            : Sorted(
                "Uniforms",
                "PageHeap",
                "VisibleClusters",
                "DeformCache",
                "CacheOffsets",
                "TessellationCounters",
                "SplitWorkItems",
                "DicedTriangles",
                "RasterDispatchArgs");
        Assert.Equal(expected, ResourceNames(shader, entryPoint));
    }

    [Fact]
    public async Task DicedVisibilityEntryConsumesOnlyTheDicedTriangleAbi()
    {
        (Shader shader, string entryPoint) = await ConfiguredComputeOperation(
            ClusterShaderOperationRole.TessellationVisibilityRaster);

        Assert.Equal(
            Sorted(
                "VisibilityUniforms",
                "VisibilityViews",
                "VisibilityDicedTriangles",
                "VisibilityTessellationCounters",
                "VisibilityDepthTarget",
                "VisibilityTarget",
                "VisibilityDepthUav"),
            ResourceNames(shader, entryPoint));
    }

    [Fact]
    public async Task StandardShadeEntryExplicitlyConsumesLightingFeatureResources()
    {
        Shader shader = await ReadCookedShader("cluster_shade_material.shader.asset");
        string[] resources = ResourceNames(shader, "CSMaterialShadeCached");

        Assert.Contains("LightingViews", resources);
        Assert.Contains("LightBuffer", resources);
        Assert.Contains("ClusteredLightGrid", resources);
        Assert.Contains("LightIndexList", resources);
        Assert.Contains("LightingCookieAtlas", resources);
    }

    [Theory]
    [InlineData(
        "clustered_light_grid.shader.asset",
        "BuildClusteredLightGrid",
        "ClusteredLightGrid|LightBuffer|LightIndexList|LightingViews")]
    [InlineData(
        "render_motion.shader.asset",
        "BuildMotionVectors",
        "CacheOffsets|DeformCache|InstanceData|InstanceProperties|MotionUniforms|OutputMotionVectors|PageHeap|Views|VisBuffer|VisibleClusters")]
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
        AssetManifestRecord configurationRecord = Assert.Single(
            manifest.List(AssetType<ClusterShaders>.Name));
        ClusterShaders configuration = await AssetProject.ReadAsync<ClusterShaders>(
            Path.Combine(root, configurationRecord.Path));
        ClusterShaderOperation operation = Assert.Single(
            configuration.Operations!,
            candidate => candidate.Role == role);
        ShaderRef shaderRef = Assert.Single(operation.Shaders!);
        Assert.Equal(ShaderStage.Compute, shaderRef.Stage);
        Assert.False(string.IsNullOrWhiteSpace(shaderRef.EntryPoint));
        Assert.True(AssetGuid.TryParse(shaderRef.AssetGuid, out AssetGuid shaderGuid));
        Assert.True(manifest.TryGetAsset(shaderGuid, out AssetManifestRecord shaderRecord));
        Shader shader = await Shader.ReadAsync(Path.Combine(root, shaderRecord.Path));
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

    private static ValueTask<Shader> ReadCookedShader(string assetName) =>
        Shader.ReadAsync(Path.Combine(
            TestProjectPaths.ProjectRoot(),
            "assets",
            "Shaders",
            assetName));

    private static string[] Sorted(params string[] names)
    {
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }
}
