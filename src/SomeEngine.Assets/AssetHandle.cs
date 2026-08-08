namespace SomeEngine.Assets;

/// <summary>The observable lifecycle of one logical asset reference.</summary>
public enum AssetLoadState : byte
{
    Invalid = 0,
    Loading = 1,
    Ready = 2,
    Failed = 3,
    Unloaded = 4,
}

/// <summary>
/// A strong, type-safe reference to one logical asset. Copies share one internal reference and
/// therefore never copy the asset value or its streamed backing. The referenced value lives in
/// the owning <see cref="AssetLoader"/> and is accessed through an <see cref="AssetRead{T}"/>.
/// </summary>
public readonly struct AssetHandle<T> : IEquatable<AssetHandle<T>>
    where T : class
{
    private readonly AssetHandleState<T>? _reference;

    internal AssetHandle(AssetHandleState<T> reference)
        => _reference = reference ?? throw new ArgumentNullException(nameof(reference));

    // Test-only structural references remain assembly-internal. Product callers can obtain a
    // loader-owned handle only through AssetLoader.Load.
    internal AssetHandle(int id, int generation)
        : this(-1, id, generation)
    {
    }

    internal AssetHandle(int loaderId, int id, int generation)
    {
        if (id <= 0)
            throw new ArgumentOutOfRangeException(nameof(id));
        if (generation <= 0)
            throw new ArgumentOutOfRangeException(nameof(generation));
        _reference = AssetHandleState<T>.CreateStructural(loaderId, id, generation);
    }

    /// <summary>The stable typed identity encoded by scenes and dependency publications.</summary>
    public AssetId<T> AssetId => _reference?.AssetId ?? default;

    /// <summary>The current load state of this logical reference.</summary>
    public AssetLoadState LoadState => _reference?.LoadState ?? AssetLoadState.Invalid;

    /// <summary>
    /// Monotonically increasing ready-value revision. Zero means no value has been published.
    /// </summary>
    public ulong Revision => _reference?.Revision ?? 0;

    /// <summary>The terminal failure from the current load attempt, when state is Failed.</summary>
    public Exception? Failure => _reference?.Failure;

    internal int LoaderId => _reference?.LoaderId ?? 0;

    /// <summary>Loader-local dense slot used only for runtime lookup and diagnostics.</summary>
    public int Id => _reference?.Slot ?? 0;

    /// <summary>Loader-local slot generation.</summary>
    public int Generation => _reference?.Generation ?? 0;

    /// <summary>True when this reference was issued by a loader or an assembly test fixture.</summary>
    public bool IsValid => _reference is not null;

    internal AssetHandleState<T>? Reference => _reference;

    public bool Equals(AssetHandle<T> other)
        => LoaderId == other.LoaderId
            && Id == other.Id
            && Generation == other.Generation;

    public override bool Equals(object? obj)
        => obj is AssetHandle<T> other && Equals(other);

    public override int GetHashCode()
        => HashCode.Combine(LoaderId, Id, Generation);

    public static bool operator ==(AssetHandle<T> left, AssetHandle<T> right)
        => left.Equals(right);

    public static bool operator !=(AssetHandle<T> left, AssetHandle<T> right)
        => !left.Equals(right);

    public override string ToString()
    {
        if (!IsValid)
            return $"{typeof(T).Name}#Invalid";
        string identity = AssetId.IsValid ? AssetId.Value.ToString() : "structural";
        return $"{typeof(T).Name}:{identity}@{LoaderId}#{Id}:{Generation}/{LoadState}/r{Revision}";
    }
}

internal abstract class AssetHandleState
{
    protected AssetHandleState(AssetGuid assetGuid)
        => AssetGuid = assetGuid;

    internal AssetGuid AssetGuid { get; }

    internal abstract void AcquireDependencyPin();

    internal abstract void ReleaseDependencyPin();
}

internal sealed class AssetHandleState<T> : AssetHandleState
    where T : class
{
    private readonly object _gate = new();
    private readonly ResidentAssetSet<T>? _owner;
    private TaskCompletionSource<AssetHandle<T>> _completion = NewCompletion();
    private TaskCompletionSource? _readerDrain;
    private TaskCompletionSource? _dependencyDrain;
    private AssetHandleState[] _dependencies = [];
    private T? _asset;
    private Exception? _failure;
    private int _loadState;
    private int _activeReaders;
    private int _dependencyPins;
    private bool _finalizationPending;
    private ulong _revision;

    internal AssetHandleState(
        ResidentAssetSet<T> owner,
        int loaderId,
        int slot,
        int generation,
        AssetId<T> assetId)
        : base(assetId.Value)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        LoaderId = loaderId;
        Slot = slot;
        Generation = generation;
        AssetId = assetId;
        _loadState = (int)AssetLoadState.Loading;
    }

    private AssetHandleState(int loaderId, int slot, int generation)
        : base(AssetGuid.Empty)
    {
        LoaderId = loaderId;
        Slot = slot;
        Generation = generation;
        AssetId = default;
        _loadState = (int)AssetLoadState.Ready;
    }

    internal int LoaderId { get; }
    internal int Slot { get; }
    internal int Generation { get; }
    internal AssetId<T> AssetId { get; }
    internal AssetLoadState LoadState => (AssetLoadState)Volatile.Read(ref _loadState);
    internal ulong Revision => Volatile.Read(ref _revision);
    internal Exception? Failure => Volatile.Read(ref _failure);
    internal int DependencyCount
    {
        get
        {
            lock (_gate)
                return _dependencies.Length;
        }
    }
    internal Task Drained { get; set; } = Task.CompletedTask;

    internal static AssetHandleState<T> CreateStructural(
        int loaderId,
        int slot,
        int generation)
        => new(loaderId, slot, generation);

    internal ValueTask<AssetHandle<T>> WaitAsync(
        AssetHandle<T> handle,
        CancellationToken cancellationToken)
    {
        Task<AssetHandle<T>> wait;
        lock (_gate)
        {
            switch ((AssetLoadState)_loadState)
            {
                case AssetLoadState.Ready:
                    return ValueTask.FromResult(handle);
                case AssetLoadState.Failed:
                    return ValueTask.FromException<AssetHandle<T>>(
                        _failure ?? new InvalidDataException("Asset loading failed without a recorded error."));
                case AssetLoadState.Unloaded:
                    return ValueTask.FromException<AssetHandle<T>>(
                        new ObjectDisposedException(nameof(AssetLoader)));
                case AssetLoadState.Loading:
                    wait = _completion.Task;
                    break;
                default:
                    return ValueTask.FromException<AssetHandle<T>>(
                        new InvalidOperationException("Asset reference has an invalid lifecycle state."));
            }
        }

        return cancellationToken.CanBeCanceled
            ? new ValueTask<AssetHandle<T>>(wait.WaitAsync(cancellationToken))
            : new ValueTask<AssetHandle<T>>(wait);
    }

    internal bool TryRead(out AssetRead<T>? read)
    {
        lock (_gate)
        {
            if ((AssetLoadState)_loadState != AssetLoadState.Ready || _asset is null)
            {
                read = null;
                return false;
            }

            _activeReaders = checked(_activeReaders + 1);
            read = new AssetRead<T>(this, _asset, _revision);
            return true;
        }
    }

    internal Task BeginReload()
    {
        lock (_gate)
        {
            AssetLoadState state = (AssetLoadState)_loadState;
            if (state is not (AssetLoadState.Ready or AssetLoadState.Failed))
            {
                throw new InvalidOperationException(
                    $"Only a ready or failed asset may begin replacement; current state is {state}.");
            }

            _completion = NewCompletion();
            _failure = null;
            Volatile.Write(ref _loadState, (int)AssetLoadState.Loading);
            return _activeReaders == 0
                ? Task.CompletedTask
                : (_readerDrain ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
    }

    internal async ValueTask<AssetRetirement<T>> TakeReloadValueAsync(Task readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        await readers.ConfigureAwait(false);

        lock (_gate)
        {
            if ((AssetLoadState)_loadState == AssetLoadState.Unloaded)
                return default;
            if ((AssetLoadState)_loadState != AssetLoadState.Loading)
            {
                throw new InvalidOperationException(
                    "Asset replacement lost its exclusive loading state.");
            }

            T? asset = _asset;
            AssetHandleState[] dependencies = _dependencies;
            _asset = null;
            _dependencies = [];
            return new AssetRetirement<T>(asset, dependencies);
        }
    }

    internal bool IsLoading
        => LoadState == AssetLoadState.Loading;

    internal bool Publish(T asset, AssetHandleState[] dependencies)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(dependencies);
        int pinned = 0;
        try
        {
            for (; pinned < dependencies.Length; pinned++)
                dependencies[pinned].AcquireDependencyPin();
        }
        catch
        {
            for (int index = pinned - 1; index >= 0; index--)
                dependencies[index].ReleaseDependencyPin();
            throw;
        }

        TaskCompletionSource<AssetHandle<T>> completion;
        AssetHandle<T> handle;
        bool accepted = false;
        try
        {
            lock (_gate)
            {
                if ((AssetLoadState)_loadState == AssetLoadState.Unloaded)
                {
                    completion = _completion;
                    handle = default;
                }
                else
                {
                    if ((AssetLoadState)_loadState != AssetLoadState.Loading || _asset is not null)
                        throw new InvalidOperationException("Only one asset value may be published for a load attempt.");

                    ulong revision = _revision + 1;
                    if (revision == 0)
                        throw new InvalidOperationException("Asset revision space is exhausted.");
                    _asset = asset;
                    _dependencies = dependencies;
                    _failure = null;
                    Volatile.Write(ref _revision, revision);
                    Volatile.Write(ref _loadState, (int)AssetLoadState.Ready);
                    completion = _completion;
                    handle = new AssetHandle<T>(this);
                    accepted = true;
                }
            }
        }
        catch
        {
            for (int index = dependencies.Length - 1; index >= 0; index--)
                dependencies[index].ReleaseDependencyPin();
            throw;
        }

        if (!accepted)
        {
            for (int index = dependencies.Length - 1; index >= 0; index--)
                dependencies[index].ReleaseDependencyPin();
            return false;
        }
        completion.TrySetResult(handle);
        return true;
    }

    internal void Fail(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        TaskCompletionSource<AssetHandle<T>> completion;
        lock (_gate)
        {
            if ((AssetLoadState)_loadState == AssetLoadState.Unloaded)
                return;
            if ((AssetLoadState)_loadState != AssetLoadState.Loading)
                throw new InvalidOperationException("Only a loading asset may fail.");
            _failure = failure;
            Volatile.Write(ref _loadState, (int)AssetLoadState.Failed);
            completion = _completion;
        }
        completion.TrySetException(failure);
    }

    internal async ValueTask<AssetRetirement<T>> UnloadAsync(Exception reason)
    {
        ArgumentNullException.ThrowIfNull(reason);
        Task readers;
        Task dependencyPins;
        TaskCompletionSource<AssetHandle<T>> completion;
        lock (_gate)
        {
            if ((AssetLoadState)_loadState == AssetLoadState.Unloaded)
                return default;

            Volatile.Write(ref _loadState, (int)AssetLoadState.Unloaded);
            _finalizationPending = false;
            _failure = reason;
            completion = _completion;
            readers = _activeReaders == 0
                ? Task.CompletedTask
                : (_readerDrain ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
            dependencyPins = _dependencyPins == 0
                ? Task.CompletedTask
                : (_dependencyDrain ??= new TaskCompletionSource(
                    TaskCreationOptions.RunContinuationsAsynchronously)).Task;
        }
        completion.TrySetException(reason);
        await Task.WhenAll(readers, dependencyPins).ConfigureAwait(false);

        T? asset;
        AssetHandleState[] dependencies;
        lock (_gate)
        {
            asset = _asset;
            dependencies = _dependencies;
            _asset = null;
            _dependencies = [];
        }
        GC.SuppressFinalize(this);
        return new AssetRetirement<T>(asset, dependencies);
    }

    internal void ReleaseRead()
    {
        TaskCompletionSource? drain = null;
        lock (_gate)
        {
            if (_activeReaders <= 0)
                throw new InvalidOperationException("Asset read count is already zero.");
            _activeReaders--;
            if (_activeReaders == 0)
            {
                drain = _readerDrain;
                _readerDrain = null;
            }
        }
        drain?.TrySetResult();
    }

    internal override void AcquireDependencyPin()
    {
        lock (_gate)
        {
            if ((AssetLoadState)_loadState == AssetLoadState.Unloaded)
                throw new ObjectDisposedException(typeof(T).FullName);
            _dependencyPins = checked(_dependencyPins + 1);
        }
    }

    internal override void ReleaseDependencyPin()
    {
        TaskCompletionSource? drain = null;
        AssetRetirement<T> retirement = default;
        bool retire = false;
        lock (_gate)
        {
            if (_dependencyPins <= 0)
                throw new InvalidOperationException("Asset dependency pin count is already zero.");
            _dependencyPins--;
            if (_dependencyPins == 0)
            {
                drain = _dependencyDrain;
                _dependencyDrain = null;
                if (_finalizationPending)
                {
                    _finalizationPending = false;
                    Volatile.Write(ref _loadState, (int)AssetLoadState.Unloaded);
                    retirement = new AssetRetirement<T>(_asset, _dependencies);
                    _asset = null;
                    _dependencies = [];
                    retire = true;
                }
            }
        }
        drain?.TrySetResult();
        if (retire)
            _owner?.RetireAfterDependencyRelease(this, retirement);
    }

    internal bool NeedsPinnedRetention
    {
        get
        {
            lock (_gate)
                return _finalizationPending && _dependencyPins != 0;
        }
    }

    internal bool RevivePinnedFinalization()
    {
        bool revive;
        lock (_gate)
        {
            revive = _finalizationPending
                && _dependencyPins != 0
                && (AssetLoadState)_loadState != AssetLoadState.Unloaded;
            if (revive)
                _finalizationPending = false;
        }
        if (revive)
            GC.ReRegisterForFinalize(this);
        return revive;
    }

    ~AssetHandleState()
    {
        try
        {
            T? asset;
            AssetHandleState[] dependencies;
            bool pinned;
            lock (_gate)
            {
                if ((AssetLoadState)_loadState == AssetLoadState.Unloaded)
                    return;
                pinned = _dependencyPins != 0;
                if (pinned)
                {
                    _finalizationPending = true;
                    asset = null;
                    dependencies = [];
                }
                else
                {
                    Volatile.Write(ref _loadState, (int)AssetLoadState.Unloaded);
                    asset = _asset;
                    dependencies = _dependencies;
                    _asset = null;
                    _dependencies = [];
                }
            }
            if (pinned)
            {
                _owner?.RetainPinnedFinalization(this);
                return;
            }
            _owner?.RetireFromFinalizer(this, new AssetRetirement<T>(asset, dependencies));
        }
        catch
        {
            // Finalizers cannot surface failures. The owning set records disposal failures from
            // its retirement worker and reports them from AssetLoader.DisposeAsync.
        }
    }

    private static TaskCompletionSource<AssetHandle<T>> NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}

internal readonly record struct AssetRetirement<T>(
    T? Value,
    AssetHandleState[] Dependencies)
    where T : class;
