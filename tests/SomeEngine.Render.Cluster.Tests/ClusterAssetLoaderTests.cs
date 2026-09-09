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
            shaderGuids[index] = project.CreateAsset(
                $"assets/shaders/cluster-{index}.shader.asset",
                CreateShaderData($"cluster-{index}").ToAsset());
        }

        Guid clusterGuid = project.CreateAsset(
            "assets/pipelines/main.clusterrender.asset",
            CreateClusterRoot(shaderGuids).ToAsset());
        await using var loader = new AssetLoader(project.CreateStorage());

        Asset<ClusterShaders>[] opened = await Task.WhenAll(
            loader.OpenAsync(new AssetId<ClusterShaders>(clusterGuid)).AsTask(),
            loader.OpenAsync(new AssetId<ClusterShaders>(clusterGuid)).AsTask());
        await using Asset<ClusterShaders> first = opened[0];
        await using Asset<ClusterShaders> second = opened[1];
        Assert.NotSame(first, second);
        Assert.Equal(shaderGuids.Order(), first.Dependencies.Order());
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
    public async Task ClusterRenderAssetWithMalformedLastShaderGuidFailsBeforeDependenciesOrPublication()
    {
        using var directory = new TemporaryDirectory();
        Guid clusterGuid = Guid.NewGuid();
        string relativePath = "assets/pipelines/invalid.clusterrender.asset";
        string fullPath = Path.Combine(directory.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Guid valid = Guid.NewGuid();
        int operationCount = Enum.GetValues<ClusterShaderOperationRole>()
            .Count(static role => role != ClusterShaderOperationRole.None);
        ClusterShadersData root = CreateClusterRoot(
            Enumerable.Repeat(valid, operationCount).ToArray());
        root.AssetGuid = clusterGuid.ToString();
        root.Operations![^1].Shaders![0].AssetGuid = "not-a-guid";
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await using (FileStream output = File.Create(fullPath))
            await BinaryDocumentWriter.Create(root).WriteAsync(output);

        var manifest = new AssetManifest();
        manifest.AddAsset(
            clusterGuid,
            "invalid",
            relativePath,
            Asset<ClusterShaders>.FileType,
            Asset<ClusterShaders>.SchemaHash);
        var storage = new DependencyCountingStorage(
            new LooseAssetStorage(directory.Path, manifest),
            clusterGuid);
        await using var loader = new AssetLoader(storage);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => loader.OpenAsync(new AssetId<ClusterShaders>(clusterGuid)).AsTask());
        Assert.Contains($"Operations[{operationCount - 1}].Shader", error.Message);
        Assert.Equal(0, storage.DependencyResolutions);
    }

    private static ShaderData CreateShaderData(string name)
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

    private static ClusterShadersData CreateClusterRoot(IReadOnlyList<Guid> shaders)
    {
        ClusterShaderOperationRole[] roles = Enum.GetValues<ClusterShaderOperationRole>()
            .Where(static role => role != ClusterShaderOperationRole.None)
            .ToArray();
        Assert.Equal(roles.Length, shaders.Count);
        return new ClusterShadersData
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

    private sealed class DependencyCountingStorage : IAssetStorage
    {
        private readonly IAssetStorage _inner;
        private readonly Guid _rootGuid;
        private int _dependencyResolutions;

        internal DependencyCountingStorage(IAssetStorage inner, Guid rootGuid)
        {
            _inner = inner;
            _rootGuid = rootGuid;
        }

        internal int DependencyResolutions => Volatile.Read(ref _dependencyResolutions);

        public bool TryFind(Guid assetGuid, out AssetEntry entry)
        {
            if (assetGuid != _rootGuid)
                Interlocked.Increment(ref _dependencyResolutions);
            return _inner.TryFind(assetGuid, out entry);
        }

        public ValueTask<SomeEngine.Serialization.IO.IRangeSource> OpenAsync(
            AssetEntry entry,
            CancellationToken cancellationToken = default)
            => _inner.OpenAsync(entry, cancellationToken);
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
