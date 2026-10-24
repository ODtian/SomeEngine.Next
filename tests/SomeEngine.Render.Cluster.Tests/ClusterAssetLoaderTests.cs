using SomeEngine.Assets;
using SomeEngine.Core.Collections;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Materials;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterAssetLoaderTests
{
    [Fact]
    public async Task ClusterRenderAssetsUseTheSameLiveShaders()
    {
        using var directory = new TemporaryDirectory();
        var project = new AssetProject(directory.Path, []);
        ClusterShaderOperationRole[] roles = Enum.GetValues<ClusterShaderOperationRole>()
            .Where(static role => role != ClusterShaderOperationRole.None)
            .ToArray();
        Guid[] shaderGuids = new Guid[roles.Length];
        for (int index = 0; index < shaderGuids.Length; index++)
        {
            using ShaderAsset shader = CreateShaderData($"cluster-{index}", directory.Path);
            shaderGuids[index] = project.RegisterAsset(await shader.SaveAsync(
                Path.Combine(project.RootPath, $"assets/shaders/cluster-{index}.shader.asset")));
        }

        using ClusterShadersAsset cluster = CreateClusterRoot(shaderGuids);
        Guid clusterGuid = project.RegisterAsset(await cluster.SaveAsync(
            Path.Combine(project.RootPath, "assets/pipelines/main.clusterrender.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());

        ClusterShadersAsset[] opened = await Task.WhenAll(
            ClusterShadersAsset.OpenAsync(loader, new AssetId<ClusterShadersAsset>(clusterGuid)).AsTask(),
            ClusterShadersAsset.OpenAsync(loader, new AssetId<ClusterShadersAsset>(clusterGuid)).AsTask());
        await using ClusterShadersAsset first = opened[0];
        await using ClusterShadersAsset second = opened[1];
        Assert.NotSame(first, second);
        Assert.Equal(shaderGuids.Order(), first.Describe().Dependencies.Order());
        var values = new WeakValues<AssetResourceKey<ShaderAsset>, Shader>();
        Dictionary<AssetId<ShaderAsset>, Shader> references = await ShaderRef.LoadAsync(
            first.GetBlock<ClusterShadersBlock>().References(), loader, values,
            TestShaderLoading.Slang, TestShaderLoading.CreateSessionDescription());
        ClusterShaders shaders = first.GetBlock<ClusterShadersBlock>().Create(first.Name, references);
        Dictionary<AssetId<ShaderAsset>, Shader> otherReferences = await ShaderRef.LoadAsync(
            second.GetBlock<ClusterShadersBlock>().References(), loader, values,
            TestShaderLoading.Slang, TestShaderLoading.CreateSessionDescription());
        ClusterShaders other = second.GetBlock<ClusterShadersBlock>().Create(second.Name, otherReferences);
        Assert.NotSame(shaders, other);
        Assert.Equal("MainCluster", shaders.Name);
        Assert.Equal(roles.Length, shaders.Operations?.Count);
        for (int index = 0; index < roles.Length; index++)
        {
            ShaderOperation operation = shaders.Operations![index];
            Assert.Equal(roles[index], operation.Role);
            Shader shader = operation.Shaders[0];
            Guid shaderGuid = first.GetBlock<ClusterShadersBlock>().Operations[index].Shaders![0].Asset.Value;
            Assert.Equal($"cluster-{index}", shader.Name);
            Assert.Same(shader, other.Operations[index].Shaders[0]);
            Assert.Equal(shaderGuids[index], shaderGuid);
        }
    }

    [Fact]
    public async Task ClusterRenderAssetWithEmptyLastShaderReferenceFailsBeforeWriting()
    {
        using var directory = new TemporaryDirectory();
        string relativePath = "assets/pipelines/invalid.clusterrender.asset";
        string fullPath = Path.Combine(directory.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Guid valid = Guid.NewGuid();
        int operationCount = Enum.GetValues<ClusterShaderOperationRole>()
            .Count(static role => role != ClusterShaderOperationRole.None);
        using ClusterShadersAsset root = CreateClusterRoot(
            Enumerable.Repeat(valid, operationCount).ToArray());
        ClusterShadersBlock settings = root.GetBlock<ClusterShadersBlock>();
        ClusterShaderOperation last = settings.Operations[^1];
        settings.Operations[^1] = last with { Shaders = last.Shaders!.SetItem(0, last.Shaders[0] with { Asset = default }) };
        await Assert.ThrowsAsync<InvalidDataException>(
            () => root.SaveAsync(fullPath).AsTask());
        Assert.False(File.Exists(fullPath));
    }
    private static ShaderAsset CreateShaderData(string name, string directory)
    {
        const string source = """
            [shader("vertex")]
            float4 vertex(uint id : SV_VertexID) : SV_Position
            {
                return float4(id == 1 ? 1 : -1, id == 2 ? 1 : -1, 0, 1);
            }

            [shader("fragment")]
            float4 pixel() : SV_Target0
            {
                return float4(1, 1, 1, 1);
            }

            [shader("compute")]
            [numthreads(1, 1, 1)]
            void compute()
            {
            }
            """;
        string sourcePath = Path.Combine(directory, "cluster-test.slang");
        File.WriteAllText(sourcePath, source);
        using var definition = new ShaderAsset { Name = name };
        definition.GetBlock<ShaderSourceBlock>().SourceNames = ["cluster-test.slang"];
        return SlangShaderImporter.CompileDefinition(
            definition,
            Path.Combine(directory, name + ".shader"),
            projectRoot: directory);
    }

    private static ClusterShadersAsset CreateClusterRoot(IReadOnlyList<Guid> shaders)
    {
        ClusterShaderOperationRole[] roles = Enum.GetValues<ClusterShaderOperationRole>()
            .Where(static role => role != ClusterShaderOperationRole.None)
            .ToArray();
        Assert.Equal(roles.Length, shaders.Count);
        var asset = new ClusterShadersAsset { Name = "MainCluster" };
        using var settings = new ClusterShadersBlock
        {
            Operations = roles
                .Select((role, index) => Operation(role, shaders[index]))
                .ToArray(),
        };
        asset.SetBlock(settings);
        return asset;
    }

    private static ClusterShaderOperation Operation(
        ClusterShaderOperationRole role,
        Guid guid)
    {
        var result = new ClusterShaderOperation
        {
            Role = role,
        };
        if (role is ClusterShaderOperationRole.HardwareVisibilityRaster
            or ClusterShaderOperationRole.VisibilityResolveImage
            or ClusterShaderOperationRole.VisibilityResolveBuffer)
        {
            result = result with { Shaders =
            [
                new ShaderRef
                {
                    Asset = new(guid),
                    EntryPoint = "vertex",
                },
                new ShaderRef
                {
                    Asset = new(guid),
                    EntryPoint = "pixel",
                },
            ] };
        }
        else
        {
            result = result with { Shaders =
            [
                new ShaderRef
                {
                    Asset = new(guid),
                    EntryPoint = "compute",
                },
            ] };
        }
        return result;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SomeEngine-ClusterRuntime-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
