using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets;

/// <summary>Rebuildable directory information and the version selected by storage.</summary>
public readonly record struct AssetEntry
{
    public AssetEntry(Guid assetGuid, string assetType, ulong schemaFingerprint, Guid version)
    {
        if (assetGuid == Guid.Empty)
            throw new ArgumentException("An asset GUID cannot be empty.", nameof(assetGuid));
        ArgumentException.ThrowIfNullOrWhiteSpace(assetType);
        if (schemaFingerprint == 0)
            throw new ArgumentOutOfRangeException(nameof(schemaFingerprint));
        AssetGuid = assetGuid;
        AssetType = assetType;
        SchemaFingerprint = schemaFingerprint;
        Version = version;
    }

    public AssetEntry(Guid assetGuid, string name, string path, string assetType,
        ulong schemaFingerprint, SourceGuid sourceGuid = default, string subAssetKey = "")
        : this(assetGuid, assetType, schemaFingerprint, Guid.Empty)
    {
        Name = name;
        Path = path;
        SourceGuid = sourceGuid;
        SubAssetKey = subAssetKey;
    }

    public Guid AssetGuid { get; init; }
    public string Name { get; init; } = "";
    public string Path { get; init; } = "";
    public string AssetType { get; init; }
    public ulong SchemaFingerprint { get; init; }
    public Guid Version { get; init; }
    public SourceGuid SourceGuid { get; init; }
    public string SubAssetKey { get; init; } = "";
    public IReadOnlyList<Guid> Dependencies { get; init; } = [];
}

/// <summary>
/// The sole GUID-to-byte-range boundary. A storage implementation locates immutable bytes but
/// cannot select a document format, decode assets, own asset instances, or coordinate loading.
/// </summary>
public interface IAssetStorage
{
    bool TryFind(Guid assetGuid, out AssetEntry entry);

    ValueTask<IRangeSource> OpenAsync(
        AssetEntry entry,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Development loose-file storage backed by one manifest snapshot. Bytes are opened at request
/// time and must still match the exact entry identity; immutable shipping publication is
/// provided by AssetPackStorage.
/// </summary>
public sealed class LooseAssetStorage : IAssetStorage
{
    private readonly string _projectRoot;
    private readonly Dictionary<Guid, AssetEntry> _entries;

    public LooseAssetStorage(string projectRoot, AssetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _projectRoot = Path.GetFullPath(projectRoot);
        _entries = manifest.Assets.ToDictionary(static pair => pair.Key, static pair => pair.Value);
    }


    public bool TryFind(Guid assetGuid, out AssetEntry entry)
    {
        if (_entries.TryGetValue(assetGuid, out AssetEntry record))
        {
            entry = new AssetEntry(
                assetGuid,
                record.AssetType,
                record.SchemaFingerprint,
                ComputeVersion(record.Path))
            { Name = record.Name, Path = record.Path, Dependencies = record.Dependencies };
            return true;
        }

        entry = default;
        return false;
    }

    public async ValueTask<IRangeSource> OpenAsync(
        AssetEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!_entries.TryGetValue(entry.AssetGuid, out AssetEntry record))
            throw new KeyNotFoundException($"Asset {entry.AssetGuid} was not found in the loose storage publication.");
        if (!StringComparer.Ordinal.Equals(record.AssetType, entry.AssetType)
            || record.SchemaFingerprint != entry.SchemaFingerprint)
        {
            throw new InvalidDataException(
                $"Asset {entry.AssetGuid} entry does not match its loose storage record.");
        }

        string fullPath = ResolveContainedPath(record.Path);
        FileRangeSource source = FileRangeSource.Open(fullPath);
        try
        {
            Guid version = await BinaryDocument.ReadVersionAsync(source, cancellationToken).ConfigureAwait(false);
            if (entry.Version != version)
                throw new IOException("The selected asset file version has been replaced. Open the new entry.");
            return source;
        }
        catch
        {
            await source.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private string ResolveContainedPath(string manifestPath)
    {
        string fullPath = Path.GetFullPath(Path.Combine(
            _projectRoot,
            manifestPath.Replace('/', Path.DirectorySeparatorChar)));
        string relative = Path.GetRelativePath(_projectRoot, fullPath);
        if (Path.IsPathRooted(relative)
            || relative == ".."
            || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Stored asset path '{manifestPath}' escapes project root '{_projectRoot}'.");
        }
        return fullPath;
    }

    private Guid ComputeVersion(string path)
    {
        FileRangeSource source = FileRangeSource.Open(ResolveContainedPath(path));
        try { return BinaryDocument.ReadVersionAsync(source).AsTask().GetAwaiter().GetResult(); }
        finally { source.DisposeAsync().AsTask().GetAwaiter().GetResult(); }
    }
}

/// <summary>Highest-priority-first base/DLC/hotfix storage over immutable asset packs.</summary>
public sealed class AssetPackStorage : IAssetStorage, IAsyncDisposable
{
    private readonly AssetPack[] _packs;
    private int _disposed;

    /// <summary>Creates storage over packs in highest-priority-first order.</summary>
    public AssetPackStorage(IEnumerable<AssetPack> packsHighestPriorityFirst)
    {
        ArgumentNullException.ThrowIfNull(packsHighestPriorityFirst);
        _packs = packsHighestPriorityFirst.ToArray();
        if (_packs.Length == 0 || _packs.Any(static pack => pack is null))
            throw new ArgumentException("At least one non-null pack is required.", nameof(packsHighestPriorityFirst));
    }

    private bool TryResolve(Guid assetId, out AssetPack? pack, out AssetPackEntry? entry)
    {
        foreach (AssetPack candidate in _packs)
        {
            if (candidate.TryGetEntry(assetId, out entry))
            {
                pack = candidate;
                return true;
            }
        }
        pack = null;
        entry = null;
        return false;
    }

    public bool TryFind(Guid assetGuid, out AssetEntry entry)
    {
        ThrowIfDisposed();
        if (TryResolve(assetGuid, out AssetPack? pack, out AssetPackEntry? packEntry))
        {
            entry = new AssetEntry(
                assetGuid,
                packEntry!.AssetType,
                packEntry.SchemaFingerprint,
                pack!.Generation);
            return true;
        }

        entry = default;
        return false;
    }

    public async ValueTask<IRangeSource> OpenAsync(
        AssetEntry entry,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (!TryResolve(entry.AssetGuid, out AssetPack? pack, out AssetPackEntry? packEntry))
            throw new KeyNotFoundException($"Asset {entry.AssetGuid} was not found in pack storage.");
        if (pack!.Generation != entry.Version)
            throw new InvalidDataException("Asset entry belongs to a different pack publication.");
        if (!StringComparer.Ordinal.Equals(packEntry!.AssetType, entry.AssetType)
            || packEntry.SchemaFingerprint != entry.SchemaFingerprint)
        {
            throw new InvalidDataException($"Asset {entry.AssetGuid} entry does not match its pack record.");
        }

        return await pack.OpenAssetSourceAsync(entry.AssetGuid, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        var errors = new List<Exception>();
        foreach (AssetPack pack in _packs)
            try { await pack.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0)
            throw new AggregateException("Some asset packs could not be closed.", errors);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
