using System.Runtime.InteropServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Owners;

/// <summary>
/// Owns only derived relation adjacency, constraints, ordering and maintenance
/// state. Canonical endpoints and payloads remain components on edge entities.
/// </summary>
internal sealed partial class RelationGraph
{
    private readonly RelationTypeSlotTable _states = new();
    private readonly object _stateRegistrationLock = new();
    private readonly HashSet<Entity> _destroyingEdges = new();
    private readonly RelationComponentSlotTable<IRelationEndpointTracker> _endpointTrackers = new();
    private int _commandBatchDepth;
    private readonly HashSet<Type> _commandBatchPayloads = new();

    internal bool Any => _states.Count != 0;

    /// <summary>
    /// Creates an exact detached graph root. Each type wrapper retains its immutable current
    /// generation until a prepared transition publishes a replacement; dirty-placement state,
    /// endpoint trackers and all preimages are independently mutable in the clone.
    /// </summary>
    internal RelationGraph CloneDetached()
    {
        if (_destroyingEdges.Count != 0 ||
            _commandBatchDepth != 0 ||
            _commandBatchPayloads.Count != 0)
        {
            throw new InvalidOperationException(
                "Cannot clone relation graph state while a command batch or edge destroy is active.");
        }

        var clone = new RelationGraph();
        _states.CloneDetachedInto(clone._states);

        foreach (IRelationEndpointTracker sourceTracker in _endpointTrackers)
        {
            IRelationEndpointTracker clonedTracker = sourceTracker.CloneDetached(
                clone,
                clone._states[sourceTracker.PayloadComponentId]);
            clone._endpointTrackers.Add(sourceTracker.EndpointComponentId, clonedTracker);
        }
        return clone;
    }

    internal void Reset()
    {
        _states.Clear();
        _destroyingEdges.Clear();
        _endpointTrackers.Clear();
        _commandBatchDepth = 0;
        _commandBatchPayloads.Clear();
    }

    internal void TrackEndpoint<T>(World world, Entity edge)
        where T : struct
    {
        if (!ComponentMetadata<T>.IsRelationshipSource &&
            !ComponentMetadata<T>.IsRelationshipTarget)
        {
            return;
        }

        if (_endpointTrackers.TryGetValue(ComponentMetadata<T>.Id, out var tracker))
            tracker.Capture(world, edge);
    }

    internal void TrackEndpointRange(
        World world,
        ReadOnlySpan<Entity> edges,
        int componentId)
    {
        ref readonly ComponentInfo info = ref ComponentRegistry.Get(componentId);
        if (!info.IsRelationshipSource && !info.IsRelationshipTarget)
            return;

        if (!_endpointTrackers.TryGetValue(componentId, out var tracker))
            return;

        for (int i = 0; i < edges.Length; i++)
            tracker.Capture(world, edges[i]);
    }

    internal void ValidateDeferredWrites(World world)
    {
        foreach (IRelationEndpointTracker tracker in _endpointTrackers)
            tracker.Validate(world);
    }

    internal void CommitDeferredWrites()
    {
        foreach (IRelationEndpointTracker tracker in _endpointTrackers)
            tracker.Commit();
    }

    internal void RollbackDeferredWrites(World world)
    {
        foreach (IRelationEndpointTracker tracker in _endpointTrackers)
            tracker.Rollback(world);
    }

    private bool HasPendingEndpointPreimages()
    {
        foreach (IRelationEndpointTracker tracker in _endpointTrackers)
        {
            if (tracker.HasPendingPreimages)
                return true;
        }
        return false;
    }

    internal RelationEdge<T> Create<T>(
        World world,
        Entity first,
        Entity second,
        in T payload,
        int? firstInsertIndex = null,
        int? secondInsertIndex = null,
        RelationMaintenanceTiming timing = RelationMaintenanceTiming.Immediate)
        where T : struct, IComponent
    {
        world.ThrowIfIterating();
        if (timing != RelationMaintenanceTiming.Immediate &&
            timing != RelationMaintenanceTiming.Deferred)
        {
            throw new ArgumentOutOfRangeException(nameof(timing), timing, "Unknown relation maintenance timing.");
        }

        var state = State<T>();
        bool commandBatch = TouchCommandBatch(world, state);
        if (!commandBatch)
            Maintain<T>(world, state);
        ValidateEndpoints(world, state.Schema, first, second);
        var endpointPair = new RelationEndpointPair(first, second);

        // Immediate commands execute in stable playback order against the last-applied image.
        // Deferred canonical writes do not free an applied unique key or change an applied shard
        // until maintenance. Their complete final image is validated once at batch finalization.
        // Validate with a placeholder identity before consuming an Entity slot or journal version;
        // the real prepare cannot then fail unless this single-writer World changes in between.
        if (timing == RelationMaintenanceTiming.Immediate)
        {
            state.ValidateAdd(
                endpointPair,
                firstInsertIndex,
                secondInsertIndex);
        }

        var edgeEntity = world.CreateEntity();
        var edge = new RelationEdge<T>(edgeEntity);
        PreparedRelationState<T> prepared;
        try
        {
            bool applyImmediately = timing == RelationMaintenanceTiming.Immediate;
            prepared = applyImmediately
                ? state.PrepareAdd(edge, endpointPair, firstInsertIndex, secondInsertIndex)
                : state.PrepareRegister(edge);
        }
        catch
        {
            world.DestroyEntity(edgeEntity);
            throw;
        }

        try
        {
            AddCanonicalEndpoints<T>(world, edgeEntity, state.Schema, first, second);
            world.Add(edgeEntity, new AppliedRelationEndpoints<T>
            {
                EndpointA = first,
                EndpointB = second,
                IsApplied = timing == RelationMaintenanceTiming.Immediate,
            });
            world.Add(edgeEntity, in payload);
            PublishWithMarkers(world, state, prepared);
            if (timing == RelationMaintenanceTiming.Deferred)
            {
                state.MarkDirty(
                    edge,
                    new RelationAppliedEndpointImage(endpointPair, IsApplied: false),
                    endpointPair,
                    firstInsertIndex,
                    secondInsertIndex);
            }
            return edge;
        }
        catch
        {
            state.Restore(prepared.Previous);
            if (world.IsAlive(edgeEntity))
                world.DestroyEntity(edgeEntity);
            throw;
        }
    }

    internal void Destroy<T>(World world, RelationEdge<T> edge)
        where T : struct, IComponent
    {
        world.ThrowIfIterating();
        var state = State<T>();
        bool commandBatch = TouchCommandBatch(world, state);
        if (!commandBatch)
            Maintain<T>(world, state);
        RequireLiveEdge(world, state, edge);
        DestroyAppliedEdge(world, state, edge);
        if (commandBatch)
            EndpointTracker<T>().Forget(edge.Entity);
    }

    internal void Retarget<T>(
        World world,
        RelationEdge<T> edge,
        Entity first,
        Entity second,
        RelationMaintenanceTiming timing,
        int? firstInsertIndex = null,
        int? secondInsertIndex = null)
        where T : struct, IComponent
    {
        var state = State<T>();
        bool commandBatch = TouchCommandBatch(world, state);
        RequireLiveEdge(world, state, edge);
        ValidateEndpoints(world, state.Schema, first, second);
        var current = RelationEndpointAccess.ReadCurrent<T>(world, edge.Entity, state.Schema);
        var replacement = new RelationEndpointPair(first, second);

        if (timing != RelationMaintenanceTiming.Immediate &&
            timing != RelationMaintenanceTiming.Deferred)
        {
            throw new ArgumentOutOfRangeException(nameof(timing), timing, "Unknown relation maintenance timing.");
        }

        if (timing == RelationMaintenanceTiming.Deferred)
        {
            if (current == replacement && firstInsertIndex is null && secondInsertIndex is null)
                return;
            if (!commandBatch)
            {
                ValidateDeferredImage(world, state, edge, replacement, firstInsertIndex, secondInsertIndex);
            }
            else
            {
                EndpointTracker<T>().Capture(world, edge.Entity);
            }
            var deferredApplied = RelationEndpointAccess.ReadAppliedImage<T>(world, edge.Entity);
            WriteCanonicalEndpoints<T>(world, edge.Entity, state.Schema, replacement);
            state.MarkDirty(
                edge,
                deferredApplied,
                replacement,
                firstInsertIndex,
                secondInsertIndex);
            if (commandBatch)
            {
                // This command already advanced the typed dirty revision and recorded the exact
                // placement. The endpoint tracker exists for arbitrary component ref/span writes;
                // retaining this command-owned preimage would mark the same mutation dirty again
                // during finalization and force a redundant full validation projection scan.
                EndpointTracker<T>().Forget(edge.Entity);
            }
            return;
        }

        world.ThrowIfIterating();
        if (!commandBatch)
            Maintain<T>(world, state);

        var appliedImage = RelationEndpointAccess.ReadAppliedImage<T>(world, edge.Entity);
        bool consumesPending = state.IsDirty(edge.Entity);
        if (appliedImage.IsApplied &&
            appliedImage.Endpoints == replacement &&
            firstInsertIndex is null &&
            secondInsertIndex is null)
        {
            if (consumesPending)
            {
                if (commandBatch)
                    state.TrackCommandBatchCanonical(edge, replacement);
                WriteCanonicalEndpoints<T>(world, edge.Entity, state.Schema, replacement);
                state.ClearDirty([edge]);
                if (commandBatch)
                {
                    EndpointTracker<T>().Forget(edge.Entity);
                }
            }
            return;
        }
        var applied = appliedImage.Endpoints;
        var transition = new RelationEndpointTransition<T>(
            edge,
            applied,
            replacement,
            firstInsertIndex,
            secondInsertIndex,
            appliedImage.IsApplied);
        var prepared = state.PrepareRetarget(transition);
        if (!prepared.HasChanges)
            return;

        try
        {
            WriteCanonicalEndpoints<T>(world, edge.Entity, state.Schema, replacement);
            world.Components.Replace(edge.Entity, new AppliedRelationEndpoints<T>
            {
                EndpointA = first,
                EndpointB = second,
                IsApplied = true,
            });
            PublishWithMarkers(world, state, prepared);
            if (consumesPending)
            {
                state.ClearDirty([edge]);
            }
            if (commandBatch)
                EndpointTracker<T>().Forget(edge.Entity);
        }
        catch
        {
            state.Restore(prepared.Previous);
            TryRestoreEndpoints<T>(
                world,
                edge.Entity,
                state.Schema,
                current,
                applied,
                appliedImage.IsApplied);
            throw;
        }
    }

    internal void Maintain<T>(World world)
        where T : struct, IComponent
    {
        world.ThrowIfIterating();
        Maintain<T>(world, State<T>());
    }

    internal void SetOrderPolicy<T>(
        World world,
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationAdjacencyOrderPolicy policy)
        where T : struct, IComponent
    {
        world.ThrowIfIterating();
        var state = State<T>();
        bool commandBatch = TouchCommandBatch(world, state);
        if (!commandBatch)
            Maintain<T>(world, state);
        ValidateEndpoint(world, endpoint, nameof(endpoint));
        var prepared = state.PrepareOrderPolicy(endpoint, role, policy);
        if (!prepared.HasChanges)
            return;
        PublishWithMarkers(world, state, prepared);
    }

    internal void Reorder<T>(
        World world,
        Entity endpoint,
        RelationAdjacencyRole role,
        RelationEdge<T> edge,
        int insertIndex)
        where T : struct, IComponent
    {
        world.ThrowIfIterating();
        var state = State<T>();
        bool commandBatch = TouchCommandBatch(world, state);
        if (!commandBatch)
            Maintain<T>(world, state);
        RequireLiveEdge(world, state, edge);
        if (commandBatch && state.IsDirty(edge.Entity))
        {
            ValidateEndpoint(world, endpoint, nameof(endpoint));
            var current = RelationEndpointAccess.ReadCurrent<T>(world, edge.Entity, state.Schema);
            var applied = RelationEndpointAccess.ReadAppliedImage<T>(world, edge.Entity);
            var pending = state.PendingPlacement(edge);
            int? firstIndex = pending.FirstInsertIndex;
            int? secondIndex = pending.SecondInsertIndex;
            if (state.Schema.Direction == RelationDirection.Directed)
            {
                if (role == RelationAdjacencyRole.Outgoing && endpoint == current.First)
                    firstIndex = insertIndex;
                else if (role == RelationAdjacencyRole.Incoming && endpoint == current.Second)
                    secondIndex = insertIndex;
                else
                    throw new InvalidOperationException(
                        $"Endpoint {endpoint} is not the edge's {role} endpoint.");
            }
            else if (role == RelationAdjacencyRole.Incident && endpoint == current.First)
            {
                firstIndex = insertIndex;
            }
            else if (role == RelationAdjacencyRole.Incident && endpoint == current.Second)
            {
                secondIndex = insertIndex;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Endpoint {endpoint} is not incident to edge {edge.Entity}.");
            }

            state.MarkDirty(edge, applied, current, firstIndex, secondIndex);
            return;
        }
        var prepared = state.PrepareReorder(endpoint, role, edge, insertIndex);
        if (!prepared.HasChanges)
            return;
        PublishWithMarkers(world, state, prepared);
    }

    internal RelationAdjacencySnapshot<T> Snapshot<T>(
        Entity endpoint,
        RelationAdjacencyRole role)
        where T : struct, IComponent
    {
        // Adjacency generations are immutable COW roots. Reading one must not touch the World's
        // entity/row store: relaxed inverse readers are allowed to overlap canonical writers.
        // An invalid, stale or destroyed endpoint therefore has the same sensible lookup result
        // as any key absent from this generation: an empty snapshot at the current generation.
        if (_states.TryGetValue(ComponentMetadata<T>.Id, out var existing))
            return ((RelationTypeState<T>)existing).Snapshot(endpoint, role);

        // A read must not lazily register graph/tracker state either. Match a freshly created
        // payload generation without mutating this RelationGraph.
        return RelationTypeState<T>.EmptySnapshot(RelationSchema.For<T>(), role);
    }

    internal DirectedRelationEndpoints<T> DirectedEndpoints<T>(
        World world,
        RelationEdge<T> edge)
        where T : struct, IComponent
    {
        if (!_states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing))
            throw new InvalidOperationException($"Entity {edge.Entity} is not a live {typeof(T).Name} relation edge in this World.");
        var state = (RelationTypeState<T>)existing;
        RequireLiveEdge(world, state, edge);
        RequireDirection<T>(state.Schema, RelationDirection.Directed);
        return world.Components.Read<DirectedRelationEndpoints<T>>(edge.Entity);
    }

    internal UndirectedRelationEndpoints<T> UndirectedEndpoints<T>(
        World world,
        RelationEdge<T> edge)
        where T : struct, IComponent
    {
        if (!_states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing))
            throw new InvalidOperationException($"Entity {edge.Entity} is not a live {typeof(T).Name} relation edge in this World.");
        var state = (RelationTypeState<T>)existing;
        RequireLiveEdge(world, state, edge);
        RequireDirection<T>(state.Schema, RelationDirection.Undirected);
        return world.Components.Read<UndirectedRelationEndpoints<T>>(edge.Entity);
    }

    internal RelationEdgeQuery<T> EdgesBetween<T>(Entity first, Entity second)
        where T : struct, IComponent
    {
        // Like the adjacency snapshot APIs, this lookup reads only a safely published immutable
        // generation. Stale, destroyed, or absent endpoint keys therefore produce an empty result
        // without touching the mutable entity store or lazily registering relation state.
        if (_states.TryGetValue(ComponentMetadata<T>.Id, out var existing))
            return ((RelationTypeState<T>)existing).EdgesBetween(first, second);

        _ = RelationSchema.For<T>();
        return new RelationEdgeQuery<T>(
            ReadOnlySpan<RelationAdjacencyEntry<T>>.Empty,
            second);
    }

    internal void DestroyAllBetween<T>(World world, Entity first, Entity second)
        where T : struct, IComponent
    {
        world.ThrowIfIterating();
        var state = State<T>();
        bool commandBatch = TouchCommandBatch(world, state);
        if (!commandBatch)
            Maintain<T>(world, state);
        if (!commandBatch)
        {
            RelationAdjacencyRole role = state.Schema.Direction == RelationDirection.Directed
                ? RelationAdjacencyRole.Outgoing
                : RelationAdjacencyRole.Incident;
            RelationAdjacencySnapshot<T> snapshot = state.Snapshot(first, role);
            ReadOnlySpan<RelationAdjacencyEntry<T>> entries = snapshot.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                if (entries[i].OtherEndpoint != second)
                    continue;
                RelationEdge<T> edge = entries[i].Edge;
                if (world.IsAlive(edge.Entity) && state.IsEdge(edge.Entity))
                    DestroyAppliedEdge(world, state, edge);
            }
            return;
        }

        RelationEdge<T>[] edges = state.CommandBatchEdgesBetween(world, first, second);
        for (int i = 0; i < edges.Length; i++)
        {
            if (world.IsAlive(edges[i].Entity) && state.IsEdge(edges[i].Entity))
                DestroyAppliedEdge(world, state, edges[i]);
        }
    }

    internal void DestroyAllAt<T>(
        World world,
        Entity endpoint,
        RelationAdjacencyRole role)
        where T : struct, IComponent
    {
        world.ThrowIfIterating();
        var state = State<T>();
        bool commandBatch = TouchCommandBatch(world, state);
        if (!commandBatch)
            Maintain<T>(world, state);
        if (!commandBatch)
        {
            RelationAdjacencySnapshot<T> snapshot = state.Snapshot(endpoint, role);
            ReadOnlySpan<RelationAdjacencyEntry<T>> entries = snapshot.Entries;
            for (int i = 0; i < entries.Length; i++)
            {
                RelationEdge<T> edge = entries[i].Edge;
                if (world.IsAlive(edge.Entity) && state.IsEdge(edge.Entity))
                    DestroyAppliedEdge(world, state, edge);
            }
            return;
        }

        RelationEdge<T>[] edges = state.CommandBatchEdgesAt(world, endpoint, role);
        for (int i = 0; i < edges.Length; i++)
        {
            RelationEdge<T> edge = edges[i];
            if (world.IsAlive(edge.Entity) && state.IsEdge(edge.Entity))
                DestroyAppliedEdge(world, state, edge);
        }
    }

    /// <summary>
    /// Called before an arbitrary Entity is destroyed. It removes the entity's
    /// own edge topology and every edge whose current or applied endpoints
    /// reference the entity.
    /// </summary>
    internal void CleanupEntity(World world, Entity entity)
    {
        if (_states.Count == 0)
            return;

        var faults = new ExceptionAccumulator();
        bool entityTopologyAlreadyDestroying = _destroyingEdges.Contains(entity);
        IRelationTypeState[] states = _states.SnapshotValues();
        for (int stateIndex = 0; stateIndex < states.Length; stateIndex++)
        {
            var state = states[stateIndex];
            TouchCommandBatch(state);
            if (state.IsEdge(entity) && !entityTopologyAlreadyDestroying)
            {
                try
                {
                    state.DestroyEdge(this, world, entity, destroyEntity: false);
                }
                catch (Exception exception)
                {
                    faults.Add(exception);
                }
            }

            state.DestroyIncidentEdges(this, world, entity, faults);

            if (!state.HasEndpointState(entity))
                continue;

            try
            {
                state.DropEndpointState(entity);
            }
            catch (Exception exception)
            {
                faults.Add(exception);
            }
        }
        faults.ThrowIfAny();
    }

    /// <summary>
    /// Owner-finalization hook for source refs/spans supplied by the shared
    /// component access layer. The caller must invoke this after writes and
    /// before releasing the mutation owner.
    /// </summary>
    internal void ValidateAndTrackDeferred<T>(
        World world,
        ReadOnlySpan<RelationEdge<T>> edges)
        where T : struct, IComponent
    {
        var state = State<T>();
        if (edges.Length == 0)
            return;
        ValidateDeferredState(world, state);
    }

    private static void ValidateDeferredState<T>(World world, RelationTypeState<T> state)
        where T : struct, IComponent
    {
        if (state.IsCommandBatchActive)
            return;

        List<RelationEndpointTransition<T>> transitions = CollectDirtyTransitions(
            world,
            state,
            out _);
        _ = state.PreviewBatch(CollectionsMarshal.AsSpan(transitions));
    }

    private static List<RelationEndpointTransition<T>> CollectDirtyTransitions<T>(
        World world,
        RelationTypeState<T> state,
        out int transitionVisits)
        where T : struct, IComponent
    {
        RelationEdge<T>[] dirty = state.DirtyEdgesStable();
        transitionVisits = dirty.Length;
        var transitions = new List<RelationEndpointTransition<T>>(dirty.Length);
        for (int i = 0; i < dirty.Length; i++)
        {
            RelationEdge<T> edge = dirty[i];
            if (!world.IsAlive(edge.Entity) || !state.IsEdge(edge.Entity))
                continue;
            RelationEndpointPair current =
                RelationEndpointAccess.ReadCurrent<T>(world, edge.Entity, state.Schema);
            ValidateEndpoints(world, state.Schema, current.First, current.Second);
            RelationAppliedEndpointImage applied =
                RelationEndpointAccess.ReadAppliedImage<T>(world, edge.Entity);
            RelationPendingPlacement placement = state.PendingPlacement(edge);
            transitions.Add(new RelationEndpointTransition<T>(
                edge,
                applied.Endpoints,
                current,
                placement.FirstInsertIndex,
                placement.SecondInsertIndex,
                applied.IsApplied));
        }
        return transitions;
    }

    private RelationTypeState<T> State<T>() where T : struct, IComponent
    {
        int payloadComponentId = ComponentMetadata<T>.Id;
        if (_states.TryGetValue(payloadComponentId, out var existing))
            return (RelationTypeState<T>)existing;

        lock (_stateRegistrationLock)
        {
            if (_states.TryGetValue(payloadComponentId, out existing))
                return (RelationTypeState<T>)existing;

            var state = new RelationTypeState<T>(RelationSchema.For<T>());
            int endpointComponentId = state.Schema.Direction == RelationDirection.Directed
                ? ComponentMetadata<DirectedRelationEndpoints<T>>.Id
                : ComponentMetadata<UndirectedRelationEndpoints<T>>.Id;
            var tracker = new RelationEndpointTracker<T>(
                this,
                state,
                payloadComponentId,
                endpointComponentId);
            _states.Add(payloadComponentId, state);
            _endpointTrackers.Add(endpointComponentId, tracker);
            return state;
        }
    }

    private RelationEndpointTracker<T> EndpointTracker<T>()
        where T : struct, IComponent
    {
        _ = State<T>();
        int componentId = RelationSchema.For<T>().Direction == RelationDirection.Directed
            ? ComponentMetadata<DirectedRelationEndpoints<T>>.Id
            : ComponentMetadata<UndirectedRelationEndpoints<T>>.Id;
        return (RelationEndpointTracker<T>)_endpointTrackers[componentId];
    }

    private static void Maintain<T>(World world, RelationTypeState<T> state)
        where T : struct, IComponent
    {
        if (!state.HasDirtyEdges)
            return;

        var dirty = state.DirtyEdgesStable();
        var transitions = new List<RelationEndpointTransition<T>>(dirty.Length);
        for (int i = 0; i < dirty.Length; i++)
        {
            var edge = dirty[i];
            if (!world.IsAlive(edge.Entity) || !state.IsEdge(edge.Entity))
                continue;
            var current = RelationEndpointAccess.ReadCurrent<T>(world, edge.Entity, state.Schema);
            var applied = RelationEndpointAccess.ReadAppliedImage<T>(world, edge.Entity);
            ValidateEndpoints(world, state.Schema, current.First, current.Second);
            var placement = state.PendingPlacement(edge);
            transitions.Add(new RelationEndpointTransition<T>(
                edge,
                applied.Endpoints,
                current,
                placement.FirstInsertIndex,
                placement.SecondInsertIndex,
                applied.IsApplied));
        }

        if (transitions.Count == 0)
        {
            state.ClearDirty(dirty);
            return;
        }

        var prepared = state.PrepareBatch(CollectionsMarshal.AsSpan(transitions));
        try
        {
            for (int i = 0; i < transitions.Count; i++)
            {
                var pair = transitions[i].Current;
                world.Components.Replace(transitions[i].Edge.Entity, new AppliedRelationEndpoints<T>
                {
                    EndpointA = pair.First,
                    EndpointB = pair.Second,
                    IsApplied = true,
                });
            }
            PublishWithMarkers(world, state, prepared);
            state.ClearDirty(dirty);
        }
        catch
        {
            state.Restore(prepared.Previous);
            for (int i = 0; i < transitions.Count; i++)
            {
                var pair = transitions[i].Applied;
                if (world.IsAlive(transitions[i].Edge.Entity) &&
                    world.Has<AppliedRelationEndpoints<T>>(transitions[i].Edge.Entity))
                {
                    world.Components.Replace(transitions[i].Edge.Entity, new AppliedRelationEndpoints<T>
                    {
                        EndpointA = pair.First,
                        EndpointB = pair.Second,
                        IsApplied = transitions[i].HasAppliedMembership,
                    });
                }
            }
            throw;
        }
    }

    private static PreparedRelationState<T> ValidateDeferredImage<T>(
        World world,
        RelationTypeState<T> state,
        RelationEdge<T> candidate,
        RelationEndpointPair replacement,
        int? firstInsertIndex,
        int? secondInsertIndex)
        where T : struct, IComponent
    {
        var dirty = state.DirtyEdgesStable();
        var transitions = new List<RelationEndpointTransition<T>>(dirty.Length + 1);
        for (int i = 0; i < dirty.Length; i++)
        {
            var edge = dirty[i];
            if (edge == candidate)
                continue;
            if (!world.IsAlive(edge.Entity) || !state.IsEdge(edge.Entity))
                continue;
            var applied = RelationEndpointAccess.ReadAppliedImage<T>(world, edge.Entity);
            var current = RelationEndpointAccess.ReadCurrent<T>(world, edge.Entity, state.Schema);
            var pendingPlacement = state.PendingPlacement(edge);
            int? firstIndex = pendingPlacement.FirstInsertIndex;
            int? secondIndex = pendingPlacement.SecondInsertIndex;
            transitions.Add(new RelationEndpointTransition<T>(
                edge,
                applied.Endpoints,
                current,
                firstIndex,
                secondIndex,
                applied.IsApplied));
        }

        var candidateApplied = RelationEndpointAccess.ReadAppliedImage<T>(world, candidate.Entity);
        transitions.Add(new RelationEndpointTransition<T>(
            candidate,
            candidateApplied.Endpoints,
            replacement,
            firstInsertIndex,
            secondInsertIndex,
            candidateApplied.IsApplied));

        return state.PreviewBatch(CollectionsMarshal.AsSpan(transitions));
    }

    private static void ValidateCreateAgainstDeferredImage<T>(
        World world,
        RelationTypeState<T> state,
        RelationEndpointPair endpoints,
        int? firstInsertIndex,
        int? secondInsertIndex)
        where T : struct, IComponent
    {
        var dirty = state.DirtyEdgesStable();
        var transitions = new List<RelationEndpointTransition<T>>(dirty.Length);
        for (int i = 0; i < dirty.Length; i++)
        {
            var edge = dirty[i];
            if (!world.IsAlive(edge.Entity) || !state.IsEdge(edge.Entity))
                continue;
            var applied = RelationEndpointAccess.ReadAppliedImage<T>(world, edge.Entity);
            var current = RelationEndpointAccess.ReadCurrent<T>(world, edge.Entity, state.Schema);
            ValidateEndpoints(world, state.Schema, current.First, current.Second);
            var placement = state.PendingPlacement(edge);
            transitions.Add(new RelationEndpointTransition<T>(
                edge,
                applied.Endpoints,
                current,
                placement.FirstInsertIndex,
                placement.SecondInsertIndex,
                applied.IsApplied));
        }

        state.ValidateAddAgainstBatchImage(
            CollectionsMarshal.AsSpan(transitions),
            endpoints,
            firstInsertIndex,
            secondInsertIndex);
    }

    private void DestroyAppliedEdge<T>(
        World world,
        RelationTypeState<T> state,
        RelationEdge<T> edge)
        where T : struct, IComponent
    {
        if (_commandBatchDepth != 0)
            _ = TouchCommandBatch(world, state);
        if (!_destroyingEdges.Add(edge.Entity))
            return;
        try
        {
            var applied = RelationEndpointAccess.ReadAppliedImage<T>(world, edge.Entity);
            var prepared = state.PrepareRemove(edge, applied.Endpoints, applied.IsApplied);
            state.ClearDirty([edge]);
            PublishWithMarkers(world, state, prepared);
            try
            {
                world.DestroyEntity(edge.Entity);
            }
            catch
            {
                if (world.IsAlive(edge.Entity) && !world.IsPendingCleanup(edge.Entity))
                {
                    state.Restore(prepared.Previous);
                    SynchronizeMarkers(
                        world,
                        state,
                        prepared.Previous,
                        prepared.AffectedShards);
                }
                throw;
            }
        }
        finally
        {
            _destroyingEdges.Remove(edge.Entity);
        }
    }

    internal void DestroyEdgeTyped<T>(
        World world,
        RelationTypeState<T> state,
        Entity edgeEntity,
        bool destroyEntity)
        where T : struct, IComponent
    {
        if (_commandBatchDepth != 0)
            _ = TouchCommandBatch(world, state);
        var edge = new RelationEdge<T>(edgeEntity);
        if (!state.IsEdge(edgeEntity) || !_destroyingEdges.Add(edgeEntity))
            return;
        try
        {
            var applied = RelationEndpointAccess.ReadAppliedImage<T>(world, edgeEntity);
            var prepared = state.PrepareRemove(edge, applied.Endpoints, applied.IsApplied);
            state.ClearDirty([edge]);
            PublishWithMarkers(world, state, prepared);
            if (destroyEntity && world.IsAlive(edgeEntity))
                world.DestroyEntity(edgeEntity);
        }
        finally
        {
            _destroyingEdges.Remove(edgeEntity);
        }
    }

    private static void PublishWithMarkers<T>(
        World world,
        RelationTypeState<T> state,
        PreparedRelationState<T> prepared)
        where T : struct, IComponent
    {
        state.Publish(prepared);
        try
        {
            SynchronizeMarkers(
                world,
                state,
                prepared.Next,
                prepared.AffectedShards);
        }
        catch
        {
            state.Restore(prepared.Previous);
            SynchronizeMarkers(
                world,
                state,
                prepared.Previous,
                prepared.AffectedShards);
            throw;
        }
    }

    private static void SynchronizeMarkers<T>(
        World world,
        RelationTypeState<T> state,
        RelationGeneration<T> next,
        ReadOnlySpan<RelationAffectedShard> affectedShards)
        where T : struct, IComponent
    {
        for (int i = 0; i < affectedShards.Length; i++)
        {
            Entity endpoint = affectedShards[i].Endpoint;
            RelationAdjacencyRole role = affectedShards[i].Role;
            if (!world.IsAlive(endpoint) || world.IsPendingCleanup(endpoint))
                continue;
            var shard = state.GetShardMetrics(next, endpoint, role);
            bool shouldExist = shard.Count != 0 ||
                               shard.Policy == RelationAdjacencyOrderPolicy.Ordered;
            switch (role)
            {
                case RelationAdjacencyRole.Outgoing:
                    WriteMarker(world, endpoint, new Outgoing<T>(shard.Count, next.Id), shouldExist);
                    break;
                case RelationAdjacencyRole.Incoming:
                    WriteMarker(world, endpoint, new Incoming<T>(shard.Count, next.Id), shouldExist);
                    break;
                case RelationAdjacencyRole.Incident:
                    WriteMarker(world, endpoint, new Incident<T>(shard.Count, next.Id), shouldExist);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(role));
            }
        }
    }

    private static void WriteMarker<TMarker>(
        World world,
        Entity endpoint,
        in TMarker marker,
        bool shouldExist)
        where TMarker : struct, IRelationshipTarget
    {
        bool exists = world.Has<TMarker>(endpoint);
        if (!shouldExist)
        {
            if (exists)
                world.Components.Remove<TMarker>(endpoint);
            return;
        }

        if (exists)
            world.Components.Replace(endpoint, in marker);
        else
            world.Components.Add(endpoint, in marker);
    }

    private static void AddCanonicalEndpoints<T>(
        World world,
        Entity edge,
        RelationSchema schema,
        Entity first,
        Entity second)
        where T : struct, IComponent
    {
        if (schema.Direction == RelationDirection.Directed)
        {
            world.Components.Add(edge, new DirectedRelationEndpoints<T>
            {
                Source = first,
                Target = second,
            });
            return;
        }

        world.Components.Add(edge, new UndirectedRelationEndpoints<T>
        {
            EndpointA = first,
            EndpointB = second,
        });
    }

    private static void WriteCanonicalEndpoints<T>(
        World world,
        Entity edge,
        RelationSchema schema,
        RelationEndpointPair endpoints)
        where T : struct, IComponent
    {
        if (schema.Direction == RelationDirection.Directed)
        {
            world.Components.Replace(edge, new DirectedRelationEndpoints<T>
            {
                Source = endpoints.First,
                Target = endpoints.Second,
            });
            return;
        }

        world.Components.Replace(edge, new UndirectedRelationEndpoints<T>
        {
            EndpointA = endpoints.First,
            EndpointB = endpoints.Second,
        });
    }

    private static void TryRestoreEndpoints<T>(
        World world,
        Entity edge,
        RelationSchema schema,
        RelationEndpointPair current,
        RelationEndpointPair applied,
        bool wasApplied)
        where T : struct, IComponent
    {
        if (!world.IsAlive(edge))
            return;
        WriteCanonicalEndpoints<T>(world, edge, schema, current);
        world.Components.Replace(edge, new AppliedRelationEndpoints<T>
        {
            EndpointA = applied.First,
            EndpointB = applied.Second,
            IsApplied = wasApplied,
        });
    }

    private static void RequireLiveEdge<T>(
        World world,
        RelationTypeState<T> state,
        RelationEdge<T> edge)
        where T : struct, IComponent
    {
        if (!world.IsAlive(edge.Entity) ||
            !state.IsEdge(edge.Entity) ||
            !RelationEndpointAccess.HasCurrent<T>(world, edge.Entity, state.Schema))
        {
            throw new InvalidOperationException(
                $"Entity {edge.Entity} is not a live {typeof(T).Name} relation edge in this World.");
        }
    }

    internal RelationSchema Schema<T>() where T : struct, IComponent => RelationSchema.For<T>();

    internal TopologyOrderDiagnostics OrderDiagnostics<T>()
        where T : struct, IComponent =>
        _states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing)
            ? ((RelationTypeState<T>)existing).OrderDiagnostics
            : default;

    internal object? StateBackingIdentity<T>()
        where T : struct, IComponent =>
        _states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing)
            ? ((RelationTypeState<T>)existing).BackingIdentity
            : null;

    internal int StateDetachCount<T>()
        where T : struct, IComponent =>
        _states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing)
            ? ((RelationTypeState<T>)existing).DetachCount
            : 0;

    internal long StateFullCloneCount<T>()
        where T : struct, IComponent =>
        _states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing)
            ? ((RelationTypeState<T>)existing).FullCloneCount
            : 0;

    internal RelationAdjacencyBatchDiagnostics StateAdjacencyBatchDiagnostics<T>()
        where T : struct, IComponent =>
        _states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing)
            ? ((RelationTypeState<T>)existing).AdjacencyBatchDiagnostics
            : default;

    internal RelationCommandBatchValidationDiagnostics StateCommandBatchValidationDiagnostics<T>()
        where T : struct, IComponent =>
        _states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing)
            ? ((RelationTypeState<T>)existing).CommandBatchValidationDiagnostics
            : default;

    internal RelationCanonicalLookupDiagnostics StateCanonicalLookupDiagnostics<T>()
        where T : struct, IComponent =>
        _states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing)
            ? ((RelationTypeState<T>)existing).CanonicalLookupDiagnostics
            : default;

    internal bool HasEndpointState<T>(Entity endpoint)
        where T : struct, IComponent =>
        _states.TryGetValue(ComponentMetadata<T>.Id, out IRelationTypeState? existing) &&
        ((RelationTypeState<T>)existing).HasEndpointState(endpoint);

    private static void RequireDirection<T>(RelationSchema schema, RelationDirection direction)
        where T : struct, IComponent
    {
        if (schema.Direction != direction)
        {
            throw new InvalidOperationException(
                $"Relation payload {typeof(T).Name} is {schema.Direction}, not {direction}.");
        }
    }

    private static void ValidateEndpoints(
        World world,
        RelationSchema schema,
        Entity first,
        Entity second)
    {
        ValidateEndpoint(world, first, nameof(first));
        ValidateEndpoint(world, second, nameof(second));
        if (!schema.AllowSelfEdge && first == second)
            throw new InvalidOperationException("This relation type does not allow self-edges.");
    }

    private static void ValidateEndpoint(World world, Entity endpoint, string parameter)
    {
        if (endpoint == Entity.Null || !world.IsAlive(endpoint))
            throw new InvalidOperationException($"Relation {parameter} endpoint {endpoint} is not alive.");
        if (world.IsPendingCleanup(endpoint))
            throw new InvalidOperationException($"Relation {parameter} endpoint {endpoint} is pending cleanup.");
    }

    private static int CompareEntities(Entity left, Entity right)
    {
        int index = left.Index.CompareTo(right.Index);
        return index != 0 ? index : left.Generation.CompareTo(right.Generation);
    }

}
