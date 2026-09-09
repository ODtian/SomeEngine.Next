using SomeEngine.Serialization;
using SomeEngine.Serialization.IO;

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
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxCachedFiles);
        ArgumentOutOfRangeException.ThrowIfNegative(_options.MaxCachedRootBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxConcurrentReads);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(_options.MaxQueuedReads);
        _readConcurrency = new(_options.MaxConcurrentReads, _options.MaxConcurrentReads);
    }

    public async ValueTask<Asset<T>> OpenAsync<T>(AssetId<T> id, CancellationToken cancellationToken = default)
        where T : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (id.IsEmpty)
            throw new ArgumentException("An asset ID cannot be empty when opening a file.", nameof(id));
        lock (_gate)
            ObjectDisposedException.ThrowIf(_closed, this);

        // Storage may inspect a file header. Never hold the cache lock across external I/O.
        AssetFormat<T> format = AssetFormat<T>.Current;
        if (!_storage.TryFind(id.Value, out AssetEntry entry))
            throw new KeyNotFoundException($"Asset {id} was not found in storage.");
        if (entry.AssetGuid != id.Value)
            throw new InvalidDataException("Storage returned a different asset number.");
        format.Validate(entry);

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
            _ = ReadFileAsync(format, entry, key, read);

        try
        {
            AssetFile file = await read.Completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_closed, this);
                cancellationToken.ThrowIfCancellationRequested();
                return new Asset<T>(file.Duplicate());
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

    /// <summary>Opens a file without a project or a loader-owned cache.</summary>
    public static async ValueTask<Asset<T>> OpenFileAsync<T>(string path,
        BinaryReadLimits? limits = null, CancellationToken cancellationToken = default) where T : class
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        AssetFormat<T> format = AssetFormat<T>.Current;
        if (!format.MatchesPath(path))
            throw new InvalidDataException($"Asset type '{format.AssetType}' does not accept path '{path}'.");
        cancellationToken.ThrowIfCancellationRequested();
        IRangeSource source = FileRangeSource.Open(path);
        var entry = new AssetEntry
        {
            AssetType = format.AssetType,
            SchemaFingerprint = format.WireType.SchemaFingerprint,
            Path = Path.GetFullPath(path),
        };
        return new Asset<T>(await format.ReadAsync(source, entry, limits ?? BinaryReadLimits.Default,
            cancellationToken).ConfigureAwait(false));
    }

    private async Task ReadFileAsync<T>(AssetFormat<T> format, AssetEntry entry,
        (Guid Id, Guid Version) key, FileRead read) where T : class
    {
        AssetFile? file = null;
        Exception? failure = null;
        bool entered = false;
        try
        {
            await _readConcurrency.WaitAsync(_stop.Token).ConfigureAwait(false);
            entered = true;
            IRangeSource source = await _storage.OpenAsync(entry, _stop.Token).ConfigureAwait(false);
            file = await format.ReadAsync(source, entry, _options.ReadLimits, _stop.Token).ConfigureAwait(false);
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
            _cachedRootBytes -= file.RootByteLength;
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
        internal int Waiters;
        internal long LastAccess;
    }
}

public sealed class AssetLoaderOptions
{
    public BinaryReadLimits ReadLimits { get; init; } = BinaryReadLimits.Default;
    public int MaxConcurrentReads { get; init; } = 8;
    /// <summary>Maximum number of queued and executing file reads combined.</summary>
    public int MaxQueuedReads { get; init; } = 1024;
    public int MaxCachedFiles { get; init; } = 128;
    /// <summary>Budget for encoded root fields retained by the file cache, not decoded chunks or GPU memory.</summary>
    public long MaxCachedRootBytes { get; init; } = 16L * 1024 * 1024;
}
