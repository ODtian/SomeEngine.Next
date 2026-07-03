using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Hooks;

public readonly struct DeferredWorld
{
    private readonly World _world;

    internal DeferredWorld(World world)
    {
        _world = world;
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

    public ReadOnlySpan<Entity> GetByIndex<TComponent, TKey>(TKey key)
        where TComponent : struct, IIndexedComponent<TKey>
        where TKey : notnull, IEquatable<TKey>
    {
        return _world.GetByIndex<TComponent, TKey>(key);
    }

    public CommandBuffer Commands()
    {
        return _world.Commands();
    }
}

