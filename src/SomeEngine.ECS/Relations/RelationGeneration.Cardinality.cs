using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Relations;

/// <summary>
/// Endpoint-local cardinality validation for relation generations. Persistent uniqueness is
/// derived from immutable adjacency shards; only an in-flight batch or topology import retains
/// the small transition workspace below.
/// </summary>
internal sealed partial class RelationGeneration<T>
    where T : struct, IComponent
{
    private CardinalityWorkspace? _cardinalityWorkspace;

    private void AddCardinality(RelationEdge<T> edge, RelationEndpointPair endpoints)
    {
        if (Schema.Cardinality == RelationCardinality.Parallel)
            return;

        ValidateCardinality(endpoints, edge.Entity);
        (_cardinalityWorkspace ??= new CardinalityWorkspace()).AddPending(edge.Entity, endpoints);
    }

    private void RemoveCardinality(RelationEdge<T> edge, RelationEndpointPair endpoints)
    {
        _ = endpoints;
        if (Schema.Cardinality == RelationCardinality.Parallel)
            return;

        (_cardinalityWorkspace ??= new CardinalityWorkspace()).AddDetached(edge.Entity);
    }

    /// <summary>
    /// Finishes the import-only cardinality workspace after all final adjacency shards have been
    /// populated. Import reads canonical edges before their endpoint-local shards, so its claims
    /// must remain temporary until this final image can be checked and then released.
    /// </summary>
    internal void CompleteTopologyImportCardinality()
    {
        try
        {
            CardinalityWorkspace? workspace = _cardinalityWorkspace;
            if (workspace is not null && workspace.DetachedCount != 0)
                throw new InvalidDataException("Relation topology import retained detached cardinality claims.");
            if (Schema.Cardinality != RelationCardinality.Parallel &&
                (workspace?.PendingCount ?? 0) != Edges.Count)
            {
                throw new InvalidDataException(
                    "Relation topology import cardinality claims do not cover the canonical edge image.");
            }

            ValidateFinalCardinalityImage();
        }
        finally
        {
            _cardinalityWorkspace = null;
        }
    }

    private void ValidateCardinality(RelationEndpointPair endpoints, Entity ignoredEdge)
    {
        switch (Schema.Cardinality)
        {
            case RelationCardinality.Parallel:
                return;
            case RelationCardinality.UniquePair:
                RequireUniquePairAvailable(endpoints, ignoredEdge);
                return;
            case RelationCardinality.UniqueSource:
                RequireEndpointAvailable(
                    endpoints.First,
                    RelationAdjacencyRole.Outgoing,
                    ignoredEdge,
                    "source");
                RequirePendingSourceAvailable(endpoints.First, ignoredEdge, "source");
                return;
            case RelationCardinality.UniqueTarget:
                RequireEndpointAvailable(
                    endpoints.Second,
                    RelationAdjacencyRole.Incoming,
                    ignoredEdge,
                    "target");
                RequirePendingTargetAvailable(endpoints.Second, ignoredEdge, "target");
                return;
            case RelationCardinality.OneToOne when Schema.Direction == RelationDirection.Directed:
                RequireEndpointAvailable(
                    endpoints.First,
                    RelationAdjacencyRole.Outgoing,
                    ignoredEdge,
                    "source");
                RequirePendingSourceAvailable(endpoints.First, ignoredEdge, "source");
                RequireEndpointAvailable(
                    endpoints.Second,
                    RelationAdjacencyRole.Incoming,
                    ignoredEdge,
                    "target");
                RequirePendingTargetAvailable(endpoints.Second, ignoredEdge, "target");
                return;
            case RelationCardinality.OneToOne:
                RequireEndpointAvailable(
                    endpoints.First,
                    RelationAdjacencyRole.Incident,
                    ignoredEdge,
                    "incident endpoint");
                RequirePendingIncidentAvailable(endpoints.First, ignoredEdge);
                if (endpoints.Second != endpoints.First)
                {
                    RequireEndpointAvailable(
                        endpoints.Second,
                        RelationAdjacencyRole.Incident,
                        ignoredEdge,
                        "incident endpoint");
                    RequirePendingIncidentAvailable(endpoints.Second, ignoredEdge);
                }
                return;
            default:
                throw new InvalidOperationException(
                    $"Relation {typeof(T).Name} has unknown cardinality {Schema.Cardinality}.");
        }
    }

    private void RequireUniquePairAvailable(RelationEndpointPair endpoints, Entity ignoredEdge)
    {
        RelationAdjacencyRole role = Schema.Direction == RelationDirection.Directed
            ? RelationAdjacencyRole.Outgoing
            : RelationAdjacencyRole.Incident;
        Entity existing = FindShardOccupant(
            endpoints.First,
            role,
            endpoints.Second,
            ignoredEdge);
        if (existing != Entity.Null)
            ThrowCardinality("endpoint pair", existing);

        CardinalityWorkspace? workspace = _cardinalityWorkspace;
        if (workspace is null)
            return;
        for (int i = 0; i < workspace.PendingCount; i++)
        {
            PendingCardinalityClaim claim = workspace.PendingAt(i);
            if (claim.Edge != ignoredEdge && SamePair(claim.Endpoints, endpoints))
                ThrowCardinality("endpoint pair", claim.Edge);
        }
    }

    private void RequireEndpointAvailable(
        Entity endpoint,
        RelationAdjacencyRole role,
        Entity ignoredEdge,
        string constraint)
    {
        Entity existing = FindShardOccupant(endpoint, role, otherEndpoint: null, ignoredEdge);
        if (existing != Entity.Null)
            ThrowCardinality(constraint, existing);
    }

    private void RequirePendingSourceAvailable(
        Entity source,
        Entity ignoredEdge,
        string constraint)
    {
        CardinalityWorkspace? workspace = _cardinalityWorkspace;
        if (workspace is null)
            return;
        for (int i = 0; i < workspace.PendingCount; i++)
        {
            PendingCardinalityClaim claim = workspace.PendingAt(i);
            if (claim.Edge != ignoredEdge && claim.Endpoints.First == source)
                ThrowCardinality(constraint, claim.Edge);
        }
    }

    private void RequirePendingTargetAvailable(
        Entity target,
        Entity ignoredEdge,
        string constraint)
    {
        CardinalityWorkspace? workspace = _cardinalityWorkspace;
        if (workspace is null)
            return;
        for (int i = 0; i < workspace.PendingCount; i++)
        {
            PendingCardinalityClaim claim = workspace.PendingAt(i);
            if (claim.Edge != ignoredEdge && claim.Endpoints.Second == target)
                ThrowCardinality(constraint, claim.Edge);
        }
    }

    private void RequirePendingIncidentAvailable(Entity endpoint, Entity ignoredEdge)
    {
        CardinalityWorkspace? workspace = _cardinalityWorkspace;
        if (workspace is null)
            return;
        for (int i = 0; i < workspace.PendingCount; i++)
        {
            PendingCardinalityClaim claim = workspace.PendingAt(i);
            if (claim.Edge != ignoredEdge &&
                (claim.Endpoints.First == endpoint || claim.Endpoints.Second == endpoint))
            {
                ThrowCardinality("incident endpoint", claim.Edge);
            }
        }
    }

    private Entity FindShardOccupant(
        Entity endpoint,
        RelationAdjacencyRole role,
        Entity? otherEndpoint,
        Entity ignoredEdge)
    {
        ReadOnlySpan<RelationAdjacencyEntry<T>> entries = GetShard(endpoint, role).Entries;
        for (int i = 0; i < entries.Length; i++)
        {
            RelationAdjacencyEntry<T> entry = entries[i];
            Entity existing = entry.Edge.Entity;
            if (existing == ignoredEdge ||
                (_cardinalityWorkspace?.IsDetached(existing) ?? false))
            {
                continue;
            }
            if (otherEndpoint is null || entry.OtherEndpoint == otherEndpoint.Value)
                return existing;
        }

        return Entity.Null;
    }

    private bool SamePair(RelationEndpointPair left, RelationEndpointPair right)
    {
        if (Schema.Direction == RelationDirection.Directed)
            return left.First == right.First && left.Second == right.Second;
        return (left.First == right.First && left.Second == right.Second) ||
               (left.First == right.Second && left.Second == right.First);
    }

    private static void ThrowCardinality(string constraint, Entity existing) =>
        throw new InvalidOperationException(
            $"Relation {typeof(T).Name} violates its unique {constraint} cardinality; existing edge is {existing}.");

    private void ValidateFinalCardinalityImage()
    {
        switch (Schema.Cardinality)
        {
            case RelationCardinality.Parallel:
                return;
            case RelationCardinality.UniquePair:
                ValidateFinalUniquePairs(
                    Schema.Direction == RelationDirection.Directed ? Outgoing : Incident);
                return;
            case RelationCardinality.UniqueSource:
                ValidateFinalEndpointCounts(Outgoing, "source");
                return;
            case RelationCardinality.UniqueTarget:
                ValidateFinalEndpointCounts(Incoming, "target");
                return;
            case RelationCardinality.OneToOne when Schema.Direction == RelationDirection.Directed:
                ValidateFinalEndpointCounts(Outgoing, "source");
                ValidateFinalEndpointCounts(Incoming, "target");
                return;
            case RelationCardinality.OneToOne:
                ValidateFinalEndpointCounts(Incident, "incident endpoint");
                return;
            default:
                throw new InvalidDataException(
                    $"Relation {typeof(T).Name} has unknown cardinality {Schema.Cardinality}.");
        }
    }

    private static void ValidateFinalUniquePairs(
        RelationEntityMap<RelationAdjacencyShard<T>> shards)
    {
        foreach (KeyValuePair<Entity, RelationAdjacencyShard<T>> pair in shards)
        {
            ReadOnlySpan<RelationAdjacencyEntry<T>> entries = pair.Value.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                for (int j = i + 1; j < entries.Length; j++)
                {
                    if (entries[i].OtherEndpoint == entries[j].OtherEndpoint)
                    {
                        throw new InvalidDataException(
                            $"Relation {typeof(T).Name} violates its unique endpoint pair cardinality; " +
                            $"existing edges are {entries[i].Edge.Entity} and {entries[j].Edge.Entity}.");
                    }
                }
            }
        }
    }

    private static void ValidateFinalEndpointCounts(
        RelationEntityMap<RelationAdjacencyShard<T>> shards,
        string constraint)
    {
        foreach (KeyValuePair<Entity, RelationAdjacencyShard<T>> pair in shards)
        {
            ReadOnlySpan<RelationAdjacencyEntry<T>> entries = pair.Value.Entries;
            if (entries.Length > 1)
            {
                throw new InvalidDataException(
                    $"Relation {typeof(T).Name} violates its unique {constraint} cardinality at {pair.Key}.");
            }
        }
    }

    private void CompleteCardinalityDetachment(RelationEdge<T> edge)
    {
        _cardinalityWorkspace?.RemoveDetached(edge.Entity);
        ReleaseEmptyCardinalityWorkspace();
    }

    private void CompleteCardinalityAttachment(RelationEdge<T> edge)
    {
        _cardinalityWorkspace?.RemovePending(edge.Entity);
        ReleaseEmptyCardinalityWorkspace();
    }

    private void ReleaseEmptyCardinalityWorkspace()
    {
        if (_cardinalityWorkspace?.IsEmpty == true)
            _cardinalityWorkspace = null;
    }

    private sealed class CardinalityWorkspace
    {
        private readonly List<Entity> _detachedEdges = new();
        private readonly List<PendingCardinalityClaim> _pending = new();

        internal int DetachedCount => _detachedEdges.Count;

        internal int PendingCount => _pending.Count;

        internal bool IsEmpty => _detachedEdges.Count == 0 && _pending.Count == 0;

        internal PendingCardinalityClaim PendingAt(int index) => _pending[index];

        internal bool IsDetached(Entity edge)
        {
            for (int i = 0; i < _detachedEdges.Count; i++)
            {
                if (_detachedEdges[i] == edge)
                    return true;
            }
            return false;
        }

        internal void AddDetached(Entity edge)
        {
            if (IsDetached(edge))
                throw new InvalidOperationException($"Relation edge {edge} has duplicate detached cardinality state.");
            _detachedEdges.Add(edge);
        }

        internal void RemoveDetached(Entity edge)
        {
            for (int i = 0; i < _detachedEdges.Count; i++)
            {
                if (_detachedEdges[i] != edge)
                    continue;
                _detachedEdges.RemoveAt(i);
                return;
            }
        }

        internal void AddPending(Entity edge, RelationEndpointPair endpoints)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Edge == edge)
                    throw new InvalidOperationException($"Relation edge {edge} has duplicate pending cardinality state.");
            }
            _pending.Add(new PendingCardinalityClaim(edge, endpoints));
        }

        internal void RemovePending(Entity edge)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (_pending[i].Edge != edge)
                    continue;
                _pending.RemoveAt(i);
                return;
            }
        }
    }

    private readonly record struct PendingCardinalityClaim(
        Entity Edge,
        RelationEndpointPair Endpoints);
}
