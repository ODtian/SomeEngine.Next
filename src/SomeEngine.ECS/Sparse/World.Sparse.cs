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

    private bool HasSparseValue<T>(Entity entity) where T : struct
    {
        using WorldAccess access = OpenStructure(false);
        return _sparse?.Find<T>() is { } store && store.Has(entity);
    }

    private ref readonly T ReadSparseValue<T>(Entity entity) where T : struct
    {
        RequireValue<T>(false, WorldValueKind.Sparse);
        return ref Sparse<T>().Get(entity);
    }

    private ref T GetSparseValue<T>(Entity entity) where T : struct
    {
        RequireValue<T>(true, WorldValueKind.Sparse);
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed write view.");
        return ref Sparse<T>().Get(entity, ComponentType<T>.TracksWritesType ? NextVersion() : 0);
    }

    private void AddSparseValue<T>(Entity entity, in T value) where T : struct
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

    private void SetSparseValue<T>(Entity entity, in T value) where T : struct
    {
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the component's typed write view.");
        if (Execution is not null) { SetSparseWithExecution(entity, value); return; }
        ref EntityRecord record = ref Resolve(entity);
        RequireActiveWrite<T>(in record);
        Sparse<T>().Set(this, _sparse!, entity, value);
    }

    private void RemoveSparseValue<T>(Entity entity) where T : struct
    {
        using WorldAccess access = OpenStructure();
        RequireStructure();
        Resolve(entity);
        if (_sparse?.Find<T>() is not { } store || !store.Remove(this, _sparse, entity))
            throw new InvalidOperationException("The entity requires this sparse component before removing it.");
        if (ComponentType<T>.IsCleanupType) ReleaseAfterCleanup(entity);
    }

    /// <summary>Borrows the dense member arrays of a sparse component for whole-store reads.</summary>
    public SparseValues<T> OpenSparseValues<T>() where T : struct
    {
        BeginValueRead<T>(false, WorldValueKind.Sparse);
        return new(this, Sparse<T>());
    }

    /// <summary>Borrows the dense member arrays of a sparse component for whole-store writes.</summary>
    public SparseValueWrites<T> OpenSparseValueWrites<T>() where T : struct
    {
        if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the typed write view of this component.");
        BeginValueRead<T>(true, WorldValueKind.Sparse);
        return new(this, Sparse<T>());
    }
}

/// <summary>The dense entity and value arrays of one sparse component, read together.</summary>
public readonly ref struct SparseValues<T> where T : struct
{
    private readonly World _world;
    private readonly SparseStore<T> _store;
    internal SparseValues(World world, SparseStore<T> store) => (_world, _store) = (world, store);
    public int Count => _store.Count;
    public ReadOnlySpan<Entity> Entities => _store.Entities.AsSpan(0, _store.Count);
    public ReadOnlySpan<T> Values => _store.Values.AsSpan(0, _store.Count);
    public void Dispose() => _world.EndValueRead();
}

/// <summary>The dense entity and value arrays of one sparse component; values are written as one batch.</summary>
public ref struct SparseValueWrites<T> where T : struct
{
    private readonly World _world;
    private readonly SparseStore<T> _store;
    private ulong _version;
    internal SparseValueWrites(World world, SparseStore<T> store) => (_world, _store) = (world, store);
    public int Count => _store.Count;
    public ReadOnlySpan<Entity> Entities => _store.Entities.AsSpan(0, _store.Count);
    public Span<T> Values
    {
        get
        {
            if (ComponentType<T>.TracksWritesType)
            {
                ulong version = _world.WriteVersion(ref _version);
                for (int row = 0; row < _store.Count; row++)
                    _store.Changes![0].Stamp(row, version, false, ComponentType<T>.TracksAddedType);
            }
            return _store.Values.AsSpan(0, _store.Count);
        }
    }
    public void Dispose() => _world.EndValueRead();
}
