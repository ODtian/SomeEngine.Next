using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using SomeEngine.Assets;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Render.Assets;
using SomeEngine.Render.Materials;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;
using SomeEngine.Serialization.Streaming;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Render.Tests;

public sealed class AssetLoaderTests
{
    [Fact]
    public async Task RequestShaderRejectsMissingSerializedEntryPointReflection()
    {
        using var directory = new TemporaryDirectory();
        AssetGuid guid = AssetGuid.New();
        string packPath = directory.File("invalid-shader.sepack");
        await new AssetPackBuilder()
            .AddAsset(
                guid.Value,
                AssetType<Shader>.Name,
                CookShader(guid, includeReflection: false),
                Shader.SchemaFingerprint)
            .PublishAsync(packPath);

        AssetPack pack = await AssetPack.OpenAsync(packPath);
        var storage = new AssetPackStorage(new AssetPackOverlay([pack]));
        await using var loader = new AssetLoader(storage);

        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(
            () => loader.LoadAsync(new AssetId<Shader>(guid)).AsTask());
        Assert.Contains("serialized entry-point reflection", error.Message);
    }

    [Fact]
    public async Task ConcurrentMaterialInstanceParentTypeMismatchFailsClosedWithoutDeadlock()
    {
        using var directory = new TemporaryDirectory();
        AssetGuid firstGuid = AssetGuid.New();
        AssetGuid secondGuid = AssetGuid.New();
        string packPath = directory.File("material-cycle.sepack");
        await new AssetPackBuilder()
            .AddAsset(
                firstGuid.Value,
                AssetType<MaterialInstance>.Name,
                CookMaterialInstance(firstGuid, secondGuid),
                MaterialInstance.SchemaFingerprint)
            .AddAsset(
                secondGuid.Value,
                AssetType<MaterialInstance>.Name,
                CookMaterialInstance(secondGuid, firstGuid),
                MaterialInstance.SchemaFingerprint)
            .PublishAsync(packPath);

        AssetPack pack = await AssetPack.OpenAsync(packPath);
        var storage = new AssetPackStorage(new AssetPackOverlay([pack]));
        await using var loader = new AssetLoader(storage);
        Task<MaterialInstance> first = loader.LoadAsync(
            new AssetId<MaterialInstance>(firstGuid)).AsTask();
        Task<MaterialInstance> second = loader.LoadAsync(
            new AssetId<MaterialInstance>(secondGuid)).AsTask();

        Exception firstError = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await first.WaitAsync(TimeSpan.FromSeconds(2)));
        Exception secondError = await Assert.ThrowsAnyAsync<Exception>(async () =>
            await second.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.True(firstError is InvalidDataException or InvalidOperationException);
        Assert.True(secondError is InvalidDataException or InvalidOperationException);
    }

    [Fact]
    public async Task TextureIsSingleFlightAndOwnsItsOneStreamedDocument()
    {
        using var directory = new TemporaryDirectory();
        AssetGuid guid = AssetGuid.New();
        string packPath = directory.File("custom-kind.sepack");
        await new AssetPackBuilder()
            .AddAsset(
                guid.Value,
                AssetType<Texture>.Name,
                CookTexture(guid, [1, 2, 3, 4], [5, 6]),
                Texture.SchemaFingerprint)
            .PublishAsync(packPath);

        AssetPack pack = await AssetPack.OpenAsync(packPath);
        var storage = new CountingStorage(
            new AssetPackStorage(new AssetPackOverlay([pack])));
        Texture texture;
        await using (var loader = new AssetLoader(storage))
        {
            Task<Texture>[] requested = Enumerable.Range(0, 32)
                .Select(_ => loader.LoadAsync(new AssetId<Texture>(guid)).AsTask())
                .ToArray();
            Texture[] loaded = await Task.WhenAll(requested);

            Assert.All(loaded, value => Assert.Same(loaded[0], value));
            Assert.Equal(1, storage.OpenCount);
            texture = loaded[0];
            Assert.True(texture.IsStreamed);

            using ResidentChunkLease lease = await texture.AcquireMipTileAsync(
                mipLevel: 1,
                arrayLayer: 2,
                face: 0,
                depthSlice: 0,
                tileX: 0,
                tileY: 0);
            Assert.Equal([5, 6], lease.Memory.ToArray());

            await using var foreign = new AssetLoader(new EmptyStorage());
            Assert.False(foreign.TryGetAssetId(texture, out _));
        }

        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await texture.AcquireMipTileAsync(1, 2, 0, 0, 0, 0));
    }

    [Fact]
    public async Task AssetLoaderLoadsEveryRuntimeTypeAndKeepsTextureDocumentLazy()
    {
        AssetGuid guid = AssetGuid.Parse("6575cc07-f3bf-465c-b976-4a3301525511");
        AssetGuid materialGuid = AssetGuid.Parse("7575cc07-f3bf-465c-b976-4a3301525511");
        AssetGuid shaderGuid = AssetGuid.Parse("8575cc07-f3bf-465c-b976-4a3301525511");
        AssetGuid meshGuid = AssetGuid.Parse("9575cc07-f3bf-465c-b976-4a3301525511");
        byte[] mipZero = [1, 3, 3, 7];
        byte[] mipOne = [9, 2];
        byte[] textureData = CookTexture(guid, mipZero, mipOne);
        using var packDirectory = new TemporaryDirectory();
        string packPath = packDirectory.File("runtime-assets.sepack");
        await new AssetPackBuilder()
            .AddAsset(guid.Value, AssetType<Texture>.Name, textureData, Texture.SchemaFingerprint)
            .AddAsset(
                materialGuid.Value,
                AssetType<Material>.Name,
                CookMaterial(materialGuid, guid),
                Material.SchemaFingerprint)
            .AddAsset(
                shaderGuid.Value,
                AssetType<Shader>.Name,
                CookShader(shaderGuid),
                Shader.SchemaFingerprint)
            .AddAsset(
                meshGuid.Value,
                AssetType<Mesh>.Name,
                CookMesh(meshGuid),
                Mesh.SchemaFingerprint)
            .PublishAsync(packPath);

        AssetPack pack = await AssetPack.OpenAsync(packPath);
        var storage = new AssetPackStorage(new AssetPackOverlay([pack]));
        var residency = new ResidencyBudgetLedger(new ResidencyBudgets
        {
            CompressedBytes = 1024 * 1024,
            DecodedCpuBytes = 1024 * 1024,
            UploadStagingBytes = 1024 * 1024,
            GpuBytes = 1024 * 1024,
        });

        AssetLoaderOptions options = AssetLoaderOptions.Empty.With(
            new TextureLoadOptions(1024 * 1024, residency));
        await using (var loader = new AssetLoader(storage, options))
        {
            Texture[] loaded = await Task.WhenAll(
                loader.LoadAsync(new AssetId<Texture>(guid)).AsTask(),
                loader.LoadAsync(new AssetId<Texture>(guid)).AsTask());

            Assert.Same(loaded[0], loaded[1]);
            Texture texture = loaded[0];
            Assert.True(texture.IsStreamed);
            Assert.Same(residency, texture.Residency);
            IList<TextureMipTile> mipTiles = Assert.IsAssignableFrom<IList<TextureMipTile>>(
                texture.MipTiles);
            Assert.Equal(2, mipTiles.Count);
            Assert.All(mipTiles, static tile => Assert.Null(tile.Payload));

            using ResidentChunkLease lease = await texture.AcquireMipTileAsync(
                mipLevel: 1,
                arrayLayer: 2,
                face: 0,
                depthSlice: 0,
                tileX: 0,
                tileY: 0);
            Assert.Equal(mipOne, lease.Memory.ToArray());
            Assert.Equal(mipOne.Length, residency.Used(ResidencyClass.DecodedCpu));
            Assert.True(texture.StreamingMetrics!.Snapshot().Requests >= 1);

            Shader shader = await loader.LoadAsync(new AssetId<Shader>(shaderGuid));
            Assert.True(shader.TryVariant(
                "test",
                "main",
                SomeEngine.Assets.Schema.ShaderStage.Compute,
                out ShaderBytecode shaderVariant));
            Assert.Equal([4, 2, 1], shaderVariant.Data!.Value.ToArray());

            Mesh mesh = await loader.LoadAsync(new AssetId<Mesh>(meshGuid));
            Assert.True(mesh.IsStreamed);

            Material material = await loader.LoadAsync(new AssetId<Material>(materialGuid));
            TextureBinding binding = Assert.Single(material.Textures!);
            Assert.Equal(guid.ToFlatString(), binding.TextureGuid);
            Assert.Contains(
                Enumerable.Range(0, material.SlotCount),
                slot => ReferenceEquals(texture, material.GetSlotValue(checked((uint)slot))));

            await using var foreignLoader = new AssetLoader(new EmptyStorage());
            Assert.False(foreignLoader.TryGetAssetId(texture, out _));
        }

        Assert.Equal(0, residency.Used(ResidencyClass.DecodedCpu));
        Assert.Equal(0, residency.Used(ResidencyClass.Compressed));
    }

    private static byte[] CookTexture(AssetGuid guid, byte[] mipZero, byte[] mipOne)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.texture.asset");
        AssetWriter.Write(new Texture
        {
            AssetGuid = guid.ToFlatString(),
            Name = "stored-texture",
            Width = 2,
            Height = 2,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 2,
            ArrayLayerCount = 3,
            Format = SomeEngine.Graphics.Format.R8UNorm,
            SampledFormat = SomeEngine.Graphics.Format.R8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2DArray,
            MipTiles =
            [
                new TextureMipTile
                {
                    MipLevel = 0,
                    Width = 2,
                    Height = 2,
                    RowPitch = 2,
                    SlicePitch = 4,
                    Payload = mipZero,
                },
                new TextureMipTile
                {
                    MipLevel = 1,
                    ArrayLayer = 2,
                    Face = 0,
                    DepthSlice = 0,
                    Width = 1,
                    Height = 1,
                    RowPitch = 1,
                    SlicePitch = 2,
                    Payload = mipOne,
                },
            ],
        }, path);
        return File.ReadAllBytes(path);
    }

    private static byte[] CookMaterial(AssetGuid guid, AssetGuid textureGuid)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.material.asset");
        AssetWriter.Write(new Material
        {
            AssetGuid = guid.ToFlatString(),
            Name = "stored-material",
            Passes = [],
            Textures =
            [
                new TextureBinding
                {
                    Name = "AlbedoMap",
                    TextureGuid = textureGuid.ToFlatString(),
                },
            ],
            Scalars = [],
        }, path);
        return File.ReadAllBytes(path);
    }

    private static byte[] CookShader(AssetGuid guid, bool includeReflection = true)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.shader.asset");
        AssetWriter.Write(new Shader
        {
            AssetGuid = guid.ToFlatString(),
            Name = "stored-shader",
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
            EntryPointReflections = includeReflection
                ?
                [
                    new ShaderEntryPointReflection
                    {
                        Backend = "test",
                        EntryPoint = "main",
                        Stage = SomeEngine.Assets.Schema.ShaderStage.Compute,
                    },
                ]
                : [],
        }, path);
        return File.ReadAllBytes(path);
    }

    private static byte[] CookMaterialInstance(AssetGuid guid, AssetGuid parentGuid)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("cycle.materialinstance.asset");
        AssetWriter.Write(new MaterialInstance
        {
            AssetGuid = guid.ToFlatString(),
            ParentGuid = parentGuid.ToFlatString(),
            Overrides = [],
            ScalarOverrides = [],
        }, path);
        return File.ReadAllBytes(path);
    }

    private static byte[] CookMesh(AssetGuid guid)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.mesh.asset");
        int pageSize = checked(MeshPageHeader.Size + GPUCluster.SizeInBytes);
        byte[] payload = new byte[checked(pageSize + Marshal.SizeOf<ClusterBVHNode>())];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            ClustersOffset = MeshPageHeader.Size,
            PositionsOffset = checked((uint)pageSize),
            AttributesOffset = checked((uint)pageSize),
            IndicesOffset = checked((uint)pageSize),
            VertexStride = 32,
            QuantStep = 1f,
        };
        MemoryMarshal.Write(payload.AsSpan(0, MeshPageHeader.Size), in header);
        AssetWriter.Write(new Mesh
        {
            AssetGuid = guid.ToFlatString(),
            Name = "stored-mesh",
            BvhOffset = checked((ulong)pageSize),
            Payload = payload,
            VertexStride = 32,
            Regions = [],
        }, path);
        return File.ReadAllBytes(path);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SomeEngine-RuntimeTexture-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string File(string relative)
            => System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }

    private sealed class EmptyStorage : IAssetStorage
    {
        public bool TryFind(AssetGuid assetGuid, out AssetEntry entry)
        {
            entry = default;
            return false;
        }

        public ValueTask<IRangeSource> OpenAsync(
            AssetEntry entry,
            CancellationToken cancellationToken = default)
            => ValueTask.FromException<IRangeSource>(
                new KeyNotFoundException($"Asset {entry.AssetGuid} is absent."));
    }

    private sealed class CountingStorage(AssetPackStorage inner)
        : IAssetStorage, IAsyncDisposable
    {
        private readonly AssetPackStorage _inner = inner;
        private int _openCount;

        public int OpenCount => Volatile.Read(ref _openCount);
        public bool TryFind(AssetGuid assetGuid, out AssetEntry entry)
            => _inner.TryFind(assetGuid, out entry);

        public async ValueTask<IRangeSource> OpenAsync(
            AssetEntry entry,
            CancellationToken cancellationToken = default)
        {
            IRangeSource source = await _inner.OpenAsync(entry, cancellationToken);
            Interlocked.Increment(ref _openCount);
            return source;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

}
