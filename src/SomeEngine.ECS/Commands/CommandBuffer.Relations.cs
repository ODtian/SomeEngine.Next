using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Commands;

public sealed partial class CommandBuffer
{
    public RelationCommandWriter<T> Relations<T>()
        where T : struct, IComponent
    {
        ValidateRecordAccess();
        return new RelationCommandWriter<T>(this);
    }

    internal RelationCommandWriter<T> Relations<T>(HookCommandToken token)
        where T : struct, IComponent
    {
        ValidateRecordAccess(token);
        return new RelationCommandWriter<T>(this, token);
    }
}

/// <summary>
/// Command-local identity for an edge that will be allocated by relation Create during playback.
/// It is deliberately not implicitly convertible to RelationEdge because no live edge exists yet.
/// </summary>
public readonly struct DeferredRelationEdge<T> : IEquatable<DeferredRelationEdge<T>>
    where T : struct, IComponent
{
    private readonly DeferredRelationEdgeCell<T>? _cell;

    internal DeferredRelationEdge(DeferredRelationEdgeCell<T> cell)
    {
        _cell = cell;
    }

    public bool IsResolved => _cell?.IsResolved == true;

    public RelationEdge<T> Resolve()
    {
        if (_cell is null)
            throw new InvalidOperationException("The deferred relation edge handle is default/uninitialized.");
        return _cell.Resolve();
    }

    public bool TryResolve(out RelationEdge<T> edge)
    {
        if (_cell is not null && _cell.TryResolve(out edge))
            return true;

        edge = default;
        return false;
    }

    public bool Equals(DeferredRelationEdge<T> other) =>
        ReferenceEquals(_cell, other._cell);

    public override bool Equals(object? obj) =>
        obj is DeferredRelationEdge<T> other && Equals(other);

    public override int GetHashCode() =>
        _cell is null ? 0 : System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_cell);

    public static bool operator ==(
        DeferredRelationEdge<T> left,
        DeferredRelationEdge<T> right) =>
        left.Equals(right);

    public static bool operator !=(
        DeferredRelationEdge<T> left,
        DeferredRelationEdge<T> right) =>
        !left.Equals(right);

    internal RelationCommandEdge<T> AsCommandEdge(CommandBuffer owner)
    {
        if (_cell is null)
            throw new InvalidOperationException("The deferred relation edge handle is default/uninitialized.");
        _cell.RequireOwner(owner);
        return new RelationCommandEdge<T>(_cell);
    }
}

/// <summary>
/// Typed relation command recorder. Every single-edge operation uses edge identity; endpoint pairs
/// occur only on Create/Retarget and explicitly named bulk-destroy operations.
/// </summary>
public ref struct RelationCommandWriter<T>
    where T : struct, IComponent
{
    private readonly CommandBuffer _buffer;
    private readonly HookCommandToken _token;
    private readonly bool _hasHookToken;

    internal RelationCommandWriter(CommandBuffer buffer)
    {
        _buffer = buffer;
        _token = default;
        _hasHookToken = false;
    }

    internal RelationCommandWriter(CommandBuffer buffer, HookCommandToken token)
    {
        _buffer = buffer;
        _token = token;
        _hasHookToken = true;
    }

    public DeferredRelationEdge<T> Create(
        Entity first,
        Entity second,
        in T payload,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordCreate(new CommandEntity(first), new CommandEntity(second), in payload, timing);
    }

    public DeferredRelationEdge<T> Create(
        DeferredEntity first,
        Entity second,
        in T payload,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordCreate(first.AsCommandEntity(_buffer), new CommandEntity(second), in payload, timing);
    }

    public DeferredRelationEdge<T> Create(
        Entity first,
        DeferredEntity second,
        in T payload,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordCreate(new CommandEntity(first), second.AsCommandEntity(_buffer), in payload, timing);
    }

    public DeferredRelationEdge<T> Create(
        DeferredEntity first,
        DeferredEntity second,
        in T payload,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordCreate(first.AsCommandEntity(_buffer), second.AsCommandEntity(_buffer), in payload, timing);
    }

    public DeferredRelationEdge<T> Create(
        Entity source,
        Entity target,
        in T payload,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordDirectedCreate(
                    new CommandEntity(source),
                    new CommandEntity(target),
                    in payload,
                    placement,
                    timing);
    }

    public DeferredRelationEdge<T> Create(
        DeferredEntity source,
        Entity target,
        in T payload,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordDirectedCreate(
                    source.AsCommandEntity(_buffer),
                    new CommandEntity(target),
                    in payload,
                    placement,
                    timing);
    }

    public DeferredRelationEdge<T> Create(
        Entity source,
        DeferredEntity target,
        in T payload,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordDirectedCreate(
                    new CommandEntity(source),
                    target.AsCommandEntity(_buffer),
                    in payload,
                    placement,
                    timing);
    }

    public DeferredRelationEdge<T> Create(
        DeferredEntity source,
        DeferredEntity target,
        in T payload,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordDirectedCreate(
                    source.AsCommandEntity(_buffer),
                    target.AsCommandEntity(_buffer),
                    in payload,
                    placement,
                    timing);
    }

    public DeferredRelationEdge<T> Create(
        Entity endpointA,
        Entity endpointB,
        in T payload,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordUndirectedCreate(
                    new CommandEntity(endpointA),
                    new CommandEntity(endpointB),
                    in payload,
                    placement,
                    timing);
    }

    public DeferredRelationEdge<T> Create(
        DeferredEntity endpointA,
        Entity endpointB,
        in T payload,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordUndirectedCreate(
                    endpointA.AsCommandEntity(_buffer),
                    new CommandEntity(endpointB),
                    in payload,
                    placement,
                    timing);
    }

    public DeferredRelationEdge<T> Create(
        Entity endpointA,
        DeferredEntity endpointB,
        in T payload,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordUndirectedCreate(
                    new CommandEntity(endpointA),
                    endpointB.AsCommandEntity(_buffer),
                    in payload,
                    placement,
                    timing);
    }

    public DeferredRelationEdge<T> Create(
        DeferredEntity endpointA,
        DeferredEntity endpointB,
        in T payload,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        return RecordUndirectedCreate(
                    endpointA.AsCommandEntity(_buffer),
                    endpointB.AsCommandEntity(_buffer),
                    in payload,
                    placement,
                    timing);
    }

    public void Destroy(RelationEdge<T> edge)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDestroy(new RelationCommandEdge<T>(edge));
    }

    public void Destroy(DeferredRelationEdge<T> edge)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDestroy(edge.AsCommandEdge(_buffer));
    }

    public void Retarget(
        RelationEdge<T> edge,
        Entity first,
        Entity second,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordRetarget(
                    new RelationCommandEdge<T>(edge),
                    new CommandEntity(first),
                    new CommandEntity(second),
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        DeferredEntity first,
        Entity second,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordRetarget(
                    new RelationCommandEdge<T>(edge),
                    first.AsCommandEntity(_buffer),
                    new CommandEntity(second),
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        Entity first,
        DeferredEntity second,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordRetarget(
                    new RelationCommandEdge<T>(edge),
                    new CommandEntity(first),
                    second.AsCommandEntity(_buffer),
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        DeferredEntity first,
        DeferredEntity second,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordRetarget(
                    new RelationCommandEdge<T>(edge),
                    first.AsCommandEntity(_buffer),
                    second.AsCommandEntity(_buffer),
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        Entity first,
        Entity second,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordRetarget(
                    edge.AsCommandEdge(_buffer),
                    new CommandEntity(first),
                    new CommandEntity(second),
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        DeferredEntity first,
        Entity second,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordRetarget(
                    edge.AsCommandEdge(_buffer),
                    first.AsCommandEntity(_buffer),
                    new CommandEntity(second),
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        Entity first,
        DeferredEntity second,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordRetarget(
                    edge.AsCommandEdge(_buffer),
                    new CommandEntity(first),
                    second.AsCommandEntity(_buffer),
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        DeferredEntity first,
        DeferredEntity second,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordRetarget(
                    edge.AsCommandEdge(_buffer),
                    first.AsCommandEntity(_buffer),
                    second.AsCommandEntity(_buffer),
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        Entity source,
        Entity target,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDirectedRetarget(
                    new RelationCommandEdge<T>(edge),
                    new CommandEntity(source),
                    new CommandEntity(target),
                    placement,
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        DeferredEntity source,
        Entity target,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDirectedRetarget(
                    new RelationCommandEdge<T>(edge),
                    source.AsCommandEntity(_buffer),
                    new CommandEntity(target),
                    placement,
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        Entity source,
        DeferredEntity target,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDirectedRetarget(
                    new RelationCommandEdge<T>(edge),
                    new CommandEntity(source),
                    target.AsCommandEntity(_buffer),
                    placement,
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        DeferredEntity source,
        DeferredEntity target,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDirectedRetarget(
                    new RelationCommandEdge<T>(edge),
                    source.AsCommandEntity(_buffer),
                    target.AsCommandEntity(_buffer),
                    placement,
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        Entity source,
        Entity target,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDirectedRetarget(
                    edge.AsCommandEdge(_buffer),
                    new CommandEntity(source),
                    new CommandEntity(target),
                    placement,
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        DeferredEntity source,
        Entity target,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDirectedRetarget(
                    edge.AsCommandEdge(_buffer),
                    source.AsCommandEntity(_buffer),
                    new CommandEntity(target),
                    placement,
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        Entity source,
        DeferredEntity target,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDirectedRetarget(
                    edge.AsCommandEdge(_buffer),
                    new CommandEntity(source),
                    target.AsCommandEntity(_buffer),
                    placement,
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        DeferredEntity source,
        DeferredEntity target,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordDirectedRetarget(
                    edge.AsCommandEdge(_buffer),
                    source.AsCommandEntity(_buffer),
                    target.AsCommandEntity(_buffer),
                    placement,
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        Entity endpointA,
        Entity endpointB,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordUndirectedRetarget(
                    new RelationCommandEdge<T>(edge),
                    new CommandEntity(endpointA),
                    new CommandEntity(endpointB),
                    placement,
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        DeferredEntity endpointA,
        Entity endpointB,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordUndirectedRetarget(
                    new RelationCommandEdge<T>(edge),
                    endpointA.AsCommandEntity(_buffer),
                    new CommandEntity(endpointB),
                    placement,
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        Entity endpointA,
        DeferredEntity endpointB,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordUndirectedRetarget(
                    new RelationCommandEdge<T>(edge),
                    new CommandEntity(endpointA),
                    endpointB.AsCommandEntity(_buffer),
                    placement,
                    timing);
    }

    public void Retarget(
        RelationEdge<T> edge,
        DeferredEntity endpointA,
        DeferredEntity endpointB,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordUndirectedRetarget(
                    new RelationCommandEdge<T>(edge),
                    endpointA.AsCommandEntity(_buffer),
                    endpointB.AsCommandEntity(_buffer),
                    placement,
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        Entity endpointA,
        Entity endpointB,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordUndirectedRetarget(
                    edge.AsCommandEdge(_buffer),
                    new CommandEntity(endpointA),
                    new CommandEntity(endpointB),
                    placement,
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        DeferredEntity endpointA,
        Entity endpointB,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordUndirectedRetarget(
                    edge.AsCommandEdge(_buffer),
                    endpointA.AsCommandEntity(_buffer),
                    new CommandEntity(endpointB),
                    placement,
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        Entity endpointA,
        DeferredEntity endpointB,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordUndirectedRetarget(
                    edge.AsCommandEdge(_buffer),
                    new CommandEntity(endpointA),
                    endpointB.AsCommandEntity(_buffer),
                    placement,
                    timing);
    }

    public void Retarget(
        DeferredRelationEdge<T> edge,
        DeferredEntity endpointA,
        DeferredEntity endpointB,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordUndirectedRetarget(
                    edge.AsCommandEdge(_buffer),
                    endpointA.AsCommandEntity(_buffer),
                    endpointB.AsCommandEntity(_buffer),
                    placement,
                    timing);
    }

    public void SetAdjacencyOrder(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationAdjacencyOrderPolicy policy)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new SetRelationAdjacencyOrderCommand<T>(new CommandEntity(endpoint), role, policy));
    }

    public void SetAdjacencyOrder(
        DeferredEntity endpoint,
        RelationAdjacencyRole role,
        RelationAdjacencyOrderPolicy policy)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new SetRelationAdjacencyOrderCommand<T>(
                endpoint.AsCommandEntity(_buffer),
                role,
                policy));
    }

    public void Reorder(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationEdge<T> edge,
        int insertIndex)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordReorder(
                    new CommandEntity(endpoint),
                    role,
                    new RelationCommandEdge<T>(edge),
                    insertIndex);
    }

    public void Reorder(
        DeferredEntity endpoint,
        RelationAdjacencyRole role,
        RelationEdge<T> edge,
        int insertIndex)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordReorder(
                    endpoint.AsCommandEntity(_buffer),
                    role,
                    new RelationCommandEdge<T>(edge),
                    insertIndex);
    }

    public void Reorder(
        Entity endpoint,
        RelationAdjacencyRole role,
        DeferredRelationEdge<T> edge,
        int insertIndex)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordReorder(
                    new CommandEntity(endpoint),
                    role,
                    edge.AsCommandEdge(_buffer),
                    insertIndex);
    }

    public void Reorder(
        DeferredEntity endpoint,
        RelationAdjacencyRole role,
        DeferredRelationEdge<T> edge,
        int insertIndex)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        RecordReorder(
                    endpoint.AsCommandEntity(_buffer),
                    role,
                    edge.AsCommandEdge(_buffer),
                    insertIndex);
    }

    public void DestroyAllBetween(Entity first, Entity second)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Between,
                new CommandEntity(first),
                new CommandEntity(second)));
    }

    public void DestroyAllBetween(DeferredEntity first, Entity second)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Between,
                first.AsCommandEntity(_buffer),
                new CommandEntity(second)));
    }

    public void DestroyAllBetween(Entity first, DeferredEntity second)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Between,
                new CommandEntity(first),
                second.AsCommandEntity(_buffer)));
    }

    public void DestroyAllBetween(DeferredEntity first, DeferredEntity second)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Between,
                first.AsCommandEntity(_buffer),
                second.AsCommandEntity(_buffer)));
    }

    public void DestroyAllOutgoing(Entity source)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Outgoing,
                new CommandEntity(source),
                new CommandEntity(Entity.Null)));
    }

    public void DestroyAllOutgoing(DeferredEntity source)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Outgoing,
                source.AsCommandEntity(_buffer),
                new CommandEntity(Entity.Null)));
    }

    public void DestroyAllIncoming(Entity target)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Incoming,
                new CommandEntity(target),
                new CommandEntity(Entity.Null)));
    }

    public void DestroyAllIncoming(DeferredEntity target)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Incoming,
                target.AsCommandEntity(_buffer),
                new CommandEntity(Entity.Null)));
    }

    public void DestroyAllIncident(Entity endpoint)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Incident,
                new CommandEntity(endpoint),
                new CommandEntity(Entity.Null)));
    }

    public void DestroyAllIncident(DeferredEntity endpoint)
    {
        using CommandBuffer.RecordAccessScope access = EnterOperation();
        Record(
            new BulkDestroyRelationCommand<T>(
                RelationBulkDestroy.Incident,
                endpoint.AsCommandEntity(_buffer),
                new CommandEntity(Entity.Null)));
    }

    private DeferredRelationEdge<T> RecordCreate(
        CommandEntity first,
        CommandEntity second,
        in T payload,
        RelationMaintenanceTiming timing) =>
        RecordCreate(
            first,
            second,
            in payload,
            RelationCreatePlacement.None,
            default,
            default,
            timing);

    private DeferredRelationEdge<T> RecordDirectedCreate(
        CommandEntity source,
        CommandEntity target,
        in T payload,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing)
    {
        RequireDirection(RelationDirection.Directed);
        return RecordCreate(
            source,
            target,
            in payload,
            RelationCreatePlacement.Directed,
            placement,
            default,
            timing);
    }

    private DeferredRelationEdge<T> RecordUndirectedCreate(
        CommandEntity endpointA,
        CommandEntity endpointB,
        in T payload,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing)
    {
        RequireDirection(RelationDirection.Undirected);
        return RecordCreate(
            endpointA,
            endpointB,
            in payload,
            RelationCreatePlacement.Undirected,
            default,
            placement,
            timing);
    }

    private DeferredRelationEdge<T> RecordCreate(
        CommandEntity first,
        CommandEntity second,
        in T payload,
        RelationCreatePlacement placementKind,
        DirectedRelationPlacement directedPlacement,
        UndirectedRelationPlacement undirectedPlacement,
        RelationMaintenanceTiming timing)
    {
        ValidateTiming(timing);
        var cell = new DeferredRelationEdgeCell<T>(_buffer);
        Record(
            new CreateRelationCommand<T>(
                cell,
                first,
                second,
                in payload,
                placementKind,
                directedPlacement,
                undirectedPlacement,
                timing));
        return new DeferredRelationEdge<T>(cell);
    }

    private void RecordDestroy(RelationCommandEdge<T> edge)
    {
        Record(new DestroyRelationCommand<T>(edge));
    }

    private void RecordRetarget(
        RelationCommandEdge<T> edge,
        CommandEntity first,
        CommandEntity second,
        RelationMaintenanceTiming timing)
    {
        ValidateTiming(timing);
        Record(
            new RetargetRelationCommand<T>(
                edge,
                first,
                second,
                timing,
                RelationCreatePlacement.None,
                default,
                default));
    }

    private void RecordDirectedRetarget(
        RelationCommandEdge<T> edge,
        CommandEntity source,
        CommandEntity target,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing)
    {
        RequireDirection(RelationDirection.Directed);
        ValidateTiming(timing);
        Record(
            new RetargetRelationCommand<T>(
                edge,
                source,
                target,
                timing,
                RelationCreatePlacement.Directed,
                placement,
                default));
    }

    private void RecordUndirectedRetarget(
        RelationCommandEdge<T> edge,
        CommandEntity endpointA,
        CommandEntity endpointB,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing)
    {
        RequireDirection(RelationDirection.Undirected);
        ValidateTiming(timing);
        Record(
            new RetargetRelationCommand<T>(
                edge,
                endpointA,
                endpointB,
                timing,
                RelationCreatePlacement.Undirected,
                default,
                placement));
    }

    private void RecordReorder(
        CommandEntity endpoint,
        RelationAdjacencyRole role,
        RelationCommandEdge<T> edge,
        int insertIndex)
    {
        Record(
            new ReorderRelationCommand<T>(endpoint, role, edge, insertIndex));
    }

    private static void RequireDirection(RelationDirection direction)
    {
        var schema = RelationSchema.For<T>();
        if (schema.Direction != direction)
        {
            throw new InvalidOperationException(
                $"Relation payload {typeof(T).Name} is {schema.Direction}, not {direction}.");
        }
    }

    private static void ValidateTiming(RelationMaintenanceTiming timing)
    {
        if (timing != RelationMaintenanceTiming.Immediate &&
            timing != RelationMaintenanceTiming.Deferred)
        {
            throw new ArgumentOutOfRangeException(nameof(timing), timing, "Unknown relation timing.");
        }
    }

    private CommandBuffer.RecordAccessScope EnterOperation() =>
        _hasHookToken
            ? _buffer.EnterRecordAccess(_token)
            : _buffer.EnterRecordAccess();

    private void Record(ITypedRelationshipCommand command)
    {
        _buffer.RecordTypedRelationshipUnderGate(command);
    }
}

internal sealed class DeferredRelationEdgeCell<T>
    where T : struct, IComponent
{
    private readonly CommandBuffer _owner;
    private World? _world;
    private RelationEdge<T> _edge;
    private long _publicationEpoch;
    private bool _prepared;
    private bool _invalidated;

    internal DeferredRelationEdgeCell(CommandBuffer owner)
    {
        _owner = owner;
    }

    internal bool IsResolved
    {
        get
        {
            lock (_owner.CommandGate)
                return IsResolvedUnderGate;
        }
    }

    private bool IsResolvedUnderGate =>
        _prepared &&
        !_invalidated &&
        _world!.IsStructureEpochPublished(_publicationEpoch);

    internal void Prepare(World world, RelationEdge<T> edge, long publicationEpoch)
    {
        lock (_owner.CommandGate)
        {
            ArgumentNullException.ThrowIfNull(world);
            if (_invalidated)
                throw new InvalidOperationException("Deferred relation edge was invalidated before playback.");
            if (_prepared)
                throw new InvalidOperationException("Relation Create command has already been played back.");
            if (publicationEpoch <= 0)
                throw new ArgumentOutOfRangeException(nameof(publicationEpoch));

            _world = world;
            _edge = edge;
            _publicationEpoch = publicationEpoch;
            _prepared = true;
        }
    }

    internal RelationEdge<T> Resolve()
    {
        lock (_owner.CommandGate)
        {
            if (_invalidated)
                throw new InvalidOperationException("Deferred relation edge command was cleared or disposed.");
            if (!IsResolvedUnderGate)
            {
                throw new InvalidOperationException(
                    _prepared
                        ? "Deferred relation edge belongs to a structural transaction that has not been published."
                        : "Deferred relation edge has not been created by playback yet.");
            }
            return _edge;
        }
    }

    internal bool TryResolve(out RelationEdge<T> edge)
    {
        lock (_owner.CommandGate)
        {
            if (IsResolvedUnderGate)
            {
                edge = _edge;
                return true;
            }

            edge = default;
            return false;
        }
    }

    internal void InvalidatePending()
    {
        lock (_owner.CommandGate)
        {
            if (!IsResolvedUnderGate)
                _invalidated = true;
        }
    }

    internal void Invalidate()
    {
        lock (_owner.CommandGate)
            _invalidated = true;
    }

    internal void RequireOwner(CommandBuffer owner)
    {
        if (!ReferenceEquals(_owner, owner))
        {
            throw new InvalidOperationException(
                "A deferred relation edge may only be referenced by the CommandBuffer that records its Create. " +
                "After playback, Resolve it to a live RelationEdge before using another buffer.");
        }

        lock (_owner.CommandGate)
        {
            if (_invalidated)
                throw new InvalidOperationException("Deferred relation edge command was cleared or disposed.");
            if (_prepared)
            {
                throw new InvalidOperationException(
                    "A played-back deferred relation edge cannot be recorded again as command-local identity. " +
                    "Resolve it to a live RelationEdge first.");
            }
        }
    }
}

internal readonly struct RelationCommandEdge<T>
    where T : struct, IComponent
{
    private readonly RelationEdge<T> _live;
    private readonly DeferredRelationEdgeCell<T>? _deferred;

    internal RelationCommandEdge(RelationEdge<T> live)
    {
        _live = live;
        _deferred = null;
    }

    internal RelationCommandEdge(DeferredRelationEdgeCell<T> deferred)
    {
        _live = default;
        _deferred = deferred;
    }

    internal RelationEdge<T> Resolve(CommandPlaybackContext context) =>
        _deferred is null ? _live : context.Resolve(_deferred);
}

internal enum RelationCreatePlacement : byte
{
    None,
    Directed,
    Undirected,
}

internal sealed class CreateRelationCommand<T> : TypedRelationshipCommand
    where T : struct, IComponent
{
    private readonly DeferredRelationEdgeCell<T> _cell;
    private readonly CommandEntity _first;
    private readonly CommandEntity _second;
    private readonly T _payload;
    private readonly RelationCreatePlacement _placementKind;
    private readonly DirectedRelationPlacement _directedPlacement;
    private readonly UndirectedRelationPlacement _undirectedPlacement;
    private readonly RelationMaintenanceTiming _timing;

    internal CreateRelationCommand(
        DeferredRelationEdgeCell<T> cell,
        CommandEntity first,
        CommandEntity second,
        in T payload,
        RelationCreatePlacement placementKind,
        DirectedRelationPlacement directedPlacement,
        UndirectedRelationPlacement undirectedPlacement,
        RelationMaintenanceTiming timing)
    {
        _cell = cell;
        _first = first;
        _second = second;
        _payload = payload;
        _placementKind = placementKind;
        _directedPlacement = directedPlacement;
        _undirectedPlacement = undirectedPlacement;
        _timing = timing;
    }

    public override void Playback(World world, CommandPlaybackContext context)
    {
        if (context.IsResolved(_cell))
            throw new InvalidOperationException("Relation Create command has already been played back.");

        Entity first = _first.Resolve(context);
        Entity second = _second.Resolve(context);
        RelationEdge<T> edge = _placementKind switch
        {
            RelationCreatePlacement.None =>
                world.CreateRelation(first, second, in _payload, _timing),
            RelationCreatePlacement.Directed =>
                world.CreateRelation(first, second, in _payload, _directedPlacement, _timing),
            RelationCreatePlacement.Undirected =>
                world.CreateRelation(first, second, in _payload, _undirectedPlacement, _timing),
            _ => throw new InvalidOperationException("Unknown relation create placement kind."),
        };
        context.Complete(_cell, edge);
    }

    public override void Cancel()
    {
        _cell.InvalidatePending();
    }

    public override void PlaybackFailed()
    {
        _cell.Invalidate();
    }
}

internal sealed class DestroyRelationCommand<T> : TypedRelationshipCommand
    where T : struct, IComponent
{
    private readonly RelationCommandEdge<T> _edge;

    internal DestroyRelationCommand(RelationCommandEdge<T> edge)
    {
        _edge = edge;
    }

    public override void Playback(World world, CommandPlaybackContext context) =>
        world.DestroyRelation(_edge.Resolve(context));
}

internal sealed class RetargetRelationCommand<T> : TypedRelationshipCommand
    where T : struct, IComponent
{
    private readonly RelationCommandEdge<T> _edge;
    private readonly CommandEntity _first;
    private readonly CommandEntity _second;
    private readonly RelationMaintenanceTiming _timing;
    private readonly RelationCreatePlacement _placementKind;
    private readonly DirectedRelationPlacement _directedPlacement;
    private readonly UndirectedRelationPlacement _undirectedPlacement;

    internal RetargetRelationCommand(
        RelationCommandEdge<T> edge,
        CommandEntity first,
        CommandEntity second,
        RelationMaintenanceTiming timing,
        RelationCreatePlacement placementKind,
        DirectedRelationPlacement directedPlacement,
        UndirectedRelationPlacement undirectedPlacement)
    {
        _edge = edge;
        _first = first;
        _second = second;
        _timing = timing;
        _placementKind = placementKind;
        _directedPlacement = directedPlacement;
        _undirectedPlacement = undirectedPlacement;
    }

    public override void Playback(World world, CommandPlaybackContext context)
    {
        RelationEdge<T> edge = _edge.Resolve(context);
        Entity first = _first.Resolve(context);
        Entity second = _second.Resolve(context);
        if (_timing == RelationMaintenanceTiming.Deferred)
        {
            switch (_placementKind)
            {
                case RelationCreatePlacement.None:
                    world.RetargetRelationDeferred(edge, first, second);
                    break;
                case RelationCreatePlacement.Directed:
                    world.RetargetRelationDeferred(
                        edge,
                        first,
                        second,
                        _directedPlacement);
                    break;
                case RelationCreatePlacement.Undirected:
                    world.RetargetRelationDeferred(
                        edge,
                        first,
                        second,
                        _undirectedPlacement);
                    break;
                default:
                    throw new InvalidOperationException("Unknown relation retarget placement kind.");
            }
            return;
        }

        switch (_placementKind)
        {
            case RelationCreatePlacement.None:
                world.RetargetRelationImmediate(edge, first, second);
                break;
            case RelationCreatePlacement.Directed:
                world.RetargetRelationImmediate(
                    edge,
                    first,
                    second,
                    _directedPlacement);
                break;
            case RelationCreatePlacement.Undirected:
                world.RetargetRelationImmediate(
                    edge,
                    first,
                    second,
                    _undirectedPlacement);
                break;
            default:
                throw new InvalidOperationException("Unknown relation retarget placement kind.");
        }
    }
}

internal sealed class SetRelationAdjacencyOrderCommand<T> : TypedRelationshipCommand
    where T : struct, IComponent
{
    private readonly CommandEntity _endpoint;
    private readonly RelationAdjacencyRole _role;
    private readonly RelationAdjacencyOrderPolicy _policy;

    internal SetRelationAdjacencyOrderCommand(
        CommandEntity endpoint,
        RelationAdjacencyRole role,
        RelationAdjacencyOrderPolicy policy)
    {
        _endpoint = endpoint;
        _role = role;
        _policy = policy;
    }

    public override void Playback(World world, CommandPlaybackContext context) =>
        world.SetRelationAdjacencyOrder<T>(_endpoint.Resolve(context), _role, _policy);
}

internal sealed class ReorderRelationCommand<T> : TypedRelationshipCommand
    where T : struct, IComponent
{
    private readonly CommandEntity _endpoint;
    private readonly RelationAdjacencyRole _role;
    private readonly RelationCommandEdge<T> _edge;
    private readonly int _insertIndex;

    internal ReorderRelationCommand(
        CommandEntity endpoint,
        RelationAdjacencyRole role,
        RelationCommandEdge<T> edge,
        int insertIndex)
    {
        _endpoint = endpoint;
        _role = role;
        _edge = edge;
        _insertIndex = insertIndex;
    }

    public override void Playback(World world, CommandPlaybackContext context) =>
        world.ReorderRelationAdjacency(
            _endpoint.Resolve(context),
            _role,
            _edge.Resolve(context),
            _insertIndex);
}

internal enum RelationBulkDestroy : byte
{
    Between,
    Outgoing,
    Incoming,
    Incident,
}

internal sealed class BulkDestroyRelationCommand<T> : TypedRelationshipCommand
    where T : struct, IComponent
{
    private readonly RelationBulkDestroy _kind;
    private readonly CommandEntity _first;
    private readonly CommandEntity _second;

    internal BulkDestroyRelationCommand(
        RelationBulkDestroy kind,
        CommandEntity first,
        CommandEntity second)
    {
        _kind = kind;
        _first = first;
        _second = second;
    }

    public override void Playback(World world, CommandPlaybackContext context)
    {
        Entity first = _first.Resolve(context);
        Entity second = _second.Resolve(context);
        switch (_kind)
        {
            case RelationBulkDestroy.Between:
                world.DestroyAllRelationsBetween<T>(first, second);
                break;
            case RelationBulkDestroy.Outgoing:
                world.DestroyAllOutgoingRelations<T>(first);
                break;
            case RelationBulkDestroy.Incoming:
                world.DestroyAllIncomingRelations<T>(first);
                break;
            case RelationBulkDestroy.Incident:
                world.DestroyAllIncidentRelations<T>(first);
                break;
            default:
                throw new InvalidOperationException("Unknown relation bulk-destroy command kind.");
        }
    }
}
