using SomeEngine.Assets;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Cluster;
using SomeEngine.Render.Materials;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Render.Cluster.Tests;

public sealed class ClusterAssetLoaderTests
{
    [Fact]
    public async Task ClusterRenderAssetRequestsEveryShaderThroughTheSameGenericLoader()
    {
        using var directory = new TemporaryDirectory();
        var project = new AssetProject(directory.Path, []);
        ClusterShaderOperationRole[] roles = Enum.GetValues<ClusterShaderOperationRole>()
            .Where(static role => role != ClusterShaderOperationRole.None)
            .ToArray();
        Guid[] shaderGuids = new Guid[roles.Length];
        for (int index = 0; index < shaderGuids.Length; index++)
        {
            using Shader.Asset shader = CreateShaderData($"cluster-{index}");
            shaderGuids[index] = project.RegisterAsset(await shader.SaveAsync(
                Path.Combine(project.RootPath, $"assets/shaders/cluster-{index}.shader.asset")));
        }

        using ClusterShaders.Asset cluster = CreateClusterRoot(shaderGuids);
        Guid clusterGuid = project.RegisterAsset(await cluster.SaveAsync(
            Path.Combine(project.RootPath, "assets/pipelines/main.clusterrender.asset")));
        await using var loader = new AssetLoader(project.CreateStorage());

        ClusterShaders.Asset[] opened = await Task.WhenAll(
            ClusterShaders.Asset.OpenAsync(loader, new AssetId<ClusterShaders>(clusterGuid)).AsTask(),
            ClusterShaders.Asset.OpenAsync(loader, new AssetId<ClusterShaders>(clusterGuid)).AsTask());
        await using ClusterShaders.Asset first = opened[0];
        await using ClusterShaders.Asset second = opened[1];
        Assert.NotSame(first, second);
        Assert.Equal(shaderGuids.Order(), first.Describe().Dependencies.Order());
        ClusterShaders shaders = await ClusterShaders.CreateAsync(first, loader);
        ClusterShaders other = await ClusterShaders.CreateAsync(second, loader);
        Assert.NotSame(shaders, other);
        Assert.Equal("MainCluster", shaders.Name);
        Assert.Equal(roles.Length, shaders.Operations?.Count);
        for (int index = 0; index < roles.Length; index++)
        {
            ClusterShaderOperation operation = shaders.Operations![index];
            Assert.Equal(roles[index], operation.Role);
            ShaderRef shader = Assert.IsAssignableFrom<IList<ShaderRef>>(operation.Shaders)[0];
            Assert.True(Guid.TryParse(shader.AssetGuid, out Guid shaderGuid));
            Assert.Equal($"cluster-{index}", shaders.GetShader(shader).Name);
            Assert.NotSame(shaders.GetShader(shader), other.GetShader(shader));
            Assert.Equal(shaderGuids[index], shaderGuid);
        }
    }

    [Fact]
    public async Task ClusterRenderAssetWithMalformedLastShaderGuidFailsBeforeWriting()
    {
        using var directory = new TemporaryDirectory();
        Guid clusterGuid = Guid.NewGuid();
        string relativePath = "assets/pipelines/invalid.clusterrender.asset";
        string fullPath = Path.Combine(directory.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Guid valid = Guid.NewGuid();
        int operationCount = Enum.GetValues<ClusterShaderOperationRole>()
            .Count(static role => role != ClusterShaderOperationRole.None);
        using ClusterShaders.Asset root = CreateClusterRoot(
            Enumerable.Repeat(valid, operationCount).ToArray());
        root.Operations![^1].Shaders![0].AssetGuid = "not-a-guid";
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => root.SaveAsync(fullPath).AsTask());
        Assert.Contains($"Operations[{operationCount - 1}].Shader", error.Message);
        Assert.False(File.Exists(fullPath));
    }
    private static Shader.Asset CreateShaderData(string name)
        => new()
        {
            Name = name,
            Programs = [CookedShaderTestProgram.Create()],
            EntryPointReflections =
            [
                new ShaderEntryPointReflection
                {
                    Backend = "spirv",
                    EntryPoint = "VSMain",
                    Stage = SomeEngine.Assets.Schema.ShaderStage.Vertex,
                },
            ],
        };

    private static ClusterShaders.Asset CreateClusterRoot(IReadOnlyList<Guid> shaders)
    {
        ClusterShaderOperationRole[] roles = Enum.GetValues<ClusterShaderOperationRole>()
            .Where(static role => role != ClusterShaderOperationRole.None)
            .ToArray();
        Assert.Equal(roles.Length, shaders.Count);
        return new ClusterShaders.Asset
        {
            Name = "MainCluster",
            Operations = roles
                .Select((role, index) => Operation(role, shaders[index]))
                .ToArray(),
        };
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
            result.Shaders =
            [
                new ShaderRef
                {
                    AssetGuid = guid.ToString(),
                    EntryPoint = "vertex",
                    Stage = ShaderStage.Vertex,
                },
                new ShaderRef
                {
                    AssetGuid = guid.ToString(),
                    EntryPoint = "pixel",
                    Stage = ShaderStage.Pixel,
                },
            ];
        }
        else
        {
            result.Shaders =
            [
                new ShaderRef
                {
                    AssetGuid = guid.ToString(),
                    EntryPoint = "compute",
                    Stage = ShaderStage.Compute,
                },
            ];
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
