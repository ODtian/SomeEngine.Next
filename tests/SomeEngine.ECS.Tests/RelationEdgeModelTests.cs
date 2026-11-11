using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using Xunit;

namespace SomeEngine.ECS.Tests;

public sealed class RelationEdgeModelTests
{
    [Fact]
    public void Schema_IsStaticPerPayload_AndRejectsInvalidUndirectedCardinality()
    {
        var directed = RelationSchema.For<ParallelDirected>();
        var undirected = RelationSchema.For<UniquePairUndirected>();

        Assert.Equal(RelationDirection.Directed, directed.Direction);
        Assert.Equal(RelationCardinality.Parallel, directed.Cardinality);
        Assert.Equal(RelationDirection.Undirected, undirected.Direction);
        Assert.Equal(RelationCardinality.UniquePair, undirected.Cardinality);
        Assert.Throws<InvalidOperationException>(
            () => RelationSchema.For<InvalidUndirectedUniqueSource>());
    }

    [Fact]
    public void ParallelEdges_HaveIndependentEntityIdentityPayloadAndLifetime()
    {
        var world = new World();
        var source = world.Create();
        var target = world.Create();
        int baselineCount = world.EntityCount;

        var first = world.CreateRelation(source, target, new ParallelDirected { Value = 1 });
        var second = world.CreateRelation(source, target, new ParallelDirected { Value = 2 });

        Assert.NotEqual(first, second);
        Assert.Equal(baselineCount + 2, world.EntityCount);
        Assert.Equal(1, world.Read<ParallelDirected>(first.Entity).Value);
        Assert.Equal(2, world.Read<ParallelDirected>(second.Entity).Value);
        Assert.Equal(2, ReadOutgoing<ParallelDirected>(world, source).Length);
        Assert.Equal(2, ReadIncoming<ParallelDirected>(world, target).Length);

        world.DestroyRelation(first);

        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(first.Entity);

        Assert.False(world.IsAlive(first.Entity));
        Assert.True(world.IsAlive(second.Entity));
        Assert.Equal(baselineCount + 1, world.EntityCount);
        Assert.Single(ReadOutgoing<ParallelDirected>(world, source).ToArray());
    }

    [Fact]
    public void EndpointsAndAdjacency_UseNativeAddedChangedRemovedFacts()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        Entity replacement = world.Create();
        ulong before = world.Version;
        RelationEdge<ParallelDirected> edge = world.CreateRelation(source, target, new ParallelDirected { Value = 1 });
        Query added = world.Query(new QueryDescription().WithAdded<RelationEndpoints<ParallelDirected>>(before));
        Assert.Equal(1, added.Count);
        Assert.Equal(edge, Assert.Single(ReadOutgoing<ParallelDirected>(world, source)).Edge);
        Assert.Equal(edge, Assert.Single(ReadIncoming<ParallelDirected>(world, target)).Edge);
        ulong beforeRetarget = world.Version;
        Relations<ParallelDirected>.Retarget(world, edge, source, replacement);
        Query changed = world.Query(new QueryDescription().WithChanged<RelationEndpoints<ParallelDirected>>(beforeRetarget));
        Assert.Equal(1, changed.Count);
        Assert.Empty(ReadIncoming<ParallelDirected>(world, target));
        Assert.Equal(edge, Assert.Single(ReadIncoming<ParallelDirected>(world, replacement)).Edge);
        world.DestroyRelation(edge);
        Assert.False(world.Has<RelationEndpoints<ParallelDirected>>(edge.Entity));
        Assert.True(world.Has<Removed<RelationEndpoints<ParallelDirected>>>(edge.Entity));
        Assert.Equal(new RelationEndpoints<ParallelDirected>(source, replacement),
            world.Read<Removed<RelationEndpoints<ParallelDirected>>>(edge.Entity).Value);
        Assert.Empty(ReadOutgoing<ParallelDirected>(world, source));
        Assert.Empty(ReadIncoming<ParallelDirected>(world, replacement));
    }

    [Fact]
    public void EdgeEntity_IsDirectlyQueryableAsOnePayloadAndEndpointRow()
    {
        var world = new World();
        var source = world.Create();
        var target = world.Create();
        var edge = world.CreateRelation(source, target, new ParallelDirected { Value = 41 });
        var query = world.Query(
            new QueryDescription().WithStored<ParallelDirected>().WithStored<RelationEndpoints<ParallelDirected>>());

        var rows = new List<Entity>();
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
            {
                rows.Add(rowChunk.Entities[row]);
                Assert.Equal(41, rowChunk.Read<ParallelDirected>()[row].Value);
                Assert.Equal(source, rowChunk.Read<RelationEndpoints<ParallelDirected>>()[row].Source);
                Assert.Equal(target, rowChunk.Read<RelationEndpoints<ParallelDirected>>()[row].Target);
            }

        };

        Assert.Equal([edge.Entity], rows);
    }

    [Fact]
    public void CreateDuringIteration_ThrowsBeforeAllocatingEdgeEntity()
    {
        var world = new World();
        var source = world.Create(new QueryProbe());
        var target = world.Create();
        int baseline = world.EntityCount;
        var query = world.Query(new QueryDescription().WithStored<QueryProbe>());

        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk _Chunk in cursor)
                for (int _ = 0; _ < _Chunk.Count; _++)
            {
                Assert.Throws<InvalidOperationException>(
                    () => world.CreateRelation(source, target, new ParallelDirected()));
            }

        };

        Assert.Equal(baseline, world.EntityCount);
    }

    [Fact]
    public void DestroyingEndpoint_DestroysEveryIncidentEdgeAndCleansSurvivingAdjacency()
    {
        var world = new World();
        var source = world.Create();
        var targetA = world.Create();
        var targetB = world.Create();
        var incomingSource = world.Create();
        var first = world.CreateRelation(source, targetA, new ParallelDirected { Value = 1 });
        var second = world.CreateRelation(source, targetB, new ParallelDirected { Value = 2 });
        var incoming = world.CreateRelation(incomingSource, source, new ParallelDirected { Value = 3 });

        world.Destroy(source);

        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(first.Entity);

        Assert.False(world.IsAlive(first.Entity));
        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(second.Entity);
        Assert.False(world.IsAlive(second.Entity));
        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(incoming.Entity);
        Assert.False(world.IsAlive(incoming.Entity));
        Assert.Empty(ReadIncoming<ParallelDirected>(world, targetA).ToArray());
        Assert.Empty(ReadIncoming<ParallelDirected>(world, targetB).ToArray());
        Assert.Empty(ReadOutgoing<ParallelDirected>(world, incomingSource).ToArray());
    }

    [Fact]
    public void DestroyingFormerAppliedEndpointKeepsRetargetedEdge()
    {
        var world = new World();
        var source = world.Create();
        var oldTarget = world.Create();
        var newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        world.RetargetRelationDeferred(edge, source, newTarget);

        world.Destroy(oldTarget);

        Assert.True(world.IsAlive(edge.Entity));
        Assert.Equal(newTarget, ReadEndpoints(world, edge).Target);
        Assert.Empty(ReadIncoming<ParallelDirected>(world, oldTarget));
        Assert.Equal(edge, Assert.Single(ReadOutgoing<ParallelDirected>(world, source)).Edge);
        Assert.Equal(edge, Assert.Single(ReadIncoming<ParallelDirected>(world, newTarget)).Edge);
        world.MaintainRelations<ParallelDirected>();
        Assert.Equal(edge, Assert.Single(ReadIncoming<ParallelDirected>(world, newTarget)).Edge);
    }

    [Fact]
    public void DestroyingCurrentOnlyEndpointDuringDeferredWindow_DestroysTheEdge()
    {
        var world = new World();
        var source = world.Create();
        var oldTarget = world.Create();
        var newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        world.RetargetRelationDeferred(edge, source, newTarget);

        world.Destroy(newTarget);

        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(edge.Entity);

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(ReadOutgoing<ParallelDirected>(world, source).ToArray());
        Assert.Empty(ReadIncoming<ParallelDirected>(world, oldTarget).ToArray());
    }

    [Fact]
    public void DestroyingDeferredCreateEndpointBeforeFirstMaintain_DestroysTheEdge()
    {
        var world = new World();
        var source = world.Create();
        var target = world.Create();
        var edge = world.CreateRelation(
            source,
            target,
            new ParallelDirected(),
            true);

        world.Destroy(target);

        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(edge.Entity);

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(ReadOutgoing<ParallelDirected>(world, source).ToArray());
    }

    [Fact]
    public void DestroyingEdgeEntityDirectly_CleansGraphStateWithoutTypedDestroyCall()
    {
        var world = new World();
        var source = world.Create();
        var target = world.Create();
        var edge = world.CreateRelation(source, target, new ParallelDirected());

        world.Destroy(edge.Entity);

        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(edge.Entity);

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(ReadOutgoing<ParallelDirected>(world, source).ToArray());
        Assert.Empty(ReadIncoming<ParallelDirected>(world, target).ToArray());
    }

    [Fact]
    public void EveryCardinality_RejectsOnlyItsDeclaredCollision()
    {
        var world = new World();
        var a = world.Create();
        var b = world.Create();
        var c = world.Create();
        var d = world.Create();

        world.CreateRelation(a, b, new UniquePairDirected());
        world.CreateRelation(a, c, new UniquePairDirected());
        Assert.Throws<InvalidOperationException>(
            () => world.CreateRelation(a, b, new UniquePairDirected()));

        world.CreateRelation(a, b, new UniqueSourceDirected());
        Assert.Throws<InvalidOperationException>(
            () => world.CreateRelation(a, c, new UniqueSourceDirected()));
        world.CreateRelation(c, b, new UniqueSourceDirected());

        world.CreateRelation(a, b, new UniqueTargetDirected());
        Assert.Throws<InvalidOperationException>(
            () => world.CreateRelation(c, b, new UniqueTargetDirected()));
        world.CreateRelation(a, d, new UniqueTargetDirected());

        world.CreateRelation(a, b, new OneToOneDirected());
        Assert.Throws<InvalidOperationException>(
            () => world.CreateRelation(a, c, new OneToOneDirected()));
        Assert.Throws<InvalidOperationException>(
            () => world.CreateRelation(c, b, new OneToOneDirected()));
        world.CreateRelation(c, d, new OneToOneDirected());

        world.CreateRelation(a, b, new UniquePairUndirected());
        Assert.Throws<InvalidOperationException>(
            () => world.CreateRelation(b, a, new UniquePairUndirected()));
    }

    [Fact]
    public void RejectedCreate_DoesNotConsumeEntityIdentity()
    {
        var failedWorld = new World();
        var failedA = failedWorld.Create();
        var failedB = failedWorld.Create();
        failedWorld.CreateRelation(failedA, failedB, new UniquePairDirected());
        Assert.Throws<InvalidOperationException>(
            () => failedWorld.CreateRelation(failedA, failedB, new UniquePairDirected()));
        Entity afterFailure = failedWorld.Create();

        var controlWorld = new World();
        var controlA = controlWorld.Create();
        var controlB = controlWorld.Create();
        controlWorld.CreateRelation(controlA, controlB, new UniquePairDirected());
        Entity withoutFailure = controlWorld.Create();

        Assert.Equal(withoutFailure, afterFailure);
    }

    [Fact]
    public void DirectedDeferredRetarget_ExposesCanonicalEndpointsThenMaintainsAdjacency()
    {
        var world = new World();
        var source = world.Create();
        var oldTarget = world.Create();
        var newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected { Value = 7 });

        world.RetargetRelationDeferred(edge, source, newTarget);

        var canonical = ReadEndpoints(world, edge);
        Assert.Equal(source, canonical.Source);
        Assert.Equal(newTarget, canonical.Target);
        Assert.Single(ReadIncoming<ParallelDirected>(world, oldTarget).ToArray());
        Assert.Empty(ReadIncoming<ParallelDirected>(world, newTarget).ToArray());

        world.MaintainRelations<ParallelDirected>();

        Assert.Empty(ReadIncoming<ParallelDirected>(world, oldTarget).ToArray());
        Assert.Equal(edge, Assert.Single(ReadIncoming<ParallelDirected>(world, newTarget).ToArray()).Edge);
        Assert.Equal(7, world.Read<ParallelDirected>(edge.Entity).Value);
    }

    [Fact]
    public void DeferredCreate_PublishesCanonicalEdgeBeforeAppliedAdjacency()
    {
        var world = new World();
        var source = world.Create();
        var target = world.Create();

        var edge = world.CreateRelation(
            source,
            target,
            new ParallelDirected { Value = 23 },
            true);

        Assert.True(world.IsAlive(edge.Entity));
        Assert.Equal(23, world.Read<ParallelDirected>(edge.Entity).Value);
        Assert.Equal(target, ReadEndpoints(world, edge).Target);
        Assert.Empty(ReadOutgoing<ParallelDirected>(world, source).ToArray());
        Assert.Empty(ReadIncoming<ParallelDirected>(world, target).ToArray());

        world.MaintainRelations<ParallelDirected>();

        Assert.Equal(edge, Assert.Single(
            ReadOutgoing<ParallelDirected>(world, source).ToArray()).Edge);
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<ParallelDirected>(world, target).ToArray()).Edge);
    }

    [Fact]
    public void ImmediateSameEndpointRetarget_FirstFlushesPendingAdjacency()
    {
        var world = new World();
        var source = world.Create();
        var oldTarget = world.Create();
        var newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        world.RetargetRelationDeferred(edge, source, newTarget);

        world.RetargetRelationImmediate(edge, source, newTarget);

        Assert.Empty(ReadIncoming<ParallelDirected>(world, oldTarget).ToArray());
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<ParallelDirected>(world, newTarget).ToArray()).Edge);
    }

    [Fact]
    public void EndpointRefWrite_IsValidatedAtOwnerReleaseAndDefersAdjacency()
    {
        var world = new World();
        var source = world.Create();
        var oldTarget = world.Create();
        var newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        Relations<ParallelDirected>.RetargetDeferred(world, edge, source, newTarget);

        Assert.Equal(newTarget, ReadEndpoints(world, edge).Target);
        Assert.Single(ReadIncoming<ParallelDirected>(world, oldTarget).ToArray());
        Assert.Empty(ReadIncoming<ParallelDirected>(world, newTarget).ToArray());

        world.MaintainRelations<ParallelDirected>();

        Assert.Empty(ReadIncoming<ParallelDirected>(world, oldTarget).ToArray());
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<ParallelDirected>(world, newTarget).ToArray()).Edge);
    }

    [Fact]
    public void EndpointRefWrite_BreakCommitsInsideRuntimeOwnedQuery()
    {
        var world = new World();
        var source = world.Create();
        var oldTarget = world.Create();
        var newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        Relations<ParallelDirected>.RetargetDeferred(world, edge, source, newTarget);

        Assert.Equal(newTarget, ReadEndpoints(world, edge).Target);
        Assert.Single(ReadIncoming<ParallelDirected>(world, oldTarget).ToArray());
        Assert.Empty(ReadIncoming<ParallelDirected>(world, newTarget).ToArray());
        world.MaintainRelations<ParallelDirected>();
        Assert.Empty(ReadIncoming<ParallelDirected>(world, oldTarget).ToArray());
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<ParallelDirected>(world, newTarget).ToArray()).Edge);
    }

    [Fact]
    public void InvalidTypedEndpointEditPreservesEndpointsAndAdjacency()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        var edge = world.CreateRelation(source, target, new NoSelfDirected());
        Assert.Throws<InvalidOperationException>(() => Relations<NoSelfDirected>.RetargetDeferred(world, edge, source, source));
        Assert.Equal(target, ReadEndpoints(world, edge).Target);
        Assert.Equal(edge, Assert.Single(ReadOutgoing<NoSelfDirected>(world, source)).Edge);
        Relations<NoSelfDirected>.Maintain(world);
        Assert.Equal(target, ReadEndpoints(world, edge).Target);
    }

    [Fact]
    public void EndpointBatchValidatesUniqueTargetSwapTogether()
    {
        using var world = new World();
        Entity sourceA = world.Create();
        Entity sourceB = world.Create();
        Entity targetA = world.Create();
        Entity targetB = world.Create();
        var edgeA = world.CreateRelation(sourceA, targetA, new UniqueTargetDirected());
        var edgeB = world.CreateRelation(sourceB, targetB, new UniqueTargetDirected());
        Relations<UniqueTargetDirected>.Edit(world,
            [new(edgeA.Entity, sourceA, targetB), new(edgeB.Entity, sourceB, targetA)], [], deferred: true);
        Assert.Equal(edgeA, Assert.Single(ReadIncoming<UniqueTargetDirected>(world, targetA)).Edge);
        Relations<UniqueTargetDirected>.Maintain(world);
        Assert.Equal(targetB, ReadEndpoints(world, edgeA).Target);
        Assert.Equal(targetA, ReadEndpoints(world, edgeB).Target);
        Assert.Equal(edgeB, Assert.Single(ReadIncoming<UniqueTargetDirected>(world, targetA)).Edge);
        Assert.Equal(edgeA, Assert.Single(ReadIncoming<UniqueTargetDirected>(world, targetB)).Edge);
    }

    [Fact]
    public void EndpointReadPreservesWorldVersionAndAdjacency()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        var edge = world.CreateRelation(source, target, new ParallelDirected());
        ulong before = world.Version;
        using (ReadLookup<RelationEndpoints<ParallelDirected>> values = world.OpenRead<RelationEndpoints<ParallelDirected>>())
            Assert.Equal(target, values.Read(edge.Entity).Target);
        Relations<ParallelDirected>.Maintain(world);
        Assert.Equal(before, world.Version);
        Assert.Equal(edge, Assert.Single(ReadOutgoing<ParallelDirected>(world, source)).Edge);
    }

    [Fact]
    public void NoOpEndpointRefWrite_PreservesPendingSamePairPlacement()
    {
        var world = new World();
        var source = world.Create();
        var targetA = world.Create();
        var targetB = world.Create();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(source, targetA, new ParallelDirected());
        var second = world.CreateRelation(source, targetB, new ParallelDirected());
        world.RetargetRelationDeferred(first, source, targetA);
        Relations<ParallelDirected>.ReorderDeferred(world, source, RelationAdjacencyRole.Outgoing, first, 1);
        using (QueryScope scope = world.Query(new QueryDescription().WithStored<RelationEndpoints<ParallelDirected>>()).OpenRead())
            foreach (QueryChunk chunk in scope) Assert.Equal(2, chunk.Read<RelationEndpoints<ParallelDirected>>().Length);
        world.MaintainRelations<ParallelDirected>();

        Assert.Equal(
            new[] { second, first },
            ReadOutgoing<ParallelDirected>(world, source).ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void DeferredPlacementAndRetarget_OrderMatchesImmediateSequence()
    {
        var world = new World();
        var source = world.Create();
        var targetA = world.Create();
        var targetB = world.Create();
        var targetC = world.Create();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(source, targetA, new ParallelDirected());
        var second = world.CreateRelation(source, targetB, new ParallelDirected());

        world.RetargetRelationDeferred(first, source, targetA);
        Relations<ParallelDirected>.ReorderDeferred(world, source, RelationAdjacencyRole.Outgoing, first, 1);
        world.RetargetRelationDeferred(second, source, targetC);
        Relations<ParallelDirected>.ReorderDeferred(world, source, RelationAdjacencyRole.Outgoing, second, 1);
        world.MaintainRelations<ParallelDirected>();

        Assert.Equal(
            new[] { first, second },
            ReadOutgoing<ParallelDirected>(world, source).ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void MultipleDeferredPlacements_UseMutationSequenceNotEntityOrder()
    {
        var world = new World();
        var source = world.Create();
        var targetA = world.Create();
        var targetB = world.Create();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(source, targetA, new ParallelDirected());
        var second = world.CreateRelation(source, targetB, new ParallelDirected());

        world.RetargetRelationDeferred(second, source, targetB);
        Relations<ParallelDirected>.ReorderDeferred(world, source, RelationAdjacencyRole.Outgoing, second, 0);
        world.RetargetRelationDeferred(first, source, targetA);
        Relations<ParallelDirected>.ReorderDeferred(world, source, RelationAdjacencyRole.Outgoing, first, 0);
        world.MaintainRelations<ParallelDirected>();

        Assert.Equal(
            new[] { first, second },
            ReadOutgoing<ParallelDirected>(world, source).ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void CandidateEndpointFailurePreservesSourceValues()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity oldTarget = world.Create();
        Entity newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        Assert.Throws<ApplicationException>((Action)(() =>
        {
            Relations<ParallelDirected>.RetargetDeferred(candidate, edge, source, newTarget);
            throw new ApplicationException();
        }));
        Assert.Equal(oldTarget, ReadEndpoints(world, edge).Target);
        Assert.Equal(edge, Assert.Single(ReadIncoming<ParallelDirected>(world, oldTarget)).Edge);
        Assert.Empty(ReadIncoming<ParallelDirected>(world, newTarget));
    }

    [Fact]
    public void UndirectedSelfEdge_HasOneIncidentMembership_AndFixedSlots()
    {
        var world = new World();
        var endpoint = world.Create();

        var edge = world.CreateRelation(endpoint, endpoint, new ParallelUndirected { Value = 3 });

        var endpoints = ReadEndpoints(world, edge);
        Assert.Equal(endpoint, endpoints.Source);
        Assert.Equal(endpoint, endpoints.Target);
        Assert.Equal(edge, Assert.Single(ReadIncident<ParallelUndirected>(world, endpoint).ToArray()).Edge);
    }

    [Fact]
    public void LocalOrder_IsIndependentPerEndpointRole_AndOldSnapshotStaysValid()
    {
        var world = new World();
        var source = world.Create();
        var targetA = world.Create();
        var targetB = world.Create();
        var targetC = world.Create();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);

        var first = world.CreateRelation(source, targetA, new ParallelDirected { Value = 1 });
        var second = world.CreateRelation(source, targetB, new ParallelDirected { Value = 2 });
        var oldSnapshot = ReadOutgoing<ParallelDirected>(world, source);
        var third = world.CreateRelation(source, targetC, new ParallelDirected { Value = 3 });
        world.ReorderRelationAdjacency(
            source,
            RelationAdjacencyRole.Outgoing,
            third,
            insertIndex: 0);

        Assert.Equal(new[] { first, second }, oldSnapshot.Select(static item => item.Edge));
        Assert.Equal(
            new[] { third, first, second },
            ReadOutgoing<ParallelDirected>(world, source).ToArray().Select(static item => item.Edge));
        Assert.False(Relations<ParallelDirected>.IsOrdered(world, targetA, RelationAdjacencyRole.Incoming));
    }

    [Fact]
    public async Task RelationReadScopePrecedesConcurrentEdgeCreation()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 1 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = world.Create();
        Entity firstTarget = world.Create();
        Entity secondTarget = world.Create();
        var first = world.CreateRelation(source, firstTarget, new ParallelDirected());
        using var started = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Task writer;
        using (RelationRead values = Relations<ParallelDirected>.Outgoing(world, source))
        {
            writer = Task.Run(() =>
            {
                started.Set();
                world.CreateRelation(source, secondTarget, new ParallelDirected());
                events.Enqueue("write");
            });
            started.Wait();
            Assert.Equal(first.Entity, Assert.Single(values.Entries.ToArray()).Edge);
            events.Enqueue("read");
        }
        await writer;
        Assert.Equal(new[] { "read", "write" }, events.ToArray());
        Assert.Equal(2, ReadOutgoing<ParallelDirected>(world, source).Length);
    }

    [Fact]
    public async Task UnknownPayloadReadsRemainEmptyDuringOrderRegistration()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 1 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity endpoint = world.Create();
        using var start = new ManualResetEventSlim();
        Task writer = Task.Run(() =>
        {
            start.Wait();
            Relations<FirstRegistrationRelation>.SetOrdered(world, endpoint, RelationAdjacencyRole.Outgoing, true);
        });
        Task reader = Task.Run(() =>
        {
            start.Wait();
            for (int iteration = 0; iteration < 64; iteration++)
                Assert.Empty(ReadOutgoing<FirstRegistrationRelation>(world, endpoint));
        });
        start.Set();
        await Task.WhenAll(writer, reader);
        Assert.True(Relations<FirstRegistrationRelation>.IsOrdered(world, endpoint, RelationAdjacencyRole.Outgoing));
        Assert.Empty(ReadOutgoing<FirstRegistrationRelation>(world, endpoint));
    }

    [Fact]
    public void UnregisteredEdgesBetweenLookup_DoesNotReadLivenessOrRegisterRelationState()
    {
        var world = new World();
        Entity first = world.Create();
        Entity second = world.Create();

        Assert.Empty(
            ReadPairEdges<FirstRegistrationRelation>(world, first, second).ToArray());

        world.Destroy(first);
        world.Destroy(second);

        Assert.Empty(
            ReadPairEdges<FirstRegistrationRelation>(world, first, second).ToArray());
    }

    [Fact]
    public void DestroyingAnEdgeThatIsAlsoAnEndpoint_CleansItsIncidentEdges()
    {
        var world = new World();
        var source = world.Create();
        var target = world.Create();
        var other = world.Create();
        var endpointEdge = world.CreateRelation(source, target, new ParallelDirected());
        var incidentEdge = world.CreateRelation(endpointEdge.Entity, other, new ParallelDirected());

        world.DestroyRelation(endpointEdge);

        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(endpointEdge.Entity);

        Assert.False(world.IsAlive(endpointEdge.Entity));
        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(incidentEdge.Entity);
        Assert.False(world.IsAlive(incidentEdge.Entity));
        Assert.Empty(ReadIncoming<ParallelDirected>(world, other).ToArray());
    }

    [Fact]
    public void DestroyingEdgeEndpoint_CleansAnotherEdgesCurrentOnlyReference()
    {
        var world = new World();
        var source = world.Create();
        var target = world.Create();
        var otherSource = world.Create();
        var oldTarget = world.Create();
        var endpointEdge = world.CreateRelation(source, target, new ParallelDirected());
        var incidentEdge = world.CreateRelation(otherSource, oldTarget, new ParallelDirected());
        world.RetargetRelationDeferred(incidentEdge, otherSource, endpointEdge.Entity);

        world.DestroyRelation(endpointEdge);

        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(endpointEdge.Entity);

        Assert.False(world.IsAlive(endpointEdge.Entity));
        world.Remove<Removed<RelationEndpoints<ParallelDirected>>>(incidentEdge.Entity);
        Assert.False(world.IsAlive(incidentEdge.Entity));
        Assert.Empty(ReadIncoming<ParallelDirected>(world, oldTarget).ToArray());
    }

    [Fact]
    public void TypedDestroyPublishesItsFactAfterRemovingAdjacency()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        var edge = world.CreateRelation(source, target, new HookedRelation());
        using DestroyedReader reader = world.WatchDestroyed();
        world.DestroyRelation(edge);
        using DestroyedRead facts = reader.Open();
        Assert.Equal(edge.Entity, Assert.Single(facts.First.ToArray()).Entity);
        Assert.False(world.Has<RelationEndpoints<HookedRelation>>(edge.Entity));
        Assert.Empty(ReadOutgoing<HookedRelation>(world, source));
        Assert.Empty(ReadIncoming<HookedRelation>(world, target));
    }

    [Fact]
    public void EndpointCascadePublishesBothDestroyedEdgeFacts()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        Entity other = world.Create();
        var endpointEdge = world.CreateRelation(source, target, new HookedRelation());
        var incidentEdge = world.CreateRelation(endpointEdge.Entity, other, new HookedRelation());
        using DestroyedReader reader = world.WatchDestroyed();
        world.DestroyRelation(endpointEdge);
        using DestroyedRead facts = reader.Open();
        Assert.Equal(new[] { endpointEdge.Entity, incidentEdge.Entity }.OrderBy(entity => entity.Id),
            facts.First.ToArray().Select(fact => fact.Entity).OrderBy(entity => entity.Id));
        Assert.False(world.Has<RelationEndpoints<HookedRelation>>(endpointEdge.Entity));
        Assert.False(world.Has<RelationEndpoints<HookedRelation>>(incidentEdge.Entity));
        Assert.Empty(ReadIncoming<HookedRelation>(world, other));
        Assert.Empty(ReadOutgoing<HookedRelation>(world, source));
    }

    [Fact]
    public void DestroyingEmptyOrderedEndpointDoesNotThrow()
    {
        var world = new World();
        var endpoint = world.Create();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            endpoint,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);

        world.Destroy(endpoint);

    }

    [Fact]
    public void RepeatedOrderAndNoopReorderPreserveAdjacency()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        Relations<ParallelDirected>.SetOrdered(world, source, RelationAdjacencyRole.Outgoing, false);
        Assert.False(Relations<ParallelDirected>.IsOrdered(world, source, RelationAdjacencyRole.Outgoing));
        Relations<ParallelDirected>.SetOrdered(world, source, RelationAdjacencyRole.Outgoing, true);
        Relations<ParallelDirected>.SetOrdered(world, source, RelationAdjacencyRole.Outgoing, true);
        Assert.True(Relations<ParallelDirected>.IsOrdered(world, source, RelationAdjacencyRole.Outgoing));
        var edge = world.CreateRelation(source, target, new ParallelDirected());
        ulong before = world.Version;
        Relations<ParallelDirected>.Reorder(world, source, RelationAdjacencyRole.Outgoing, edge, 0);
        Assert.Equal(before, world.Version);
        Assert.Equal(edge, Assert.Single(ReadOutgoing<ParallelDirected>(world, source)).Edge);
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct ParallelDirected : IAddedComponent
    {
        public int Value;
    }

    [RelationSchema(RelationDirection.Undirected, RelationCardinality.Parallel)]
    private struct ParallelUndirected : IAddedComponent
    {
        public int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair)]
    private struct UniquePairDirected : IAddedComponent { }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueSource)]
    private struct UniqueSourceDirected : IAddedComponent { }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private struct UniqueTargetDirected : IAddedComponent { }

    [RelationSchema(
        RelationDirection.Directed,
        RelationCardinality.Parallel,
        AllowSelfEdge = false)]
    private struct NoSelfDirected : IAddedComponent { }

    private struct QueryProbe : IAddedComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct HookedRelation : IAddedComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct FirstRegistrationRelation : IAddedComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.OneToOne)]
    private struct OneToOneDirected : IAddedComponent { }

    [RelationSchema(RelationDirection.Undirected, RelationCardinality.UniquePair)]
    private struct UniquePairUndirected : IAddedComponent { }

    [RelationSchema(RelationDirection.Undirected, RelationCardinality.UniqueSource)]
    private struct InvalidUndirectedUniqueSource : IAddedComponent { }
}
