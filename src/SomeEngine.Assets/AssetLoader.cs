namespace SomeEngine.Assets;

/// <summary>
/// Loads one canonical object for each asset ID. Loader state, single-flight coordination and
/// reload attempts remain private; application and render code retain ordinary object references.
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

    public AssetLoader(IAssetStorage storage, AssetLoaderOptions? options = null)
    {
        _storage = storage ?? throw new ArgumentNullException(nameof(storage));
        _options = options ?? AssetLoaderOptions.Empty;
    }

    public ValueTask<T> LoadAsync<T>(
        AssetId<T> asset,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ThrowIfDisposed();
        ValidateAssetId(asset);
        AssetTypeDescriptor<T> descriptor = AssetType<T>.Descriptor;
        AssetGuid guid = asset.Value;
        AssetEntry entry = Admit(guid, descriptor);
        return _assets.LoadAsync(
            guid,
            (_, operationCancellation) =>
                LoadValueAsync(guid, entry, descriptor, operationCancellation),
            cancellationToken);
    }

    public ValueTask<T> ReloadAsync<T>(
        T asset,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(asset);
        AssetTypeDescriptor<T> descriptor = AssetType<T>.Descriptor;
        Func<T, T, CancellationToken, ValueTask> apply = descriptor.ApplyReload
            ?? throw new NotSupportedException(
                $"Asset type '{typeof(T).FullName}' does not support in-place reload.");
        if (!_assets.TryGetAssetGuid(asset, out AssetGuid guid))
            throw new InvalidOperationException("The asset does not belong to this loader.");
        return _assets.ReloadAsync(
            asset,
            (_, operationCancellation) =>
                ReloadValueAsync(guid, descriptor, operationCancellation),
            apply,
            cancellationToken);
    }

    public ValueTask<T> ReloadAsync<T>(
        AssetId<T> asset,
        CancellationToken cancellationToken = default)
        where T : class
    {
        ThrowIfDisposed();
        ValidateAssetId(asset);
        AssetTypeDescriptor<T> descriptor = AssetType<T>.Descriptor;
        Func<T, T, CancellationToken, ValueTask> apply = descriptor.ApplyReload
            ?? throw new NotSupportedException(
                $"Asset type '{typeof(T).FullName}' does not support in-place reload.");
        AssetGuid guid = asset.Value;
        return _assets.ReloadAsync(
            guid,
            (_, operationCancellation) =>
                ReloadValueAsync(guid, descriptor, operationCancellation),
            apply,
            cancellationToken);
    }

    public ulong GetRevision<T>(T asset)
        where T : class
    {
        ThrowIfDisposed();
        return _assets.GetRevision(asset);
    }

    public bool TryGetAssetId<T>(T asset, out AssetId<T> assetId)
        where T : class
    {
        ThrowIfDisposed();
        if (_assets.TryGetAssetGuid(asset, out AssetGuid guid))
        {
            assetId = new AssetId<T>(guid);
            return true;
        }
        assetId = default;
        return false;
    }

    internal IAssetStorage Storage => _storage;

    internal TOptions GetOptions<TOptions>(TOptions fallback)
        where TOptions : notnull
        => _options.Get(fallback);

    internal bool TryFind<T>(AssetGuid guid, out T? value)
        where T : class
        => _assets.TryFind(guid, out value);

    internal async ValueTask<T> LoadDependencyAsync<T>(
        AssetGuid owner,
        AssetId<T> asset,
        CancellationToken operationCancellation)
        where T : class
    {
        ValidateAssetId(asset);
        AssetGuid guid = asset.Value;
        AddDependency(owner, guid);
        try
        {
            return await LoadAsync(asset, operationCancellation).ConfigureAwait(false);
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
            await context.DisposeAsync().ConfigureAwait(false);
            return new AssetPublication<T>(result);
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

        AssetEntry entry = FindAndValidate(guid, descriptor);
        lock (_admissionGate)
        {
            if (_admissions.TryGetValue(guid, out AssetAdmission existing))
                return ValidateAdmission<T>(guid, existing);
            _admissions.Add(guid, new AssetAdmission(typeof(T), entry));
            return entry;
        }
    }

    private AssetEntry RefreshAdmission<T>(AssetGuid guid, AssetTypeDescriptor<T> descriptor)
        where T : class
    {
        lock (_admissionGate)
        {
            if (_admissions.TryGetValue(guid, out AssetAdmission existing)
                && existing.RuntimeType != typeof(T))
                _ = ValidateAdmission<T>(guid, existing);
        }

        AssetEntry entry = FindAndValidate(guid, descriptor);
        lock (_admissionGate)
        {
            _admissions[guid] = new AssetAdmission(typeof(T), entry);
            return entry;
        }
    }

    private AssetEntry FindAndValidate<T>(AssetGuid guid, AssetTypeDescriptor<T> descriptor)
        where T : class
    {
        if (!_storage.TryFind(guid, out AssetEntry entry))
            throw new KeyNotFoundException($"Asset {guid} was not found in storage.");
        if (!descriptor.Accepts(entry))
        {
            throw new InvalidDataException(
                $"Asset '{typeof(T).FullName}' does not accept stored type " +
                $"'{entry.AssetType}' with fingerprint 0x{entry.SchemaFingerprint:X16}.");
        }
        return entry;
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
            return;
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
                continue;
            foreach (AssetGuid next in outgoing.Keys)
                pending.Push(next);
        }
        return false;
    }

    private static InvalidDataException Cycle(AssetGuid owner, AssetGuid dependency)
        => new($"Asset dependency cycle detected while asset '{owner}' loaded asset '{dependency}'.");

    private static void ValidateAssetId<T>(AssetId<T> asset)
        where T : class
    {
        if (!asset.IsValid)
            throw new ArgumentException("An asset ID must be valid.", nameof(asset));
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
