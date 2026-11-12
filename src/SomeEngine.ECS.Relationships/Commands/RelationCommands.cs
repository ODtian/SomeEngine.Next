using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Commands;

public readonly struct DeferredRelationEdge<T> where T : struct
{
    private readonly DeferredEntity _entity;
    internal DeferredRelationEdge(DeferredEntity entity) => _entity = entity;
    public RelationEdge<T> Resolve() => new(_entity.Resolve());
    public bool TryResolve(out RelationEdge<T> edge)
    {
        bool completed = _entity.TryResolve(out Entity entity);
        edge = new(entity);
        return completed;
    }
    public static implicit operator CommandEntity(DeferredRelationEdge<T> edge) => new(edge._entity);
}

public static class RelationCommandsExtensions
{
    public static DeferredRelationEdge<T> CreateRelation<T>(this CommandBuffer commands, CommandEntity source,
        CommandEntity target, in T value, bool deferred = false, int outgoingIndex = -1, int incomingIndex = -1)
        where T : struct
    {
        DeferredEntity result = default;
        Span<DeferredEntity> created = new(ref result);
        commands.EditEndpoints<T>([EndpointCommandEdit.Create(source, target)], created, deferred);
        commands.Add(created[0], value);
        Place<T>(commands, source, target, created[0], deferred, outgoingIndex, incomingIndex);
        return new(created[0]);
    }

    public static void RetargetRelation<T>(this CommandBuffer commands, RelationEdge<T> edge,
        CommandEntity source, CommandEntity target, bool deferred = false, int outgoingIndex = -1,
        int incomingIndex = -1) where T : struct
        => commands.RetargetRelation<T>(new CommandEntity(edge.Entity), source, target, deferred, outgoingIndex, incomingIndex);

    public static void RetargetRelation<T>(this CommandBuffer commands, CommandEntity edge,
        CommandEntity source, CommandEntity target, bool deferred = false, int outgoingIndex = -1,
        int incomingIndex = -1) where T : struct
    {
        commands.EditEndpoints<T>([new(edge, source, target)], deferred);
        Place<T>(commands, source, target, edge, deferred, outgoingIndex, incomingIndex);
    }

    private static void Place<T>(CommandBuffer commands, CommandEntity source, CommandEntity target,
        CommandEntity edge, bool deferred, int outgoingIndex, int incomingIndex) where T : struct
    {
        if (outgoingIndex >= 0) commands.ReorderRelation<T>(source, RelationAdjacencyRole.Outgoing, edge, outgoingIndex, deferred);
        if (incomingIndex >= 0) commands.ReorderRelation<T>(target, RelationAdjacencyRole.Incoming, edge, incomingIndex, deferred);
    }

    public static void DestroyRelation<T>(this CommandBuffer commands, CommandEntity edge) where T : struct
        => commands.EditEndpoints<T>([EndpointCommandEdit.Destroy(edge)]);
    public static void DestroyRelation<T>(this CommandBuffer commands, RelationEdge<T> edge) where T : struct
        => commands.DestroyRelation<T>(new CommandEntity(edge.Entity));

    public static void SetRelationOrder<T>(this CommandBuffer commands, CommandEntity endpoint,
        RelationAdjacencyRole role, bool ordered) where T : struct
        => Record<T>(commands, RelationCommandKind.Order, endpoint, default, role, ordered ? 1 : 0);

    public static void ReorderRelation<T>(this CommandBuffer commands, CommandEntity endpoint,
        RelationAdjacencyRole role, CommandEntity edge, int insertAt, bool deferred = false) where T : struct
        => Record<T>(commands, deferred ? RelationCommandKind.DeferredReorder : RelationCommandKind.Reorder,
            endpoint, edge, role, insertAt);
    public static void ReorderRelation<T>(this CommandBuffer commands, CommandEntity endpoint,
        RelationAdjacencyRole role, RelationEdge<T> edge, int insertAt, bool deferred = false) where T : struct
        => commands.ReorderRelation<T>(endpoint, role, new CommandEntity(edge.Entity), insertAt, deferred);

    public static void DestroyRelationsBetween<T>(this CommandBuffer commands, CommandEntity source,
        CommandEntity target) where T : struct
        => Record<T>(commands, RelationCommandKind.DestroyBetween, source, target, default, 0);

    public static void DestroyRelationsAt<T>(this CommandBuffer commands, CommandEntity endpoint,
        RelationAdjacencyRole role) where T : struct
        => Record<T>(commands, RelationCommandKind.DestroyAt, endpoint, default, role, 0);

    private static void Record<T>(CommandBuffer commands, RelationCommandKind kind, CommandEntity endpoint,
        CommandEntity other, RelationAdjacencyRole role, int value) where T : struct
    {
        commands.RequireRecording();
        var bucket = commands.Bucket<RelationCommands<T>>(out int type);
        if (bucket.Count == 0) commands.Use(type);
        int index = bucket.Add(kind, commands.Encode(endpoint), commands.Encode(other), role, value);
        commands.Append(CommandKind.Composite, default, type, index);
    }
}

internal enum RelationCommandKind : byte { Order, Reorder, DeferredReorder, DestroyBetween, DestroyAt }

internal sealed class RelationCommands<T> : CommandBucket where T : struct
{
    private readonly record struct Row(RelationCommandKind Kind, Entity Endpoint, Entity Other,
        RelationAdjacencyRole Role, int Value);
    private Row[] _rows = [];
    private readonly HashSet<Entity> _destroyed = [];
    internal int Count;
    public RelationCommands() { }
    internal int Add(RelationCommandKind kind, Entity endpoint, Entity other, RelationAdjacencyRole role, int value)
    {
        if (Count == _rows.Length) Array.Resize(ref _rows, Math.Max(8, Count * 2));
        _rows[Count] = new(kind, endpoint, other, role, value);
        return Count++;
    }
    internal override Entity Execute(World world, in CommandHeader command, CommandResults? results)
    {
        Row row = _rows[command.DataIndex];
        Entity Decode(Entity entity) => entity.Id >= 0 ? entity : results!.Decode(entity);
        Entity endpoint = Decode(row.Endpoint), other = Decode(row.Other);
        switch (row.Kind)
        {
            case RelationCommandKind.Order: Relations<T>.SetOrdered(world, endpoint, row.Role, row.Value != 0); break;
            case RelationCommandKind.Reorder: Relations<T>.Reorder(world, endpoint, row.Role, new(other), row.Value); break;
            case RelationCommandKind.DeferredReorder:
                Relations<T>.Data(world).ReorderDeferred(endpoint, row.Role, other, row.Value); break;
            default: Destroy(world, row, endpoint, other); break;
        }
        return default;
    }
    private void Destroy(World world, in Row row, Entity endpoint, Entity other)
    {
        bool directed = RelationSchema.For<T>().Direction == RelationDirection.Directed;
        _destroyed.Clear();
        RelationAdjacencyRole role = row.Kind == RelationCommandKind.DestroyBetween
            ? directed ? RelationAdjacencyRole.Outgoing : RelationAdjacencyRole.Incident : row.Role;
        Relations<T>.Data(world).CollectCurrentEdges(endpoint, role, _destroyed);
        foreach (Entity entity in _destroyed)
        {
            RelationEndpoints<T> value = world.Read<RelationEndpoints<T>>(entity);
            if (row.Kind != RelationCommandKind.DestroyBetween || value.Target == other
                || !directed && value.Source == other) Relations<T>.DestroyEdge(world, new(entity));
        }
        _destroyed.Clear();
    }
    internal override void Clear(World world) { Count = 0; _destroyed.Clear(); }
}
