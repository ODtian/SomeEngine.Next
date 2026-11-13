using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Sparse;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public ref struct ReadLookup<T> where T : struct
{
    private readonly T[]? _sharedValues;
    private readonly SparseStore<T>? _sparse;
    private CachedReadLookup<T> _cached;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadLookup(World world, bool write = false)
    {
        if (ComponentType<T>.IsSparseType)
        {
            world.BeginValueRead<T>(write, WorldValueKind.Sparse);
            _cached = new(world);
            _sparse = world.Sparse<T>();
        }
        else if (ComponentType<T>.IsSharedType)
        {
            world.BeginValueRead<T>(write);
            _cached = new(world);
            _sharedValues = world.SharedValueArray<T>();
        }
        else _cached = world.OpenCached<T>(write);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T Read(Entity entity)
    {
        if (ComponentType<T>.IsSparseType) return ref _sparse!.Get(entity);
        if (ComponentType<T>.IsSharedType) return ref _cached.World.ReadSharedValue(entity, _sharedValues!);
        return ref Access(entity);
    }

    /// <summary>Reads an identity whose component presence is established for this scope.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T ReadKnown(Entity entity)
    {
        if (ComponentType<T>.IsSparseType) return ref _sparse!.Get(entity);
        if (ComponentType<T>.IsSharedType) return ref _cached.World.ReadSharedValueKnown(entity, _sharedValues!);
        return ref _cached.AccessKnown(entity.Id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Access(Entity entity, ulong writeVersion = 0)
    {
        if (ComponentType<T>.IsSparseType) return ref _sparse!.Get(entity, writeVersion);
        return ref _cached.Access(entity, writeVersion);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T AccessKnown(int entityId, ulong writeVersion = 0) => ref _cached.AccessKnown(entityId, writeVersion);

    internal ulong WriteVersion(ref ulong version) => _cached.WriteVersion(ref version);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(Entity entity)
    {
        if (ComponentType<T>.IsSparseType) return _sparse!.Has(entity);
        return ComponentType<T>.IsSharedType ? _cached.World.Has<T>(entity) : _cached.Has(entity);
    }

    public void Dispose() => _cached.Dispose();
}

public ref struct WriteLookup<T> where T : struct
{
    private CachedWriteLookup<T> _write;
    private readonly World? _sparseWorld;
    private readonly SparseStore<T>? _sparse;
    private ulong _sparseVersion;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal WriteLookup(World world)
    {
        if (ComponentType<T>.IsSparseType)
        {
            if (ComponentType<T>.IsControlledType) throw new InvalidOperationException("Use the typed write view of this component.");
            world.BeginValueRead<T>(true, WorldValueKind.Sparse);
            _sparseWorld = world;
            _sparse = world.Sparse<T>();
        }
        else _write = world.OpenCachedWrite<T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get(Entity entity)
    {
        if (ComponentType<T>.IsSparseType)
            return ref _sparse!.Get(entity, ComponentType<T>.TracksWritesType ? _sparseWorld!.WriteVersion(ref _sparseVersion) : 0);
        return ref _write.Get(entity);
    }

    /// <summary>Gets an identity whose component presence is established for this scope.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetKnown(Entity entity)
    {
        if (ComponentType<T>.IsSparseType) return ref Get(entity);
        return ref _write.GetKnown(entity);
    }

    public void SetKnown(ReadOnlySpan<Entity> entities, in T value)
    {
        if (ComponentType<T>.IsSparseType) { foreach (Entity entity in entities) Get(entity) = value; return; }
        _write.SetKnown(entities, value);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(Entity entity, in T value) => Get(entity) = value;

    public bool Has(Entity entity) => ComponentType<T>.IsSparseType ? _sparse!.Has(entity) : _write.Has(entity);
    public void Dispose()
    {
        if (ComponentType<T>.IsSparseType) _sparseWorld!.EndValueRead();
        else _write.Dispose();
    }
}

public sealed partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadLookup<T> OpenRead<T>() where T : struct => new(this);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WriteLookup<T> OpenWrite<T>() where T : struct => new(this);
}
