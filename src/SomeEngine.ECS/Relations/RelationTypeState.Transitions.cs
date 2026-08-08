using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Owners;

namespace SomeEngine.ECS.Relations;

internal sealed partial class RelationTypeState<T> : IRelationTypeState
    where T : struct, IComponent
{
    internal PreparedRelationState<T> PrepareAdd(
        RelationEdge<T> edge,
        RelationEndpointPair endpoints,
        int? firstInsertIndex = null,
        int? secondInsertIndex = null)
    {
        RelationGeneration<T> current = CurrentGeneration;
        if (current.Edges.ContainsKey(edge.Entity))
            throw new InvalidOperationException($"Relation edge {edge.Entity} is already registered as {typeof(T).Name}.");

        RelationGeneration<T> next = WritableGeneration(out RelationGeneration<T> previous);
        next.Edges.Add(edge.Entity, 0);
        next.Attach(edge, endpoints, firstInsertIndex, secondInsertIndex);
        UpsertCommandBatchCanonical(edge.Entity, endpoints);
        return new PreparedRelationState<T>(
            previous,
            next,
            StableAffected(endpoints));
    }

    internal void ValidateAdd(
        RelationEndpointPair endpoints,
        int? firstInsertIndex = null,
        int? secondInsertIndex = null) =>
        CurrentGeneration.ValidateNewEdge(endpoints, firstInsertIndex, secondInsertIndex);

    internal PreparedRelationState<T> PrepareRegister(RelationEdge<T> edge)
    {
        RelationGeneration<T> current = CurrentGeneration;
        if (current.Edges.ContainsKey(edge.Entity))
            throw new InvalidOperationException($"Relation edge {edge.Entity} is already registered as {typeof(T).Name}.");

        RelationGeneration<T> next = WritableGeneration(out RelationGeneration<T> previous);
        next.Edges.Add(edge.Entity, 0);
        return new PreparedRelationState<T>(
            previous,
            next,
            Array.Empty<RelationAffectedShard>());
    }

    internal PreparedRelationState<T> PrepareRemove(
        RelationEdge<T> edge,
        RelationEndpointPair applied,
        bool hasAppliedMembership = true)
    {
        RequireEdge(edge);
        RelationGeneration<T> next = WritableGeneration(out RelationGeneration<T> previous);
        if (hasAppliedMembership)
            next.Detach(edge, applied);
        next.Edges.Remove(edge.Entity);
        RemoveCommandBatchCanonical(edge.Entity);
        return new PreparedRelationState<T>(
            previous,
            next,
            hasAppliedMembership
                ? StableAffected(applied)
                : Array.Empty<RelationAffectedShard>());
    }

    internal PreparedRelationState<T> PrepareRetarget(
        RelationEndpointTransition<T> transition)
    {
        RequireEdge(transition.Edge);
        UpsertCommandBatchCanonical(transition.Edge.Entity, transition.Current);
        if (transition.HasAppliedMembership &&
            transition.Applied == transition.Current &&
            transition.FirstInsertIndex is null &&
            transition.SecondInsertIndex is null)
        {
            return new PreparedRelationState<T>(
                CurrentGeneration,
                CurrentGeneration,
                Array.Empty<RelationAffectedShard>(),
                hasChanges: false);
        }

        return PrepareBatch([transition]);
    }

    internal PreparedRelationState<T> PrepareBatch(
        ReadOnlySpan<RelationEndpointTransition<T>> transitions) =>
        PrepareBatchCore(transitions, useCommandBatchWorkspace: true);

    /// <summary>
    /// Builds a disposable final-image preview for validation/serialization. Unlike a published
    /// transition this must never mutate the command batch's single writable generation.
    /// </summary>
    internal PreparedRelationState<T> PreviewBatch(
        ReadOnlySpan<RelationEndpointTransition<T>> transitions) =>
        PrepareBatchCore(transitions, useCommandBatchWorkspace: false);

    private PreparedRelationState<T> PrepareBatchCore(
        ReadOnlySpan<RelationEndpointTransition<T>> transitions,
        bool useCommandBatchWorkspace)
    {
        if (transitions.Length == 0)
        {
            RelationGeneration<T> current = CurrentGeneration;
            return new PreparedRelationState<T>(
                current,
                current,
                Array.Empty<RelationAffectedShard>(),
                hasChanges: false);
        }

        RelationGeneration<T> previous = CurrentGeneration;
        RelationGeneration<T> next = useCommandBatchWorkspace
            ? WritableGeneration(out previous)
            : CloneGeneration(previous);
        bool ownsAdjacencyBatch = transitions.Length > 1 && !next.IsAdjacencyBatchActive;
        if (ownsAdjacencyBatch)
            next.BeginAdjacencyBatch();
        var seen = new HashSet<Entity>();
        var affected = new HashSet<RelationAffectedShard>();
        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            RequireEdge(transition.Edge);
            if (!seen.Add(transition.Edge.Entity))
            {
                throw new InvalidOperationException(
                    $"Relation edge {transition.Edge.Entity} appears more than once in one endpoint transition batch.");
            }

            if (transition.HasAppliedMembership)
            {
                next.DetachCardinality(transition.Edge, transition.Applied);
                AddAffected(affected, transition.Applied);
            }
            AddAffected(affected, transition.Current);
        }

        // Detach the complete preimage before validating/attaching the final
        // image. This permits valid swaps under uniqueness constraints.
        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            next.AttachCardinality(transition.Edge, transition.Current);
        }

        // Cardinality is validated against the complete final image above,
        // while the same prepared generation keeps the original adjacency image
        // and applies local membership changes in mutation sequence. A second
        // full generation would copy edge/cardinality state that ordering never uses.
        for (int i = 0; i < transitions.Length; i++)
        {
            var transition = transitions[i];
            if (transition.HasAppliedMembership)
                next.DetachAdjacency(transition.Edge, transition.Applied);
            next.AttachAdjacency(
                transition.Edge,
                transition.Current,
                transition.FirstInsertIndex,
                transition.SecondInsertIndex);
            if (useCommandBatchWorkspace)
                UpsertCommandBatchCanonical(transition.Edge.Entity, transition.Current);
        }

        if (ownsAdjacencyBatch)
            next.FreezeAdjacencyBatch();

        return new PreparedRelationState<T>(
            previous,
            next,
            StableAffected(affected));
    }

    internal void ValidateAddAgainstBatchImage(
        ReadOnlySpan<RelationEndpointTransition<T>> transitions,
        RelationEndpointPair endpoints,
        int? firstInsertIndex = null,
        int? secondInsertIndex = null)
    {
        // A command batch can begin while older deferred canonical endpoint
        // writes are still waiting for maintenance. Validate a new edge
        // against that complete canonical image, not only the last-applied
        // adjacency generation.
        var prepared = PreviewBatch(transitions);
        prepared.Next.ValidateNewEdge(endpoints, firstInsertIndex, secondInsertIndex);
    }

    internal PreparedRelationState<T> PrepareOrderPolicy(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationAdjacencyOrderPolicy policy)
    {
        ValidateRole(role);
        RelationGeneration<T> current = CurrentGeneration;
        if (current.GetShardPolicy(endpoint, role) == policy)
        {
            return new PreparedRelationState<T>(
                current,
                current,
                Array.Empty<RelationAffectedShard>(),
                hasChanges: false);
        }
        RelationGeneration<T> next = WritableGeneration(out RelationGeneration<T> previous);
        next.SetOrderPolicy(endpoint, role, policy);
        return new PreparedRelationState<T>(
            previous,
            next,
            [new RelationAffectedShard(endpoint, role)]);
    }

    internal PreparedRelationState<T> PrepareReorder(
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationEdge<T> edge,
        int insertIndex)
    {
        RequireEdge(edge);
        ValidateRole(role);
        RelationGeneration<T> next = WritableGeneration(out RelationGeneration<T> previous);
        if (!next.Reorder(endpoint, role, edge, insertIndex))
        {
            return new PreparedRelationState<T>(
                previous,
                previous,
                Array.Empty<RelationAffectedShard>(),
                hasChanges: false);
        }
        return new PreparedRelationState<T>(
            previous,
            next,
            [new RelationAffectedShard(endpoint, role)]);
    }

    internal void Publish(PreparedRelationState<T> prepared)
    {
        if (!prepared.HasChanges)
            return;

        if (_commandBatchBase is not null)
        {
            if (!ReferenceEquals(_commandBatchGeneration, prepared.Next) ||
                (!ReferenceEquals(prepared.Previous, _commandBatchBase) &&
                 !ReferenceEquals(prepared.Previous, _commandBatchGeneration)))
            {
                throw new InvalidOperationException(
                    "Relation command-batch generation changed while a prepared transition was pending.");
            }

            _commandBatchHasChanges = true;
            return;
        }

        if (!ReferenceEquals(Volatile.Read(ref _generation), prepared.Previous))
            throw new InvalidOperationException("Relation generation changed while a prepared transition was pending.");

        PublishGeneration(prepared.Previous, prepared.Next);
    }

    internal void Restore(RelationGeneration<T> generation)
    {
        if (_commandBatchBase is not null)
        {
            // A command batch runs against a detached structural candidate. Any operation fault
            // aborts that candidate, so rewinding a shared mutable builder would require another
            // full copy for state that can never be published. Keep it internally coherent for
            // cleanup and let EndCommandBatch(false) discard it.
            return;
        }
        Volatile.Write(ref _generation, generation);
    }

    private RelationGeneration<T> CurrentGeneration =>
        _commandBatchGeneration ?? Volatile.Read(ref _generation);

    private RelationGeneration<T> WritableGeneration(
        out RelationGeneration<T> previous)
    {
        previous = CurrentGeneration;
        if (_commandBatchBase is null)
            return CloneGeneration(previous);

        if (_commandBatchGeneration is null)
        {
            _commandBatchGeneration = CloneGeneration(_commandBatchBase);
            _commandBatchGeneration.BeginAdjacencyBatch();
        }
        return _commandBatchGeneration;
    }

    private RelationGeneration<T> CloneGeneration(RelationGeneration<T> source)
    {
        _fullCloneCount++;
        return source.CloneNext(_orderDiagnostics, _adjacencyBatchDiagnostics);
    }

    internal bool IsCommandBatchActive => _commandBatchBase is not null;

    internal bool RequiresCommandBatchValidationSynchronization
    {
        get
        {
            RequireCommandBatch();
            return !_commandBatchValidationHasSynchronizedDirtyRevision ||
                   _commandBatchValidationSyncedDirtyRevision != _dirtyMutationRevision;
        }
    }

    internal void RecordCommandBatchValidationFullScan(int transitionVisits)
    {
        RequireCommandBatch();
        _commandBatchValidationFullScanCount++;
        _commandBatchValidationTransitionVisitCount += transitionVisits;
    }

    internal void MarkCommandBatchValidationSynchronized()
    {
        RequireCommandBatch();
        _commandBatchValidationSyncedDirtyRevision = _dirtyMutationRevision;
        _commandBatchValidationHasSynchronizedDirtyRevision = true;
    }

    private void RequireCommandBatch()
    {
        if (_commandBatchBase is null)
            throw new InvalidOperationException("A relation command batch is required for final-image validation.");
    }

    private void PublishGeneration(
        RelationGeneration<T> previous,
        RelationGeneration<T> next)
    {
        if (!ReferenceEquals(previous, next) && previous.IsShared)
            DetachCount++;
        Volatile.Write(ref _generation, next);
    }
}
