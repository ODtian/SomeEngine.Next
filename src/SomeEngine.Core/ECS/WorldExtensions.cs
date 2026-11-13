using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.Core.ECS;

public static class WorldExtensions
{
    public static bool TryRead<T>(this World world, EntityId entity, out T component)
        where T : struct, global::SomeEngine.ECS.IComponent
    {
        ArgumentNullException.ThrowIfNull(world);
        if (entity != EntityId.Null && world.IsAlive(entity) && world.Has<T>(entity))
        {
            component = world.ReadValue<T>(entity);
            return true;
        }

        component = default;
        return false;
    }

    public static void AddOrSet<T>(this World world, EntityId entity, in T component)
        where T : struct, global::SomeEngine.ECS.IComponent
    {
        ArgumentNullException.ThrowIfNull(world);
        if (world.Has<T>(entity))
            world.Set(entity, component);
        else
            world.Add(entity, component);
    }

    public static void AddTagIfMissing<T>(this World world, EntityId entity)
        where T : struct, ITag
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Has<T>(entity))
            world.Add(entity, default(T));
    }

    public static Query AllEntities(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Query(new QueryDescription());
    }

    public static void CollectEntities(this World world, Query query, List<EntityId> destination)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        using QueryScope scope = query.OpenRead();
        foreach (QueryChunk chunk in scope)
            destination.AddRange(chunk.Entities);
    }
}
