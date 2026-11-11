using System.Runtime.CompilerServices;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Storage;

namespace SomeEngine.ECS;

public ref struct ReadLookup<T> where T : struct
{
    private readonly T[]? _sharedValues;
    private CachedReadLookup<T> _cached;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ReadLookup(World world, bool write = false)
    {
        if (ComponentType<T>.IsSharedType)
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
        if (ComponentType<T>.IsSharedType) return ref _cached.World.ReadSharedValue(entity, _sharedValues!);
        return ref Access(entity);
    }

    /// <summary>Reads an identity whose component presence is established for this scope.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref readonly T ReadKnown(Entity entity)
    {
        if (ComponentType<T>.IsSharedType) return ref _cached.World.ReadSharedValueKnown(entity, _sharedValues!);
        return ref _cached.AccessKnown(entity.Id);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T Access(Entity entity, ulong writeVersion = 0)
    {
        return ref _cached.Access(entity, writeVersion);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref T AccessKnown(int entityId, ulong writeVersion = 0) => ref _cached.AccessKnown(entityId, writeVersion);

    internal ulong WriteVersion(ref ulong version) => _cached.WriteVersion(ref version);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Has(Entity entity)
    {
        return ComponentType<T>.IsSharedType ? _cached.World.Has<T>(entity) : _cached.Has(entity);
    }

    public void Dispose() => _cached.Dispose();
}

public ref struct WriteLookup<T> where T : struct
{
    private CachedWriteLookup<T> _write;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal WriteLookup(World world)
    {
        _write = world.OpenCachedWrite<T>();
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T Get(Entity entity)
        => ref _write.Get(entity);

    /// <summary>Gets an identity whose component presence is established for this scope.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ref T GetKnown(Entity entity)
        => ref _write.GetKnown(entity);

    public void SetKnown(ReadOnlySpan<Entity> entities, in T value) => _write.SetKnown(entities, value);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void Set(Entity entity, in T value) => Get(entity) = value;

    public bool Has(Entity entity) => _write.Has(entity);
    public void Dispose() => _write.Dispose();
}

public sealed partial class World
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ReadLookup<T> OpenRead<T>() where T : struct => new(this);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public WriteLookup<T> OpenWrite<T>() where T : struct => new(this);
}
