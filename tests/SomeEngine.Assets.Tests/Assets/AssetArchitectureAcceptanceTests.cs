using SomeEngine.Geometry.Cluster;
using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.Resources.Geometry;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Assets.Tests.Assets;

public sealed class AssetArchitectureAcceptanceTests
{


    [Fact]
    public async Task RootAssetsRoundTripClusterShadersAndStrongMaterialAsync()
    {
        using var directory = new TemporaryDirectory();
        Guid shaderGuid = Guid.NewGuid();
        Guid clusterGuid = Guid.NewGuid();
        string clusterPath = directory.File("root.clusterrender.asset");
        using ClusterShadersAsset cluster = CreateClusterRenderAsset(
            clusterGuid,
            "cluster-root",
            shaderGuid);
        await cluster.SaveAsync(clusterPath);

        Guid materialGuid = Guid.NewGuid();
        string materialPath = directory.File("root.material.asset");
        using MaterialAsset material = new StandardMaterialAsset(materialGuid) {
            Name = "strong material",
        };
        _ = new StandardMaterialParameters(material.GetBlock<MaterialBlock>()) { Roughness = 0.37f };
        await material.SaveAsync(materialPath);

        using ClusterShadersAsset loadedCluster = await ClusterShadersAsset.OpenAsync(clusterPath);

        Assert.Equal(cluster.Id, loadedCluster.Id);
        Assert.Equal(cluster.Name, loadedCluster.Name);
        Assert.Equal(cluster.GetBlock<ClusterShadersBlock>().Operations.Count, loadedCluster.GetBlock<ClusterShadersBlock>().Operations.Count);
        Assert.All(
            loadedCluster.GetBlock<ClusterShadersBlock>().Operations,
            operation => Assert.All(
                operation.Shaders!,
                shader => Assert.Equal(new AssetId<ShaderAsset>(shaderGuid), shader.Asset)));
        await using MaterialAsset materialFile = await MaterialAsset.OpenAsync(materialPath);
        await using var loader = new AssetLoader(new LooseAssetStorage(directory.Path, new AssetManifest()));
        using Material instance = await materialFile.CreateAsync(loader);
        StandardMaterial standard = Assert.IsType<StandardMaterial>(instance);
        Assert.Equal(materialGuid, materialFile.Id);
        Assert.Equal("strong material", standard.Name);
        Assert.Equal(0.37f, standard.Roughness);
    }

    [Fact]
    public async Task TextureMipTile_IsAvailableThroughPublicDocument()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("lease.texture.asset");
        byte[] expected = [3, 1, 4, 1, 5, 9, 2, 6];
        using var pixels = new TextureBlock {
            Width = 2,
            Height = 1,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledFormat = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles =
            [
                new TextureMipTile
                {
                    Width = 2,
                    Height = 1,
                    ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = (ulong)expected.Length,
                },
            ],
        };
        pixels.SetBytes(pixels.MipTiles[0].ChunkKey, expected);
        using var texture = new TextureAsset(Guid.NewGuid()) { Name = "lease-texture" };
        texture.SetBlock(pixels);
        await texture.SaveAsync(path);

        using TextureAsset file = await TextureAsset.OpenAsync(path);
        await using BinaryDocument document = await BinaryDocument.OpenAsync(FileRangeSource.Open(path), TextureAsset.WireType, ownsSource: true);
        TextureBlock content = await file.GetBlockAsync<TextureBlock>();
        TextureMipTile tile = Assert.Single(content.MipTiles);
        Assert.Empty(content.Chunks.Keys);
        ulong storedKey = file.BlockDescriptions.Single(block => block.BlockTypeId == TextureBlock.BlockTypeId).GetStorageKey(tile.ChunkKey);
        using ChunkLease lease = await document.AcquireChunkAsync(new BinaryChunkRef(storedKey, (long)tile.DecodedLength));

        Assert.Equal(expected, lease.Memory.ToArray());
    }

    [Fact]
    public async Task ShaderCodeChunkIsAvailableThroughPublicDocument()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("lease.shader.asset");
        byte[] expected = Enumerable.Range(0, 257).Select(static value => (byte)value).ToArray();
        using var shader = new ShaderAsset(Guid.NewGuid()) { Name = "lease-shader" };
        ShaderBlock code = shader.GetBlock<ShaderBlock>();
        var sessionDescription = ShaderTestCompilation.CreateSessionDescription();
        ulong contentKey = ShaderBlock.TargetCodeKey(sessionDescription, 0);
        code.SetTargetCode(sessionDescription, 0, expected);
        await shader.SaveAsync(path);

        using ShaderAsset file = await ShaderAsset.OpenAsync(path);
        await using BinaryDocument document = await BinaryDocument.OpenAsync(FileRangeSource.Open(path), ShaderAsset.WireType, ownsSource: true);
        Assert.Empty(file.GetBlock<ShaderBlock>().Chunks.Keys);
        ulong codeKey = file.BlockDescriptions.Single(block => block.BlockTypeId == ShaderBlock.BlockTypeId)
            .GetStorageKey(contentKey);
        using ChunkLease lease = await document.AcquireChunkAsync(
            new BinaryChunkRef(codeKey, expected.Length));

        Assert.Equal(expected, lease.Memory.ToArray());
    }

    [Fact]
    public async Task MeshPayloadRangeSource_ReadsOnlyRequestedSliceThroughPublicDocument()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("range.mesh.asset");
        byte[] payload = CreateMinimalStreamedMeshPayload(out ulong bvhOffset);
        using var mesh = new MeshAsset(Guid.NewGuid()) { Name = "range-mesh" };
        using var geometry = new MeshBlock();
        using var clusters = new ClusterMeshBlock { VertexEncoding = StandardClusterVertexEncoder.EncodingCompatibility };
        ReadOnlyMemory<byte> bytes = payload;
        clusters.SetPayload(bytes, bvhOffset);
        mesh.SetBlock(geometry);
        mesh.SetBlock(clusters);
        await mesh.SaveAsync(path);

        using MeshAsset file = await MeshAsset.OpenAsync(path);
        await using BinaryDocument document = await BinaryDocument.OpenAsync(FileRangeSource.Open(path), MeshAsset.WireType, ownsSource: true);
        await using IRangeSource rangeSource =
            await document.OpenChunkRangeSourceAsync(new BinaryChunkRef(
                file.BlockDescriptions.Single(block => block.BlockTypeId == ClusterMeshBlock.BlockTypeId).GetStorageKey(ClusterMeshBlock.PayloadChunkKey),
                checked((long)file.GetBlock<ClusterMeshBlock>().PayloadLength)));
        using RangeLease lease = await rangeSource.AcquireAsync(offset: 17, length: 23);

        Assert.Equal(payload.AsSpan(17, 23).ToArray(), lease.Memory.ToArray());
        Assert.Equal(payload.Length, rangeSource.Length);
    }

    [Fact]
    public async Task LooseStorageOpensEveryBuiltInAssetTypeThroughOneReader()
    {
        using var directory = new TemporaryDirectory();
        Guid textureGuid = Guid.NewGuid();
        Guid meshGuid = Guid.NewGuid();
        Guid shaderGuid = Guid.NewGuid();
        Guid materialGuid = Guid.NewGuid();
        Guid clusterGuid = Guid.NewGuid();

        string texturePath = directory.File("stored.texture.asset");
        string meshPath = directory.File("stored.mesh.asset");
        string shaderPath = directory.File("stored.shader.asset");
        string materialPath = directory.File("stored.material.asset");
        string clusterPath = directory.File("stored.clusterrender.asset");

        using var pixels = new TextureBlock {
            Width = 1,
            Height = 1,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledFormat = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 1, Height = 1, ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = 4 }],
        };
        pixels.SetBytes(pixels.MipTiles[0].ChunkKey, new byte[] { 1, 0, 0, 255 });
        using var texture = new TextureAsset(textureGuid) { Name = "texture" };
        texture.SetBlock(pixels);
        await texture.SaveAsync(texturePath);
        byte[] streamedMeshPayload = CreateMinimalStreamedMeshPayload(out ulong bvhOffset);
        using var mesh = new MeshAsset(meshGuid) { Name = "mesh" };
        using var geometry = new MeshBlock();
        using var clusters = new ClusterMeshBlock { VertexEncoding = StandardClusterVertexEncoder.EncodingCompatibility };
        ReadOnlyMemory<byte> bytes = streamedMeshPayload;
        clusters.SetPayload(bytes, bvhOffset);
        mesh.SetBlock(geometry);
        mesh.SetBlock(clusters);
        await mesh.SaveAsync(meshPath);
        using var shader = new ShaderAsset(shaderGuid) { Name = "shader" };
        ShaderBlock code = shader.GetBlock<ShaderBlock>();
        code.SetTargetCode(ShaderTestCompilation.CreateSessionDescription(), 0, new byte[] { 3 });
        await shader.SaveAsync(shaderPath);
        using MaterialAsset material = new StandardMaterialAsset(materialGuid) {
            Name = "material",
        };
            await material.SaveAsync(materialPath);
        using var cluster = CreateClusterRenderAsset(clusterGuid, "cluster", shaderGuid);
        await cluster.SaveAsync(clusterPath);

        AssetManifest manifest = new();
        manifest.AddAsset(textureGuid, "texture", Path.GetFileName(texturePath), TextureAsset.FileType, TextureAsset.SchemaHash);
        manifest.AddAsset(meshGuid, "mesh", Path.GetFileName(meshPath), MeshAsset.FileType, MeshAsset.SchemaHash);
        manifest.AddAsset(shaderGuid, "shader", Path.GetFileName(shaderPath), ShaderAsset.FileType, ShaderAsset.SchemaHash);
        manifest.AddAsset(materialGuid, "material", Path.GetFileName(materialPath), MaterialAsset.FileType, MaterialAsset.SchemaHash);
        manifest.AddAsset(clusterGuid, "cluster", Path.GetFileName(clusterPath), ClusterShadersAsset.FileType, ClusterShadersAsset.SchemaHash);
        IAssetStorage storage = new LooseAssetStorage(directory.Path, manifest);

        await using var loader = new AssetLoader(storage);
        using var textureFile = await TextureAsset.OpenAsync(loader, new(textureGuid));
        Assert.Equal(textureGuid, textureFile.Id);
        using var meshFile = await MeshAsset.OpenAsync(loader, new(meshGuid));
        Assert.Equal(meshGuid, meshFile.Id);
        using var shaderFile = await ShaderAsset.OpenAsync(loader, new(shaderGuid));
        Assert.Equal(shaderGuid, shaderFile.Id);
        using var materialFile = await MaterialAsset.OpenAsync(loader, new(materialGuid));
        Assert.Equal(materialGuid, materialFile.Id);
        using var clusterFile = await ClusterShadersAsset.OpenAsync(loader, new(clusterGuid));
        Assert.Equal(clusterGuid, clusterFile.Id);
    }

    private static ClusterShadersAsset CreateClusterRenderAsset(
        Guid assetGuid,
        string name,
        Guid shaderGuid)
    {
        AssetId<ShaderAsset> shader = new(shaderGuid);
        var asset = new ClusterShadersAsset(assetGuid) { Name = name };
        using var settings = new ClusterShadersBlock {
            Operations = Enum.GetValues<ClusterShaderOperationRole>()
                .Where(static role => role != ClusterShaderOperationRole.None)
                .Select(role => CreateClusterOperation(role, shader))
                .ToArray(),
        };
        asset.SetBlock(settings);
        return asset;
    }

    private static ClusterShaderOperation CreateClusterOperation(
        ClusterShaderOperationRole role,
        AssetId<ShaderAsset> shaderGuid)
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
                    Asset = shaderGuid,
                    EntryPoint = "vertex",
                },
                new ShaderRef
                {
                    Asset = shaderGuid,
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
                    Asset = shaderGuid,
                    EntryPoint = "compute",
                },
            ] };
        }
        return result;
    }

    private static byte[] CreateMinimalStreamedMeshPayload(out ulong bvhOffset)
    {
        const int vertexBlobSize = StandardClusterVertexEncoder.HeaderSize;
        int pageSize = checked(MeshPageHeader.Size + PackedCluster.SizeInBytes + vertexBlobSize + 3);
        byte[] payload = new byte[checked(pageSize + Marshal.SizeOf<ClusterBVHNode>())];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            TotalVertexCount = 1,
            TotalTriangleCount = 1,
            VertexBlobBytes = vertexBlobSize,
            ClustersOffset = MeshPageHeader.Size,
            VertexBlobsOffset = MeshPageHeader.Size + PackedCluster.SizeInBytes,
            IndicesOffset = MeshPageHeader.Size + PackedCluster.SizeInBytes + vertexBlobSize,
            ExtensionDataOffset = checked((uint)pageSize),
            VertexEncodingHash = VertexEncodingCompatibility.Hash(
                StandardClusterVertexEncoder.EncodingCompatibility),
        };
        MemoryMarshal.Write(payload.AsSpan(0, MeshPageHeader.Size), in header);
        var cluster = new PackedCluster
        {
            VertexBlobSize = vertexBlobSize,
            ExtensionOffset = uint.MaxValue,
            LODRadius = 1,
            BoundMax = Vector3.One,
            PackedCounts = 1u | (1u << 8),
            PackedRanges = 0x00000101u,
            MaterialTableOffset = uint.MaxValue,
        };
        MemoryMarshal.Write(
            payload.AsSpan(MeshPageHeader.Size, PackedCluster.SizeInBytes),
            in cluster);
        bvhOffset = checked((ulong)pageSize);
        return payload;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"SomeEngine-AssetAcceptance-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }
        public string File(string name) => System.IO.Path.Combine(Path, name);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
