using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public sealed partial class World
{
    private SparseStores? _sparse;

    internal SparseStore<T> Sparse<T>() where T : struct
        => _sparse?.Find<T>() is { } found ? found : CreateSparse<T>();

    private SparseStore<T> CreateSparse<T>() where T : struct
    {
        lock (LazyInitializer.EnsureInitialized(ref _queryBindingsLock))
            return (_sparse ??= new SparseStores(this, Records.Length)).GetOrCreate<T>();
    }

    public bool HasSparse<T>(Entity entity) where T : struct
    {
        using WorldAccess access = OpenStructure(false);
        return _sparse?.Find<T>() is { } store && store.Has(entity);
    }

    public ref readonly T ReadSparse<T>(Entity entity) where T : struct
    {
        RequireValue<T>(false, WorldValueKind.Sparse);
        return ref Sparse<T>().Get(entity);
    }

    public ref T GetSparse<T>(Entity entity) where T : struct
    {
        RequireValue<T>(true, WorldValueKind.Sparse);
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed write view.");
        return ref Sparse<T>().Get(entity, ComponentType<T>.TracksWritesType ? NextVersion() : 0);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void AddSparse<T>(Entity entity, in T value) where T : struct
    {
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed write view.");
        using WorldAccess access = OpenStructure();
        RequireStructure();
        ref EntityRecord record = ref Resolve(entity);
        RequireActiveWrite<T>(in record);
        SparseStore<T> store = Sparse<T>();
        if (store.Has(entity)) throw new InvalidOperationException("The entity already has this sparse component.");
        store.Set(this, _sparse!, entity, value);
    }

    public void ReplaceSparse<T>(Entity entity, in T value) where T : struct
    {
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed write view.");
        using WorldAccess access = OpenValues<T>(true, WorldValueKind.Sparse);
        ref EntityRecord record = ref Resolve(entity);
        RequireActiveWrite<T>(in record);
        if (_sparse?.Find<T>() is not { } store || !store.Has(entity))
            throw new InvalidOperationException("The entity requires this sparse component before replacing it.");
        store.Set(this, _sparse, entity, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void SetSparse<T>(Entity entity, in T value) where T : struct
    {
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed write view.");
        if (Execution is not null) { SetSparseWithExecution(entity, value); return; }
        ref EntityRecord record = ref Resolve(entity);
        RequireActiveWrite<T>(in record);
        Sparse<T>().Set(this, _sparse!, entity, value);
    }

    public void RemoveSparse<T>(Entity entity) where T : struct
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        Resolve(entity);
        if (_sparse?.Find<T>() is not { } store || !store.Remove(this, _sparse, entity))
            throw new InvalidOperationException("The entity requires this sparse component before removing it.");
        if (ComponentType<T>.IsCleanupType) ReleaseAfterCleanup(entity);
    }

    public SparseReadScope<T> OpenSparseRead<T>() where T : struct => new(this, Sparse<T>());
    public SparseWriteScope<T> OpenSparseWrite<T>() where T : struct => new(this, Sparse<T>());
}

public ref struct SparseReadScope<T> where T : struct
{
    private readonly World _world;
    private readonly SparseStore<T> _store;

    internal SparseReadScope(World world, SparseStore<T> store, bool write = false)
    {
        _world = world;
        _store = store;
        world.BeginValueRead<T>(write, WorldValueKind.Sparse);
    }

    public int Count => _store.Count;
    public ReadOnlySpan<Entity> Entities => _store.Entities.AsSpan(0, _store.Count);
    public ReadOnlySpan<T> Values => _store.Values.AsSpan(0, _store.Count);
    public bool Has(Entity entity) => _store.Has(entity);
    public ref readonly T Read(Entity entity) => ref Access(entity);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Access(Entity entity, ulong writeVersion = 0)
    {
        return ref _store.Get(entity, writeVersion);
    }

    internal Span<T> MutableValues(ulong writeVersion)
    {
        if (writeVersion != 0)
            for (int row = 0; row < _store.Count; row++)
                _store.Changes![0].Stamp(row, writeVersion, false, ComponentType<T>.TracksAddedType);
        return _store.Values.AsSpan(0, _store.Count);
    }
    internal ulong WriteVersion(ref ulong version) => _world.WriteVersion(ref version);
    public void Dispose() => _world.EndValueRead();
}

public ref struct SparseWriteScope<T> where T : struct
{
    private SparseReadScope<T> _read;
    private ulong _version;
    internal SparseWriteScope(World world, SparseStore<T> store)
    {
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed write view.");
        _read = new(world, store, true);
    }
    public int Count => _read.Count;
    public ReadOnlySpan<Entity> Entities => _read.Entities;
    public Span<T> Values => _read.MutableValues(ComponentType<T>.TracksWritesType ? _read.WriteVersion(ref _version) : 0);
    public ref T Get(Entity entity)
        => ref _read.Access(entity, ComponentType<T>.TracksWritesType ? _read.WriteVersion(ref _version) : 0);
    public bool Has(Entity entity) => _read.Has(entity);
    public void Dispose() => _read.Dispose();
}
