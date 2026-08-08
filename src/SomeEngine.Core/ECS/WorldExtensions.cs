using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.Core.ECS;

public static class WorldExtensions
{
    public static bool TryRead<T>(this World world, EntityId entity, out T component)
        where T : struct, global::SomeEngine.ECS.IComponent
    {
        ArgumentNullException.ThrowIfNull(world);
        if (entity != EntityId.Null && world.IsAlive(entity) && world.Has<T>(entity))
        {
            component = world.Read<T>(entity);
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
            world.Replace(entity, component);
        else
            world.Add(entity, component);
    }

    public static void AddTagIfMissing<T>(this World world, EntityId entity)
        where T : struct, ITag
    {
        ArgumentNullException.ThrowIfNull(world);
        if (!world.Has<T>(entity))
            world.AddTag<T>(entity);
    }

    public static QueryHandle AllEntities(this World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        return world.Query(QueryDefinition.Empty);
    }

    public static void CollectEntities(this World world, QueryHandle query, List<EntityId> destination)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(destination);

        destination.Clear();
        world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                ReadOnlySpan<EntityId> entities = chunk.Entities;
                for (int i = 0; i < entities.Length; i++)
                    destination.Add(entities[i]);
            }
        });
    }
}

