using SlangShaderSharp;
using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Resources;
using SomeEngine.Resources.Geometry;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Serialization.Streaming;

Game.Assets.MaterialTypes.Register();
string directory = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "obj"), $"SomeEngine-Assets-Aot-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
const string relativePath = "native.texture.asset";
string path = Path.Combine(directory, relativePath);
try
{
    await SomeEngine.Assets.AotSmoke.ImportingSmoke.RunAsync(directory);
    var project = new AssetProject(directory, []);
    Guid expectedGuid = await WriteTextureAsync(project, path);

    await using var loader = new AssetLoader(project.CreateStorage());
    TextureAsset[] opened = await Task.WhenAll(
        TextureAsset.OpenAsync(loader, new AssetId<TextureAsset>(expectedGuid)).AsTask(),
        TextureAsset.OpenAsync(loader, new AssetId<TextureAsset>(expectedGuid)).AsTask());
    await using TextureAsset first = opened[0];
    await using TextureAsset second = opened[1];
    if (ReferenceEquals(first, second) || first.Id != second.Id)
        throw new InvalidOperationException("Disk opens must have independent close boundaries.");
    await using Texture original = await first.GetBlock<TextureBlock>().CreateAsync(first.Name, firstMip: 0);
    await using Texture other = await second.GetBlock<TextureBlock>().CreateAsync(second.Name);
    if (ReferenceEquals(original, other))
        throw new InvalidOperationException("Domain instances must be independent.");
    await first.DisposeAsync();
    await second.DisposeAsync();
    await loader.DisposeAsync();
    if (!original.TryGetTile(TextureTiles.ChunkKey(0, 0, 0), out var mip))
        throw new InvalidOperationException("Explicitly acquired texture pixels are missing.");
    var mipRead = mip;
    if (mipRead.Length != 16 || mipRead.Span[0] != 255)
        throw new InvalidOperationException("Texture streaming must survive asset and loader closure.");
    await using TextureAsset replacement = await TextureAsset.OpenAsync(path);
    await replacement.GetBlock<TextureBlock>().UpdateAsync(original, replacement.Name);
    if (original.Revision != 2 || other.Revision != 1)
        throw new InvalidOperationException("Updating one domain object changed another instance.");
    using var paletteFile = new SmokePaletteAsset
    {
        Name = "palette",
    };
    using var paletteBlock = new SmokePaletteBlock { Colors = [0xff123456u, 0xffabcdefu] };
    paletteFile.SetBlock(paletteBlock);
    Guid paletteId = project.RegisterAsset(await paletteFile.SaveAsync(Path.Combine(directory, "palette.palette.asset")));
    await using var paletteLoader = new AssetLoader(project.CreateStorage());
    await using SmokePaletteAsset paletteContent = await SmokePaletteAsset.OpenAsync(paletteLoader, new AssetId<SmokePaletteAsset>(paletteId));
    SmokePalette palette = (await paletteContent.GetBlockAsync<SmokePaletteBlock>()).Create(paletteContent.Name);
    if (palette.Colors.Length != 2 || palette.Colors[1] != 0xffabcdefu)
        throw new InvalidOperationException("Downstream generated asset format did not run under NativeAOT.");
    using var mesh = CreateMesh();
    using var meshCopy = mesh.Duplicate();
    var meshRead = mesh.GetBlock<SomeEngine.Assets.Schema.ClusterMeshBlock>().Chunks[SomeEngine.Assets.Schema.ClusterMeshBlock.PayloadChunkKey];
    long marker = MeshPageHeader.Size + PackedCluster.SizeInBytes;
    byte[] editedMesh = meshRead.ToArray();
    editedMesh[marker] = 9;
    meshCopy.GetBlock<ClusterMeshBlock>().SetPayload(editedMesh, meshCopy.GetBlock<ClusterMeshBlock>().BvhOffset);
    var originalBytes = mesh.GetBlock<SomeEngine.Assets.Schema.ClusterMeshBlock>().Chunks[SomeEngine.Assets.Schema.ClusterMeshBlock.PayloadChunkKey];
    var copiedBytes = meshCopy.GetBlock<SomeEngine.Assets.Schema.ClusterMeshBlock>().Chunks[SomeEngine.Assets.Schema.ClusterMeshBlock.PayloadChunkKey];
    if (meshRead.Span[checked((int)marker)] != 1 || originalBytes.Span[checked((int)marker)] != 1 || copiedBytes.Span[checked((int)marker)] != 9)
        throw new InvalidOperationException("A mesh byte edit changed another branch or reader.");
    AssetEntry meshEntry = await meshCopy.SaveAsync(Path.Combine(directory, "mesh.mesh.asset"));
    using var meshFile = await MeshAsset.OpenAsync(meshEntry.Path);
    var readyMesh = await meshFile.GetBlock<ClusterMeshBlock>().CreateAsync(meshFile.GetBlock<MeshBlock>(), meshFile.Name);
    if (!readyMesh.TryGetPage(0, out _))
        throw new InvalidOperationException("Mesh creation did not acquire minimum drawable geometry.");
    await meshFile.GetBlock<ClusterMeshBlock>().LoadPagesAsync(readyMesh, [0]);
    meshFile.Dispose();
    byte[] page = new byte[readyMesh.GetPageByteLength(0)];
    readyMesh.ReadPage(0, page);
    if (page[marker] != 9) throw new InvalidOperationException("Saved Mesh pages lost an edit or require a closed source.");
    using var parent = new StandardMaterialAsset();
    _ = new StandardMaterialParameters(parent.GetBlock<MaterialBlock>()) { Roughness = 0.25f };
    project.RegisterAsset(await parent.SaveAsync(Path.Combine(directory, "parent.material.asset")));
    await using var materialLoader = new AssetLoader(project.CreateStorage());
    using var child = new StandardMaterialAsset();
    child.GetBlock<MaterialBlock>().Parent = parent.Reference;
    _ = new StandardMaterialParameters(child.GetBlock<MaterialBlock>()) { Roughness = 0 };
    AssetEntry childEntry = await child.SaveAsync(Path.Combine(directory, "child.material.asset"));
    using var childRead = await StandardMaterialAsset.OpenAsync(childEntry.Path);
    using var zero = await childRead.CreateAsync(materialLoader);
    if (zero.Roughness != 0 || childRead.SourceVersion != childEntry.Version)
        throw new InvalidOperationException("A material lost its explicit zero override or output version.");
    using var childCopy = childRead.Duplicate();
    childCopy.GetBlock<MaterialBlock>().Reset(nameof(StandardMaterial.Roughness));
    using var inherited = await childCopy.CreateAsync(materialLoader);
    if (inherited.Roughness != 0.25f || !childRead.GetBlock<MaterialBlock>().HasOverride(nameof(StandardMaterial.Roughness)))
        throw new InvalidOperationException("Resetting an override changed another branch or lost inheritance.");
    var worldTypes = new SerializationRegistry().Register<SmokeWorldValue, SmokeWorldCodec>(
        new(new Guid("91165D12-730E-4EA2-8183-4C7B1BED4417"), nameof(SmokeWorldValue), 0x534D4F4B450001UL));
    using var world = new World();
    var entity = world.CreateEntity(new SmokeWorldValue { Number = 16, Reference = new(parent.Id) });
    using var worldAsset = new WorldAsset { Name = "world" };
    worldAsset.WriteWorld(world, worldTypes);
    using var worldCopy = worldAsset.Duplicate();
    world.Replace(entity, new SmokeWorldValue { Number = 21 });
    worldCopy.WriteWorld(world, worldTypes);
    AssetEntry worldEntry = await worldAsset.SaveAsync(Path.Combine(directory, "world.scene.asset"));
    using var openedWorld = await WorldAsset.OpenAsync(worldEntry.Path);
    using World loadedWorld = await openedWorld.LoadAsync(worldTypes);
    using World changedWorld = await worldCopy.LoadAsync(worldTypes);
    if (!loadedWorld.IsAlive(entity) || loadedWorld.Read<SmokeWorldValue>(entity).Number != 16
        || changedWorld.Read<SmokeWorldValue>(entity).Number != 21
        || !worldEntry.Dependencies.SequenceEqual(new[] { parent.Id }))
        throw new InvalidOperationException("World asset persistence lost ECS identity, independent content, or dependencies.");
    string curvePath = Path.Combine(directory, "curve.source");
    using var curveAsset = new SomeEngine.Examples.Curves.CurveAsset
    { Name = "speed" };
    curveAsset.GetBlock<SomeEngine.Examples.Curves.CurveBlock>().Points = [new(0, 1), new(1, 5)];
    await curveAsset.SaveSourceAsync(curvePath);
    using var curveSource = await SomeEngine.Examples.Curves.CurveAsset.OpenSourceAsync(curvePath);
    var curve = curveSource.GetBlock<SomeEngine.Examples.Curves.CurveBlock>().CreateCurve();
    curveSource.GetBlock<SomeEngine.Examples.Curves.CurveBlock>().Points[1] = new(1, 9);
    if (curve.Evaluate(0.5f) != 3 || curveSource.Id != curveAsset.Id)
        throw new InvalidOperationException("External text decoding lost identity or shared points.");
    curveSource.GetBlock<SomeEngine.Examples.Curves.CurveBlock>().Update(curve);
    curveSource.Dispose();
    curveAsset.Dispose();
    if (curve.Evaluate(0.5f) != 5)
        throw new InvalidOperationException("The curve lost its data when its source closed.");
    var textShapes = new SmokeTextChoices
    {
        Values = new Dictionary<SmokeTextMode, ISmokeTextChoice?>
        { [SmokeTextMode.Linear] = new SmokeTextNumber { Number = 3 }, [SmokeTextMode.Constant] = null }
    };
    var textDecoded = TextSerializer.Deserialize<SmokeTextChoices>(TextSerializer.Serialize(textShapes));
    if (textDecoded.Values[SmokeTextMode.Linear] is not SmokeTextNumber { Number: 3 }
        || textDecoded.Values[SmokeTextMode.Constant] is not null)
        throw new InvalidOperationException("Static text dictionary, enum or union encoding failed.");
    using var shaderFile = await ShaderAsset.OpenAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "triangle.shader.asset"));
    SlangResult slangResult = Slang.CreateGlobalSession(Slang.ApiVersion, out IGlobalSession slang);
    if (slangResult.Failed || slang is null)
        throw new InvalidOperationException($"Slang global-session creation failed with {slangResult}.");
    TargetDesc[] targets =
    [
        new TargetDesc
        {
            Format = SlangCompileTarget.Dxil,
            Profile = slang.FindProfile("sm_6_6"),
        },
        new TargetDesc
        {
            Format = SlangCompileTarget.Spirv,
            Profile = slang.FindProfile("glsl_460"),
        },
    ];
    SessionDesc sessionDescription = new()
    {
        Targets = targets,
        DefaultMatrixLayoutMode = SlangMatrixLayoutMode.RowMajor,
        CompilerOptionEntries =
        [
            new(CompilerOptionName.NoMangle, CompilerOptionValue.FromInt(1, 0)),
            new(CompilerOptionName.VulkanEmitReflection, CompilerOptionValue.FromInt(1, 0)),
            new(CompilerOptionName.DebugInformation, CompilerOptionValue.FromInt(0, 0)),
            new(CompilerOptionName.ExperimentalFeature, CompilerOptionValue.FromInt(1, 0)),
        ],
    };
    Shader shader = await shaderFile.GetBlock<ShaderBlock>().CreateAsync(
        shaderFile.GetBlock<ShaderSourceBlock>(),
        slang,
        sessionDescription,
        shaderFile.Name);
    shaderFile.Dispose();
    if (shader.SlangTargetCount != 2 || shader.SlangEntryCount == 0)
        throw new InvalidOperationException("Built shader code did not survive its file closing.");
    Console.WriteLine($"assets-nativeaot-smoke:ok schema={TextureAsset.SchemaHash:X16} instances=2 independent-open=ok ready-after-close=ok update=ok extension=ok mesh-file=ok material-inheritance=ok world=ok curve-source=ok text-shapes=ok shader-file=ok");
}
finally
{
    if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
}

static async ValueTask<Guid> WriteTextureAsync(AssetProject project, string path)
{
    byte[] mipZero =
    [
        255, 0, 0, 255,
        0, 255, 0, 255,
        0, 0, 255, 255,
        255, 255, 255, 255,
    ];
    byte[] mipOne = [127, 127, 127, 255];
    using var pixels = new TextureBlock
    {
        Width = 2,
        Height = 2,
        Dimension = SomeEngine.Graphics.TextureDimension.Texture2D,
        Depth = 1,
        MipLevelCount = 2,
        ArrayLayerCount = 1,
        Format = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
        SampledFormat = SomeEngine.Graphics.Format.R8G8B8A8UNorm,
        SampledDimension = SomeEngine.Graphics.TextureViewDimension.Texture2D,
        MipTiles =
            [
                new TextureMipTile
                {
                    MipLevel = 1,
                    ArrayLayer = 0,
                    Face = 0,
                    DepthSlice = 0,
                    Width = 1,
                    Height = 1,
                    RowPitch = 4,
                    SlicePitch = 4,
                    ChunkKey = TextureTiles.ChunkKey(1, 0, 0), DecodedLength = checked((ulong)mipOne.Length),
                },
                new TextureMipTile
                {
                    MipLevel = 0,
                    Width = 2,
                    Height = 2,
                    RowPitch = 8,
                    SlicePitch = 16,
                    ChunkKey = TextureTiles.ChunkKey(0, 0, 0), DecodedLength = checked((ulong)mipZero.Length),
                },
            ],
    };
    pixels.SetBytes(TextureTiles.ChunkKey(0, 0, 0), mipZero);
    pixels.SetBytes(TextureTiles.ChunkKey(1, 0, 0), mipOne);
    using var texture = new TextureAsset { Name = "native-aot" };
    texture.SetBlock(pixels);
    return project.RegisterAsset(await texture.SaveAsync(path));
}


static MeshAsset CreateMesh()
{
    const string encoding = "aot-smoke.opaque-vertex";
    int vertices = MeshPageHeader.Size + PackedCluster.SizeInBytes;
    int bvhOffset = vertices + 1 + 3;
    byte[] bytes = new byte[bvhOffset + Unsafe.SizeOf<ClusterBVHNode>()];
    var header = new MeshPageHeader
    {
        ClusterCount = 1,
        TotalVertexCount = 1,
        TotalTriangleCount = 1,
        VertexBlobBytes = 1,
        ClustersOffset = MeshPageHeader.Size,
        VertexBlobsOffset = (uint)vertices,
        IndicesOffset = (uint)(vertices + 1),
        ExtensionDataOffset = (uint)bvhOffset,
        VertexEncodingHash = VertexEncodingCompatibility.Hash(encoding),
        Flags = MeshPageHeader.RootPage,
    };
    MemoryMarshal.Write(bytes, in header);
    var cluster = new PackedCluster
    {
        VertexBlobSize = 1,
        PackedCounts = 1 | (1u << 8) | ((PackedCluster.RootLeaf | PackedCluster.StreamingLeaf | PackedCluster.FullLeaf) << 24),
        ExtensionOffset = uint.MaxValue,
        MaterialTableOffset = uint.MaxValue
    };
    MemoryMarshal.Write(bytes.AsSpan(MeshPageHeader.Size), in cluster);
    bytes[vertices] = 1;
    var bvh = new ClusterBVHNode();
    var leaf = new ClusterHierarchySlice { PageRangeKey = 1, GroupPartData = 1u << 16 };
    leaf.SetErrors(0, float.PositiveInfinity);
    bvh.SetChild(0, leaf);
    MemoryMarshal.Write(bytes.AsSpan(bvhOffset), in bvh);
    var result = new MeshAsset { Name = "mesh" };
    using var geometry = new MeshBlock();
    using var clusters = new ClusterMeshBlock { VertexEncoding = encoding };
    try
    {
        clusters.SetPayload(bytes, (ulong)bvhOffset);
        clusters.ClusterGroups = [new MeshClusterGroup { PageCount = 1,
            HierarchyParts = [new MeshHierarchyPart()] }];
        result.SetBlock(geometry);
        result.SetBlock(clusters);
        return result;
    }
    catch { result.Dispose(); throw; }
}

[Asset(".palette.asset", SchemaName = "SmokePaletteAsset"), BinarySerializable]
[BinaryFields(nameof(Id), nameof(Name), nameof(AssetType), nameof(BlockDescriptions))]
public sealed partial class SmokePaletteAsset : Asset
{
    public new System.Collections.Immutable.ImmutableArray<AssetBlockDescription> BlockDescriptions
    { get => base.BlockDescriptions; private set => base.BlockDescriptions = value; }
}
[AssetBlock("SmokePaletteBlock"), BinarySerializable, BinaryFields(nameof(Colors))]
public sealed partial class SmokePaletteBlock : AssetBlock
{
    public partial IList<uint> Colors { get; set; }
    public SmokePalette Create(string name)
    {
        using var copy = Duplicate();
        return new(name, copy.Colors.ToArray());
    }
}

public sealed record SmokePalette(string Name, uint[] Colors);

public enum SmokeTextMode : byte { Constant, Linear }

[BinaryUnion(typeof(SmokeTextNumber))]
public interface ISmokeTextChoice;

[BinarySerializable, TextSerializable, BinaryUnionCase(1)]
[BinaryFields(nameof(Number))]
public sealed partial class SmokeTextNumber : ISmokeTextChoice
{
    public int Number { get; set; }
}

[BinarySerializable, TextSerializable]
[BinaryFields(nameof(Values))]
public sealed partial class SmokeTextChoices
{
    public IDictionary<SmokeTextMode, ISmokeTextChoice?> Values { get; set; } = new Dictionary<SmokeTextMode, ISmokeTextChoice?>();
}

public struct SmokeWorldValue : SomeEngine.ECS.IComponent
{
    public int Number;
    public ExternalReferenceKey Reference;
}

public struct SmokeWorldCodec : ICanonicalComponentCodec<SmokeWorldValue>
{
    public void Write(ref DataWriter writer, in SmokeWorldValue value)
    {
        writer.WriteInt32(value.Number);
        writer.WriteExternalReference(value.Reference);
    }
    public void Read(ref DataReader reader, out SmokeWorldValue value)
        => value = new() { Number = reader.ReadInt32(), Reference = reader.ReadExternalReference() };
}
