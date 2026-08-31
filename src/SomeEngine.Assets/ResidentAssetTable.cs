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
    private readonly CancellationTokenSource _lifetime = new();
    private bool _disposed;

    internal ValueTask<T> LoadAsync<T>(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        CancellationToken cancellationToken)
        where T : class
        => Set<T>().LoadAsync(guid, load, cancellationToken);

    internal ValueTask<T> ReloadAsync<T>(
        T asset,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, CancellationToken, ValueTask> apply,
        CancellationToken cancellationToken)
        where T : class
        => Set<T>().ReloadAsync(asset, load, apply, cancellationToken);

    internal ValueTask<T> ReloadAsync<T>(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, CancellationToken, ValueTask> apply,
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

    public async ValueTask DisposeAsync()
    {
        Func<ValueTask>[] disposals;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            disposals = [.. _disposeSets];
            _disposeSets.Clear();
        }

        _lifetime.Cancel();
        List<Exception>? failures = null;
        for (int index = disposals.Length - 1; index >= 0; index--)
        {
            try
            {
                await disposals[index]().ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }
        _lifetime.Dispose();

        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private ResidentAssetSet<T> Set<T>()
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ResidentAssetSet<T> set = ResidentSets<T>.Values.GetValue(
                this,
                table => new ResidentAssetSet<T>(table._lifetime.Token));
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

    private static class ResidentSets<T>
        where T : class
    {
        internal static readonly ConditionalWeakTable<ResidentAssetTable, ResidentAssetSet<T>>
            Values = new();
    }
}

internal readonly record struct AssetPublication<T>(T Value)
    where T : class;

internal sealed class ResidentAssetSet<T> : IAsyncDisposable
    where T : class
{
    private readonly object _gate = new();
    private readonly CancellationToken _lifetime;
    private readonly Dictionary<AssetGuid, Entry> _byGuid = [];
    private readonly Dictionary<T, Entry> _byValue = new(ReferenceEqualityComparer.Instance);
    private readonly List<Entry> _publicationOrder = [];
    private int _registered;
    private bool _disposed;

    internal ResidentAssetSet(CancellationToken lifetime)
        => _lifetime = lifetime;

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
        Func<T, T, CancellationToken, ValueTask> apply,
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
        Func<T, T, CancellationToken, ValueTask> apply,
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
            if (_disposed)
                return;
            _disposed = true;
            entries = [.. _publicationOrder];
            foreach (Entry entry in _byGuid.Values)
                if (!entries.Contains(entry, ReferenceEqualityComparer.Instance))
                    entries = [.. entries, entry];
            _byGuid.Clear();
            _byValue.Clear();
            _publicationOrder.Clear();
        }

        List<Exception>? failures = null;
        for (int index = entries.Length - 1; index >= 0; index--)
        {
            Entry entry = entries[index];
            try
            {
                try
                {
                    _ = await entry.LoadTask.ConfigureAwait(false);
                }
                catch
                {
                    continue;
                }

                Task<T>? reload = entry.ReloadTask;
                if (reload is not null)
                {
                    try { _ = await reload.ConfigureAwait(false); }
                    catch { }
                }
                if (entry.Value is { } value)
                    await DisposeAssetAsync(value).ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private ValueTask<T> ReloadAsync(
        Entry entry,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, CancellationToken, ValueTask> apply,
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
                _byValue.Add(value, entry);
                _publicationOrder.Add(entry);
            }
            completion.TrySetResult(value);
        }
        catch (Exception failure)
        {
            completion.TrySetException(failure);
        }
    }

    private async Task<T> CompleteReloadAsync(
        Entry entry,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        Func<T, T, CancellationToken, ValueTask> apply)
    {
        T current = entry.Value
            ?? throw new InvalidOperationException($"Asset {entry.Guid} is not ready.");
        T? replacement = null;
        try
        {
            AssetPublication<T> publication = await load(entry.Guid, _lifetime).ConfigureAwait(false);
            replacement = publication.Value
                ?? throw new InvalidDataException("Asset reload returned null.");
            await apply(current, replacement, _lifetime).ConfigureAwait(false);
            lock (_gate)
                entry.Revision = checked(entry.Revision + 1);
            return current;
        }
        finally
        {
            if (replacement is not null && !ReferenceEquals(replacement, current))
                await DisposeAssetAsync(replacement).ConfigureAwait(false);
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
}
