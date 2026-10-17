using System.Text.Json;
using System.Text.Json.Serialization;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets;

/// <summary>A content directory's selected asset entries, byte storage and file loader.</summary>
public sealed class AssetContent : IAsyncDisposable
{
    public const string PackPathsFile = "Library/AssetPacks/packs.json";
    private readonly IAsyncDisposable? _storage;
    private readonly ContentFile[] _files;

    private AssetContent(AssetManifest manifest, IAssetStorage storage, ContentFile[] files)
    {
        Manifest = manifest;
        Loader = new(storage);
        _storage = storage as IAsyncDisposable;
        _files = files;
    }

    public AssetManifest Manifest { get; }
    public AssetLoader Loader { get; }

    /// <summary>Checks content-file metadata when deciding whether to reuse content for another scene.</summary>
    public bool HasChanges => _files.Any(file => file != ReadFile(file.Path));

    public static bool Exists(string directory) => File.Exists(Path.Combine(directory, PackPathsFile))
        || File.Exists(Path.Combine(directory, "Library/AssetManifest", AssetManifest.CatalogFileName));

    public static async ValueTask<AssetContent> OpenAsync(string directory, CancellationToken cancellationToken = default)
    {
        directory = Path.GetFullPath(directory);
        string pathsFile = Path.Combine(directory, PackPathsFile);
        var files = new List<ContentFile> { ReadFile(pathsFile) };
        if (files[0].Length < 0)
        {
            files.Add(ReadFile(Path.Combine(directory, "Library/AssetManifest", AssetManifest.CatalogFileName)));
            var project = new AssetProject(directory, []);
            return new(project.Manifest, project.CreateStorage(), files.ToArray());
        }

        string[] paths = JsonSerializer.Deserialize(await File.ReadAllTextAsync(pathsFile, cancellationToken),
            AssetContentJson.Default.StringArray)!;
        var packs = new List<AssetPack>();
        var manifest = new AssetManifest();
        try
        {
            foreach (string path in paths)
            {
                string fullPath = Path.GetFullPath(path, directory);
                files.Add(ReadFile(fullPath));
                AssetPack pack = await AssetPack.OpenAsync(fullPath, cancellationToken: cancellationToken);
                packs.Add(pack);
                foreach (AssetPackEntry entry in pack.Entries)
                {
                    if (manifest.TryGetAsset(entry.AssetId, out _)) continue;
                    manifest.AddAsset(entry.AssetId, entry.AssetType, $"{path}#{entry.AssetId:N}", entry.AssetType,
                        entry.SchemaFingerprint, version: entry.DocumentVersion);
                }
            }
            return new(manifest, new AssetPackStorage(packs), files.ToArray());
        }
        catch
        {
            foreach (AssetPack pack in packs) await pack.DisposeAsync();
            throw;
        }
    }

    private static ContentFile ReadFile(string path)
    {
        var file = new FileInfo(path);
        return new(path, file.Exists ? file.Length : -1, file.LastWriteTimeUtc);
    }

    private readonly record struct ContentFile(string Path, long Length, DateTime LastWriteTimeUtc);

    public static void WritePackPaths(string directory, params string[] paths)
    {
        string file = Path.Combine(directory, PackPathsFile);
        string text = JsonSerializer.Serialize(paths, AssetContentJson.Default.StringArray);
        if (File.Exists(file) && File.ReadAllText(file) == text) return;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, text);
    }

    public async ValueTask DisposeAsync()
    {
        try { await Loader.DisposeAsync(); }
        finally { if (_storage is not null) await _storage.DisposeAsync(); }
    }
}

[JsonSerializable(typeof(string[]))]
internal partial class AssetContentJson : JsonSerializerContext;
