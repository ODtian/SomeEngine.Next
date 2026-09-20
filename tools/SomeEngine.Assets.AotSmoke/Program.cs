using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;
using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Streaming;

string directory = Path.Combine(Path.Combine(Directory.GetCurrentDirectory(), "obj"), $"SomeEngine-Assets-Aot-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
const string relativePath = "native.texture.asset";
string path = Path.Combine(directory, relativePath);
try
{
    var project = new AssetProject(directory, []);
    Guid expectedGuid = await WriteTextureAsync(project, path);

    await using var loader = new AssetLoader(project.CreateStorage());
    Texture.Asset[] opened = await Task.WhenAll(
        Texture.Asset.OpenAsync(loader, new AssetId<Texture>(expectedGuid)).AsTask(),
        Texture.Asset.OpenAsync(loader, new AssetId<Texture>(expectedGuid)).AsTask());
    await using Texture.Asset first = opened[0];
    await using Texture.Asset second = opened[1];
    if (ReferenceEquals(first, second) || first.Id != second.Id)
        throw new InvalidOperationException("Disk opens must have independent close boundaries.");
    await using Texture original = await Texture.CreateAsync(first);
    await using Texture other = await Texture.CreateAsync(second);
    if (ReferenceEquals(original, other))
        throw new InvalidOperationException("Domain instances must be independent.");
    await first.DisposeAsync();
    await second.DisposeAsync();
    await loader.DisposeAsync();
    using ResidentChunkLease mip = await original.AcquireMipTileAsync(0, 0, 0);
    if (mip.Memory.Length != 16 || mip.Memory.Span[0] != 255)
        throw new InvalidOperationException("Texture streaming must survive asset and loader closure.");
    await using Texture.Asset replacement = await Texture.Asset.OpenAsync(path);
    await original.UpdateAsync(replacement);
    if (original.Revision != 2 || other.Revision != 1)
        throw new InvalidOperationException("Updating one domain object changed another instance.");
    using var paletteFile = new SmokePalette.Asset
    {
        Name = "palette", Colors = [0xff123456u, 0xffabcdefu],
    };
    Guid paletteId = project.RegisterAsset(await paletteFile.SaveAsync(Path.Combine(directory, "palette.palette.asset")));
    await using var paletteLoader = new AssetLoader(project.CreateStorage());
    await using SmokePalette.Asset paletteContent = await SmokePalette.Asset.OpenAsync(paletteLoader, new AssetId<SmokePalette>(paletteId));
    SmokePalette palette = await paletteContent.LoadAsync();
    if (palette.Colors.Length != 2 || palette.Colors[1] != 0xffabcdefu)
        throw new InvalidOperationException("Downstream generated asset format did not run under NativeAOT.");
    using var mesh = new Mesh.Asset { Payload = new byte[] { 1, 2, 3 } };
    using var meshCopy = mesh.Duplicate();
    using var meshRead = mesh.ReadPayload()!;
    using (var edit = meshCopy.WritePayload()) edit.Span[0] = 9;
    if (meshRead.Memory.Span[0] != 1 || mesh.Payload!.Value.Span[0] != 1 || meshCopy.Payload!.Value.Span[0] != 9)
        throw new InvalidOperationException("A mesh byte edit changed another branch or reader.");
    using var parent = new StandardMaterial.Asset { Roughness = 0.25f };
    project.RegisterAsset(await parent.SaveAsync(Path.Combine(directory, "parent.material.asset")));
    await using var materialLoader = new AssetLoader(project.CreateStorage());
    using var child = new StandardMaterial.Asset { Parent = parent.Id, Roughness = 0 };
    AssetEntry childEntry = await child.SaveAsync(Path.Combine(directory, "child.material.asset"));
    using var childRead = await StandardMaterial.Asset.OpenAsync(childEntry.Path);
    using var zero = await childRead.LoadAsync(materialLoader);
    if (zero.Roughness != 0 || childRead.SourceVersion != childEntry.Version)
        throw new InvalidOperationException("A material lost its explicit zero override or output version.");
    using var childCopy = childRead.Duplicate();
    childCopy.Reset(nameof(StandardMaterial.Roughness));
    using var inherited = await childCopy.LoadAsync(materialLoader);
    if (inherited.Roughness != 0.25f || !childRead.HasOverride(nameof(StandardMaterial.Roughness)))
        throw new InvalidOperationException("Resetting an override changed another branch or lost inheritance.");
    var worldTypes = new SerializationRegistry().Register<SmokeWorldValue, SmokeWorldCodec>(
        new(new Guid("91165D12-730E-4EA2-8183-4C7B1BED4417"), nameof(SmokeWorldValue), 0x534D4F4B450001UL));
    using var world = new World();
    var entity = world.CreateEntity(new SmokeWorldValue { Number = 16, Reference = new(parent.Id.Value) });
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
        || !worldEntry.Dependencies.SequenceEqual(new[] { parent.Id.Value }))
        throw new InvalidOperationException("World asset persistence lost ECS identity, independent content, or dependencies.");
    Console.WriteLine($"assets-nativeaot-smoke:ok schema={Texture.Asset.SchemaHash:X16} instances=2 independent-open=ok streaming-after-close=ok update=ok extension=ok");
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
    using var texture = new Texture.Asset
        {
            Name = "native-aot",
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
                    Payload = mipOne,
                },
                new TextureMipTile
                {
                    MipLevel = 0,
                    Width = 2,
                    Height = 2,
                    RowPitch = 8,
                    SlicePitch = 16,
                    Payload = mipZero,
                },
            ],
        };
    return project.RegisterAsset(await texture.SaveAsync(path));
}


[Asset(".palette.asset")]
public sealed partial class SmokePalette
{
    public string Name { get; set; } = "";
    public uint[] Colors { get; set; } = [];
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
