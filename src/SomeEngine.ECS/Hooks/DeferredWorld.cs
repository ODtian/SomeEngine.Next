using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Hooks;

public readonly struct DeferredWorld
{
    private readonly World _world;
    private readonly HookCommandToken _commandToken;

    internal DeferredWorld(World world, HookCommandToken commandToken)
    {
        _world = world;
        _commandToken = commandToken;
    }

    public T Read<T>(Entity entity)
        where T : struct, IComponent
    {
        return _world.Read<T>(entity);
    }

    public bool TryRead<T>(Entity entity, out T value)
        where T : struct, IComponent
    {
        if (!_world.Has<T>(entity))
        {
            value = default;
            return false;
        }

        value = _world.Read<T>(entity);
        return true;
    }

    public bool Has<T>(Entity entity)
        where T : struct
    {
        return _world.Has<T>(entity);
    }

    public bool IsAlive(Entity entity)
    {
        return _world.IsAlive(entity);
    }

    /// <summary>
    /// Returns the immutable index-bucket generation visible to this hook.
    /// The span remains stable if a later deferred command changes the index.
    /// </summary>
    public ReadOnlySpan<Entity> GetByIndex<TComponent, TKey>(TKey key)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        return _world.GetByIndex<TComponent, TKey>(key);
    }

    public DeferredCommandWriter Commands()
    {
        return _world.CommandsFromHook(_commandToken);
    }
}

internal readonly struct HookCommandToken
{
    internal HookCommandToken(int threadId, long epoch)
    {
        ThreadId = threadId;
        Epoch = epoch;
    }

    internal int ThreadId { get; }

    internal long Epoch { get; }
}

/// <summary>
/// A record-only command capability scoped to one immediate component hook invocation.
/// It deliberately exposes no playback, count, clear, or disposal surface.
/// </summary>
public readonly ref struct DeferredCommandWriter
{
    private readonly CommandBuffer _buffer;
    private readonly HookCommandToken _token;

    internal DeferredCommandWriter(CommandBuffer buffer, HookCommandToken token)
    {
        _buffer = buffer;
        _token = token;
    }

    public DeferredEntity CreateEntity() => _buffer.CreateEntity(_token);

    public void DestroyEntity(Entity entity) => _buffer.DestroyEntity(_token, entity);

    public void DestroyEntity(DeferredEntity entity) => _buffer.DestroyEntity(_token, entity);

    public void Add<T>(Entity entity, in T value)
        where T : struct, IComponent =>
        _buffer.Add(_token, entity, in value);

    public void Add<T>(DeferredEntity entity, in T value)
        where T : struct, IComponent =>
        _buffer.Add(_token, entity, in value);

    public void Replace<T>(Entity entity, in T value)
        where T : struct, IComponent =>
        _buffer.Replace(_token, entity, in value);

    public void Replace<T>(DeferredEntity entity, in T value)
        where T : struct, IComponent =>
        _buffer.Replace(_token, entity, in value);

    public void Remove<T>(Entity entity)
        where T : struct, IComponent =>
        _buffer.Remove<T>(_token, entity);

    public void Remove<T>(DeferredEntity entity)
        where T : struct, IComponent =>
        _buffer.Remove<T>(_token, entity);

    public void AddTag<T>(Entity entity)
        where T : struct, ITag =>
        _buffer.AddTag<T>(_token, entity);

    public void AddTag<T>(DeferredEntity entity)
        where T : struct, ITag =>
        _buffer.AddTag<T>(_token, entity);

    public void RemoveTag<T>(Entity entity)
        where T : struct, ITag =>
        _buffer.RemoveTag<T>(_token, entity);

    public void RemoveTag<T>(DeferredEntity entity)
        where T : struct, ITag =>
        _buffer.RemoveTag<T>(_token, entity);

    public RelationCommandWriter<T> Relations<T>()
        where T : struct, IComponent =>
        _buffer.Relations<T>(_token);

    public HierarchyCommandWriter<TDomain> Hierarchy<TDomain>()
        where TDomain : IHierarchyDomain =>
        _buffer.Hierarchy<TDomain>(_token);

    public HierarchyCommandWriter<DefaultHierarchyDomain> Hierarchy() =>
        _buffer.Hierarchy<DefaultHierarchyDomain>(_token);
}

