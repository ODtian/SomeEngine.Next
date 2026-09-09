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
    Guid expectedGuid = WriteTextureData(project, relativePath);

    await using var loader = new AssetLoader(project.CreateStorage());
    Asset<Texture>[] opened = await Task.WhenAll(
        loader.OpenAsync(new AssetId<Texture>(expectedGuid)).AsTask(),
        loader.OpenAsync(new AssetId<Texture>(expectedGuid)).AsTask());
    await using Asset<Texture> first = opened[0];
    await using Asset<Texture> second = opened[1];
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
    await using Asset<Texture> replacement = await AssetLoader.OpenFileAsync<Texture>(path);
    await original.UpdateAsync(replacement);
    if (original.Revision != 2 || other.Revision != 1)
        throw new InvalidOperationException("Updating one domain object changed another instance.");
    using Asset<SmokePalette> paletteFile = new SmokePaletteFields
    {
        Name = "palette", Colors = [0xff123456u, 0xffabcdefu],
    }.ToAsset();
    Guid paletteId = project.CreateAsset("palette.palette.asset", paletteFile);
    await using var paletteLoader = new AssetLoader(project.CreateStorage());
    await using Asset<SmokePalette> paletteContent = await paletteLoader.OpenAsync(new AssetId<SmokePalette>(paletteId));
    SmokePalette palette = SmokePalette.Create(paletteContent);
    if (palette.Colors.Length != 2 || palette.Colors[1] != 0xffabcdefu)
        throw new InvalidOperationException("Downstream generated asset format did not run under NativeAOT.");
    Console.WriteLine($"assets-nativeaot-smoke:ok schema={Asset<Texture>.SchemaHash:X16} instances=2 independent-open=ok streaming-after-close=ok update=ok extension=ok");
}
finally
{
    if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
}

static Guid WriteTextureData(AssetProject project, string relativePath)
{
    byte[] mipZero =
    [
        255, 0, 0, 255,
        0, 255, 0, 255,
        0, 0, 255, 255,
        255, 255, 255, 255,
    ];
    byte[] mipOne = [127, 127, 127, 255];
    return project.CreateAsset(
        relativePath,
        new TextureData
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
        }.ToAsset());
}


public sealed class SmokePalette(uint[] colors)
{
    public uint[] Colors { get; } = colors;
    public static SmokePalette Create(Asset<SmokePalette> asset)
        => new(asset.GetFieldsGenerated<SmokePaletteFields>().Colors);
}

[Asset(typeof(SmokePalette), ".palette.asset")]
[SomeEngine.Serialization.BinaryContract]
public sealed partial class SmokePaletteFields
{
    public string? AssetGuid { get; set; }
    public string Name { get; set; } = "";
    public uint[] Colors { get; set; } = [];
}
