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
                "Uniforms",
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
                "Uniforms",
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

    private static string[] Sorted(params string[] names)
    {
        Array.Sort(names, StringComparer.Ordinal);
        return names;
    }
}
