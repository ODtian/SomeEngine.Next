namespace SomeEngine.Assets;

/// <summary>
/// The sole asset loading and residency service. It owns storage I/O, exact type admission, dependency-cycle
/// detection, single-flight loading, resident values, handles, and lifetime drain.
/// </summary>
public sealed class AssetLoader : IAsyncDisposable
{
    private readonly IAssetStorage _storage;
    private readonly ResidentAssetTable _assets = new();
    private readonly AssetLoaderOptions _options;
    private readonly object _admissionGate = new();
    private readonly Dictionary<AssetGuid, AssetAdmission> _admissions = [];
    private readonly object _dependencyGate = new();
    private readonly Dictionary<AssetGuid, Dictionary<AssetGuid, int>> _dependencies = [];
    private int _disposed;

    /// <summary>
    /// Creates the loader and takes exclusive ownership of <paramref name="storage"/>. Storage is
    /// disposed only after all resident and in-flight assets have drained.
    /// </summary>
    public AssetLoader(IAssetStorage storage, AssetLoaderOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? AssetLoaderOptions.Empty;
    }

    /// <summary>
    /// Returns the canonical strong reference immediately and starts one shared background load
    /// when necessary. The same logical asset in this loader always returns the same reference.
    /// </summary>
    public AssetHandle<T> Load<T>(AssetId<T> asset)
        where T : class
    {
        ThrowIfDisposed();
        if (!asset.IsValid)
            throw new ArgumentException("An asset ID must be valid.", nameof(asset));
        AssetTypeDescriptor<T> descriptor = AssetType<T>.Descriptor;
        AssetGuid guid = asset.Value;
        AssetEntry entry = Admit<T>(guid, descriptor);
        return LoadCore(guid, entry, descriptor);
    }

    /// <summary>Waits for the current load attempt without cancelling the shared operation.</summary>
    public ValueTask<AssetHandle<T>> WaitAsync<T>(
        AssetHandle<T> handle,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ThrowIfDisposed();
        return _assets.WaitAsync(handle, cancellationToken);
    }

    /// <summary>
    /// Replaces the current value behind the same strong handle. New reads are blocked, existing
    /// reads drain, and the old value is completely released before storage is queried or the new
    /// document is opened. Concurrent callers join the same replacement attempt.
    /// </summary>
    public ValueTask<AssetHandle<T>> ReloadAsync<T>(
        AssetHandle<T> handle,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ThrowIfDisposed();
        AssetTypeDescriptor<T> descriptor = AssetType<T>.Descriptor;
        return _assets.ReloadAsync(
            handle,
            (guid, operationCancellation) =>
                ReloadValueAsync(guid, descriptor, operationCancellation),
            cancellationToken);
    }

    /// <summary>
    /// Acquires a scoped read of the current ready value. Unload and replacement wait for every
    /// acquired read to leave before releasing the asset's one backing.
    /// </summary>
    public AssetRead<T> Read<T>(AssetHandle<T> handle)
        where T : class
    {
        ThrowIfDisposed();
        return _assets.Read(handle);
    }

    public bool TryRead<T>(AssetHandle<T> handle, out AssetRead<T>? read)
        where T : class
    {
        ThrowIfDisposed();
        return _assets.TryRead(handle, out read);
    }

    internal IAssetStorage Storage => _storage;

    internal TOptions GetOptions<TOptions>(TOptions fallback)
        where TOptions : notnull
        => _options.Get(fallback);

    internal bool TryFind<T>(AssetGuid guid, out AssetHandle<T> handle)
        where T : class
        => _assets.TryFind(guid, out handle);

    internal async Task<AssetHandle<T>> LoadDependencyAsync<T>(
        AssetGuid owner,
        AssetId<T> asset,
        CancellationToken operationCancellation)
        where T : class
    {
        AssetTypeDescriptor<T> descriptor = AssetType<T>.Descriptor;
        AssetGuid guid = asset.Value;
        ValidateGuid(guid);
        AssetEntry entry = Admit<T>(guid, descriptor);
        AddDependency(owner, guid);
        try
        {
            AssetHandle<T> handle = LoadCore(guid, entry, descriptor);
            return await _assets.WaitAsync(handle, operationCancellation).ConfigureAwait(false);
        }
        finally
        {
            RemoveDependency(owner, guid);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        try
        {
            await _assets.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            lock (_dependencyGate)
                _dependencies.Clear();
            lock (_admissionGate)
                _admissions.Clear();

            if (_storage is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else if (_storage is IDisposable disposable)
                disposable.Dispose();
        }
    }

    private AssetHandle<T> LoadCore<T>(
        AssetGuid guid,
        AssetEntry entry,
        AssetTypeDescriptor<T> descriptor)
        where T : class
        => _assets.Load<T>(
            guid,
            (_, operationCancellation) =>
                LoadValueAsync(guid, entry, descriptor, operationCancellation));

    private async Task<AssetPublication<T>> LoadValueAsync<T>(
        AssetGuid guid,
        AssetEntry entry,
        AssetTypeDescriptor<T> descriptor,
        CancellationToken operationCancellation)
        where T : class
    {
        var context = new AssetLoadContext(this, guid, entry, operationCancellation);
        T? result = null;
        try
        {
            result = await descriptor.Load(context, operationCancellation).ConfigureAwait(false)
                ?? throw new InvalidDataException(
                    $"Asset loader for '{typeof(T).FullName}' returned null.");
            context.Commit(result);
            AssetHandleState[] dependencies = context.TakeDependencies();
            await context.DisposeAsync().ConfigureAwait(false);
            return new AssetPublication<T>(result, dependencies);
        }
        catch
        {
            bool contextOwnsResult = result is not null && context.Owns(result);
            try
            {
                await context.DisposeAsync().ConfigureAwait(false);
            }
            finally
            {
                if (result is not null && !contextOwnsResult)
                    await DisposeUnpublishedAsync(result).ConfigureAwait(false);
            }
            throw;
        }
        finally
        {
            context.Seal();
        }
    }

    private Task<AssetPublication<T>> ReloadValueAsync<T>(
        AssetGuid guid,
        AssetTypeDescriptor<T> descriptor,
        CancellationToken operationCancellation)
        where T : class
    {
        AssetEntry entry = RefreshAdmission(guid, descriptor);
        return LoadValueAsync(guid, entry, descriptor, operationCancellation);
    }

    private AssetEntry Admit<T>(AssetGuid guid, AssetTypeDescriptor<T> descriptor)
        where T : class
    {
        lock (_admissionGate)
        {
            if (_admissions.TryGetValue(guid, out AssetAdmission existing))
                return ValidateAdmission<T>(guid, existing);
        }

        if (!_storage.TryFind(guid, out AssetEntry entry))
            throw new KeyNotFoundException($"Asset {guid} was not found in storage.");
        if (!descriptor.Accepts(entry))
        {
            throw new InvalidDataException(
                $"Asset '{typeof(T).FullName}' does not accept stored type " +
                $"'{entry.AssetType}' with fingerprint 0x{entry.SchemaFingerprint:X16}.");
        }

        lock (_admissionGate)
        {
            if (_admissions.TryGetValue(guid, out AssetAdmission existing))
                return ValidateAdmission<T>(guid, existing);

            _admissions.Add(guid, new AssetAdmission(typeof(T), entry));
            return entry;
        }
    }

    private AssetEntry RefreshAdmission<T>(
        AssetGuid guid,
        AssetTypeDescriptor<T> descriptor)
        where T : class
    {
        lock (_admissionGate)
        {
            if (_admissions.TryGetValue(guid, out AssetAdmission existing)
                && existing.RuntimeType != typeof(T))
            {
                _ = ValidateAdmission<T>(guid, existing);
            }
        }

        if (!_storage.TryFind(guid, out AssetEntry entry))
            throw new KeyNotFoundException($"Asset {guid} was not found in storage.");
        if (!descriptor.Accepts(entry))
        {
            throw new InvalidDataException(
                $"Asset '{typeof(T).FullName}' does not accept stored type " +
                $"'{entry.AssetType}' with fingerprint 0x{entry.SchemaFingerprint:X16}.");
        }

        lock (_admissionGate)
        {
            if (_admissions.TryGetValue(guid, out AssetAdmission existing)
                && existing.RuntimeType != typeof(T))
            {
                return ValidateAdmission<T>(guid, existing);
            }

            _admissions[guid] = new AssetAdmission(typeof(T), entry);
            return entry;
        }
    }

    private static AssetEntry ValidateAdmission<T>(AssetGuid guid, AssetAdmission admission)
        where T : class
    {
        if (admission.RuntimeType != typeof(T))
        {
            throw new InvalidOperationException(
                $"Asset GUID '{guid}' is already loaded as '{admission.RuntimeType.FullName}', not " +
                $"'{typeof(T).FullName}'. One GUID may have only one asset instance and backing.");
        }

        return admission.Entry;
    }

    private void AddDependency(AssetGuid owner, AssetGuid dependency)
    {
        lock (_dependencyGate)
        {
            if (owner == dependency)
                throw Cycle(owner, dependency);

            if (!_dependencies.TryGetValue(owner, out Dictionary<AssetGuid, int>? outgoing))
            {
                outgoing = [];
                _dependencies.Add(owner, outgoing);
            }

            outgoing.TryGetValue(dependency, out int count);
            outgoing[dependency] = checked(count + 1);
            if (CanReach(dependency, owner))
            {
                RemoveDependencyCore(owner, dependency);
                throw Cycle(owner, dependency);
            }
        }
    }

    private void RemoveDependency(AssetGuid owner, AssetGuid dependency)
    {
        lock (_dependencyGate)
            RemoveDependencyCore(owner, dependency);
    }

    private void RemoveDependencyCore(AssetGuid owner, AssetGuid dependency)
    {
        if (!_dependencies.TryGetValue(owner, out Dictionary<AssetGuid, int>? outgoing)
            || !outgoing.TryGetValue(dependency, out int count))
        {
            return;
        }

        if (count > 1)
            outgoing[dependency] = count - 1;
        else
            outgoing.Remove(dependency);
        if (outgoing.Count == 0)
            _dependencies.Remove(owner);
    }

    private bool CanReach(AssetGuid start, AssetGuid target)
    {
        var pending = new Stack<AssetGuid>();
        var visited = new HashSet<AssetGuid>();
        pending.Push(start);
        while (pending.TryPop(out AssetGuid current))
        {
            if (current == target)
                return true;
            if (!visited.Add(current)
                || !_dependencies.TryGetValue(current, out Dictionary<AssetGuid, int>? outgoing))
            {
                continue;
            }

            foreach (AssetGuid next in outgoing.Keys)
                pending.Push(next);
        }

        return false;
    }

    private static InvalidDataException Cycle(AssetGuid owner, AssetGuid dependency)
        => new(
            "Asset dependency cycle detected while " +
            $"asset '{owner}' loaded asset '{dependency}'.");

    private static void ValidateGuid(AssetGuid guid)
    {
        if (guid.IsEmpty)
            throw new ArgumentException("An asset GUID cannot be empty.", nameof(guid));
    }

    private static async ValueTask DisposeUnpublishedAsync(object value)
    {
        if (value is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync().ConfigureAwait(false);
        else if (value is IDisposable disposable)
            disposable.Dispose();
    }

    private void ThrowIfDisposed()
        => ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);

    private readonly record struct AssetAdmission(Type RuntimeType, AssetEntry Entry);
}

/// <summary>Immutable type-keyed configuration for asset-specific loading behavior.</summary>
public sealed class AssetLoaderOptions
{
    private readonly IReadOnlyDictionary<Type, object> _values;

    public AssetLoaderOptions()
        : this(new Dictionary<Type, object>())
    {
    }

    private AssetLoaderOptions(IReadOnlyDictionary<Type, object> values)
        => _values = values;

    public static AssetLoaderOptions Empty { get; } = new();

    public AssetLoaderOptions With<TOptions>(TOptions value)
        where TOptions : notnull
    {
        var values = new Dictionary<Type, object>(_values)
        {
            [typeof(TOptions)] = value,
        };
        return new AssetLoaderOptions(values);
    }

    internal TOptions Get<TOptions>(TOptions fallback)
        where TOptions : notnull
        => _values.TryGetValue(typeof(TOptions), out object? value)
            ? (TOptions)value
            : fallback;
}
