using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;

namespace SomeEngine.Assets;

/// <summary>
/// Loader-owned type buckets. Buckets weakly index ready references; the shared state held by an
/// <see cref="AssetHandle{T}"/> is the strong owner of the one published asset value.
/// </summary>
internal sealed class ResidentAssetTable : IAsyncDisposable
{
    private static int s_nextIdentity;
    private readonly object _gate = new();
    private readonly List<Func<ValueTask>> _disposeSets = [];
    private readonly CancellationTokenSource _lifetime = new();
    private readonly int _identity = NextIdentity();
    private bool _disposed;

    internal bool TryFind<T>(AssetGuid guid, out AssetHandle<T> handle)
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (TrySet(out ResidentAssetSet<T>? set)
                && set.TryFind(guid, out handle))
            {
                return true;
            }
        }

        handle = default;
        return false;
    }

    internal bool TryRead<T>(AssetHandle<T> handle, out AssetRead<T>? read)
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (handle.LoaderId == _identity
                && TrySet(out ResidentAssetSet<T>? set))
            {
                return set.TryRead(handle, out read);
            }
        }

        read = null;
        return false;
    }

    internal AssetRead<T> Read<T>(AssetHandle<T> handle)
        where T : class
        => TryRead(handle, out AssetRead<T>? read)
            ? read!
            : throw new InvalidOperationException(
                $"Asset handle '{handle}' is not ready in this asset loader.");

    internal ValueTask<AssetHandle<T>> WaitAsync<T>(
        AssetHandle<T> handle,
        CancellationToken cancellationToken)
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (handle.LoaderId != _identity
                || !TrySet(out ResidentAssetSet<T>? set))
            {
                return ValueTask.FromException<AssetHandle<T>>(
                    new InvalidOperationException(
                        $"Asset handle '{handle}' does not belong to this asset loader."));
            }
            return set.WaitAsync(handle, cancellationToken);
        }
    }

    internal AssetHandle<T> Load<T>(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(load);
        return Set<T>().Load(guid, load);
    }

    internal ValueTask<AssetHandle<T>> ReloadAsync<T>(
        AssetHandle<T> handle,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(load);
        return Set<T>().ReloadAsync(handle, load, cancellationToken);
    }

    internal AssetHandle<T> Load<T>(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<T?>> load)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(load);
        return Set<T>().Load(
            guid,
            async (assetGuid, token) =>
            {
                T? value = await load(assetGuid, token).ConfigureAwait(false);
                return value is null
                    ? throw new InvalidDataException("Asset loading returned null.")
                    : new AssetPublication<T>(value, []);
            });
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

        ValueTask[] draining = new ValueTask[disposals.Length];
        for (int index = 0; index < disposals.Length; index++)
            draining[index] = disposals[index]();

        _lifetime.Cancel();
        List<Exception>? failures = null;
        for (int index = 0; index < draining.Length; index++)
        {
            try
            {
                await draining[index].ConfigureAwait(false);
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }
        _lifetime.Dispose();

        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException("Asset residency cleanup failed.", failures);
        }
    }

    private ResidentAssetSet<T> Set<T>()
        where T : class
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            ResidentAssetSet<T> set = ResidentSets<T>.Values.GetValue(
                this,
                static table => new ResidentAssetSet<T>(
                    table._identity,
                    table._lifetime.Token));
            if (set.MarkRegistered())
            {
                _disposeSets.Add(set.DisposeAsync);
            }
            return set;
        }
    }

    private bool TrySet<T>([NotNullWhen(true)] out ResidentAssetSet<T>? set)
        where T : class
        => ResidentSets<T>.Values.TryGetValue(this, out set);

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ResidentAssetTable));
    }

    private static int NextIdentity()
    {
        int identity = Interlocked.Increment(ref s_nextIdentity);
        return identity > 0
            ? identity
            : throw new InvalidOperationException("Resident asset table identity overflow.");
    }

    private static class ResidentSets<T>
        where T : class
    {
        internal static readonly ConditionalWeakTable<ResidentAssetTable, ResidentAssetSet<T>>
            Values = new();
    }
}

internal readonly record struct AssetPublication<T>(
    T Value,
    AssetHandleState[] Dependencies)
    where T : class;

internal sealed class ResidentAssetSet<T> : IAsyncDisposable
    where T : class
{
    private static readonly Task<Exception?> NoRetirementFailure = Task.FromResult<Exception?>(null);
    private readonly object _gate = new();
    private readonly int _tableIdentity;
    private readonly CancellationToken _lifetime;
    private readonly List<AssetResidence<T>?> _entries = [null];
    private readonly Dictionary<AssetGuid, AssetResidence<T>> _byGuid = new();
    private readonly Dictionary<AssetGuid, AssetHandleState<T>> _loads = new();
    private readonly List<Task<Exception?>> _retirements = [];
    private readonly HashSet<AssetHandleState<T>> _pinnedFinalizations =
        new(ReferenceEqualityComparer.Instance);
    private int _registered;
    private bool _disposed;

    internal ResidentAssetSet(int tableIdentity, CancellationToken lifetime)
    {
        _tableIdentity = tableIdentity;
        _lifetime = lifetime;
    }

    internal bool MarkRegistered()
        => Interlocked.CompareExchange(ref _registered, 1, 0) == 0;

    internal AssetHandle<T> Load(
        AssetGuid guid,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load)
    {
        ArgumentNullException.ThrowIfNull(load);
        if (guid.IsEmpty)
            throw new ArgumentException("An asset GUID cannot be empty.", nameof(guid));

        lock (_gate)
        {
            ThrowIfDisposed();
            AssetResidence<T>? predecessor = null;
            if (_byGuid.TryGetValue(guid, out AssetResidence<T>? indexed))
            {
                if (indexed.Reference.TryGetTarget(out AssetHandleState<T>? existingState)
                    && existingState.LoadState != AssetLoadState.Unloaded)
                {
                    if (existingState.RevivePinnedFinalization())
                        _pinnedFinalizations.Remove(existingState);
                    return new AssetHandle<T>(existingState);
                }
                predecessor = indexed;
            }

            int slot = _entries.Count;
            var state = new AssetHandleState<T>(
                this,
                _tableIdentity,
                slot,
                generation: 1,
                new AssetId<T>(guid));
            var residence = new AssetResidence<T>(state);
            _entries.Add(residence);
            _byGuid[guid] = residence;
            _loads.Add(guid, state);
            state.Drained = CompleteLoadAsync(
                state,
                predecessor?.Retired.Task ?? NoRetirementFailure,
                load);
            return new AssetHandle<T>(state);
        }
    }

    internal ValueTask<AssetHandle<T>> ReloadAsync(
        AssetHandle<T> handle,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(load);
        AssetHandleState<T>? state = handle.Reference;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!Owns(state))
            {
                return ValueTask.FromException<AssetHandle<T>>(
                    new InvalidOperationException(
                        $"Asset handle '{handle}' does not belong to this resident set."));
            }

            if (state!.LoadState is AssetLoadState.Ready or AssetLoadState.Failed)
            {
                Task readers = state.BeginReload();
                _loads[state.AssetGuid] = state;
                state.Drained = CompleteReloadAsync(state, readers, load);
            }
        }

        return state!.WaitAsync(handle, cancellationToken);
    }

    internal bool TryFind(AssetGuid guid, out AssetHandle<T> handle)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            return TryFindCore(guid, out handle);
        }
    }

    internal ValueTask<AssetHandle<T>> WaitAsync(
        AssetHandle<T> handle,
        CancellationToken cancellationToken)
    {
        AssetHandleState<T>? state = handle.Reference;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!Owns(state))
            {
                return ValueTask.FromException<AssetHandle<T>>(
                    new InvalidOperationException(
                        $"Asset handle '{handle}' does not belong to this resident set."));
            }
        }
        return state!.WaitAsync(handle, cancellationToken);
    }

    internal bool TryRead(AssetHandle<T> handle, out AssetRead<T>? read)
    {
        AssetHandleState<T>? state = handle.Reference;
        lock (_gate)
        {
            ThrowIfDisposed();
            if (!Owns(state))
            {
                read = null;
                return false;
            }
        }
        return state!.TryRead(out read);
    }

    public async ValueTask DisposeAsync()
    {
        AssetHandleState<T>[] states;
        var disposed = new ObjectDisposedException(nameof(AssetLoader));
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;

            var unique = new HashSet<AssetHandleState<T>>(ReferenceEqualityComparer.Instance);
            foreach (AssetHandleState<T> loading in _loads.Values)
                unique.Add(loading);
            foreach (AssetHandleState<T> pinned in _pinnedFinalizations)
                unique.Add(pinned);
            for (int index = 1; index < _entries.Count; index++)
            {
                if (_entries[index] is { } residence
                    && residence.Reference.TryGetTarget(out AssetHandleState<T>? state))
                {
                    unique.Add(state);
                }
            }
            states = [.. unique];
            _loads.Clear();
            _pinnedFinalizations.Clear();
            _byGuid.Clear();
            _entries.Clear();
        }

        Task<AssetRetirement<T>>[] unloads = new Task<AssetRetirement<T>>[states.Length];
        for (int index = 0; index < states.Length; index++)
            unloads[index] = states[index].UnloadAsync(disposed).AsTask();

        List<Exception>? failures = null;
        for (int index = 0; index < states.Length; index++)
        {
            try
            {
                await states[index].Drained.ConfigureAwait(false);
            }
            catch
            {
                // Load failure is owned by the handle's completion and LoadState. Shutdown drains
                // the operation but must not report that same failure a second time.
            }
        }

        for (int index = 0; index < unloads.Length; index++)
        {
            try
            {
                AssetRetirement<T> retirement = await unloads[index].ConfigureAwait(false);
                try
                {
                    if (retirement.Value is not null)
                        await DisposeAssetAsync(retirement.Value).ConfigureAwait(false);
                }
                finally
                {
                    ReleaseDependencies(retirement.Dependencies);
                }
            }
            catch (Exception failure)
            {
                (failures ??= []).Add(failure);
            }
        }

        Exception[] retirementFailures = await DrainRetirementAsync().ConfigureAwait(false);
        if (retirementFailures.Length != 0)
        {
            failures ??= [];
            failures.AddRange(retirementFailures);
        }

        if (failures is not null)
        {
            throw failures.Count == 1
                ? failures[0]
                : new AggregateException("Resident asset cleanup failed.", failures);
        }
    }

    internal void RetireFromFinalizer(
        AssetHandleState<T> state,
        AssetRetirement<T> retirement)
    {
        try
        {
            lock (_gate)
                ScheduleRetirementCore(state, retirement);
        }
        catch
        {
            // State finalization must not escape. A value handed to a retirement task is disposed
            // there; loader shutdown reports any disposal failure recorded by that task.
        }
    }

    internal void RetainPinnedFinalization(AssetHandleState<T> state)
    {
        lock (_gate)
        {
            if (state.NeedsPinnedRetention)
            {
                _pinnedFinalizations.Add(state);
                if ((uint)state.Slot < (uint)_entries.Count)
                    _entries[state.Slot]?.RefreshReference(state);
            }
        }
    }

    internal void RetireAfterDependencyRelease(
        AssetHandleState<T> state,
        AssetRetirement<T> retirement)
    {
        lock (_gate)
        {
            _pinnedFinalizations.Remove(state);
            ScheduleRetirementCore(state, retirement);
        }
    }

    private async Task CompleteLoadAsync(
        AssetHandleState<T> state,
        Task<Exception?> predecessorRetirement,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load)
    {
        AssetPublication<T>? publication = null;
        try
        {
            Exception? predecessorFailure = await predecessorRetirement.ConfigureAwait(false);
            if (predecessorFailure is not null)
            {
                throw new InvalidOperationException(
                    $"The previous value for asset '{state.AssetGuid}' could not be released; " +
                    "a replacement value was not loaded.",
                    predecessorFailure);
            }

            publication = await load(state.AssetGuid, _lifetime).ConfigureAwait(false);
            if (publication.Value.Value is null)
                throw new InvalidDataException("Asset loading returned null.");

            lock (_gate)
                RemoveLoadCore(state);
            if (!state.Publish(publication.Value.Value, publication.Value.Dependencies))
            {
                try
                {
                    await DisposeAssetAsync(publication.Value.Value).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    lock (_gate)
                        _retirements.Add(Task.FromResult<Exception?>(cleanupFailure));
                }
                publication = null;
            }
        }
        catch (Exception failure)
        {
            lock (_gate)
                RemoveLoadCore(state);
            if (publication is { } unpublished)
            {
                try
                {
                    await DisposeAssetAsync(unpublished.Value).ConfigureAwait(false);
                }
                catch (Exception cleanupFailure)
                {
                    lock (_gate)
                        _retirements.Add(Task.FromResult<Exception?>(cleanupFailure));
                    failure = new AggregateException(
                        "Asset publication failed and its unpublished value could not be released.",
                        failure,
                        cleanupFailure);
                }
            }
            state.Fail(failure);
        }
    }

    private async Task CompleteReloadAsync(
        AssetHandleState<T> state,
        Task readers,
        Func<AssetGuid, CancellationToken, Task<AssetPublication<T>>> load)
    {
        try
        {
            AssetRetirement<T> previous = await state
                .TakeReloadValueAsync(readers)
                .ConfigureAwait(false);
            try
            {
                if (previous.Value is not null)
                    await DisposeAssetAsync(previous.Value).ConfigureAwait(false);
            }
            finally
            {
                ReleaseDependencies(previous.Dependencies);
            }

            if (!state.IsLoading)
            {
                lock (_gate)
                    RemoveLoadCore(state);
                return;
            }

            await CompleteLoadAsync(state, NoRetirementFailure, load).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            lock (_gate)
                RemoveLoadCore(state);
            state.Fail(failure);
        }
    }

    private bool TryFindCore(AssetGuid guid, out AssetHandle<T> handle)
    {
        if (_byGuid.TryGetValue(guid, out AssetResidence<T>? residence)
            && residence.Reference.TryGetTarget(out AssetHandleState<T>? state)
            && state.LoadState != AssetLoadState.Unloaded)
        {
            if (state.RevivePinnedFinalization())
                _pinnedFinalizations.Remove(state);
            handle = new AssetHandle<T>(state);
            return true;
        }

        handle = default;
        return false;
    }

    private bool Owns(AssetHandleState<T>? state)
    {
        if (state is null
            || state.LoaderId != _tableIdentity
            || state.Slot <= 0
            || state.Slot >= _entries.Count
            || _entries[state.Slot] is not { } residence
            || !residence.Reference.TryGetTarget(out AssetHandleState<T>? current))
        {
            return false;
        }
        return ReferenceEquals(current, state);
    }

    private void RemoveLoadCore(AssetHandleState<T> state)
    {
        if (_loads.TryGetValue(state.AssetGuid, out AssetHandleState<T>? current)
            && ReferenceEquals(current, state))
        {
            _loads.Remove(state.AssetGuid);
        }
    }

    private void ScheduleRetirementCore(
        AssetHandleState<T> state,
        AssetRetirement<T> retirement)
    {
        AssetResidence<T>? residence = (uint)state.Slot < (uint)_entries.Count
            ? _entries[state.Slot]
            : null;
        if (residence is null && retirement.Value is null)
        {
            ReleaseDependencies(retirement.Dependencies);
            return;
        }

        _retirements.Add(RetireAsync(residence, retirement));
    }

    private async Task<Exception[]> DrainRetirementAsync()
    {
        var failures = new List<Exception>();
        int drained = 0;
        while (true)
        {
            Task<Exception?>[] observed;
            lock (_gate)
            {
                if (drained == _retirements.Count)
                    return [.. failures];
                observed = _retirements.GetRange(
                    drained,
                    _retirements.Count - drained).ToArray();
                drained = _retirements.Count;
            }

            for (int index = 0; index < observed.Length; index++)
            {
                Exception? failure = await observed[index].ConfigureAwait(false);
                if (failure is not null)
                    failures.Add(failure);
            }
        }
    }

    private static async Task<Exception?> RetireAsync(
        AssetResidence<T>? residence,
        AssetRetirement<T> retirement)
    {
        await Task.Yield();
        Exception? failure = null;
        try
        {
            if (retirement.Value is not null)
                await DisposeAssetAsync(retirement.Value).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            failure = error;
        }
        finally
        {
            ReleaseDependencies(retirement.Dependencies);
            residence?.Retired.TrySetResult(failure);
        }

        return failure;
    }

    private static void ReleaseDependencies(AssetHandleState[]? dependencies)
    {
        if (dependencies is null)
            return;
        for (int index = dependencies.Length - 1; index >= 0; index--)
            dependencies[index].ReleaseDependencyPin();
    }

    private sealed class AssetResidence<TAsset>
        where TAsset : class
    {
        internal AssetResidence(AssetHandleState<TAsset> state)
        {
            ArgumentNullException.ThrowIfNull(state);
            Reference = new WeakReference<AssetHandleState<TAsset>>(
                state,
                trackResurrection: true);
        }

        internal WeakReference<AssetHandleState<TAsset>> Reference { get; private set; }

        internal void RefreshReference(AssetHandleState<TAsset> state)
            => Reference = new WeakReference<AssetHandleState<TAsset>>(
                state,
                trackResurrection: true);

        internal TaskCompletionSource<Exception?> Retired { get; } = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(ResidentAssetTable));
    }

    private static async ValueTask DisposeAssetAsync(T asset)
    {
        if (asset is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (asset is IDisposable disposable)
            disposable.Dispose();
    }
}
