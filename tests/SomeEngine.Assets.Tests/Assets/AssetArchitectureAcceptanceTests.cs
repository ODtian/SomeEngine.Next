using System.Runtime.InteropServices;
using System.Numerics;
using SomeEngine.Assets.Data;
using SomeEngine.Assets.Importers;
using SomeEngine.Assets.Schema;
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
        ClusterShaders.Asset cluster = CreateClusterRenderAsset(
            clusterGuid,
            "cluster-root",
            shaderGuid);
        ((cluster)).SaveAsync(clusterPath).AsTask().GetAwaiter().GetResult();

        Guid materialGuid = Guid.NewGuid();
        string materialPath = directory.File("root.material.asset");
        using Material.Asset material = new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(materialGuid)) {
            Name = "strong material",
            Roughness = 0.37f,
        };
        ((material)).SaveAsync(materialPath).AsTask().GetAwaiter().GetResult();

        using ClusterShaders.Asset loadedCluster = await ClusterShaders.Asset.OpenAsync(clusterPath);

        Assert.Equal(cluster.Id, loadedCluster.Id);
        Assert.Equal(cluster.Name, loadedCluster.Name);
        Assert.Equal(cluster.Operations?.Count, loadedCluster.Operations?.Count);
        Assert.All(
            loadedCluster.Operations!,
            operation => Assert.All(
                operation.Shaders!,
                shader => Assert.Equal(shaderGuid.ToString(), shader.AssetGuid)));
        await using Material.Asset materialFile = await Material.Asset.OpenAsync(materialPath);
        await using var loader = new AssetLoader(new LooseAssetStorage(directory.Path, new AssetManifest()));
        using Material instance = await Material.CreateAsync(materialFile, loader);
        StandardMaterial standard = Assert.IsType<StandardMaterial>(instance);
        Assert.Equal(materialGuid, materialFile.Id.Value);
        Assert.Equal("strong material", standard.Name);
        Assert.Equal(0.37f, standard.Roughness);
    }

    [Fact]
    public async Task TextureMipTile_IsAvailableThroughPublicDocument()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("lease.texture.asset");
        byte[] expected = [3, 1, 4, 1, 5, 9, 2, 6];
        ((new Texture.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Texture>(Guid.NewGuid())) {
            Name = "lease-texture",
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
                    Payload = expected,
                },
            ],
        })).SaveAsync(path).AsTask().GetAwaiter().GetResult();

        using Texture.Asset file = await Texture.Asset.OpenAsync(path);
        await using BinaryDocument document = file.RetainSource()!;
        TextureMipTile tile = Assert.Single(file.MipTiles!);
        Assert.Null(tile.Payload);
        using ChunkLease lease = await document.AcquireChunkAsync(tile.PayloadChunk);

        Assert.Equal(expected, lease.Memory.ToArray());
    }

    [Fact]
    public async Task ShaderVariant_IsAvailableThroughPublicDocument()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("lease.shader.asset");
        byte[] expected = Enumerable.Range(0, 257).Select(static value => (byte)value).ToArray();
        ((new Shader.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Shader>(Guid.NewGuid())) {
            Name = "lease-shader",
            Programs =
            [
                new CookedShaderProgram
                {
                    Backend = "test",
                    Data = expected,
                },
            ],
            EntryPointReflections = [new ShaderEntryPointReflection { EntryPoint = "main" }],
        })).SaveAsync(path).AsTask().GetAwaiter().GetResult();

        using Shader.Asset file = await Shader.Asset.OpenAsync(path);
        await using BinaryDocument document = file.RetainSource()!;
        CookedShaderProgram program = Assert.Single(file.Programs!);
        Assert.Null(program.Data);
        using ChunkLease lease = await document.AcquireChunkAsync(program.DataChunk);

        Assert.Equal(expected, lease.Memory.ToArray());
    }

    [Fact]
    public async Task MeshPayloadRangeSource_ReadsOnlyRequestedSliceThroughPublicDocument()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("range.mesh.asset");
        byte[] payload = CreateMinimalStreamedMeshPayload(out ulong bvhOffset);
        ((new Mesh.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Mesh>(Guid.NewGuid())) {
            Name = "range-mesh",
            Payload = payload,
            BvhOffset = bvhOffset,
            VertexEncoding = StandardClusterVertexEncoder.EncodingCompatibility,
            Regions = [],
        })).SaveAsync(path).AsTask().GetAwaiter().GetResult();

        using Mesh.Asset file = await Mesh.Asset.OpenAsync(path);
        await using BinaryDocument document = file.RetainSource()!;
        await using IRangeSource rangeSource =
            await document.OpenChunkRangeSourceAsync(new BinaryChunkRef(file.PayloadKey, checked((long)file.PayloadLength)));
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

        ((new Texture.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Texture>(textureGuid)) {
            Name = "texture",
            Width = 1,
            Height = 1,
            Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
            Depth = 1,
            MipLevelCount = 1,
            ArrayLayerCount = 1,
            Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledFormat = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
            SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
            MipTiles = [new TextureMipTile { Width = 1, Height = 1, Payload = new byte[] { 1 } }],
        })).SaveAsync(texturePath).AsTask().GetAwaiter().GetResult();
        byte[] streamedMeshPayload = CreateMinimalStreamedMeshPayload(out ulong bvhOffset);
        ((new Mesh.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Mesh>(meshGuid)) { Name = "mesh", Payload = streamedMeshPayload,
            BvhOffset = bvhOffset,
            VertexEncoding = StandardClusterVertexEncoder.EncodingCompatibility,
            Regions = [],
        })).SaveAsync(meshPath).AsTask().GetAwaiter().GetResult();
        ((new Shader.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Shader>(shaderGuid)) {
            Name = "shader",
            Programs = [new CookedShaderProgram { Backend = "test", Data = new byte[] { 3 } }],
            EntryPointReflections = [new ShaderEntryPointReflection { EntryPoint = "main" }],
        })).SaveAsync(shaderPath).AsTask().GetAwaiter().GetResult();
        using Material.Asset material = new StandardMaterial.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.Material>(materialGuid)) {
            Name = "material",
        };
        ((material)).SaveAsync(materialPath).AsTask().GetAwaiter().GetResult();
        ((
            CreateClusterRenderAsset(clusterGuid, "cluster", shaderGuid))).SaveAsync(clusterPath).AsTask().GetAwaiter().GetResult();

        AssetManifest manifest = new();
        manifest.AddAsset(textureGuid, "texture", Path.GetFileName(texturePath), Texture.Asset.FileType, Texture.Asset.SchemaHash);
        manifest.AddAsset(meshGuid, "mesh", Path.GetFileName(meshPath), Mesh.Asset.FileType, Mesh.Asset.SchemaHash);
        manifest.AddAsset(shaderGuid, "shader", Path.GetFileName(shaderPath), Shader.Asset.FileType, Shader.Asset.SchemaHash);
        manifest.AddAsset(materialGuid, "material", Path.GetFileName(materialPath), Material.Asset.FileType, Material.Asset.SchemaHash);
        manifest.AddAsset(clusterGuid, "cluster", Path.GetFileName(clusterPath), ClusterShaders.Asset.FileType, ClusterShaders.Asset.SchemaHash);
        IAssetStorage storage = new LooseAssetStorage(directory.Path, manifest);

        await using var loader = new AssetLoader(storage);
        using var textureFile = await Texture.Asset.OpenAsync(loader, new(textureGuid));
        Assert.Equal(textureGuid, textureFile.Id.Value);
        using var meshFile = await Mesh.Asset.OpenAsync(loader, new(meshGuid));
        Assert.Equal(meshGuid, meshFile.Id.Value);
        using var shaderFile = await Shader.Asset.OpenAsync(loader, new(shaderGuid));
        Assert.Equal(shaderGuid, shaderFile.Id.Value);
        using var materialFile = await Material.Asset.OpenAsync(loader, new(materialGuid));
        Assert.Equal(materialGuid, materialFile.Id.Value);
        using var clusterFile = await ClusterShaders.Asset.OpenAsync(loader, new(clusterGuid));
        Assert.Equal(clusterGuid, clusterFile.Id.Value);
    }

    private static ClusterShaders.Asset CreateClusterRenderAsset(
        Guid assetGuid,
        string name,
        Guid shaderGuid)
    {
        string shader = shaderGuid.ToString();
        return new ClusterShaders.Asset(new global::SomeEngine.Assets.AssetId<global::SomeEngine.Assets.Schema.ClusterShaders>(assetGuid)) {
            Name = name,
            Operations = Enum.GetValues<ClusterShaderOperationRole>()
                .Where(static role => role != ClusterShaderOperationRole.None)
                .Select(role => CreateClusterOperation(role, shader))
                .ToArray(),
        };
    }

    private static ClusterShaderOperation CreateClusterOperation(
        ClusterShaderOperationRole role,
        string shaderGuid)
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
                    AssetGuid = shaderGuid,
                    EntryPoint = "vertex",
                    Stage = ShaderStage.Vertex,
                },
                new ShaderRef
                {
                    AssetGuid = shaderGuid,
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
                    AssetGuid = shaderGuid,
                    EntryPoint = "compute",
                    Stage = ShaderStage.Compute,
                },
            ];
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
