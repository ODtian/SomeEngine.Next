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
        AssetGuid[] shaderGuids = new AssetGuid[roles.Length];
        for (int index = 0; index < shaderGuids.Length; index++)
        {
            shaderGuids[index] = project.CreateAsset(
                $"assets/shaders/cluster-{index}.shader.asset",
                CreateShaderData($"cluster-{index}"));
        }

        AssetGuid clusterGuid = project.CreateAsset(
            "assets/pipelines/main.clusterrender.asset",
            CreateClusterRoot(shaderGuids));
        await using var loader = new AssetLoader(project.CreateStorage());

        AssetHandle<ClusterShaders> first = loader.Load(new AssetId<ClusterShaders>(clusterGuid));
        AssetHandle<ClusterShaders> second = loader.Load(new AssetId<ClusterShaders>(clusterGuid));
        await Task.WhenAll(
            loader.WaitAsync(first).AsTask(),
            loader.WaitAsync(second).AsTask());
        using AssetRead<ClusterShaders> firstRead = loader.Read(first);
        using AssetRead<ClusterShaders> secondRead = loader.Read(second);
        ClusterShaders shaders = firstRead.Value;

        Assert.Equal(first, second);
        Assert.Same(shaders, secondRead.Value);
        Assert.Equal("MainCluster", shaders.Name);
        Assert.Equal(roles.Length, shaders.Operations?.Count);
        for (int index = 0; index < roles.Length; index++)
        {
            ClusterShaderOperation operation = shaders.Operations![index];
            Assert.Equal(roles[index], operation.Role);
            ShaderRef shader = Assert.IsAssignableFrom<IList<ShaderRef>>(operation.Shaders)[0];
            Assert.True(AssetGuid.TryParse(shader.AssetGuid, out AssetGuid shaderGuid));
            Assert.True(loader.TryFind(shaderGuid, out AssetHandle<Shader> handle));
            using AssetRead<Shader> read = loader.Read(handle);
            Assert.Equal($"cluster-{index}", read.Value.Name);
        }
    }

    [Fact]
    public async Task ClusterRenderAssetWithMalformedLastShaderGuidFailsBeforeDependenciesOrPublication()
    {
        using var directory = new TemporaryDirectory();
        AssetGuid clusterGuid = AssetGuid.New();
        string relativePath = "assets/pipelines/invalid.clusterrender.asset";
        string fullPath = Path.Combine(directory.Path, relativePath.Replace('/', Path.DirectorySeparatorChar));
        AssetGuid valid = AssetGuid.New();
        int operationCount = Enum.GetValues<ClusterShaderOperationRole>()
            .Count(static role => role != ClusterShaderOperationRole.None);
        ClusterShaders root = CreateClusterRoot(
            Enumerable.Repeat(valid, operationCount).ToArray());
        root.AssetGuid = clusterGuid.ToFlatString();
        root.Operations![^1].Shaders![0].AssetGuid = "not-a-guid";
        AssetWriter.Write(BinaryDocumentWriter.Create(root), fullPath);

        var manifest = new AssetManifest();
        manifest.AddAsset(
            clusterGuid,
            "invalid",
            relativePath,
            AssetType<ClusterShaders>.Name,
            ClusterShaders.SchemaFingerprint);
        var storage = new DependencyCountingStorage(
            new LooseAssetStorage(directory.Path, manifest),
            clusterGuid);
        await using var loader = new AssetLoader(storage);

        AssetHandle<ClusterShaders> handle = loader.Load(
            new AssetId<ClusterShaders>(clusterGuid));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => loader.WaitAsync(handle).AsTask());
        Assert.Contains($"Operations[{operationCount - 1}].Shader", error.Message);
        Assert.Equal(0, storage.DependencyResolutions);
    }

    private static Shader CreateShaderData(string name)
        => new()
        {
            Name = name,
            Variants =
            [
                new ShaderBytecode
                {
                    Backend = "test",
                    EntryPoint = "main",
                    Stage = SomeEngine.Assets.Schema.ShaderStage.Compute,
                    Data = new byte[] { 4, 2, 1 },
                },
            ],
            EntryPointReflections =
            [
                new ShaderEntryPointReflection
                {
                    Backend = "test",
                    EntryPoint = "main",
                    Stage = SomeEngine.Assets.Schema.ShaderStage.Compute,
                },
            ],
        };

    private static ClusterShaders CreateClusterRoot(IReadOnlyList<AssetGuid> shaders)
    {
        ClusterShaderOperationRole[] roles = Enum.GetValues<ClusterShaderOperationRole>()
            .Where(static role => role != ClusterShaderOperationRole.None)
            .ToArray();
        Assert.Equal(roles.Length, shaders.Count);
        return new ClusterShaders
        {
            Name = "MainCluster",
            Operations = roles
                .Select((role, index) => Operation(role, shaders[index]))
                .ToArray(),
        };
    }

    private static ClusterShaderOperation Operation(
        ClusterShaderOperationRole role,
        AssetGuid guid)
    {
        var result = new ClusterShaderOperation
        {
            Role = role,
        };
        if (role is ClusterShaderOperationRole.HardwareVisibilityRaster
            or ClusterShaderOperationRole.SoftwareDepthMerge
            or ClusterShaderOperationRole.TemporalResolve
            or ClusterShaderOperationRole.ToneMapAndPresent)
        {
            result.Shaders =
            [
                new ShaderRef
                {
                    AssetGuid = guid.ToFlatString(),
                    EntryPoint = "vertex",
                    Stage = ShaderStage.Vertex,
                },
                new ShaderRef
                {
                    AssetGuid = guid.ToFlatString(),
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
                    AssetGuid = guid.ToFlatString(),
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
        private readonly AssetGuid _rootGuid;
        private int _dependencyResolutions;

        internal DependencyCountingStorage(IAssetStorage inner, AssetGuid rootGuid)
        {
            _inner = inner;
            _rootGuid = rootGuid;
        }

        internal int DependencyResolutions => Volatile.Read(ref _dependencyResolutions);

        public bool TryFind(AssetGuid assetGuid, out AssetEntry entry)
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
