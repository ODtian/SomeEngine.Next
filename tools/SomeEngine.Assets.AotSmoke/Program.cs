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
        AssetHandle<Texture> first = loader.Load(new AssetId<Texture>(expectedGuid));
        AssetHandle<Texture> second = loader.Load(new AssetId<Texture>(expectedGuid));
        await Task.WhenAll(
            loader.WaitAsync(first).AsTask(),
            loader.WaitAsync(second).AsTask());
        Texture original;
        using (AssetRead<Texture> firstRead = loader.Read(first))
        using (AssetRead<Texture> secondRead = loader.Read(second))
        {
            original = firstRead.Value;
            if (first != second || !ReferenceEquals(original, secondRead.Value))
                throw new InvalidOperationException("NativeAOT runtime requests did not remain single-flight.");
            if (!AssetGuid.TryParse(original.AssetGuid, out AssetGuid loadedGuid)
                || loadedGuid != expectedGuid
                || original.Name != "native-aot"
                || original.MipTiles?.Count != 2
                || original.MipTiles.Any(static tile => tile.Payload.HasValue))
            {
                throw new InvalidOperationException("Texture mip/tile payloads must remain outside the binary-document root.");
            }

            using ResidentChunkLease lease = await original.AcquireMipTileAsync(
                mipLevel: 1,
                arrayLayer: 1,
                face: 2,
                depthSlice: 3,
                tileX: 0,
                tileY: 0);
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

        AssetHandle<Texture> reloaded = await loader.ReloadAsync(first);
        using AssetRead<Texture> replacementRead = loader.Read(reloaded);
        if (reloaded != first
            || reloaded.Revision != 2
            || ReferenceEquals(original, replacementRead.Value))
        {
            throw new InvalidOperationException(
                "NativeAOT reload did not replace the value behind the same strong handle.");
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
            Format = "RGBA8_UNorm",
            MipTiles =
            [
                new TextureMipTile
                {
                    MipLevel = 1,
                    ArrayLayer = 1,
                    Face = 2,
                    DepthSlice = 3,
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
