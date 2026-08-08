using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Owners;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationTypeState<T> : IRelationTypeState
    where T : struct, IComponent
{
    private bool TouchesOrderedAdjacency(
        RelationAppliedEndpointImage applied,
        RelationEndpointPair current)
    {
        RelationGeneration<T> generation = CurrentGeneration;
        if (applied.IsApplied && generation.HasOrderedAdjacency(applied.Endpoints))
            return true;
        return generation.HasOrderedAdjacency(current);
    }

    internal RelationAdjacencySnapshot<T> Snapshot(
        Entity endpoint,
        RelationAdjacencyRole role)
    {
        ValidateRole(role);
        RelationGeneration<T> generation = CurrentGeneration;
        var shard = generation.GetShard(endpoint, role);
        return new RelationAdjacencySnapshot<T>(
            shard.EntryMemory,
            generation.Id,
            shard.Policy);
    }

    internal static RelationAdjacencySnapshot<T> EmptySnapshot(
        RelationSchema schema,
        RelationAdjacencyRole role)
    {
        ValidateRole(schema, role);
        return new RelationAdjacencySnapshot<T>(
            Array.Empty<RelationAdjacencyEntry<T>>(),
            generation: 1,
            policy: RelationAdjacencyOrderPolicy.Unordered);
    }

    internal RelationEdgeQuery<T> EdgesBetween(Entity first, Entity second)
    {
        RelationGeneration<T> generation = CurrentGeneration;
        RelationAdjacencyRole role = Schema.Direction == RelationDirection.Directed
            ? RelationAdjacencyRole.Outgoing
            : RelationAdjacencyRole.Incident;
        var shard = generation.GetShard(first, role);
        return new RelationEdgeQuery<T>(shard.Entries, second);
    }

    public void DestroyIncidentEdges(
        RelationGraph graph,
        World world,
        Entity endpoint,
        ExceptionAccumulator faults)
    {
        _cleanupLookupCount++;
        RelationGeneration<T> generation = CurrentGeneration;
        ReadOnlySpan<RelationAdjacencyEntry<T>> first;
        ReadOnlySpan<RelationAdjacencyEntry<T>> second;
        if (Schema.Direction == RelationDirection.Directed)
        {
            first = generation
                .GetShard(endpoint, RelationAdjacencyRole.Outgoing)
                .Entries;
            second = generation
                .GetShard(endpoint, RelationAdjacencyRole.Incoming)
                .Entries;
        }
        else
        {
            first = generation
                .GetShard(endpoint, RelationAdjacencyRole.Incident)
                .Entries;
            second = ReadOnlySpan<RelationAdjacencyEntry<T>>.Empty;
        }

        DestroyAppliedEdges(graph, world, endpoint, first, ReadOnlySpan<RelationAdjacencyEntry<T>>.Empty, faults);
        DestroyAppliedEdges(graph, world, endpoint, second, first, faults);

        // Applied adjacency may be stale during a deferred window. Include
        // only the endpoint-local canonical dirty bucket. Scanning the complete
        // dirty set here makes unrelated entity destruction quadratic.
        if (_dirtyEdgesByEndpoint.TryGetValue(endpoint, out RelationDirtyEdgeBucket dirtyBucket))
        {
            ReadOnlySpan<Entity> dirtyEdges = dirtyBucket.Entities;
            _cleanupDirtyEntryVisits += dirtyEdges.Length;
            for (int i = 0; i < dirtyEdges.Length; i++)
            {
                Entity edgeEntity = dirtyEdges[i];
                if (ContainsEdge(first, edgeEntity) ||
                    ContainsEdge(second, edgeEntity) ||
                    ContainsEntity(dirtyEdges[..i], edgeEntity))
                {
                    continue;
                }

                DestroyIncidentEdge(graph, world, endpoint, edgeEntity, faults);
            }
        }
    }

    private void AddAffected(
        HashSet<RelationAffectedShard> destination,
        RelationEndpointPair endpoints)
    {
        if (Schema.Direction == RelationDirection.Directed)
        {
            destination.Add(new RelationAffectedShard(
                endpoints.First,
                RelationAdjacencyRole.Outgoing));
            destination.Add(new RelationAffectedShard(
                endpoints.Second,
                RelationAdjacencyRole.Incoming));
            return;
        }

        destination.Add(new RelationAffectedShard(
            endpoints.First,
            RelationAdjacencyRole.Incident));
        destination.Add(new RelationAffectedShard(
            endpoints.Second,
            RelationAdjacencyRole.Incident));
    }

    private RelationAffectedShard[] StableAffected(RelationEndpointPair endpoints)
    {
        var affected = new HashSet<RelationAffectedShard>();
        AddAffected(affected, endpoints);
        return StableAffected(affected);
    }

    private static RelationAffectedShard[] StableAffected(
        HashSet<RelationAffectedShard> affected)
    {
        RelationAffectedShard[] result = affected.ToArray();
        Array.Sort(result, static (left, right) =>
        {
            int role = left.Role.CompareTo(right.Role);
            return role != 0
                ? role
                : CompareEntities(left.Endpoint, right.Endpoint);
        });
        return result;
    }

    internal RelationAdjacencyShard<T> GetShard(
        RelationGeneration<T> generation,
        Entity endpoint,
        RelationAdjacencyRole role) => generation.GetShard(endpoint, role);

    internal (int Count, RelationAdjacencyOrderPolicy Policy) GetShardMetrics(
        RelationGeneration<T> generation,
        Entity endpoint,
        RelationAdjacencyRole role) => generation.GetShardMetrics(endpoint, role);

    private void ValidateRole(RelationAdjacencyRole role)
    {
        ValidateRole(Schema, role);
    }

    private static void ValidateRole(
        RelationSchema schema,
        RelationAdjacencyRole role)
    {
        bool valid = schema.Direction switch
        {
            RelationDirection.Directed =>
                role == RelationAdjacencyRole.Outgoing || role == RelationAdjacencyRole.Incoming,
            RelationDirection.Undirected => role == RelationAdjacencyRole.Incident,
            _ => false,
        };

        if (!valid)
        {
            throw new InvalidOperationException(
                $"Adjacency role {role} is not valid for {schema.Direction} relation {typeof(T).Name}.");
        }
    }

    private void RequireEdge(RelationEdge<T> edge)
    {
        if (!CurrentGeneration.Edges.ContainsKey(edge.Entity))
            throw new InvalidOperationException($"Entity {edge.Entity} is not a live {typeof(T).Name} relation edge.");
    }

    private void DestroyAppliedEdges(
        RelationGraph graph,
        World world,
        Entity endpoint,
        ReadOnlySpan<RelationAdjacencyEntry<T>> entries,
        ReadOnlySpan<RelationAdjacencyEntry<T>> priorEntries,
        ExceptionAccumulator faults)
    {
        _cleanupAppliedEntryVisits += entries.Length;
        for (int i = 0; i < entries.Length; i++)
        {
            Entity edgeEntity = entries[i].Edge.Entity;
            if (ContainsEdge(priorEntries, edgeEntity) ||
                ContainsEdge(entries[..i], edgeEntity))
            {
                continue;
            }

            DestroyIncidentEdge(graph, world, endpoint, edgeEntity, faults);
        }
    }

    private void DestroyIncidentEdge(
        RelationGraph graph,
        World world,
        Entity endpoint,
        Entity edgeEntity,
        ExceptionAccumulator faults)
    {
        if (edgeEntity == endpoint || !world.IsAlive(edgeEntity) || !IsEdge(edgeEntity))
            return;

        try
        {
            graph.DestroyEdgeTyped(world, this, edgeEntity, destroyEntity: true);
        }
        catch (Exception exception)
        {
            faults.Add(exception);
        }
    }

    private static bool ContainsEdge(
        ReadOnlySpan<RelationAdjacencyEntry<T>> entries,
        Entity edge)
    {
        for (int i = 0; i < entries.Length; i++)
        {
            if (entries[i].Edge.Entity == edge)
                return true;
        }

        return false;
    }

    private static bool ContainsEntity(ReadOnlySpan<Entity> entities, Entity entity)
    {
        for (int i = 0; i < entities.Length; i++)
        {
            if (entities[i] == entity)
                return true;
        }

        return false;
    }

    private static int CompareEntities(Entity left, Entity right)
    {
        int index = left.Index.CompareTo(right.Index);
        return index != 0 ? index : left.Generation.CompareTo(right.Generation);
    }
}
