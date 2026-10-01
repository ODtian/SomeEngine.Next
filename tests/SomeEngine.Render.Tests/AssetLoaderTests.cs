using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using System.Numerics;
using SlangShaderSharp;
using SomeEngine.Assets;
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Pipeline;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
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
    public async Task IndependentDiskOpensShareIoAndCreatedTexturesOutliveTheLoaderAndStorage()
    {
        using var directory = new TemporaryDirectory();
        Guid id = Guid.NewGuid();
        string path = directory.File("textures.sepack");
        await new AssetPackBuilder().AddAsset(id, TextureAsset.FileType, CookTexture(id, [1, 2, 3, 4], [5, 6]), TextureAsset.SchemaHash).PublishAsync(path);
        await using var storage = new CountingStorage(new AssetPackStorage([await AssetPack.OpenAsync(path)]));
        await using var loader = new AssetLoader(storage);
        TextureAsset[] files = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => TextureAsset.OpenAsync(loader, new AssetId<TextureAsset>(id)).AsTask()));
        Assert.Equal(32, files.Distinct(ReferenceEqualityComparer.Instance).Count());
        Assert.Equal(1, storage.OpenCount);
        await using Texture first = await files[0].GetBlock<TextureBlock>().CreateAsync(files[0].Name, firstMip: 0);
        await using Texture second = await files[1].GetBlock<TextureBlock>().CreateAsync(files[1].Name);
        foreach (TextureAsset file in files) await file.DisposeAsync();
        await loader.DisposeAsync();
        await storage.DisposeAsync();
        Assert.True(first.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var bytes));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes.ToArray());
        await first.DisposeAsync();
        Assert.True(second.TryGetTile(TextureTiles.ChunkKey(1, 2, 0, 0, 0, 0), out var other));
        Assert.Equal(new byte[] { 5, 6 }, other.ToArray());
    }

    [Fact]
    public async Task DomainCreationUsesLazyFilesAndMaterialReferencesCreateActualObjects()
    {
        using var directory = new TemporaryDirectory();
        Guid textureId = Guid.NewGuid(), shaderId = Guid.NewGuid(), meshId = Guid.NewGuid(), materialId = Guid.NewGuid();
        string path = directory.File("all.sepack");
        await new AssetPackBuilder()
            .AddAsset(textureId, TextureAsset.FileType, CookTexture(textureId, [1, 3, 3, 7], [9, 2]), TextureAsset.SchemaHash)
            .AddAsset(shaderId, ShaderAsset.FileType, CookShader(shaderId), ShaderAsset.SchemaHash)
            .AddAsset(meshId, MeshAsset.FileType, CookMesh(meshId), MeshAsset.SchemaHash)
            .AddAsset(materialId, MaterialAsset.FileType, CookMaterial(materialId, textureId), MaterialAsset.SchemaHash)
            .PublishAsync(path);
        await using var storage = new AssetPackStorage([await AssetPack.OpenAsync(path)]);
        var residency = new ResidencyBudgetLedger(new ResidencyBudgets { DecodedCpuBytes = 1024 * 1024 });
        await using var loader = new AssetLoader(storage, new() { MaxDecodedBytes = 1024 * 1024 });
        await using TextureAsset textureFile = await TextureAsset.OpenAsync(loader, new AssetId<TextureAsset>(textureId));
        await using Texture texture = await textureFile.GetBlock<TextureBlock>().CreateAsync(textureFile.Name);
        Assert.Empty(textureFile.GetBlock<TextureBlock>().Chunks.Keys);
        await using ShaderAsset shaderFile = await ShaderAsset.OpenAsync(loader, new AssetId<ShaderAsset>(shaderId));
        Shader shader = await TestShaderLoading.CreateAsync(
            shaderFile,
            SlangCompileTarget.Spirv);
        int targetIndex = shader.RequireSlangTarget(SlangCompileTarget.Spirv);
        Assert.True(shader.TryGetSlangEntry(
            targetIndex,
            "VSMain",
            out EntryPointReflection shaderEntry));
        Assert.True(
            shader.GetSlangEntryBlob(targetIndex, shaderEntry) is not null ||
            !shader.GetSlangEntryBytes(targetIndex, shaderEntry).IsEmpty);
        await using MeshAsset meshFile = await MeshAsset.OpenAsync(loader, new AssetId<MeshAsset>(meshId));
        var mesh = await meshFile.GetBlock<ClusterMeshBlock>().CreateAsync(meshFile.GetBlock<MeshBlock>(), meshFile.Name);
        Assert.True(mesh.TryGetPage(0, out _));
        await meshFile.GetBlock<ClusterMeshBlock>().LoadPagesAsync(mesh, [0]);
        await using MaterialAsset materialFile = await MaterialAsset.OpenAsync(loader, new AssetId<MaterialAsset>(materialId));
        using Material material = await materialFile.CreateAsync(loader);
        StandardMaterial standard = Assert.IsType<StandardMaterial>(material);
        Assert.NotNull(standard.AlbedoMap);
        Assert.NotSame(texture, standard.AlbedoMap);
        await loader.DisposeAsync();
        await meshFile.DisposeAsync();
        await storage.DisposeAsync();
        byte[] page = new byte[mesh.GetPageByteLength(0)];
        mesh.ReadPage(0, page);
        Assert.NotEmpty(page);
        await texture.DisposeAsync();
        material.Dispose();
        textureFile.Dispose();
        shaderFile.Dispose();
        materialFile.Dispose();
        Assert.Equal(0, residency.Used(ResidencyClass.DecodedCpu));
    }

    private static byte[] CookTexture(Guid guid, byte[] mipZero, byte[] mipOne)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.texture.asset");
        using var pixels = new TextureBlock {
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
                    ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = (ulong)mipZero.Length,
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
                    ChunkKey = TextureTiles.ChunkKey(1, 0, 0), DecodedLength = 1,
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
                    ChunkKey = TextureTiles.ChunkKey(1, 1, 0, 0, 0, 0), DecodedLength = 1,
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
                    ChunkKey = TextureTiles.ChunkKey(1, 2, 0, 0, 0, 0), DecodedLength = (ulong)mipOne.Length,
                },
            ],
        };
        pixels.SetBytes(pixels.MipTiles[0].ChunkKey, mipZero);
        pixels.SetBytes(pixels.MipTiles[1].ChunkKey, new byte[] { 0 });
        pixels.SetBytes(pixels.MipTiles[2].ChunkKey, new byte[] { 0 });
        pixels.SetBytes(pixels.MipTiles[3].ChunkKey, mipOne);
        using var asset = new TextureAsset(guid) { Name = "stored-texture" };
        asset.SetBlock(pixels);
        asset.SaveAsync(path).AsTask().GetAwaiter().GetResult();
        return File.ReadAllBytes(path);
    }

    private static byte[] CookMaterial(Guid guid, Guid textureGuid)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.material.asset");
        using var material = new StandardMaterialAsset(guid) {
            Name = "stored-material",
        };
        _ = new StandardMaterialParameters(material.GetBlock<MaterialBlock>()) { AlbedoMap = new(textureGuid) };
        ((material)).SaveAsync(path).AsTask().GetAwaiter().GetResult();
        return File.ReadAllBytes(path);
    }

    private static byte[] CookShader(Guid guid)
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("stored.shader.asset");
        string sourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "Content",
            "Shaders",
            "hello_triangle.shader.asset");
        using ShaderAsset source = ShaderAsset.OpenAsync(sourcePath).AsTask().GetAwaiter().GetResult();
        using ShaderAsset asset = source.CreateCopy(guid);
        asset.Name = "stored-shader";
        asset.SaveAsync(path).AsTask().GetAwaiter().GetResult();
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
        using var asset = new MeshAsset(guid) { Name = "stored-mesh" };
        using var geometry = new MeshBlock();
        using var clusters = new ClusterMeshBlock { VertexEncoding = vertexEncoding };
        clusters.SetPayload(payload, (ulong)pageSize);
        asset.SetBlock(geometry);
        asset.SetBlock(clusters);
        asset.SaveAsync(path).AsTask().GetAwaiter().GetResult();
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
