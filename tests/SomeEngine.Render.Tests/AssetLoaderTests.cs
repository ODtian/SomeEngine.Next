using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Numerics;
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
    public async Task ShaderCreationRejectsMissingReflectionAfterDiskOpenSucceeds()
    {
        using var directory = new TemporaryDirectory();
        Guid id = Guid.NewGuid();
        string path = directory.File("shader.sepack");
        await new AssetPackBuilder().AddAsset(id, Shader.Asset.FileType, CookShader(id, false), Shader.Asset.SchemaHash).PublishAsync(path);
        await using var storage = new AssetPackStorage([await AssetPack.OpenAsync(path)]);
        await using var loader = new AssetLoader(storage);
        await using Shader.Asset file = await Shader.Asset.OpenAsync(loader, new AssetId<Shader>(id));
        InvalidDataException error = await Assert.ThrowsAsync<InvalidDataException>(() => Shader.CreateAsync(file).AsTask());
        Assert.Contains("serialized entry-point reflection", error.Message);
    }

    [Fact]
    public async Task IndependentDiskOpensShareIoAndCreatedTexturesOutliveTheLoaderAndStorage()
    {
        using var directory = new TemporaryDirectory();
        Guid id = Guid.NewGuid();
        string path = directory.File("textures.sepack");
        await new AssetPackBuilder().AddAsset(id, Texture.Asset.FileType, CookTexture(id, [1, 2, 3, 4], [5, 6]), Texture.Asset.SchemaHash).PublishAsync(path);
        await using var storage = new CountingStorage(new AssetPackStorage([await AssetPack.OpenAsync(path)]));
        await using var loader = new AssetLoader(storage);
        Texture.Asset[] files = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Texture.Asset.OpenAsync(loader, new AssetId<Texture>(id)).AsTask()));
        Assert.Equal(32, files.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(1, storage.OpenCount);
        await using Texture first = await Texture.CreateAsync(files[0]);
        await using Texture second = await Texture.CreateAsync(files[1]);
        foreach (Texture.Asset file in files) await file.DisposeAsync();
        await loader.DisposeAsync();
        await storage.DisposeAsync();
        using ResidentChunkLease bytes = await first.AcquireMipTileAsync(0, 0, 0);
        Assert.Equal([1, 2, 3, 4], bytes.Memory.ToArray());
        await first.DisposeAsync();
        using ResidentChunkLease other = await second.AcquireMipTileAsync(1, 2, 0, 0, 0, 0);
        Assert.Equal([5, 6], other.Memory.ToArray());
    }

    [Fact]
    public async Task DomainCreationUsesLazyFilesAndMaterialReferencesCreateActualObjects()
    {
        using var directory = new TemporaryDirectory();
        Guid textureId = Guid.NewGuid(), shaderId = Guid.NewGuid(), meshId = Guid.NewGuid(), materialId = Guid.NewGuid();
        string path = directory.File("all.sepack");
        await new AssetPackBuilder()
            .AddAsset(textureId, Texture.Asset.FileType, CookTexture(textureId, [1, 3, 3, 7], [9, 2]), Texture.Asset.SchemaHash)
            .AddAsset(shaderId, Shader.Asset.FileType, CookShader(shaderId), Shader.Asset.SchemaHash)
            .AddAsset(meshId, Mesh.Asset.FileType, CookMesh(meshId), Mesh.Asset.SchemaHash)
            .AddAsset(materialId, Material.Asset.FileType, CookMaterial(materialId, textureId), Material.Asset.SchemaHash)
            .PublishAsync(path);
        await using var storage = new AssetPackStorage([await AssetPack.OpenAsync(path)]);
        await using var loader = new AssetLoader(storage);
        await using Texture.Asset textureFile = await Texture.Asset.OpenAsync(loader, new AssetId<Texture>(textureId));
        var residency = new ResidencyBudgetLedger(new ResidencyBudgets { DecodedCpuBytes = 1024 * 1024 });
        await using Texture texture = await Texture.CreateAsync(textureFile, options: new TextureLoadOptions(1024 * 1024, residency));
        Assert.Same(residency, texture.Residency);
        Assert.All(texture.MipTiles!, tile => Assert.Null(tile.Payload));
        await using Shader.Asset shaderFile = await Shader.Asset.OpenAsync(loader, new AssetId<Shader>(shaderId));
        Shader shader = await Shader.CreateAsync(shaderFile);
        Assert.True(shader.TryEntry("spirv", "VSMain", ShaderStage.Vertex, out _, out var entry));
        Assert.False(entry.Code.IsEmpty);
        await using Mesh.Asset meshFile = await Mesh.Asset.OpenAsync(loader, new AssetId<Mesh>(meshId));
        await using Mesh mesh = await Mesh.CreateAsync(meshFile);
        Assert.True(mesh.IsStreamed);
        await using Material.Asset materialFile = await Material.Asset.OpenAsync(loader, new AssetId<Material>(materialId));
        using Material material = await Material.CreateAsync(materialFile, loader);
        StandardMaterial standard = Assert.IsType<StandardMaterial>(material);
        Assert.NotNull(standard.AlbedoMap);
        Assert.NotSame(texture, standard.AlbedoMap);
        await loader.DisposeAsync();
        await meshFile.DisposeAsync();
        await storage.DisposeAsync();
        byte[] page = new byte[mesh.GetPageByteLength(0)];
        await mesh.ReadPageAsync(0, page);
        Assert.NotEmpty(page);
        await texture.DisposeAsync();
        Assert.Equal(0, residency.Used(ResidencyClass.DecodedCpu));
    }

    private static byte[] CookTexture(Guid guid, byte[] mipZero, byte[] mipOne)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.texture.asset");
        ((new Texture.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Texture>(guid)) {
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
                    ArrayLayer = 0,
                    Face = 0,
                    DepthSlice = 0,
                    Width = 1,
                    Height = 1,
                    RowPitch = 1,
                    SlicePitch = 1,
                    Payload = new byte[] { 0 },
                },
                new TextureMipTile
                {
                    MipLevel = 1,
                    ArrayLayer = 1,
                    Face = 0,
                    DepthSlice = 0,
                    Width = 1,
                    Height = 1,
                    RowPitch = 1,
                    SlicePitch = 1,
                    Payload = new byte[] { 0 },
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
        })).SaveAsync(path).AsTask().GetAwaiter().GetResult();
        return File.ReadAllBytes(path);
    }

    private static byte[] CookMaterial(Guid guid, Guid textureGuid)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.material.asset");
        using var material = new StandardMaterial.Asset(new AssetId<Material>(guid)) {
            Name = "stored-material",
            AlbedoMap = new(textureGuid),
        };
        ((material)).SaveAsync(path).AsTask().GetAwaiter().GetResult();
        return File.ReadAllBytes(path);
    }

    private static byte[] CookShader(Guid guid, bool includeReflection = true)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.shader.asset");
        ((new Shader.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Shader>(guid)) {
            Name = "stored-shader",
            Programs = [CookedShaderTestProgram.Create()],
            EntryPointReflections = includeReflection
                ?
                [
                    new ShaderEntryPointReflection
                    {
                        Backend = "spirv",
                        EntryPoint = "VSMain",
                        Stage = SomeEngine.Assets.Schema.ShaderStage.Vertex,
                    },
                ]
                : [],
        })).SaveAsync(path).AsTask().GetAwaiter().GetResult();
        return File.ReadAllBytes(path);
    }

    private static byte[] CookMesh(Guid guid)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.mesh.asset");
        const string vertexEncoding = "someengine.vertex.standard.cluster.v1";
        const int vertexBlobBytes = 1;
        const int indexBytes = 3;
        int pageSize = checked(MeshPageHeader.Size + PackedCluster.SizeInBytes
            + vertexBlobBytes + indexBytes);
        byte[] payload = new byte[checked(pageSize + Marshal.SizeOf<ClusterBVHNode>())];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            TotalVertexCount = 1,
            TotalTriangleCount = 1,
            VertexBlobBytes = vertexBlobBytes,
            ClustersOffset = MeshPageHeader.Size,
            VertexBlobsOffset = MeshPageHeader.Size + PackedCluster.SizeInBytes,
            IndicesOffset = MeshPageHeader.Size + PackedCluster.SizeInBytes + vertexBlobBytes,
            ExtensionDataOffset = checked((uint)pageSize),
            VertexEncodingHash = VertexEncodingCompatibility.Hash(vertexEncoding),
        };
        MemoryMarshal.Write(payload.AsSpan(0, MeshPageHeader.Size), in header);
        var cluster = new PackedCluster
        {
            VertexBlobSize = vertexBlobBytes,
            ExtensionOffset = uint.MaxValue,
            LODRadius = 1,
            BoundMax = Vector3.One,
            PackedCounts = 1u | (1u << 8),
            MaterialTableOffset = uint.MaxValue,
        };
        MemoryMarshal.Write(
            payload.AsSpan(MeshPageHeader.Size, PackedCluster.SizeInBytes),
            in cluster);
        ((new Mesh.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Mesh>(guid)) {
            Name = "stored-mesh",
            BvhOffset = checked((ulong)pageSize),
            Payload = payload,
            VertexEncoding = vertexEncoding,
            Regions = [],
        })).SaveAsync(path).AsTask().GetAwaiter().GetResult();
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
        public bool TryFind(Guid assetGuid, out AssetEntry entry)
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
        public bool TryFind(Guid assetGuid, out AssetEntry entry)
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
