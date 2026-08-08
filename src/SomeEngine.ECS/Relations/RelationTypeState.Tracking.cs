using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Owners;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationTypeState<T> : IRelationTypeState
    where T : struct, IComponent
{
    internal void MarkDirty(
        RelationEdge<T> edge,
        RelationAppliedEndpointImage applied,
        RelationEndpointPair current,
        int? firstInsertIndex = null,
        int? secondInsertIndex = null)
    {
        RequireEdge(edge);
        AdvanceDirtyMutationRevision();
        _dirtyEdges.TryAdd(edge.Entity, 0);
        IndexDirtyCurrent(edge.Entity, current);
        UpsertCommandBatchCanonical(edge.Entity, current);
        if (firstInsertIndex is not null ||
            secondInsertIndex is not null ||
            TouchesOrderedAdjacency(applied, current))
        {
            _pendingPlacements[edge.Entity] = new RelationPendingPlacement(
                firstInsertIndex,
                secondInsertIndex,
                checked(++_dirtySequence));
            _orderDiagnostics.RecordPlacementMetadataWrite(
                System.Runtime.CompilerServices.Unsafe.SizeOf<RelationPendingPlacement>());
        }
        else
        {
            // Pure unordered transitions are dirty members only. They have neither placement
            // payload nor mutation sequence; entity identity is their deterministic tie-break.
            _pendingPlacements.Remove(edge.Entity);
        }
    }

    internal void RestoreDirty(
        RelationEdge<T> edge,
        RelationEndpointPair current,
        bool hadPendingPlacement,
        RelationPendingPlacement placement)
    {
        RequireEdge(edge);
        AdvanceDirtyMutationRevision();
        _dirtyEdges.TryAdd(edge.Entity, 0);
        IndexDirtyCurrent(edge.Entity, current);
        UpsertCommandBatchCanonical(edge.Entity, current);
        if (!hadPendingPlacement)
        {
            _pendingPlacements.Remove(edge.Entity);
            return;
        }

        _pendingPlacements[edge.Entity] = placement;
        _orderDiagnostics.RecordPlacementMetadataWrite(
            System.Runtime.CompilerServices.Unsafe.SizeOf<RelationPendingPlacement>());
        if (placement.Sequence > _dirtySequence)
            _dirtySequence = placement.Sequence;
    }

    internal RelationPendingPlacement PendingPlacement(RelationEdge<T> edge) =>
        _pendingPlacements.TryGetValue(edge.Entity, out var placement)
            ? placement
            : default;

    internal bool TryGetPendingPlacement(
        RelationEdge<T> edge,
        out RelationPendingPlacement placement) =>
        _pendingPlacements.TryGetValue(edge.Entity, out placement);

    internal void ClearDirty(ReadOnlySpan<RelationEdge<T>> edges)
    {
        bool changed = false;
        for (int i = 0; i < edges.Length; i++)
        {
            RelationEdge<T> edge = edges[i];
            changed |= _dirtyEdges.Remove(edge.Entity);
            changed |= _pendingPlacements.Remove(edge.Entity);
            changed |= RemoveDirtyCurrent(edge.Entity);
        }
        if (changed)
            AdvanceDirtyMutationRevision();
    }

    private void AdvanceDirtyMutationRevision() =>
        _dirtyMutationRevision = unchecked(_dirtyMutationRevision + 1);

    internal RelationEdge<T>[] CommandBatchEdgesBetween(
        World world,
        Entity first,
        Entity second)
    {
        EnsureCommandBatchCanonicalIndex(world);
        _betweenLookupCount++;
        RelationEndpointPair key = CanonicalPair(new RelationEndpointPair(first, second));
        if (!_commandBatchCanonicalPairs!.TryGetValue(key, out HashSet<Entity>? bucket))
            return Array.Empty<RelationEdge<T>>();

        _betweenBucketVisits += bucket.Count;
        return StableLiveEdges(world, bucket);
    }

    internal RelationEdge<T>[] CommandBatchEdgesAt(
        World world,
        Entity endpoint,
        RelationAdjacencyRole role)
    {
        ValidateRole(role);
        EnsureCommandBatchCanonicalIndex(world);
        _atLookupCount++;
        var key = new RelationCanonicalEndpointKey(endpoint, role);
        if (!_commandBatchCanonicalRoles!.TryGetValue(key, out HashSet<Entity>? bucket))
            return Array.Empty<RelationEdge<T>>();

        _atBucketVisits += bucket.Count;
        return StableLiveEdges(world, bucket);
    }

    internal void TrackCommandBatchCanonical(
        RelationEdge<T> edge,
        RelationEndpointPair endpoints)
    {
        RequireEdge(edge);
        UpsertCommandBatchCanonical(edge.Entity, endpoints);
    }

    private void EnsureCommandBatchCanonicalIndex(World world)
    {
        RequireCommandBatch();
        if (_commandBatchCanonicalEndpoints is not null)
            return;

        _commandBatchCanonicalEndpoints = new Dictionary<Entity, RelationEndpointPair>();
        _commandBatchCanonicalPairs = new Dictionary<RelationEndpointPair, HashSet<Entity>>();
        _commandBatchCanonicalRoles =
            new Dictionary<RelationCanonicalEndpointKey, HashSet<Entity>>();
        _bulkIndexBuildCount++;

        foreach (KeyValuePair<Entity, byte> pair in CurrentGeneration.Edges)
        {
            Entity edge = pair.Key;
            _bulkIndexBuildEdgeVisits++;
            if (!world.IsAlive(edge) ||
                !RelationEndpointAccess.HasCurrent<T>(world, edge, Schema))
            {
                continue;
            }

            IndexCommandBatchCanonical(
                edge,
                RelationEndpointAccess.ReadCurrent<T>(world, edge, Schema));
        }
    }

    private void UpsertCommandBatchCanonical(
        Entity edge,
        RelationEndpointPair endpoints)
    {
        if (_commandBatchCanonicalEndpoints is null)
            return;

        if (_commandBatchCanonicalEndpoints.TryGetValue(edge, out RelationEndpointPair existing))
        {
            if (existing == endpoints)
                return;
            RemoveCommandBatchCanonicalMembership(edge, existing);
        }
        IndexCommandBatchCanonical(edge, endpoints);
    }

    private void IndexCommandBatchCanonical(
        Entity edge,
        RelationEndpointPair endpoints)
    {
        _commandBatchCanonicalEndpoints![edge] = endpoints;
        AddBucket(
            _commandBatchCanonicalPairs!,
            CanonicalPair(endpoints),
            edge);
        if (Schema.Direction == RelationDirection.Directed)
        {
            AddBucket(
                _commandBatchCanonicalRoles!,
                new RelationCanonicalEndpointKey(
                    endpoints.First,
                    RelationAdjacencyRole.Outgoing),
                edge);
            AddBucket(
                _commandBatchCanonicalRoles!,
                new RelationCanonicalEndpointKey(
                    endpoints.Second,
                    RelationAdjacencyRole.Incoming),
                edge);
            return;
        }

        AddBucket(
            _commandBatchCanonicalRoles!,
            new RelationCanonicalEndpointKey(
                endpoints.First,
                RelationAdjacencyRole.Incident),
            edge);
        if (endpoints.Second != endpoints.First)
        {
            AddBucket(
                _commandBatchCanonicalRoles!,
                new RelationCanonicalEndpointKey(
                    endpoints.Second,
                    RelationAdjacencyRole.Incident),
                edge);
        }
    }

    private void RemoveCommandBatchCanonical(Entity edge)
    {
        if (_commandBatchCanonicalEndpoints is null ||
            !_commandBatchCanonicalEndpoints.Remove(edge, out RelationEndpointPair endpoints))
        {
            return;
        }
        RemoveCommandBatchCanonicalMembership(edge, endpoints);
    }

    private void RemoveCommandBatchCanonicalMembership(
        Entity edge,
        RelationEndpointPair endpoints)
    {
        RemoveBucket(
            _commandBatchCanonicalPairs!,
            CanonicalPair(endpoints),
            edge);
        if (Schema.Direction == RelationDirection.Directed)
        {
            RemoveBucket(
                _commandBatchCanonicalRoles!,
                new RelationCanonicalEndpointKey(
                    endpoints.First,
                    RelationAdjacencyRole.Outgoing),
                edge);
            RemoveBucket(
                _commandBatchCanonicalRoles!,
                new RelationCanonicalEndpointKey(
                    endpoints.Second,
                    RelationAdjacencyRole.Incoming),
                edge);
            return;
        }

        RemoveBucket(
            _commandBatchCanonicalRoles!,
            new RelationCanonicalEndpointKey(
                endpoints.First,
                RelationAdjacencyRole.Incident),
            edge);
        if (endpoints.Second != endpoints.First)
        {
            RemoveBucket(
                _commandBatchCanonicalRoles!,
                new RelationCanonicalEndpointKey(
                    endpoints.Second,
                    RelationAdjacencyRole.Incident),
                edge);
        }
    }

    private RelationEndpointPair CanonicalPair(RelationEndpointPair endpoints)
    {
        if (Schema.Direction == RelationDirection.Directed ||
            CompareEntities(endpoints.First, endpoints.Second) <= 0)
        {
            return endpoints;
        }
        return new RelationEndpointPair(endpoints.Second, endpoints.First);
    }

    private RelationEdge<T>[] StableLiveEdges(World world, HashSet<Entity> bucket)
    {
        var matches = new List<RelationEdge<T>>(bucket.Count);
        RelationGeneration<T> generation = CurrentGeneration;
        foreach (Entity edge in bucket)
        {
            if (world.IsAlive(edge) && generation.Edges.ContainsKey(edge))
                matches.Add(new RelationEdge<T>(edge));
        }
        RelationEdge<T>[] result = matches.ToArray();
        Array.Sort(result, static (left, right) => CompareEntities(left.Entity, right.Entity));
        return result;
    }

    private void IndexDirtyCurrent(Entity edge, RelationEndpointPair current)
    {
        if (_dirtyCurrentEndpoints.TryGetValue(edge, out RelationEndpointPair existing))
        {
            if (existing == current)
                return;
            RemoveDirtyEndpointMembership(edge, existing);
        }

        _dirtyCurrentEndpoints[edge] = current;
        AddDirtyBucket(_dirtyEdgesByEndpoint, current.First, edge);
        if (current.Second != current.First)
            AddDirtyBucket(_dirtyEdgesByEndpoint, current.Second, edge);
    }

    private bool RemoveDirtyCurrent(Entity edge)
    {
        if (!_dirtyCurrentEndpoints.Remove(edge, out RelationEndpointPair current))
            return false;
        RemoveDirtyEndpointMembership(edge, current);
        return true;
    }

    private void RemoveDirtyEndpointMembership(
        Entity edge,
        RelationEndpointPair current)
    {
        RemoveDirtyBucket(_dirtyEdgesByEndpoint, current.First, edge);
        if (current.Second != current.First)
            RemoveDirtyBucket(_dirtyEdgesByEndpoint, current.Second, edge);
    }

    private static void AddDirtyBucket(
        RelationEntityMap<RelationDirtyEdgeBucket> index,
        Entity endpoint,
        Entity edge)
    {
        RelationDirtyEdgeBucket bucket = index.TryGetValue(endpoint, out var existing)
            ? existing
            : default;
        index[endpoint] = bucket.Add(edge);
    }

    private static void RemoveDirtyBucket(
        RelationEntityMap<RelationDirtyEdgeBucket> index,
        Entity endpoint,
        Entity edge)
    {
        if (!index.TryGetValue(endpoint, out RelationDirtyEdgeBucket bucket))
            return;

        RelationDirtyEdgeBucket next = bucket.Remove(edge, out bool removed);
        if (!removed)
            return;
        if (next.Entities.Length == 0)
            index.Remove(endpoint);
        else
            index[endpoint] = next;
    }

    private static void AddBucket<TKey>(
        Dictionary<TKey, HashSet<Entity>> index,
        TKey key,
        Entity edge)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out HashSet<Entity>? bucket))
        {
            bucket = new HashSet<Entity>();
            index.Add(key, bucket);
        }
        bucket.Add(edge);
    }

    private static void RemoveBucket<TKey>(
        Dictionary<TKey, HashSet<Entity>> index,
        TKey key,
        Entity edge)
        where TKey : notnull
    {
        if (!index.TryGetValue(key, out HashSet<Entity>? bucket))
            return;
        bucket.Remove(edge);
        if (bucket.Count == 0)
            index.Remove(key);
    }

    internal RelationEdge<T>[] DirtyEdgesStable()
    {
        var edges = new RelationEdge<T>[_dirtyEdges.Count];
        int offset = 0;
        foreach (KeyValuePair<Entity, byte> pair in _dirtyEdges)
            edges[offset++] = new RelationEdge<T>(pair.Key);
        if (offset != edges.Length)
            throw new InvalidOperationException("Relation dirty edge count changed during enumeration.");
        if (_pendingPlacements.Count == 0)
        {
            Array.Sort(
                edges,
                static (left, right) => CompareEntities(left.Entity, right.Entity));
            return edges;
        }

        Array.Sort(edges, (left, right) =>
        {
            bool leftPending = _pendingPlacements.TryGetValue(left.Entity, out var leftPlacement);
            bool rightPending = _pendingPlacements.TryGetValue(right.Entity, out var rightPlacement);
            if (leftPending && rightPending)
            {
                int sequence = leftPlacement.Sequence.CompareTo(rightPlacement.Sequence);
                if (sequence != 0)
                    return sequence;
            }
            else if (leftPending != rightPending)
            {
                return leftPending ? -1 : 1;
            }

            return CompareEntities(left.Entity, right.Entity);
        });
        return edges;
    }

    /// <summary>
    /// Counts the exact final topology record image without allocating a transition array or
    /// cloning the relation generation. Dirty endpoint changes alter only ordered membership
    /// counts; edge identities and ordered-policy headers already belong to the generation.
    /// </summary>
    internal long CountProjectedTopologyRecords(World world)
    {
        ArgumentNullException.ThrowIfNull(world);
        RelationGeneration<T> generation = CurrentGeneration;
        long orderedMembers = generation.CountOrderedMembers();
        foreach (KeyValuePair<Entity, byte> pair in _dirtyEdges)
        {
            Entity edgeEntity = pair.Key;
            if (!world.IsAlive(edgeEntity) || !generation.Edges.ContainsKey(edgeEntity))
                continue;

            var edge = new RelationEdge<T>(edgeEntity);
            RelationAppliedEndpointImage applied = RelationEndpointAccess.ReadAppliedImage<T>(
                world,
                edgeEntity);
            if (applied.IsApplied)
            {
                orderedMembers = checked(
                    orderedMembers - generation.CountOrderedMemberships(applied.Endpoints));
            }

            RelationEndpointPair current = RelationEndpointAccess.ReadCurrent<T>(
                world,
                edgeEntity,
                Schema);
            orderedMembers = checked(
                orderedMembers + generation.CountOrderedMemberships(current));
        }

        return checked(
            generation.Edges.Count +
            (long)generation.OrderedShardCount +
            orderedMembers);
    }
}
