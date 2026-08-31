using SomeEngine.Assets;
using SomeEngine.Assets.Schema;
using SomeEngine.Serialization.Streaming;

string[] forbiddenRuntimeDependencies =
[
    "SharpGLTF",
    "SlangShaderSharp",
    "slang-compiler",
    "MeshOptimizer",
    "FlatSharp",
    "FlatBuffers",
];
string[] unexpectedRuntimeFiles = Directory
    .EnumerateFileSystemEntries(AppContext.BaseDirectory)
    .Select(Path.GetFileName)
    .OfType<string>()
    .Where(name => forbiddenRuntimeDependencies.Any(
        prefix => name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
    .Order(StringComparer.Ordinal)
    .ToArray();
if (unexpectedRuntimeFiles.Length != 0)
{
    throw new InvalidOperationException(
        $"Runtime NativeAOT output contains importer or retired serialization dependencies: {string.Join(", ", unexpectedRuntimeFiles)}");
}

string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-Assets-Aot-{Guid.NewGuid():N}");
Directory.CreateDirectory(directory);
const string relativePath = "native.texture.asset";
string path = Path.Combine(directory, relativePath);
try
{
    var project = new AssetProject(directory, []);
    AssetGuid expectedGuid = WriteTextureData(project, relativePath);

    await using (var loader = new AssetLoader(project.CreateStorage()))
    {
        Task<Texture> firstLoad = loader.LoadAsync(new AssetId<Texture>(expectedGuid)).AsTask();
        Task<Texture> secondLoad = loader.LoadAsync(new AssetId<Texture>(expectedGuid)).AsTask();
        Texture[] loaded = await Task.WhenAll(firstLoad, secondLoad);
        Texture original = loaded[0];
        Texture second = loaded[1];
        if (!ReferenceEquals(original, second))
            throw new InvalidOperationException("NativeAOT runtime requests did not remain single-flight.");
        if (!AssetGuid.TryParse(original.AssetGuid, out AssetGuid loadedGuid)
            || loadedGuid != expectedGuid
            || original.Name != "native-aot"
            || original.MipTiles?.Count != 2
            || original.MipTiles.Any(static tile => tile.Payload.HasValue))
        {
            throw new InvalidOperationException("Texture mip/tile payloads must remain outside the binary-document root.");
        }

        using (ResidentChunkLease lease = await original.AcquireMipTileAsync(
            mipLevel: 1,
            arrayLayer: 0,
            face: 0,
            depthSlice: 0,
            tileX: 0,
            tileY: 0))
        {
            ReadOnlySpan<byte> acquired = lease.Memory.Span;
            if (acquired.Length != 4
                || acquired[0] != 127
                || acquired[1] != 127
                || acquired[2] != 127
                || acquired[3] != 255)
            {
                throw new InvalidOperationException(
                    "Texture mip/tile semantic chunk did not round-trip under NativeAOT.");
            }
        }

        Texture reloaded = await loader.ReloadAsync(original);
        if (!ReferenceEquals(reloaded, original)
            || loader.GetRevision(reloaded) != 2)
        {
            throw new InvalidOperationException(
                "NativeAOT reload did not update the canonical object in place.");
        }
    }

    Console.WriteLine(
        $"assets-nativeaot-smoke:ok rootType={Texture.TypeId:N} " +
        $"fingerprint={Texture.SchemaFingerprint:X16} bytes={new FileInfo(path).Length} " +
        "tiles=2 acquiredMip=1 payload=4 reloadRevision=2");
}
finally
{
    if (Directory.Exists(directory))
        Directory.Delete(directory, recursive: true);
}

static AssetGuid WriteTextureData(AssetProject project, string relativePath)
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
        new Texture
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
        });
}
