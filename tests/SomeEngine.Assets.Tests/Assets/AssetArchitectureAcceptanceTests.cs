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
        ClusterShadersData cluster = CreateClusterRenderAsset(
            clusterGuid,
            "cluster-root",
            shaderGuid);
        AssetWriter.Write((cluster).ToAsset(), clusterPath);

        Guid materialGuid = Guid.NewGuid();
        string materialPath = directory.File("root.material.asset");
        MaterialData material = new StandardMaterialData
        {
            AssetGuid = materialGuid.ToString(),
            Name = "strong material",
            Roughness = 0.37f,
        };
        AssetWriter.Write((material).ToAsset(), materialPath);

        await using var clusterDocument = await AssetTestFiles.OpenAsync<ClusterShaders, ClusterShadersData>(clusterPath);
        await using var materialDocument = await AssetTestFiles.OpenAsync<Material, MaterialData>(materialPath);
        ClusterShadersData loadedCluster = clusterDocument.Root;
        MaterialData loadedMaterial = materialDocument.Root;

        Assert.Equal(cluster.AssetGuid, loadedCluster.AssetGuid);
        Assert.Equal(cluster.Name, loadedCluster.Name);
        Assert.Equal(cluster.Operations?.Count, loadedCluster.Operations?.Count);
        Assert.All(
            loadedCluster.Operations!,
            operation => Assert.All(
                operation.Shaders!,
                shader => Assert.Equal(shaderGuid.ToString(), shader.AssetGuid)));
        await using Asset<Material> materialFile = await AssetLoader.OpenFileAsync<Material>(materialPath);
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
        AssetWriter.Write((new TextureData
        {
            AssetGuid = Guid.NewGuid().ToString(),
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
        }).ToAsset(), path);

        await using BinaryDocument<TextureData> document =
            await AssetTestFiles.OpenAsync<Texture, TextureData>(path);
        TextureMipTile tile = Assert.Single(document.Root.MipTiles!);
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
        AssetWriter.Write((new ShaderData
        {
            AssetGuid = Guid.NewGuid().ToString(),
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
        }).ToAsset(), path);

        await using BinaryDocument<ShaderData> document =
            await AssetTestFiles.OpenAsync<Shader, ShaderData>(path);
        CookedShaderProgram program = Assert.Single(document.Root.Programs!);
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
        AssetWriter.Write((new MeshData
        {
            AssetGuid = Guid.NewGuid().ToString(),
            Name = "range-mesh",
            Payload = payload,
            BvhOffset = bvhOffset,
            VertexEncoding = StandardClusterVertexEncoder.EncodingCompatibility,
            Regions = [],
        }).ToAsset(), path);

        await using BinaryDocument<MeshData> document =
            await AssetTestFiles.OpenAsync<Mesh, MeshData>(path);
        await using IRangeSource rangeSource =
            await document.OpenChunkRangeSourceAsync(document.Root.PayloadChunk);
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

        AssetWriter.Write((new TextureData
        {
            AssetGuid = textureGuid.ToString(),
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
        }).ToAsset(), texturePath);
        byte[] streamedMeshPayload = CreateMinimalStreamedMeshPayload(out ulong bvhOffset);
        AssetWriter.Write((new MeshData
        {
            AssetGuid = meshGuid.ToString(), Name = "mesh", Payload = streamedMeshPayload,
            BvhOffset = bvhOffset,
            VertexEncoding = StandardClusterVertexEncoder.EncodingCompatibility,
            Regions = [],
        }).ToAsset(), meshPath);
        AssetWriter.Write((new ShaderData
        {
            AssetGuid = shaderGuid.ToString(),
            Name = "shader",
            Programs = [new CookedShaderProgram { Backend = "test", Data = new byte[] { 3 } }],
            EntryPointReflections = [new ShaderEntryPointReflection { EntryPoint = "main" }],
        }).ToAsset(), shaderPath);
        MaterialData material = new StandardMaterialData
        {
            AssetGuid = materialGuid.ToString(),
            Name = "material",
        };
        AssetWriter.Write((material).ToAsset(), materialPath);
        AssetWriter.Write((
            CreateClusterRenderAsset(clusterGuid, "cluster", shaderGuid)).ToAsset(),
            clusterPath);

        AssetManifest manifest = new();
        manifest.AddAsset(textureGuid, "texture", Path.GetFileName(texturePath), Asset<Texture>.FileType, TextureData.SchemaFingerprint);
        manifest.AddAsset(meshGuid, "mesh", Path.GetFileName(meshPath), Asset<Mesh>.FileType, MeshData.SchemaFingerprint);
        manifest.AddAsset(shaderGuid, "shader", Path.GetFileName(shaderPath), Asset<Shader>.FileType, ShaderData.SchemaFingerprint);
        manifest.AddAsset(materialGuid, "material", Path.GetFileName(materialPath), Asset<Material>.FileType, MaterialData.SchemaFingerprint);
        manifest.AddAsset(clusterGuid, "cluster", Path.GetFileName(clusterPath), Asset<ClusterShaders>.FileType, ClusterShadersData.SchemaFingerprint);
        IAssetStorage storage = new LooseAssetStorage(directory.Path, manifest);

        await AssertStorageOpenAsync<Texture, TextureData>(storage, textureGuid);
        await AssertStorageOpenAsync<Mesh, MeshData>(storage, meshGuid);
        await AssertStorageOpenAsync<Shader, ShaderData>(storage, shaderGuid);
        await AssertStorageOpenAsync<Material, MaterialData>(storage, materialGuid);
        await AssertStorageOpenAsync<ClusterShaders, ClusterShadersData>(storage, clusterGuid);
    }

    private static ClusterShadersData CreateClusterRenderAsset(
        Guid assetGuid,
        string name,
        Guid shaderGuid)
    {
        string shader = shaderGuid.ToString();
        return new ClusterShadersData
        {
            AssetGuid = assetGuid.ToString(),
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

    private static async Task AssertStorageOpenAsync<T, TFields>(IAssetStorage storage, Guid id)
        where T : class where TFields : class, IBinaryContract<TFields>
    {
        await using var loader = new AssetLoader(storage);
        await using Asset<T> file = await loader.OpenAsync(new AssetId<T>(id));
        Assert.Equal(id, file.Id.Value);
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
