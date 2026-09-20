using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

namespace SomeEngine.Assets;

/// <summary>
/// Loader-owned strong residency. Public callers receive the canonical asset object; loading,
/// single-flight state and reload coordination never leave this table.
/// </summary>
internal sealed class ResidentAssetTable : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly List<Func<ValueTask>> _disposeSets = [];
    private readonly List<Func<ValueTask>> _failedDisposals = [];
    private readonly List<IResidentAssetUpdate> _pendingUpdates = [];
    private readonly SemaphoreSlim _updateBoundary = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;
    private bool _disposeCompleted;

    internal ValueTask<T> LoadAsync<T>(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        CancellationToken cancellationToken)
        where T : class
        => Set<T>().LoadAsync(guid, load, cancellationToken);

    internal ValueTask<T> ReloadAsync<T>(
        T asset,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, AssetUpdate, CancellationToken, ValueTask> apply,
        CancellationToken cancellationToken)
        where T : class
        => Set<T>().ReloadAsync(asset, load, apply, cancellationToken);

    internal ValueTask<T> ReloadAsync<T>(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, AssetUpdate, CancellationToken, ValueTask> apply,
        CancellationToken cancellationToken)
        where T : class
        => Set<T>().ReloadAsync(guid, load, apply, cancellationToken);

    internal bool TryFind<T>(AssetGuid guid, [NotNullWhen(true)] out T? value)
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (TrySet(out ResidentAssetSet<T>? set))
                return set.TryFind(guid, out value);
        }

        value = null;
        return false;
    }

    internal bool TryGetAssetGuid<T>(T asset, out AssetGuid guid)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(asset);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (TrySet(out ResidentAssetSet<T>? set))
                return set.TryGetAssetGuid(asset, out guid);
        }

        guid = default;
        return false;
    }

    internal ulong GetRevision<T>(T asset)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(asset);
        lock (_gate)
        {
            ThrowIfDisposed();
            if (TrySet(out ResidentAssetSet<T>? set))
                return set.GetRevision(asset);
        }

        throw new InvalidOperationException("The asset does not belong to this loader.");
    }

    internal void RetainCleanup(Func<ValueTask> cleanup)
    {
        lock (_gate) _failedDisposals.Add(cleanup);
    }

    private async ValueTask RetryCleanupAsync()
    {
        Func<ValueTask>[] cleanups;
        lock (_gate) cleanups = [.. _failedDisposals];
        List<Exception>? failures = null;
        foreach (Func<ValueTask> cleanup in cleanups)
        {
            try
            {
                await cleanup().ConfigureAwait(false);
                lock (_gate) _failedDisposals.Remove(cleanup);
            }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is not null) throw new AggregateException("Unpublished asset cleanup failed.", failures);
    }

    internal async ValueTask ApplyUpdatesAsync()
    {
        await _updateBoundary.WaitAsync().ConfigureAwait(false);
        try
        {
            IResidentAssetUpdate[] updates;
            lock (_gate)
            {
                ThrowIfDisposed();
                updates = [.. _pendingUpdates];
                _pendingUpdates.Clear();
            }

            List<Exception>? failures = null;
            foreach (IResidentAssetUpdate update in updates)
            {
                try
                {
                    await update.ApplyAsync().ConfigureAwait(false);
                }
                catch (Exception failure)
                {
                    if (!update.IsComplete)
                        lock (_gate) _pendingUpdates.Add(update);
                    (failures ??= []).Add(failure);
                }
            }
            try { await RetryCleanupAsync().ConfigureAwait(false); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            if (failures is not null)
                throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
        }
        finally
        {
            _updateBoundary.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _updateBoundary.WaitAsync().ConfigureAwait(false);
        try
        {
            Func<ValueTask>[] disposals;
            IResidentAssetUpdate[] updates;
            lock (_gate)
            {
                if (_disposeCompleted) return;
                _disposed = true;
                disposals = [.. _disposeSets];
                updates = [.. _pendingUpdates];
            }
            List<Exception>? failures = null;
            if (!_lifetime.IsCancellationRequested)
            {
                try { _lifetime.Cancel(); }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            foreach (IResidentAssetUpdate update in updates)
            {
                try
                {
                    await update.DiscardAsync().ConfigureAwait(false);
                    lock (_gate) _pendingUpdates.Remove(update);
                }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            for (int index = disposals.Length - 1; index >= 0; index--)
            {
                try
                {
                    await disposals[index]().ConfigureAwait(false);
                    lock (_gate) _disposeSets.Remove(disposals[index]);
                }
                catch (Exception failure) { (failures ??= []).Add(failure); }
            }
            try { await RetryCleanupAsync().ConfigureAwait(false); }
            catch (Exception failure) { (failures ??= []).Add(failure); }
            if (failures is not null) throw new AggregateException("Asset cleanup failed.", failures);
            _lifetime.Dispose();
            _disposeCompleted = true;
        }
        finally { _updateBoundary.Release(); }
    }

    private ResidentAssetSet<T> Set<T>()
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ResidentAssetSet<T> set = ResidentSets<T>.Values.GetValue(
                this,
                table => new ResidentAssetSet<T>(
                    table._lifetime.Token,
                    table.EnqueueUpdate,
                    table.RetainCleanup));
            if (set.MarkRegistered())
                _disposeSets.Add(set.DisposeAsync);
            return set;
        }
    }

    private bool TrySet<T>([NotNullWhen(true)] out ResidentAssetSet<T>? set)
        where T : class
        => ResidentSets<T>.Values.TryGetValue(this, out set);

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private void EnqueueUpdate(IResidentAssetUpdate update)
    {
        ArgumentNullException.ThrowIfNull(update);
        lock (_gate)
        {
            ThrowIfDisposed();
            _pendingUpdates.Add(update);
        }
    }

    private static class ResidentSets<T>
        where T : class
    {
        internal static readonly ConditionalWeakTable<ResidentAssetTable, ResidentAssetSet<T>>
            Values = new();
    }
}

internal interface IResidentAssetUpdate
{
    bool IsComplete { get; }
    ValueTask ApplyAsync();
    ValueTask DiscardAsync();
}

internal readonly record struct AssetPublication<T>(T Value)
    where T : class;

internal sealed class ResidentAssetSet<T> : IAsyncDisposable
    where T : class
{
    private readonly object _gate = new();
    private readonly CancellationToken _lifetime;
    private readonly Action<IResidentAssetUpdate> _enqueueUpdate;
    private readonly Action<Func<ValueTask>> _retainCleanup;
    private readonly Dictionary<AssetGuid, Entry> _byGuid = [];
    private readonly Dictionary<T, Entry> _byValue = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _publicationOrder = [];
    private int _registered;
    private bool _disposed;

    internal ResidentAssetSet(
        CancellationToken lifetime,
        Action<IResidentAssetUpdate> enqueueUpdate,
        Action<Func<ValueTask>> retainCleanup)
    {
        _lifetime = lifetime;
        _enqueueUpdate = enqueueUpdate ?? throw new ArgumentNullException(nameof(enqueueUpdate));
        _retainCleanup = retainCleanup;
    }

    internal bool MarkRegistered()
        => Interlocked.CompareExchange(ref _registered, 1, 0) == 0;

    internal ValueTask<T> LoadAsync(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (guid.IsEmpty)
            throw new ArgumentException("An asset GUID cannot be empty.", nameof(guid));

        Task<T> task;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_byGuid.TryGetValue(guid, out Entry? entry))
            {
                var completion = new TaskCompletionSource<T>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                entry = new Entry(guid, completion.Task);
                _byGuid.Add(guid, entry);
                _ = CompleteLoadAsync(entry, completion, load);
            }
            task = entry.LoadTask;
        }

        return AwaitCallerAsync(task, cancellationToken);
    }

    internal ValueTask<T> ReloadAsync(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, AssetUpdate, CancellationToken, ValueTask> apply,
        CancellationToken cancellationToken)
    {
        Entry entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_byGuid.TryGetValue(guid, out entry!))
                throw new KeyNotFoundException($"Asset {guid} is not resident.");
        }
        return ReloadAsync(entry, load, apply, cancellationToken);
    }

    internal ValueTask<T> ReloadAsync(
        T asset,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, AssetUpdate, CancellationToken, ValueTask> apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(asset);
        Entry entry;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!_byValue.TryGetValue(asset, out entry!))
                throw new InvalidOperationException("The asset does not belong to this loader.");
        }
        return ReloadAsync(entry, load, apply, cancellationToken);
    }

    internal bool TryFind(AssetGuid guid, [NotNullWhen(true)] out T? value)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_byGuid.TryGetValue(guid, out Entry? entry) && entry.Value is { } current)
            {
                value = current;
                return true;
            }
        }

        value = null;
        return false;
    }

    internal bool TryGetAssetGuid(T asset, out AssetGuid guid)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_byValue.TryGetValue(asset, out Entry? entry))
            {
                guid = entry.Guid;
                return true;
            }
        }

        guid = default;
        return false;
    }

    internal ulong GetRevision(T asset)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_byValue.TryGetValue(asset, out Entry? entry))
                return entry.Revision;
        }

        throw new InvalidOperationException("The asset does not belong to this loader.");
    }

    public async ValueTask DisposeAsync()
    {
        Entry[] entries;
        lock (_gate)
        {
            _disposed = true;
            foreach (Entry entry in _byGuid.Values)
                if (!_publicationOrder.Contains(entry)) _publicationOrder.Add(entry);
            entries = [.. _publicationOrder];
            _byGuid.Clear();
            _byValue.Clear();
        }
        List<Exception>? failures = null;
        for (int index = entries.Length - 1; index >= 0; index--)
        {
            Entry entry = entries[index];
            try
            {
                try { _ = await entry.LoadTask.ConfigureAwait(false); }
                catch { }
                if (entry.ReloadTask is { } reload)
                {
                    try { _ = await reload.ConfigureAwait(false); }
                    catch { }
                }
                if (entry.Value is { } value)
                {
                    await DisposeAssetAsync(value).ConfigureAwait(false);
                    entry.Value = null;
                }
                lock (_gate) _publicationOrder.Remove(entry);
            }
            catch (Exception failure) { (failures ??= []).Add(failure); }
        }
        if (failures is not null) throw new AggregateException("Asset content cleanup failed.", failures);
    }

    private ValueTask<T> ReloadAsync(
        Entry entry,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, AssetUpdate, CancellationToken, ValueTask> apply,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(load);
        ArgumentNullException.ThrowIfNull(apply);
        Task<T> reload;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (entry.Value is null)
                throw new InvalidOperationException($"Asset {entry.Guid} is not ready.");
            if (entry.ReloadTask is null || entry.ReloadTask.IsCompleted)
                entry.ReloadTask = CompleteReloadAsync(entry, load, apply);
            reload = entry.ReloadTask;
        }
        return AwaitCallerAsync(reload, cancellationToken);
    }

    private async Task CompleteLoadAsync(
        Entry entry,
        TaskCompletionSource<T> completion,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load)
    {
        try
        {
            AssetPublication<T> publication = await load(entry.Guid, _lifetime).ConfigureAwait(false);
            T value = publication.Value ?? throw new InvalidDataException("Asset loading returned null.");
            lock (_gate)
            {
                entry.Value = value;
                entry.Revision = 1;
                if (!_disposed)
                {
                    _byValue.Add(value, entry);
                    _publicationOrder.Add(entry);
                }
            }
            completion.TrySetResult(value);
        }
        catch (Exception failure)
        {
            lock (_gate)
            {
                if (entry.Value is null && _byGuid.TryGetValue(entry.Guid, out Entry? current) &&
                    ReferenceEquals(current, entry))
                    _byGuid.Remove(entry.Guid);
            }
            completion.TrySetException(failure);
        }
    }

    private async Task<T> CompleteReloadAsync(
        Entry entry,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, AssetUpdate, CancellationToken, ValueTask> apply)
    {
        T current = entry.Value
            ?? throw new InvalidOperationException($"Asset {entry.Guid} is not ready.");
        T? replacement = null;
        try
        {
            AssetPublication<T> publication = await load(entry.Guid, _lifetime).ConfigureAwait(false);
            replacement = publication.Value
                ?? throw new InvalidDataException("Asset reload returned null.");
            _enqueueUpdate(new PendingUpdate(this, entry, current, replacement, apply));
            replacement = null;
            return current;
        }
        catch (Exception failure)
        {
            if (replacement is not null && !ReferenceEquals(replacement, current))
            {
                T retained = replacement;
                try { await DisposeAssetAsync(retained).ConfigureAwait(false); }
                catch (Exception cleanupFailure)
                {
                    _retainCleanup(() => DisposeAssetAsync(retained));
                    throw new AggregateException(failure, cleanupFailure);
                }
            }
            throw;
        }
    }

    private static ValueTask<T> AwaitCallerAsync(Task<T> task, CancellationToken cancellationToken)
        => cancellationToken.CanBeCanceled
            ? new ValueTask<T>(task.WaitAsync(cancellationToken))
            : new ValueTask<T>(task);

    private static async ValueTask DisposeAssetAsync(T asset)
    {
        if (asset is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (asset is IDisposable disposable)
            disposable.Dispose();
    }

    private async ValueTask ApplyUpdateAsync(
        Entry entry,
        T current,
        T replacement,
        Func<T, T, AssetUpdate, CancellationToken, ValueTask> apply)
    {
        ulong nextRevision;
        lock (_gate)
        {
            ThrowIfDisposed();
            nextRevision = checked(entry.Revision + 1);
            if (!ReferenceEquals(entry.Value, current))
                throw new InvalidOperationException("The canonical asset changed before its update boundary.");
        }

        var update = new AssetUpdate(() =>
        {
            lock (_gate) entry.Revision = nextRevision;
        });
        await apply(current, replacement, update, _lifetime).ConfigureAwait(false);
        if (!update.IsAccepted)
            throw new InvalidOperationException("The reload returned without accepting its content update.");
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed class Entry(AssetGuid guid, Task<T> loadTask)
    {
        internal AssetGuid Guid { get; } = guid;
        internal Task<T> LoadTask { get; } = loadTask;
        internal T? Value { get; set; }
        internal Task<T>? ReloadTask { get; set; }
        internal ulong Revision { get; set; }
    }

    private sealed class PendingUpdate(
        ResidentAssetSet<T> owner,
        Entry entry,
        T current,
        T replacement,
        Func<T, T, AssetUpdate, CancellationToken, ValueTask> apply) : IResidentAssetUpdate
    {
        private T? _replacement = replacement;
        private bool _attempted;
        public bool IsComplete => _replacement is null;

        public async ValueTask ApplyAsync()
        {
            T? value = _replacement;
            if (value is null) return;
            Exception? failure = null;
            if (!_attempted)
            {
                _attempted = true;
                try { await owner.ApplyUpdateAsync(entry, current, value, apply).ConfigureAwait(false); }
                catch (Exception error) { failure = error; }
            }
            try { await DiscardAsync().ConfigureAwait(false); }
            catch (Exception cleanupFailure)
            {
                if (failure is not null) throw new AggregateException(failure, cleanupFailure);
                throw;
            }
            if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public async ValueTask DiscardAsync()
        {
            _attempted = true;
            T? value = _replacement;
            if (value is null) return;
            await DisposeAssetAsync(value).ConfigureAwait(false);
            _replacement = null;
        }
    }
}
