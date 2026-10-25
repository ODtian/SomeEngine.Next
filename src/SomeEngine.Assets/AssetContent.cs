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
    private readonly string _directory;
    private readonly bool _explicitPacks;

    private AssetContent(string directory, bool explicitPacks, AssetManifest manifest, IAssetStorage storage, ContentFile[] files)
    {
        _directory = directory;
        _explicitPacks = explicitPacks;
        Manifest = manifest;
        Loader = new(storage);
        _storage = storage as IAsyncDisposable;
        _files = files;
    }

    public AssetManifest Manifest { get; }
    public AssetLoader Loader { get; }

    /// <summary>Checks content-file metadata when deciding whether to reuse content for another scene.</summary>
    public bool HasChanges => _files.Any(file => file != ReadFile(file.Path));

    public static bool Exists(string directory) => File.Exists(directory)
        || File.Exists(Path.Combine(directory, PackPathsFile))
        || File.Exists(Path.Combine(directory, "Library/AssetManifest", AssetManifest.CatalogFileName));

    /// <summary>Checks explicitly selected packs, or the directory's ordinary content when the list is empty.</summary>
    public static bool Exists(string directory, IReadOnlyList<string> packPaths)
    {
        var source = SelectSource(directory, packPaths);
        return source.Packs is { } packs ? packs.All(File.Exists) : Exists(source.Directory);
    }

    /// <summary>Checks the selected source and pack order without maintaining a second storage owner.</summary>
    public bool MatchesSource(string directory, IReadOnlyList<string> packPaths)
    {
        var source = SelectSource(directory, packPaths);
        StringComparer comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        if (!comparer.Equals(_directory, source.Directory)) return false;
        if (source.Packs is not { } packs) return !_explicitPacks;
        if (!_explicitPacks || _files.Length != packs.Length) return false;
        for (int index = 0; index < packs.Length; index++)
            if (!comparer.Equals(_files[index].Path, packs[index])) return false;
        return true;
    }

    public static ValueTask<AssetContent> OpenAsync(string directory, CancellationToken cancellationToken = default)
        => OpenAsync(directory, Array.Empty<string>(), cancellationToken);

    /// <summary>Opens ordered packs with first-pack precedence, or ordinary directory content for an empty list.</summary>
    public static async ValueTask<AssetContent> OpenAsync(string directory, IReadOnlyList<string> packPaths,
        CancellationToken cancellationToken)
    {
        var source = SelectSource(directory, packPaths);
        directory = source.Directory;
        if (source.Packs is { } selected)
            return await OpenPacksAsync(directory, selected, [], explicitPacks: true, cancellationToken).ConfigureAwait(false);
        string pathsFile = Path.Combine(directory, PackPathsFile);
        var files = new List<ContentFile> { ReadFile(pathsFile) };
        if (files[0].Length < 0)
        {
            files.Add(ReadFile(Path.Combine(directory, "Library/AssetManifest", AssetManifest.CatalogFileName)));
            var project = new AssetProject(directory, []);
            return new(directory, false, project.Manifest, project.CreateStorage(), files.ToArray());
        }
        string[] paths = JsonSerializer.Deserialize(await File.ReadAllTextAsync(pathsFile, cancellationToken).ConfigureAwait(false),
            AssetContentJson.Default.StringArray)!;
        return await OpenPacksAsync(directory, paths, files, explicitPacks: false, cancellationToken).ConfigureAwait(false);
    }

    private static async ValueTask<AssetContent> OpenPacksAsync(string directory, IReadOnlyList<string> paths,
        List<ContentFile> files, bool explicitPacks, CancellationToken cancellationToken)
    {
        var packs = new List<AssetPack>();
        var manifest = new AssetManifest();
        try
        {
            foreach (string path in paths)
            {
                string fullPath = Path.GetFullPath(path, directory);
                files.Add(ReadFile(fullPath));
                AssetPack pack = await AssetPack.OpenAsync(fullPath, cancellationToken: cancellationToken).ConfigureAwait(false);
                packs.Add(pack);
                foreach (AssetPackEntry entry in pack.Entries)
                {
                    if (manifest.TryGetAsset(entry.AssetId, out _)) continue;
                    manifest.AddAsset(entry.AssetId, entry.AssetType, $"{path}#{entry.AssetId:N}", entry.AssetType,
                        entry.SchemaFingerprint, version: entry.DocumentVersion);
                }
            }
            return new(directory, explicitPacks, manifest, new AssetPackStorage(packs), files.ToArray());
        }
        catch (Exception failure)
        {
            var errors = new List<Exception> { failure };
            foreach (AssetPack pack in packs)
            {
                try { await pack.DisposeAsync().ConfigureAwait(false); }
                catch (Exception cleanup) { errors.Add(cleanup); }
            }
            if (errors.Count != 1) throw new AggregateException("Content pack opening and cleanup failed.", errors);
            throw;
        }
    }

    private static (string Directory, string[]? Packs) SelectSource(string directory, IReadOnlyList<string> packPaths)
    {
        ArgumentNullException.ThrowIfNull(packPaths);
        directory = Path.GetFullPath(directory);
        if (packPaths.Count != 0)
            return (directory, packPaths.Select(path => Path.GetFullPath(path, directory)).ToArray());
        if (File.Exists(directory)) return (Path.GetDirectoryName(directory)!, [directory]);
        return (directory, null);
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
