using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Containers;

namespace SomeEngine.Assets;

/// <summary>Opens disk content. It does not create, cache, or release domain objects.</summary>
public sealed class AssetLoader : IAsyncDisposable
{
    private readonly IAssetStorage _storage;
    private readonly AssetLoaderOptions _options;
    private readonly object _gate = new();
    private readonly Dictionary<(Guid Id, Guid Version), FileRead> _files = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _readConcurrency;
    private readonly List<Exception> _closeErrors = [];
    private Task _cleanup = Task.CompletedTask;
    private Task? _close;
    private long _access;
    private long _cachedRootBytes;
    private int _activeReads;
    private bool _closed;

    public AssetLoader(IAssetStorage storage, AssetLoaderOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? new();
        ArgumentNullException.ThrowIfNull(_options.ReadLimits);
        ArgumentNullException.ThrowIfNull(_options.Pages);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxDecodedBytes);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxCachedFiles);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxCachedRootBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxConcurrentReads);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxQueuedReads);
        _readConcurrency = new(_options.MaxConcurrentReads, _options.MaxConcurrentReads);
    }

    public PageArena Pages => _options.Pages;

    public async ValueTask<AssetFile> OpenFileAsync<T>(AssetId<T> id,
        string fileType, BinaryWireTypeDescriptor wireType, CancellationToken cancellationToken = default)
        where T : Asset
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id.IsEmpty)
            throw new ArgumentException("An asset ID cannot be empty when opening a file.", nameof(id));
        lock (_gate)
            ObjectDisposedException.ThrowIf(_closed, this);

        // Storage may inspect a file header. Never hold the cache lock across external I/O.
        if (!_storage.TryFind(id.Value, out AssetEntry entry))
            throw new KeyNotFoundException($"Asset {id} was not found in storage.");
        if (entry.AssetGuid != id.Value)
            throw new InvalidDataException("Storage returned a different asset number.");
        if (!StringComparer.Ordinal.Equals(entry.AssetType, fileType) || entry.SchemaFingerprint != wireType.SchemaFingerprint)
            throw new InvalidDataException($"Asset {id} has a different file format.");

        var key = (id.Value, entry.Version);
        FileRead read;
        bool start = false;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_closed, this);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_files.TryGetValue(key, out read!))
            {
                if (_activeReads >= _options.MaxQueuedReads)
                    throw new InvalidOperationException("The asset file read queue is full.");
                read = new();
                _files.Add(key, read);
                _activeReads++;
                start = true;
            }
            read.Waiters++;
            read.LastAccess = ++_access;
        }
        if (start)
            _ = ReadFileAsync(entry, wireType, key, read);

        try
        {
            AssetFile file = await read.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                cancellationToken.ThrowIfCancellationRequested();
                return file.Retain();
            }
        }
        finally
        {
            Task cleanup;
            lock (_gate)
            {
                read.Waiters--;
                cleanup = TrimCache();
            }
            await cleanup.ConfigureAwait(false);
        }
    }

    private async Task ReadFileAsync(AssetEntry entry, BinaryWireTypeDescriptor wireType,
        (Guid Id, Guid Version) key, FileRead read)
    {
        AssetFile? file = null;
        Exception? failure = null;
        bool entered = false;
        try
        {
            await _readConcurrency.WaitAsync(_stop.Token).ConfigureAwait(false);
            entered = true;
            IRangeSource source = await _storage.OpenAsync(entry, _stop.Token).ConfigureAwait(false);
            BinaryDocument document = await BinaryDocument.OpenAsync(source, wireType, ownsSource: true, _options.ReadLimits, _stop.Token).ConfigureAwait(false);
            try
            {
                file = new AssetFile(document, entry.Path, _options.Pages, _options.MaxDecodedBytes);
                AssetEntry actual = file.Describe();
                // OpenAsync validates the storage-selected version. For a pack this is the pack
                // generation, while the nested document has its own content generation.
                if (actual.AssetGuid != entry.AssetGuid || actual.AssetType != entry.AssetType)
                    throw new InvalidDataException("The selected asset file differs from its directory entry.");
            }
            catch
            {
                if (file is not null) await file.DisposeAsync().ConfigureAwait(false);
                else await document.DisposeAsync().ConfigureAwait(false);
                throw;
            }

        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            if (entered)
                _readConcurrency.Release();
        }

        Task cleanup;
        lock (_gate)
        {
            _activeReads--;
            if (failure is null)
            {
                read.File = file!;
                read.RootByteLength = file!.RootByteLength;
                if (!_closed)
                    _cachedRootBytes += file!.RootByteLength;
                read.Completion.TrySetResult(file!);
            }
            else
            {
                _files.Remove(key);
                if (failure is OperationCanceledException canceled)
                    read.Completion.TrySetCanceled(canceled.CancellationToken);
                else
                {
                    read.Completion.TrySetException(failure);
                    _ = read.Completion.Task.Exception;
                }
            }
            cleanup = TrimCache();
        }
        // Cleanup records each failure and is joined by DisposeAsync, including after all
        // callers cancel an open. There are no unobserved cleanup exceptions.
        await cleanup.ConfigureAwait(false);
    }

    // Called under _gate. Only the loader's document access is evicted. Returned assets and
    // streaming objects keep their independent access to the same file bytes.
    private Task TrimCache()
    {
        if (_closed)
            return _cleanup;
        List<AssetFile>? removed = null;
        while (true)
        {
            var candidates = _files.Where(static pair => pair.Value.File is not null && pair.Value.Waiters == 0);
            var obsolete = candidates.Where(pair => pair.Key.Version == Guid.Empty ||
                _files.Any(other => other.Key.Id == pair.Key.Id && other.Value.LastAccess > pair.Value.LastAccess));
            var selected = obsolete.OrderBy(static pair => pair.Value.LastAccess).FirstOrDefault();
            if (selected.Value is null)
            {
                int cachedCount = _files.Count(static pair => pair.Value.File is not null);
                if (cachedCount <= _options.MaxCachedFiles && _cachedRootBytes <= _options.MaxCachedRootBytes)
                    break;
                selected = candidates.OrderBy(static pair => pair.Value.LastAccess).FirstOrDefault();
                if (selected.Value is null)
                    break;
            }
            _files.Remove(selected.Key);
            AssetFile file = selected.Value.File!;
            _cachedRootBytes -= selected.Value.RootByteLength;
            (removed ??= []).Add(file);
        }
        if (removed is not null)
            _cleanup = CloseFilesAsync(_cleanup, removed);
        return _cleanup;
    }

    private async Task CloseFilesAsync(Task previous, IReadOnlyList<AssetFile> files)
    {
        // Disposal can call user storage code. It must never execute under _gate.
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        await previous.ConfigureAwait(false);
        foreach (AssetFile file in files)
        {
            try { await file.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { lock (_gate) _closeErrors.Add(error); }
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_close is not null)
                return new(_close);
            _closed = true;
            FileRead[] reads = [.. _files.Values];
            _files.Clear();
            _cachedRootBytes = 0;
            _close = CloseAsync(reads, _cleanup);
            return new(_close);
        }
    }

    private async Task CloseAsync(FileRead[] reads, Task cleanup)
    {
        await Task.CompletedTask.ConfigureAwait(ConfigureAwaitOptions.ForceYielding);
        try { await _stop.CancelAsync().ConfigureAwait(false); }
        catch (Exception error) { lock (_gate) _closeErrors.Add(error); }
        await cleanup.ConfigureAwait(false);
        foreach (FileRead read in reads)
        {
            AssetFile file;
            try { file = await read.Completion.Task.ConfigureAwait(false); }
            catch { continue; } // OpenAsync reports read failures to its caller.
            try { await file.DisposeAsync().ConfigureAwait(false); }
            catch (Exception error) { lock (_gate) _closeErrors.Add(error); }
        }
        _readConcurrency.Dispose();
        _stop.Dispose();
        Exception[] errors;
        lock (_gate) errors = [.. _closeErrors];
        if (errors.Length != 0)
            throw new AggregateException("One or more asset byte sources could not be closed.", errors);
    }

    private sealed class FileRead
    {
        internal TaskCompletionSource<AssetFile> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal AssetFile? File;
        internal long RootByteLength;
        internal int Waiters;
        internal long LastAccess;
    }
}

public sealed class AssetLoaderOptions
{
    public PageArena Pages { get; init; } = PageArena.Shared;
    public long MaxDecodedBytes { get; init; } = 256L * 1024 * 1024;
    public BinaryReadLimits ReadLimits { get; init; } = BinaryReadLimits.Default;
    public int MaxConcurrentReads { get; init; } = 8;
    /// <summary>Maximum number of queued and executing file reads combined.</summary>
    public int MaxQueuedReads { get; init; } = 1024;
    public int MaxCachedFiles { get; init; } = 128;
    /// <summary>Budget for encoded root fields retained by the file cache, not decoded chunks or GPU memory.</summary>
    public long MaxCachedRootBytes { get; init; } = 16L * 1024 * 1024;
}
