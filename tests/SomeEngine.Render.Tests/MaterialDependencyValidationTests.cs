using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Render.Tests;

public sealed class MaterialDependencyValidationTests
{
    [Fact]
    public async Task BaseMaterialValidatesEveryDependencyBeforeResolvingAnyOfThem()
    {
        using var directory = new TemporaryDirectory();
        AssetGuid materialGuid = AssetGuid.New();
        AssetGuid shaderGuid = AssetGuid.New();
        AssetGuid textureGuid = AssetGuid.New();
        const string relativePath = "assets/late-invalid.material.asset";
        string fullPath = directory.File(relativePath);
        var root = new Material
        {
            AssetGuid = materialGuid.ToFlatString(),
            Name = "late invalid",
            Passes =
            [
                new PassEntry
                {
                    Shader = new ShaderRef
                    {
                        AssetGuid = shaderGuid.ToFlatString(),
                        EntryPoint = "main",
                        Stage = ShaderStage.Compute,
                    },
                },
            ],
            Textures =
            [
                new TextureBinding { Name = "valid", TextureGuid = textureGuid.ToFlatString() },
                new TextureBinding { Name = "invalid", TextureGuid = "not-a-guid" },
            ],
            Scalars = [],
        };
        AssetWriter.Write(BinaryDocumentWriter.Create(root), fullPath);

        var manifest = new AssetManifest();
        manifest.AddAsset(
            materialGuid,
            "late invalid",
            relativePath,
            AssetType<Material>.Name,
            Material.SchemaFingerprint);
        var storage = new DependencyCountingStorage(
            new LooseAssetStorage(directory.Path, manifest),
            materialGuid);
        await using var loader = new AssetLoader(storage);

        AssetHandle<Material> handle = loader.Load(new AssetId<Material>(materialGuid));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => loader.WaitAsync(handle).AsTask());

        Assert.Contains("Textures[1].TextureGuid", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, storage.DependencyResolutions);
    }

    [Fact]
    public async Task MaterialInstanceValidatesEveryOverrideBeforeResolvingItsParent()
    {
        using var directory = new TemporaryDirectory();
        AssetGuid instanceGuid = AssetGuid.New();
        AssetGuid parentGuid = AssetGuid.New();
        AssetGuid textureGuid = AssetGuid.New();
        const string relativePath = "assets/late-invalid.materialinstance.asset";
        string fullPath = directory.File(relativePath);
        var root = new MaterialInstance
        {
            AssetGuid = instanceGuid.ToFlatString(),
            ParentGuid = parentGuid.ToFlatString(),
            Overrides =
            [
                new ParamOverride { Name = "valid", TextureGuid = textureGuid.ToFlatString() },
                new ParamOverride { Name = "invalid", TextureGuid = "not-a-guid" },
            ],
            ScalarOverrides = [],
        };
        AssetWriter.Write(BinaryDocumentWriter.Create(root), fullPath);

        var manifest = new AssetManifest();
        manifest.AddAsset(
            instanceGuid,
            "late invalid instance",
            relativePath,
            AssetType<MaterialInstance>.Name,
            MaterialInstance.SchemaFingerprint);
        var storage = new DependencyCountingStorage(
            new LooseAssetStorage(directory.Path, manifest),
            instanceGuid);
        await using var loader = new AssetLoader(storage);

        AssetHandle<MaterialInstance> handle = loader.Load(
            new AssetId<MaterialInstance>(instanceGuid));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => loader.WaitAsync(handle).AsTask());

        Assert.Contains("Overrides[1].TextureGuid", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, storage.DependencyResolutions);
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
                $"SomeEngine-MaterialValidation-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        internal string File(string relativePath)
        {
            string path = System.IO.Path.Combine(
                Path,
                relativePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            return path;
        }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
