using System.Runtime.InteropServices;
using System.Reflection;
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
    public void AssetApiHasOneTypedServiceAndNoParallelAssetRepresentations()
    {
        Assembly assetAssembly = typeof(AssetProject).Assembly;
        Type[] codecs = assetAssembly.ExportedTypes
            .Where(static type => type.IsAbstract
                && type.IsSealed
                && type.Name.EndsWith("Codec", StringComparison.Ordinal))
            .ToArray();
        Assert.Empty(codecs);

        string[] exportedNames = assetAssembly.ExportedTypes
            .Select(static type => type.FullName ?? type.Name)
            .ToArray();
        Assert.DoesNotContain(
            exportedNames,
            static name => name.EndsWith("AssetDataIO", StringComparison.Ordinal));
        Assert.DoesNotContain("SomeEngine.Assets.AssetDatabase", exportedNames);
        Assert.DoesNotContain("SomeEngine.Assets.AssetDataReader", exportedNames);
        Assert.DoesNotContain(
            exportedNames,
            static name => name.EndsWith("AssetData", StringComparison.Ordinal));
        Assert.DoesNotContain(
            exportedNames,
            static name => name.Contains("RuntimeAsset", StringComparison.Ordinal));
        Assert.DoesNotContain(
            exportedNames,
            static name => name.EndsWith("MeshPayloadSource", StringComparison.Ordinal));
        Assert.DoesNotContain(exportedNames, static name => name.EndsWith("Provider", StringComparison.Ordinal));
        Assert.DoesNotContain(
            assetAssembly.GetTypes(),
            static type => type.Name.EndsWith("AssetDataIO", StringComparison.Ordinal));
        Assert.DoesNotContain(
            assetAssembly.GetTypes(),
            static type => type.Name.EndsWith("DocumentIO", StringComparison.Ordinal));
        Assert.DoesNotContain(
            assetAssembly.GetTypes(),
            static type => type.Name == "IAsset`1");
        Assert.DoesNotContain(typeof(IAssetImporter).GetMethods(), static method => method.Name == "Import");
        Assert.DoesNotContain(
            typeof(AssetProject).GetMethods(),
            static method => method.Name is "Load" or "LoadAsync" or "Read" or "ReadAsync");
        Assert.Contains(typeof(AssetProject).GetMethods(), static method => method.Name == "OpenAsync");
        Assert.Contains(typeof(IAssetStorage).GetMethods(), static method => method.Name == "TryFind");
        Assert.Contains(typeof(IAssetStorage).GetMethods(), static method => method.Name == "OpenAsync");
        Assert.DoesNotContain(
            typeof(IAssetStorage).GetMethods(),
            static method => method.IsGenericMethod);
        Assert.Empty(typeof(AssetHandle<>).GetConstructors(
            System.Reflection.BindingFlags.Public
                | System.Reflection.BindingFlags.Instance
                | System.Reflection.BindingFlags.DeclaredOnly));
        FieldInfo handleReference = Assert.Single(typeof(AssetHandle<>).GetFields(
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly));
        Assert.False(handleReference.FieldType.IsValueType);
        Assert.Equal(typeof(AssetId<>), typeof(AssetHandle<>).GetProperty("AssetId")!.PropertyType.GetGenericTypeDefinition());
        Assert.Equal(typeof(AssetLoadState), typeof(AssetHandle<>).GetProperty("LoadState")!.PropertyType);
        Assert.Equal(typeof(ulong), typeof(AssetHandle<>).GetProperty("Revision")!.PropertyType);
        Assert.True(typeof(AssetRead<>).IsSealed);
        Assert.Null(typeof(Mesh).GetMethod(
            "TryRetainPayloadSource",
            BindingFlags.Public | BindingFlags.Instance));

        string[] loaderMethods = typeof(AssetLoader)
            .GetMethods()
            .Where(static method => method.DeclaringType == typeof(AssetLoader))
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("Load", loaderMethods);
        Assert.Contains("WaitAsync", loaderMethods);
        Assert.Contains("ReloadAsync", loaderMethods);
        Assert.Contains("Read", loaderMethods);
        Assert.Contains("TryRead", loaderMethods);
        Assert.Contains("DisposeAsync", loaderMethods);
        Assert.DoesNotContain("LoadAsync", loaderMethods);
        Assert.DoesNotContain("Get", loaderMethods);
        Assert.DoesNotContain("TryGet", loaderMethods);
        Assert.DoesNotContain("Request", loaderMethods);
        Assert.DoesNotContain(
            loaderMethods,
            static name => name is "Publish" or "GetVersion"
                or "RequestMesh" or "RequestTexture" or "RequestShader" or "RequestMaterial");
        Assert.DoesNotContain(
            assetAssembly.GetTypes(),
            static type => type.Name == "IResidentAssetSet");
        string[] loadContextMethods = typeof(AssetLoadContext)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(static method => method.Name)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Assert.Contains("LoadDependencyAsync", loadContextMethods);
        Assert.DoesNotContain("LoadAsync", loadContextMethods);
        Type residentTable = assetAssembly.GetType("SomeEngine.Assets.ResidentAssetTable")!;
        Assert.DoesNotContain(
            residentTable.GetFields(BindingFlags.NonPublic | BindingFlags.Instance),
            static field => field.FieldType.IsGenericType
                && field.FieldType.GetGenericTypeDefinition() == typeof(Dictionary<,>)
                && field.FieldType.GetGenericArguments()[0] == typeof(Type));
        Assert.True(typeof(AssetId<>).IsValueType);
        Assert.True(typeof(BinaryChunkRef).IsValueType);

        Type[] assetTypes =
        [
            typeof(Texture),
            typeof(Mesh),
            typeof(Shader),
            typeof(Material),
            typeof(MaterialInstance),
            typeof(ClusterShaders),
        ];
        foreach (Type assetType in assetTypes)
        {
            Assert.True(assetType.IsSealed, $"Asset root '{assetType.FullName}' must be sealed.");
            Assert.DoesNotContain(
                assetType.GetConstructors(BindingFlags.Public | BindingFlags.Instance),
                constructor =>
                {
                    ParameterInfo[] parameters = constructor.GetParameters();
                    return parameters.Length == 1 && parameters[0].ParameterType == assetType;
                });
            Assert.DoesNotContain(
                assetType.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                property => property.GetAccessors().Any(static accessor => accessor.IsVirtual));
            Assert.DoesNotContain(
                assetType.GetMethods(
                    BindingFlags.Public
                        | BindingFlags.Static
                        | BindingFlags.Instance
                        | BindingFlags.DeclaredOnly),
                static method => method.Name is
                    "ReadAsync" or "OpenAsync" or "LoadAssetAsync" or "CreateWriter" or "GetDependencies");
        }

        Assembly renderAssembly = typeof(SomeEngine.Render.Assets.LiveShaderProgram).Assembly;
        string[] singleAssetNames = assetTypes.Select(static type => type.Name).ToArray();
        Assert.DoesNotContain(
            renderAssembly.ExportedTypes,
            type => singleAssetNames.Contains(type.Name, StringComparer.Ordinal));
    }

    [Fact]
    public async Task RootOnlyAssetsRoundTripClusterShadersAndMaterialInstanceAsync()
    {
        using var directory = new TemporaryDirectory();
        AssetGuid shaderGuid = AssetGuid.New();
        AssetGuid clusterGuid = AssetGuid.New();
        string clusterPath = directory.File("root.clusterrender.asset");
        ClusterShaders cluster = CreateClusterRenderAsset(
            clusterGuid,
            "cluster-root",
            shaderGuid);
        AssetWriter.Write(cluster, clusterPath);

        AssetGuid parentGuid = AssetGuid.New();
        AssetGuid instanceGuid = AssetGuid.New();
        string instancePath = directory.File("root.materialinstance.asset");
        var instance = new MaterialInstance
        {
            AssetGuid = instanceGuid.ToFlatString(),
            ParentGuid = parentGuid.ToFlatString(),
            Overrides = [],
            ScalarOverrides = [],
        };
        AssetWriter.Write(instance, instancePath);

        ClusterShaders loadedCluster =
            await AssetProject.ReadAsync<ClusterShaders>(clusterPath);
        MaterialInstance loadedInstance =
            await AssetProject.ReadAsync<MaterialInstance>(instancePath);

        Assert.Equal(cluster.AssetGuid, loadedCluster.AssetGuid);
        Assert.Equal(cluster.Name, loadedCluster.Name);
        Assert.Equal(24, loadedCluster.Operations?.Count);
        Assert.All(
            loadedCluster.Operations!,
            operation => Assert.Equal(shaderGuid.ToFlatString(), operation.Shader?.ShaderGuid));
        Assert.Equal(instance.AssetGuid, loadedInstance.AssetGuid);
        Assert.Equal(parentGuid.ToFlatString(), loadedInstance.ParentGuid);
    }

    [Fact]
    public async Task TextureMipTile_IsAvailableThroughInternalChunkLeaseReader()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("lease.texture.asset");
        byte[] expected = [3, 1, 4, 1, 5, 9, 2, 6];
        AssetWriter.Write(new Texture
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "lease-texture",
            Width = 2,
            Height = 1,
            Format = "test",
            MipTiles =
            [
                new TextureMipTile
                {
                    Width = 2,
                    Height = 1,
                    Payload = expected,
                },
            ],
        }, path);

        await using BinaryDocument<Texture> document =
            await AssetProject.OpenAsync<Texture>(path);
        TextureMipTile tile = Assert.Single(document.Root.MipTiles!);
        Assert.Null(tile.Payload);
        using ChunkLease lease = await document.AcquireChunkAsync(tile.PayloadChunk);

        Assert.Equal(expected, lease.Memory.ToArray());
    }

    [Fact]
    public async Task ShaderVariant_IsAvailableThroughInternalChunkLeaseReader()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("lease.shader.asset");
        byte[] expected = Enumerable.Range(0, 257).Select(static value => (byte)value).ToArray();
        AssetWriter.Write(new Shader
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "lease-shader",
            Variants =
            [
                new ShaderBytecode
                {
                    Backend = "test",
                    EntryPoint = "main",
                    Data = expected,
                },
            ],
            EntryPointReflections = [new ShaderEntryPointReflection { EntryPoint = "main" }],
        }, path);

        await using BinaryDocument<Shader> document =
            await AssetProject.OpenAsync<Shader>(path);
        ShaderBytecode variant = Assert.Single(document.Root.Variants!);
        Assert.Null(variant.Data);
        using ChunkLease lease = await document.AcquireChunkAsync(variant.DataChunk);

        Assert.Equal(expected, lease.Memory.ToArray());
    }

    [Fact]
    public async Task MeshPayloadRangeSource_ReadsOnlyRequestedSliceThroughInternalReader()
    {
        using var directory = new TemporaryDirectory();
        string path = directory.File("range.mesh.asset");
        int pageBytes = MeshPageHeader.Size + GPUCluster.SizeInBytes + (3 * sizeof(ushort)) + 3;
        byte[] payload = new byte[pageBytes + Marshal.SizeOf<ClusterBVHNode>()];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            TotalVertexCount = 1,
            TotalTriangleCount = 1,
            ClustersOffset = MeshPageHeader.Size,
            PositionsOffset = checked((uint)(MeshPageHeader.Size + GPUCluster.SizeInBytes)),
            AttributesOffset = checked((uint)(MeshPageHeader.Size + GPUCluster.SizeInBytes + (3 * sizeof(ushort)))),
            IndicesOffset = checked((uint)(MeshPageHeader.Size + GPUCluster.SizeInBytes + (3 * sizeof(ushort)))),
            QuantStep = 1f,
        };
        MemoryMarshal.Write(payload, in header);
        AssetWriter.Write(new Mesh
        {
            AssetGuid = AssetGuid.New().ToFlatString(),
            Name = "range-mesh",
            Payload = payload,
            BvhOffset = checked((ulong)pageBytes),
            Attributes = [],
            Regions = [],
        }, path);

        await using BinaryDocument<Mesh> document =
            await AssetProject.OpenAsync<Mesh>(path);
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
        AssetGuid textureGuid = AssetGuid.New();
        AssetGuid meshGuid = AssetGuid.New();
        AssetGuid shaderGuid = AssetGuid.New();
        AssetGuid materialGuid = AssetGuid.New();
        AssetGuid instanceGuid = AssetGuid.New();
        AssetGuid clusterGuid = AssetGuid.New();

        string texturePath = directory.File("stored.texture.asset");
        string meshPath = directory.File("stored.mesh.asset");
        string shaderPath = directory.File("stored.shader.asset");
        string materialPath = directory.File("stored.material.asset");
        string instancePath = directory.File("stored.materialinstance.asset");
        string clusterPath = directory.File("stored.clusterrender.asset");

        AssetWriter.Write(new Texture
        {
            AssetGuid = textureGuid.ToFlatString(),
            Name = "texture",
            Width = 1,
            Height = 1,
            MipTiles = [new TextureMipTile { Width = 1, Height = 1, Payload = new byte[] { 1 } }],
        }, texturePath);
        byte[] streamedMeshPayload = CreateMinimalStreamedMeshPayload(out ulong bvhOffset);
        AssetWriter.Write(new Mesh
        {
            AssetGuid = meshGuid.ToFlatString(), Name = "mesh", Payload = streamedMeshPayload,
            BvhOffset = bvhOffset, Attributes = [], Regions = [],
        }, meshPath);
        AssetWriter.Write(new Shader
        {
            AssetGuid = shaderGuid.ToFlatString(),
            Name = "shader",
            Variants = [new ShaderBytecode { Data = new byte[] { 3 }, EntryPoint = "main" }],
            EntryPointReflections = [new ShaderEntryPointReflection { EntryPoint = "main" }],
        }, shaderPath);
        AssetWriter.Write(new Material
        {
            AssetGuid = materialGuid.ToFlatString(), Name = "material",
            Passes = [], Textures = [], Scalars = [],
        }, materialPath);
        AssetWriter.Write(new MaterialInstance
        {
            AssetGuid = instanceGuid.ToFlatString(), ParentGuid = materialGuid.ToFlatString(),
            Overrides = [], ScalarOverrides = [],
        }, instancePath);
        AssetWriter.Write(
            CreateClusterRenderAsset(clusterGuid, "cluster", shaderGuid),
            clusterPath);

        AssetManifest manifest = new();
        manifest.AddAsset(textureGuid, "texture", Path.GetFileName(texturePath), AssetType<Texture>.Name, Texture.SchemaFingerprint);
        manifest.AddAsset(meshGuid, "mesh", Path.GetFileName(meshPath), AssetType<Mesh>.Name, Mesh.SchemaFingerprint);
        manifest.AddAsset(shaderGuid, "shader", Path.GetFileName(shaderPath), AssetType<Shader>.Name, Shader.SchemaFingerprint);
        manifest.AddAsset(materialGuid, "material", Path.GetFileName(materialPath), AssetType<Material>.Name, Material.SchemaFingerprint);
        manifest.AddAsset(instanceGuid, "instance", Path.GetFileName(instancePath), AssetType<MaterialInstance>.Name, MaterialInstance.SchemaFingerprint);
        manifest.AddAsset(clusterGuid, "cluster", Path.GetFileName(clusterPath), AssetType<ClusterShaders>.Name, ClusterShaders.SchemaFingerprint);
        IAssetStorage storage = new LooseAssetStorage(directory.Path, manifest);

        await AssertStorageOpenAsync<Texture>(storage, textureGuid);
        await AssertStorageOpenAsync<Mesh>(storage, meshGuid);
        await AssertStorageOpenAsync<Shader>(storage, shaderGuid);
        await AssertStorageOpenAsync<Material>(storage, materialGuid);
        await AssertStorageOpenAsync<MaterialInstance>(storage, instanceGuid);
        await AssertStorageOpenAsync<ClusterShaders>(storage, clusterGuid);
    }

    private static ClusterShaders CreateClusterRenderAsset(
        AssetGuid assetGuid,
        string name,
        AssetGuid shaderGuid)
    {
        string shader = shaderGuid.ToFlatString();
        return new ClusterShaders
        {
            AssetGuid = assetGuid.ToFlatString(),
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
            Shader = new ShaderAssetRef { ShaderGuid = shaderGuid },
        };
        if (role is ClusterShaderOperationRole.HardwareVisibilityRaster
            or ClusterShaderOperationRole.SoftwareDepthMerge
            or ClusterShaderOperationRole.TemporalResolve
            or ClusterShaderOperationRole.ToneMapAndPresent)
        {
            result.VertexEntryPoint = "vertex";
            result.PixelEntryPoint = "pixel";
        }
        else
        {
            result.ComputeEntryPoint = "compute";
        }
        return result;
    }

    private static async Task AssertStorageOpenAsync<TContract>(
        IAssetStorage storage,
        AssetGuid guid)
        where TContract : class, IBinaryContract<TContract>
    {
        Assert.True(storage.TryFind(guid, out AssetEntry entry));
        await using BinaryDocument<TContract> document =
            await AssetProject.OpenAsync<TContract>(storage, entry);
        Assert.Equal(guid, AssetType<TContract>.Descriptor.GetAssetGuid(document.Root));
    }

    private static byte[] CreateMinimalStreamedMeshPayload(out ulong bvhOffset)
    {
        int pageSize = checked(MeshPageHeader.Size + GPUCluster.SizeInBytes);
        byte[] payload = new byte[checked(pageSize + Marshal.SizeOf<ClusterBVHNode>())];
        var header = new MeshPageHeader
        {
            ClusterCount = 1,
            ClustersOffset = MeshPageHeader.Size,
            PositionsOffset = checked((uint)pageSize),
            AttributesOffset = checked((uint)pageSize),
            IndicesOffset = checked((uint)pageSize),
            QuantStep = 1f,
        };
        MemoryMarshal.Write(payload.AsSpan(0, MeshPageHeader.Size), in header);
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
