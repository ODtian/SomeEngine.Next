using System.Security.Cryptography;
using System.Text;
using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;

namespace SomeEngine.Assets;

/// <summary>
/// Exact metadata returned by one immutable storage publication. Storage implementations validate
/// the publication token again before opening the corresponding byte range.
/// </summary>
public readonly record struct AssetEntry
{
    public AssetEntry(
        AssetGuid assetGuid,
        string assetType,
        ulong schemaFingerprint,
        Guid publication)
    {
        if (assetGuid.IsEmpty)
            throw new ArgumentException("A storage entry asset GUID cannot be empty.", nameof(assetGuid));
        ArgumentException.ThrowIfNullOrWhiteSpace(assetType);
        if (schemaFingerprint == 0)
            throw new ArgumentOutOfRangeException(nameof(schemaFingerprint));
        if (publication == Guid.Empty)
            throw new ArgumentException("A storage entry publication cannot be empty.", nameof(publication));

        AssetGuid = assetGuid;
        AssetType = assetType;
        SchemaFingerprint = schemaFingerprint;
        Publication = publication;
    }

    public AssetGuid AssetGuid { get; }
    public string AssetType { get; }
    public ulong SchemaFingerprint { get; }
    public Guid Publication { get; }
}

/// <summary>
/// The sole GUID-to-byte-range boundary. A storage implementation locates immutable bytes but
/// cannot select a document format, decode assets, own asset instances, or coordinate loading.
/// </summary>
public interface IAssetStorage
{
    bool TryFind(AssetGuid assetGuid, out AssetEntry entry);

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
    private readonly Dictionary<AssetGuid, AssetManifestRecord> _entries;

    public LooseAssetStorage(string projectRoot, AssetManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _projectRoot = Path.GetFullPath(projectRoot);
        _entries = manifest.Assets.ToDictionary(static pair => pair.Key, static pair => pair.Value);
        _publication = ComputePublication();
    }

    private readonly Guid _publication;

    public bool TryFind(AssetGuid assetGuid, out AssetEntry entry)
    {
        if (_entries.TryGetValue(assetGuid, out AssetManifestRecord record))
        {
            entry = new AssetEntry(
                assetGuid,
                record.AssetType,
                record.SchemaFingerprint,
                _publication);
            return true;
        }

        entry = default;
        return false;
    }

    public ValueTask<IRangeSource> OpenAsync(
        AssetEntry entry,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidatePublication(entry);
        if (!_entries.TryGetValue(entry.AssetGuid, out AssetManifestRecord record))
            throw new KeyNotFoundException($"Asset {entry.AssetGuid} was not found in the loose storage publication.");
        if (!StringComparer.Ordinal.Equals(record.AssetType, entry.AssetType)
            || record.SchemaFingerprint != entry.SchemaFingerprint)
        {
            throw new InvalidDataException(
                $"Asset {entry.AssetGuid} entry does not match its loose storage record.");
        }

        string fullPath = ResolveContainedPath(record.Path);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException($"Stored asset file '{fullPath}' does not exist.", fullPath);

        return ValueTask.FromResult<IRangeSource>(FileRangeSource.Open(fullPath));
    }

    private void ValidatePublication(AssetEntry entry)
    {
        if (entry.Publication != _publication)
            throw new InvalidDataException("Asset entry belongs to a different loose storage publication.");
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

    private Guid ComputePublication()
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (AssetManifestRecord record in _entries.Values
                     .OrderBy(static value => value.Guid.ToString(), StringComparer.Ordinal))
        {
            string fullPath = ResolveContainedPath(record.Path);
            var info = new FileInfo(fullPath);
            Append(hash, record.Guid.ToString());
            Append(hash, record.AssetType);
            Append(hash, record.Path);
            Append(hash, record.SchemaFingerprint.ToString("X16"));
            Append(hash, info.Exists ? info.Length.ToString() : "missing");
            Append(hash, info.Exists ? info.LastWriteTimeUtc.Ticks.ToString() : "missing");
        }
        Digest256 digest = Digest256.Finish(hash);
        Span<byte> bytes = stackalloc byte[Digest256.Size];
        digest.Write(bytes);
        return new Guid(bytes[..16], bigEndian: true);
    }

    private static void Append(IncrementalHash hash, string value)
    {
        Encoder encoder = Encoding.UTF8.GetEncoder();
        ReadOnlySpan<char> remaining = value;
        Span<byte> bytes = stackalloc byte[256];
        do
        {
            encoder.Convert(
                remaining,
                bytes,
                flush: true,
                out int charsUsed,
                out int bytesUsed,
                out bool completed);
            if (bytesUsed != 0)
                hash.AppendData(bytes[..bytesUsed]);
            remaining = remaining[charsUsed..];
            if (completed)
                break;
        }
        while (true);
        hash.AppendData("\0"u8);
    }
}

/// <summary>Highest-priority-first base/DLC/hotfix storage over immutable asset packs.</summary>
public sealed class AssetPackStorage : IAssetStorage, IAsyncDisposable
{
    private readonly AssetPackOverlay _overlay;
    private int _disposed;

    /// <summary>Creates storage and takes exclusive ownership of <paramref name="overlay"/>.</summary>
    public AssetPackStorage(AssetPackOverlay overlay)
    {
        _overlay = overlay ?? throw new ArgumentNullException(nameof(overlay));
    }

    public bool TryFind(AssetGuid assetGuid, out AssetEntry entry)
    {
        ThrowIfDisposed();
        if (_overlay.TryResolve(assetGuid.Value, out AssetPack? pack, out AssetPackEntry? packEntry))
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
        if (!_overlay.TryResolve(entry.AssetGuid.Value, out AssetPack? pack, out AssetPackEntry? packEntry))
            throw new KeyNotFoundException($"Asset {entry.AssetGuid} was not found in pack storage.");
        if (pack!.Generation != entry.Publication)
            throw new InvalidDataException("Asset entry belongs to a different pack publication.");
        if (!StringComparer.Ordinal.Equals(packEntry!.AssetType, entry.AssetType)
            || packEntry.SchemaFingerprint != entry.SchemaFingerprint)
        {
            throw new InvalidDataException($"Asset {entry.AssetGuid} entry does not match its pack record.");
        }

        return await pack.OpenAssetSourceAsync(entry.AssetGuid.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        await _overlay.DisposeAsync().ConfigureAwait(false);
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
}
