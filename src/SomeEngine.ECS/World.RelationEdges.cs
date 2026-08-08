using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS;

public partial class World
{
    public RelationEdge<T> CreateRelation<T>(
        Entity first,
        Entity second,
        in T payload)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        return _relationGraph.Create(this, first, second, in payload);
    }

    public RelationEdge<T> CreateRelation<T>(
        Entity first,
        Entity second,
        in T payload,
        RelationMaintenanceTiming timing)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        return _relationGraph.Create(this, first, second, in payload, timing: timing);
    }

    public RelationEdge<T> CreateRelation<T>(
        Entity source,
        Entity target,
        in T payload,
        DirectedRelationPlacement placement)
        where T : struct, IComponent
    {
        return CreateRelation(source, target, in payload, placement, RelationMaintenanceTiming.Immediate);
    }

    public RelationEdge<T> CreateRelation<T>(
        Entity source,
        Entity target,
        in T payload,
        DirectedRelationPlacement placement,
        RelationMaintenanceTiming timing)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        RequireRelationDirection<T>(RelationDirection.Directed);
        return _relationGraph.Create(
            this,
            source,
            target,
            in payload,
            placement.OutgoingIndex,
            placement.IncomingIndex,
            timing);
    }

    public RelationEdge<T> CreateRelation<T>(
        Entity endpointA,
        Entity endpointB,
        in T payload,
        UndirectedRelationPlacement placement)
        where T : struct, IComponent
    {
        return CreateRelation(endpointA, endpointB, in payload, placement, RelationMaintenanceTiming.Immediate);
    }

    public RelationEdge<T> CreateRelation<T>(
        Entity endpointA,
        Entity endpointB,
        in T payload,
        UndirectedRelationPlacement placement,
        RelationMaintenanceTiming timing)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        RequireRelationDirection<T>(RelationDirection.Undirected);
        return _relationGraph.Create(
            this,
            endpointA,
            endpointB,
            in payload,
            placement.EndpointAIndex,
            placement.EndpointBIndex,
            timing);
    }

    public void DestroyRelation<T>(RelationEdge<T> edge)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.Destroy(this, edge);
    }

    public void RetargetRelationImmediate<T>(
        RelationEdge<T> edge,
        Entity first,
        Entity second)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.Retarget(
            this,
            edge,
            first,
            second,
            RelationMaintenanceTiming.Immediate);
    }

    public void RetargetRelationImmediate<T>(
        RelationEdge<T> edge,
        Entity source,
        Entity target,
        DirectedRelationPlacement placement)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        RequireRelationDirection<T>(RelationDirection.Directed);
        _relationGraph.Retarget(
            this,
            edge,
            source,
            target,
            RelationMaintenanceTiming.Immediate,
            placement.OutgoingIndex,
            placement.IncomingIndex);
    }

    public void RetargetRelationImmediate<T>(
        RelationEdge<T> edge,
        Entity endpointA,
        Entity endpointB,
        UndirectedRelationPlacement placement)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        RequireRelationDirection<T>(RelationDirection.Undirected);
        _relationGraph.Retarget(
            this,
            edge,
            endpointA,
            endpointB,
            RelationMaintenanceTiming.Immediate,
            placement.EndpointAIndex,
            placement.EndpointBIndex);
    }

    public void RetargetRelationDeferred<T>(
        RelationEdge<T> edge,
        Entity first,
        Entity second)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.Retarget(
            this,
            edge,
            first,
            second,
            RelationMaintenanceTiming.Deferred);
    }

    public void RetargetRelationDeferred<T>(
        RelationEdge<T> edge,
        Entity source,
        Entity target,
        DirectedRelationPlacement placement)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        RequireRelationDirection<T>(RelationDirection.Directed);
        _relationGraph.Retarget(
            this,
            edge,
            source,
            target,
            RelationMaintenanceTiming.Deferred,
            placement.OutgoingIndex,
            placement.IncomingIndex);
    }

    public void RetargetRelationDeferred<T>(
        RelationEdge<T> edge,
        Entity endpointA,
        Entity endpointB,
        UndirectedRelationPlacement placement)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        RequireRelationDirection<T>(RelationDirection.Undirected);
        _relationGraph.Retarget(
            this,
            edge,
            endpointA,
            endpointB,
            RelationMaintenanceTiming.Deferred,
            placement.EndpointAIndex,
            placement.EndpointBIndex);
    }

    public void MaintainRelations<T>()
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.Maintain<T>(this);
    }

    public DirectedRelationEndpoints<T> GetDirectedRelationEndpoints<T>(RelationEdge<T> edge)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission =
            EnterJobComponent<DirectedRelationEndpoints<T>>(WorldStorageAccess.Read);
        return _relationGraph.DirectedEndpoints(this, edge);
    }

    public UndirectedRelationEndpoints<T> GetUndirectedRelationEndpoints<T>(RelationEdge<T> edge)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission =
            EnterJobComponent<UndirectedRelationEndpoints<T>>(WorldStorageAccess.Read);
        return _relationGraph.UndirectedEndpoints(this, edge);
    }

    public RelationAdjacencySnapshot<T> GetOutgoingRelations<T>(Entity source)
        where T : struct, IComponent =>
        _relationGraph.Snapshot<T>(source, RelationAdjacencyRole.Outgoing);

    public RelationAdjacencySnapshot<T> GetIncomingRelations<T>(Entity target)
        where T : struct, IComponent =>
        _relationGraph.Snapshot<T>(target, RelationAdjacencyRole.Incoming);

    public RelationAdjacencySnapshot<T> GetIncidentRelations<T>(Entity endpoint)
        where T : struct, IComponent =>
        _relationGraph.Snapshot<T>(endpoint, RelationAdjacencyRole.Incident);

    public RelationAdjacencySnapshot<T> GetOrderedOutgoingRelations<T>(Entity source)
        where T : struct, IComponent =>
        RequireOrdered(GetOutgoingRelations<T>(source), source, RelationAdjacencyRole.Outgoing);

    public RelationAdjacencySnapshot<T> GetOrderedIncomingRelations<T>(Entity target)
        where T : struct, IComponent =>
        RequireOrdered(GetIncomingRelations<T>(target), target, RelationAdjacencyRole.Incoming);

    public RelationAdjacencySnapshot<T> GetOrderedIncidentRelations<T>(Entity endpoint)
        where T : struct, IComponent =>
        RequireOrdered(GetIncidentRelations<T>(endpoint), endpoint, RelationAdjacencyRole.Incident);

    /// <summary>
    /// Returns the edges connecting two endpoint keys from one immutable relation generation.
    /// Absent or stale endpoint keys return an empty view; this relaxed lookup does not read the
    /// mutable entity store and may overlap publication of a later generation.
    /// </summary>
    public RelationEdgeQuery<T> GetRelationEdgesBetween<T>(Entity first, Entity second)
        where T : struct, IComponent =>
        _relationGraph.EdgesBetween<T>(first, second);

    public void SetRelationAdjacencyOrder<T>(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationAdjacencyOrderPolicy policy)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.SetOrderPolicy<T>(this, endpoint, role, policy);
    }

    public void ReorderRelationAdjacency<T>(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationEdge<T> edge,
        int insertIndex)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.Reorder(this, endpoint, role, edge, insertIndex);
    }

    public void DestroyAllRelationsBetween<T>(Entity first, Entity second)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.DestroyAllBetween<T>(this, first, second);
    }

    public void DestroyAllOutgoingRelations<T>(Entity source)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.DestroyAllAt<T>(this, source, RelationAdjacencyRole.Outgoing);
    }

    public void DestroyAllIncomingRelations<T>(Entity target)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.DestroyAllAt<T>(this, target, RelationAdjacencyRole.Incoming);
    }

    public void DestroyAllIncidentRelations<T>(Entity endpoint)
        where T : struct, IComponent
    {
        using WorldJobAdmissionScope admission = EnterJobTopologyWrite();
        _relationGraph.DestroyAllAt<T>(this, endpoint, RelationAdjacencyRole.Incident);
    }

    internal void CleanupRelationsForEntity(Entity entity) =>
        _relationGraph.CleanupEntity(this, entity);

    internal void ValidateAndTrackDeferredRelationEndpoints<T>(
        ReadOnlySpan<RelationEdge<T>> edges)
        where T : struct, IComponent =>
        _relationGraph.ValidateAndTrackDeferred(this, edges);

    private void RequireRelationDirection<T>(RelationDirection direction)
        where T : struct, IComponent
    {
        var schema = _relationGraph.Schema<T>();
        if (schema.Direction != direction)
        {
            throw new InvalidOperationException(
                $"Relation payload {typeof(T).Name} is {schema.Direction}, not {direction}.");
        }
    }

    private static RelationAdjacencySnapshot<T> RequireOrdered<T>(
        RelationAdjacencySnapshot<T> snapshot,
        Entity endpoint,
        RelationAdjacencyRole role)
        where T : struct, IComponent
    {
        if (snapshot.OrderPolicy != RelationAdjacencyOrderPolicy.Ordered)
        {
            throw new InvalidOperationException(
                $"{role} adjacency for endpoint {endpoint} is not ordered.");
        }
        return snapshot;
    }
}
