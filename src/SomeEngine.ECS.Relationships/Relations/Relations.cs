using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

public static partial class Relations<T> where T : struct
{
    internal static RelationData<T> Data(World world)
    {
        if (world.FindLifecycle<RelationData<T>>() is { } found) return found;
        world.RequireStructure();
        var data = new RelationData<T>(world);
        world.AddLifecycle(data);
        data.Initialize();
        return data;
    }

    public static RelationEdge<T> CreateEdge(World world, Entity source, Entity target, in T payload, bool deferred = false)
    {
        if (!deferred) return new(world.Create(new RelationEndpoints<T>(source, target), payload));
        using WorldAccess access = world.OpenStructure();
        RelationEdge<T> edge = default;
        Edit(world, [EndpointEdit.Create(source, target)], new Span<RelationEdge<T>>(ref edge), true);
        world.Add(edge.Entity, payload);
        return edge;
    }
    public static void Retarget(World world, RelationEdge<T> edge, Entity source, Entity target)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).Retarget(edge.Entity, new(source, target), false);
    }
    public static void RetargetDeferred(World world, RelationEdge<T> edge, Entity source, Entity target)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).Retarget(edge.Entity, new(source, target), true);
    }
    public static void Retarget(World world, ReadOnlySpan<RelationRetarget<T>> edits, bool deferred = false)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).Retarget(edits, deferred);
    }
    public static void DestroyEdge(World world, RelationEdge<T> edge)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).DestroyEdge(edge.Entity);
    }
    public static void Maintain(World world)
    {
        using WorldAccess access = world.OpenStructure();
        world.FindLifecycle<RelationData<T>>()?.Maintain();
    }
    public static void SetOrdered(World world, Entity endpoint, RelationAdjacencyRole role, bool ordered, ReadOnlySpan<Entity> order = default)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).SetOrdered(endpoint, role, ordered, order);
    }
    public static bool IsOrdered(World world, Entity endpoint, RelationAdjacencyRole role)
    {
        using WorldAccess access = world.OpenStructure(false);
        return world.FindLifecycle<RelationData<T>>()?.IsOrdered(endpoint, role) ?? false;
    }
    public static void Reorder(World world, Entity endpoint, RelationAdjacencyRole role, RelationEdge<T> edge, int insertAt)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).Reorder(endpoint, role, edge.Entity, insertAt);
    }
    public static void ReorderDeferred(World world, Entity endpoint, RelationAdjacencyRole role, RelationEdge<T> edge, int insertAt)
    {
        using WorldAccess access = world.OpenStructure();
        Data(world).ReorderDeferred(endpoint, role, edge.Entity, insertAt);
    }
    public static RelationRead Outgoing(World world, Entity source)
    {
        if (RelationSchema.For<T>().Direction != RelationDirection.Directed)
            throw new InvalidOperationException("Use Incident for an undirected relation.");
        world.BeginStructureRead();
        var data = world.FindLifecycle<RelationData<T>>();
        return new(world, data is null ? [] : data.ReadEntries(source, RelationAdjacencyRole.Outgoing));
    }
    public static RelationRead Incoming(World world, Entity target)
    {
        if (RelationSchema.For<T>().Direction != RelationDirection.Directed)
            throw new InvalidOperationException("Use Incident for an undirected relation.");
        world.BeginStructureRead();
        var data = world.FindLifecycle<RelationData<T>>();
        return new(world, data is null ? [] : data.ReadEntries(target, RelationAdjacencyRole.Incoming));
    }
    public static IncidentRelationsRead Incident(World world, Entity entity)
    {
        world.BeginStructureRead();
        var data = world.FindLifecycle<RelationData<T>>();
        if (data is null) return new(world, [], []);
        if (RelationSchema.For<T>().Direction == RelationDirection.Directed)
            return new(world, data.ReadEntries(entity, RelationAdjacencyRole.Outgoing), data.ReadEntries(entity, RelationAdjacencyRole.Incoming));
        return new(world, data.ReadEntries(entity, RelationAdjacencyRole.Incident), []);
    }
    public static RelationEdge<T> FindPair(World world, Entity source, Entity target)
    {
        using WorldAccess access = world.OpenStructure(false);
        return new(world.FindLifecycle<RelationData<T>>()?.FindPair(source, target) ?? Entity.Null);
    }
}

public static class RelationWorldExtensions
{
    public static RelationEdge<T> CreateRelation<T>(this World world, Entity source, Entity target, in T payload,
        bool deferred = false) where T : struct
        => Relations<T>.CreateEdge(world, source, target, payload, deferred);
    public static void DestroyRelation<T>(this World world, RelationEdge<T> edge) where T : struct => Relations<T>.DestroyEdge(world, edge);
    public static void RetargetRelationImmediate<T>(this World world, RelationEdge<T> edge, Entity source, Entity target) where T : struct
        => Relations<T>.Retarget(world, edge, source, target);
    public static void RetargetRelationDeferred<T>(this World world, RelationEdge<T> edge, Entity source, Entity target) where T : struct
        => Relations<T>.RetargetDeferred(world, edge, source, target);
    public static void RetargetRelations<T>(this World world, ReadOnlySpan<RelationRetarget<T>> edits, bool deferred = false) where T : struct
        => Relations<T>.Retarget(world, edits, deferred);
    public static RelationRead GetOutgoingRelations<T>(this World world, Entity source) where T : struct => Relations<T>.Outgoing(world, source);
    public static RelationRead GetIncomingRelations<T>(this World world, Entity target) where T : struct => Relations<T>.Incoming(world, target);
    public static IncidentRelationsRead GetIncidentRelations<T>(this World world, Entity entity) where T : struct => Relations<T>.Incident(world, entity);
    public static void MaintainRelations<T>(this World world) where T : struct => Relations<T>.Maintain(world);
    public static void SetRelationAdjacencyOrder<T>(this World world, Entity endpoint, RelationAdjacencyRole role, RelationAdjacencyOrderPolicy policy) where T : struct
        => Relations<T>.SetOrdered(world, endpoint, role, policy == RelationAdjacencyOrderPolicy.Ordered);
    public static void ReorderRelationAdjacency<T>(this World world, Entity endpoint, RelationAdjacencyRole role, RelationEdge<T> edge, int insertIndex) where T : struct
        => Relations<T>.Reorder(world, endpoint, role, edge, insertIndex);
}
