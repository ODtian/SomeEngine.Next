using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
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
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        int baselineCount = world.EntityCount;

        var first = world.CreateRelation(source, target, new ParallelDirected { Value = 1 });
        var second = world.CreateRelation(source, target, new ParallelDirected { Value = 2 });

        Assert.NotEqual(first, second);
        Assert.Equal(baselineCount + 2, world.EntityCount);
        Assert.Equal(1, world.Read<ParallelDirected>(first.Entity).Value);
        Assert.Equal(2, world.Read<ParallelDirected>(second.Entity).Value);
        Assert.Equal(2, world.GetOutgoingRelations<ParallelDirected>(source).Count);
        Assert.Equal(2, world.GetIncomingRelations<ParallelDirected>(target).Count);

        world.DestroyRelation(first);

        Assert.False(world.IsAlive(first.Entity));
        Assert.True(world.IsAlive(second.Entity));
        Assert.Equal(baselineCount + 1, world.EntityCount);
        Assert.Single(world.GetOutgoingRelations<ParallelDirected>(source).Entries.ToArray());
    }

    [Fact]
    public void EndpointsAndAdjacency_UseNativeAddedChangedRemovedFacts()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        Entity replacementTarget = world.CreateEntity();

        uint beforeFirst = world.AcquireSystemTick();
        RelationEdge<ParallelDirected> first = world.CreateRelation(
            source,
            target,
            new ParallelDirected { Value = 1 });

        Assert.Equal(
            new[] { first.Entity },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<DirectedRelationEndpoints<ParallelDirected>>()
                    .Added<DirectedRelationEndpoints<ParallelDirected>>()),
                beforeFirst));
        Assert.Equal(
            new[] { source },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<Outgoing<ParallelDirected>>()
                    .Added<Outgoing<ParallelDirected>>()),
                beforeFirst));
        Assert.Equal(
            new[] { target },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<Incoming<ParallelDirected>>()
                    .Added<Incoming<ParallelDirected>>()),
                beforeFirst));

        uint beforeSecond = world.AcquireSystemTick();
        RelationEdge<ParallelDirected> second = world.CreateRelation(
            source,
            target,
            new ParallelDirected { Value = 2 });
        Assert.Equal(
            new[] { source },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<Outgoing<ParallelDirected>>()
                    .Changed<Outgoing<ParallelDirected>>()),
                beforeSecond));
        Assert.Equal(
            new[] { target },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<Incoming<ParallelDirected>>()
                    .Changed<Incoming<ParallelDirected>>()),
                beforeSecond));

        uint beforeRetarget = world.AcquireSystemTick();
        world.RetargetRelationImmediate(first, source, replacementTarget);
        Assert.Equal(
            new[] { first.Entity },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<DirectedRelationEndpoints<ParallelDirected>>()
                    .Changed<DirectedRelationEndpoints<ParallelDirected>>()),
                beforeRetarget));

        world.DestroyRelation(first);
        world.DestroyRelation(second);

        Assert.Equal(
            new[] { source },
            QueryEntities(world.Query(
                world.QueryDefinition().Removed<Outgoing<ParallelDirected>>())));
        Assert.Equal(
            new[] { target, replacementTarget }.OrderBy(static entity => entity.Index).ToArray(),
            QueryEntities(world.Query(
                world.QueryDefinition().Removed<Incoming<ParallelDirected>>())));

        Entity[] QueryEntities(QueryHandle query, uint? lastVersion = null)
        {
            var entities = new List<Entity>();
            void Capture(QueryCursor cursor)
            {
                foreach (var row in cursor.Rows)
                    entities.Add(row.Entity);
            }

            if (lastVersion is uint since)
                world.ExecuteQuery(query, since, world.CurrentTick, Capture);
            else
                world.ExecuteQuery(query, Capture);
            entities.Sort(static (left, right) =>
            {
                int index = left.Index.CompareTo(right.Index);
                return index != 0 ? index : left.Generation.CompareTo(right.Generation);
            });
            return entities.ToArray();
        }
    }

    [Fact]
    public void EdgeEntity_IsDirectlyQueryableAsOnePayloadAndEndpointRow()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        var edge = world.CreateRelation(source, target, new ParallelDirected { Value = 41 });
        var query = world.Query(
            world.QueryDefinition()
                .Read<ParallelDirected>()
                .Read<DirectedRelationEndpoints<ParallelDirected>>());

        var rows = new List<Entity>();
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                rows.Add(row.Entity);
                Assert.Equal(41, row.Read<ParallelDirected>().Value);
                Assert.Equal(source, row.Read<DirectedRelationEndpoints<ParallelDirected>>().Source);
                Assert.Equal(target, row.Read<DirectedRelationEndpoints<ParallelDirected>>().Target);
            }
        });

        Assert.Equal([edge.Entity], rows);
    }

    [Fact]
    public void CreateDuringIteration_ThrowsBeforeAllocatingEdgeEntity()
    {
        var world = new World();
        var source = world.CreateEntity(new QueryProbe());
        var target = world.CreateEntity();
        int baseline = world.EntityCount;
        var query = world.Query(world.QueryDefinition().Read<QueryProbe>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var _ in cursor.Rows)
            {
                Assert.Throws<InvalidOperationException>(
                    () => world.CreateRelation(source, target, new ParallelDirected()));
            }
        });

        Assert.Equal(baseline, world.EntityCount);
    }

    [Fact]
    public void DestroyingEndpoint_DestroysEveryIncidentEdgeAndCleansSurvivingAdjacency()
    {
        var world = new World();
        var source = world.CreateEntity();
        var targetA = world.CreateEntity();
        var targetB = world.CreateEntity();
        var incomingSource = world.CreateEntity();
        var first = world.CreateRelation(source, targetA, new ParallelDirected { Value = 1 });
        var second = world.CreateRelation(source, targetB, new ParallelDirected { Value = 2 });
        var incoming = world.CreateRelation(incomingSource, source, new ParallelDirected { Value = 3 });

        world.DestroyEntity(source);

        Assert.False(world.IsAlive(first.Entity));
        Assert.False(world.IsAlive(second.Entity));
        Assert.False(world.IsAlive(incoming.Entity));
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(targetA).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(targetB).Entries.ToArray());
        Assert.Empty(world.GetOutgoingRelations<ParallelDirected>(incomingSource).Entries.ToArray());
    }

    [Fact]
    public void DestroyingEndpointDuringDeferredWindow_CleansCurrentAndAppliedMembership()
    {
        var world = new World();
        var source = world.CreateEntity();
        var oldTarget = world.CreateEntity();
        var newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        world.RetargetRelationDeferred(edge, source, newTarget);

        world.DestroyEntity(oldTarget);

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(world.GetOutgoingRelations<ParallelDirected>(source).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(newTarget).Entries.ToArray());
        world.MaintainRelations<ParallelDirected>();
    }

    [Fact]
    public void DestroyingCurrentOnlyEndpointDuringDeferredWindow_DestroysTheEdge()
    {
        var world = new World();
        var source = world.CreateEntity();
        var oldTarget = world.CreateEntity();
        var newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        world.RetargetRelationDeferred(edge, source, newTarget);

        world.DestroyEntity(newTarget);

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(world.GetOutgoingRelations<ParallelDirected>(source).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray());
    }

    [Fact]
    public void DestroyingDeferredCreateEndpointBeforeFirstMaintain_DestroysTheEdge()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        var edge = world.CreateRelation(
            source,
            target,
            new ParallelDirected(),
            RelationMaintenanceTiming.Deferred);

        world.DestroyEntity(target);

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(world.GetOutgoingRelations<ParallelDirected>(source).Entries.ToArray());
    }

    [Fact]
    public void DestroyingEdgeEntityDirectly_CleansGraphStateWithoutTypedDestroyCall()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        var edge = world.CreateRelation(source, target, new ParallelDirected());

        world.DestroyEntity(edge.Entity);

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(world.GetOutgoingRelations<ParallelDirected>(source).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(target).Entries.ToArray());
    }

    [Fact]
    public void EveryCardinality_RejectsOnlyItsDeclaredCollision()
    {
        var world = new World();
        var a = world.CreateEntity();
        var b = world.CreateEntity();
        var c = world.CreateEntity();
        var d = world.CreateEntity();

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
        var failedA = failedWorld.CreateEntity();
        var failedB = failedWorld.CreateEntity();
        failedWorld.CreateRelation(failedA, failedB, new UniquePairDirected());
        Assert.Throws<InvalidOperationException>(
            () => failedWorld.CreateRelation(failedA, failedB, new UniquePairDirected()));
        Entity afterFailure = failedWorld.CreateEntity();

        var controlWorld = new World();
        var controlA = controlWorld.CreateEntity();
        var controlB = controlWorld.CreateEntity();
        controlWorld.CreateRelation(controlA, controlB, new UniquePairDirected());
        Entity withoutFailure = controlWorld.CreateEntity();

        Assert.Equal(withoutFailure, afterFailure);
    }

    [Fact]
    public void DirectedDeferredRetarget_ExposesCanonicalEndpointsThenMaintainsAdjacency()
    {
        var world = new World();
        var source = world.CreateEntity();
        var oldTarget = world.CreateEntity();
        var newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected { Value = 7 });

        world.RetargetRelationDeferred(edge, source, newTarget);

        var canonical = world.GetDirectedRelationEndpoints(edge);
        Assert.Equal(source, canonical.Source);
        Assert.Equal(newTarget, canonical.Target);
        Assert.Single(world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(newTarget).Entries.ToArray());

        world.MaintainRelations<ParallelDirected>();

        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray());
        Assert.Equal(edge, Assert.Single(world.GetIncomingRelations<ParallelDirected>(newTarget).Entries.ToArray()).Edge);
        Assert.Equal(7, world.Read<ParallelDirected>(edge.Entity).Value);
    }

    [Fact]
    public void DeferredCreate_PublishesCanonicalEdgeBeforeAppliedAdjacency()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        var edge = world.CreateRelation(
            source,
            target,
            new ParallelDirected { Value = 23 },
            RelationMaintenanceTiming.Deferred);

        Assert.True(world.IsAlive(edge.Entity));
        Assert.Equal(23, world.Read<ParallelDirected>(edge.Entity).Value);
        Assert.Equal(target, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Empty(world.GetOutgoingRelations<ParallelDirected>(source).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(target).Entries.ToArray());

        world.MaintainRelations<ParallelDirected>();

        Assert.Equal(edge, Assert.Single(
            world.GetOutgoingRelations<ParallelDirected>(source).Entries.ToArray()).Edge);
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<ParallelDirected>(target).Entries.ToArray()).Edge);
    }

    [Fact]
    public void ImmediateSameEndpointRetarget_FirstFlushesPendingAdjacency()
    {
        var world = new World();
        var source = world.CreateEntity();
        var oldTarget = world.CreateEntity();
        var newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        world.RetargetRelationDeferred(edge, source, newTarget);

        world.RetargetRelationImmediate(edge, source, newTarget);

        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray());
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<ParallelDirected>(newTarget).Entries.ToArray()).Edge);
    }

    [Fact]
    public void EndpointRefWrite_IsValidatedAtOwnerReleaseAndDefersAdjacency()
    {
        var world = new World();
        var source = world.CreateEntity();
        var oldTarget = world.CreateEntity();
        var newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<DirectedRelationEndpoints<ParallelDirected>>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                row.ReadWrite<DirectedRelationEndpoints<ParallelDirected>>().Target = newTarget;
        });

        Assert.Equal(newTarget, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Single(world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(newTarget).Entries.ToArray());

        world.MaintainRelations<ParallelDirected>();

        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray());
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<ParallelDirected>(newTarget).Entries.ToArray()).Edge);
    }

    [Fact]
    public void EndpointRefWrite_BreakCommitsInsideRuntimeOwnedQuery()
    {
        var world = new World();
        var source = world.CreateEntity();
        var oldTarget = world.CreateEntity();
        var newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<DirectedRelationEndpoints<ParallelDirected>>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                row.ReadWrite<DirectedRelationEndpoints<ParallelDirected>>().Target = newTarget;
                break;
            }
        });

        Assert.Equal(newTarget, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Single(world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(newTarget).Entries.ToArray());
        world.MaintainRelations<ParallelDirected>();
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray());
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<ParallelDirected>(newTarget).Entries.ToArray()).Edge);
    }

    [Fact]
    public void InvalidEndpointRefWrite_RestoresCanonicalPreimageBeforeThrowing()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        var edge = world.CreateRelation(source, target, new NoSelfDirected());
        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<DirectedRelationEndpoints<NoSelfDirected>>());
        InvalidOperationException? error = null;

        try
        {
            world.ExecuteQuery(query, cursor =>
            {
                foreach (var row in cursor.Rows)
                    row.ReadWrite<DirectedRelationEndpoints<NoSelfDirected>>().Target = source;
            });
        }
        catch (InvalidOperationException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
        Assert.Equal(target, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Equal(edge, Assert.Single(
            world.GetOutgoingRelations<NoSelfDirected>(source).Entries.ToArray()).Edge);
        world.MaintainRelations<NoSelfDirected>();
        Assert.Equal(target, world.GetDirectedRelationEndpoints(edge).Target);
    }

    [Fact]
    public void EndpointSpanWrite_ValidatesUniqueTargetSwapAsOneFinalImage()
    {
        var world = new World();
        var sourceA = world.CreateEntity();
        var sourceB = world.CreateEntity();
        var targetA = world.CreateEntity();
        var targetB = world.CreateEntity();
        var edgeA = world.CreateRelation(sourceA, targetA, new UniqueTargetDirected());
        var edgeB = world.CreateRelation(sourceB, targetB, new UniqueTargetDirected());
        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<DirectedRelationEndpoints<UniqueTargetDirected>>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var chunk in cursor.Chunks)
            {
                var endpoints = chunk.ReadWrite<DirectedRelationEndpoints<UniqueTargetDirected>>();
                for (int i = 0; i < endpoints.Length; i++)
                {
                    endpoints[i].Target = endpoints[i].Source == sourceA
                        ? targetB
                        : targetA;
                }
            }
        });

        world.MaintainRelations<UniqueTargetDirected>();

        Assert.Equal(targetB, world.GetDirectedRelationEndpoints(edgeA).Target);
        Assert.Equal(targetA, world.GetDirectedRelationEndpoints(edgeB).Target);
        Assert.Equal(edgeB, Assert.Single(
            world.GetIncomingRelations<UniqueTargetDirected>(targetA).Entries.ToArray()).Edge);
        Assert.Equal(edgeA, Assert.Single(
            world.GetIncomingRelations<UniqueTargetDirected>(targetB).Entries.ToArray()).Edge);
    }

    [Fact]
    public void NoOpEndpointRefWrite_DoesNotPublishAnotherAdjacencyGeneration()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        world.CreateRelation(source, target, new ParallelDirected());
        uint before = world.GetOutgoingRelations<ParallelDirected>(source).Generation;
        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<DirectedRelationEndpoints<ParallelDirected>>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                _ = row.ReadWrite<DirectedRelationEndpoints<ParallelDirected>>().Target;
        });
        world.MaintainRelations<ParallelDirected>();

        Assert.Equal(before, world.GetOutgoingRelations<ParallelDirected>(source).Generation);
    }

    [Fact]
    public void NoOpEndpointRefWrite_PreservesPendingSamePairPlacement()
    {
        var world = new World();
        var source = world.CreateEntity();
        var targetA = world.CreateEntity();
        var targetB = world.CreateEntity();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(source, targetA, new ParallelDirected());
        var second = world.CreateRelation(source, targetB, new ParallelDirected());
        world.RetargetRelationDeferred(
            first,
            source,
            targetA,
            new DirectedRelationPlacement(OutgoingIndex: 1));
        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<DirectedRelationEndpoints<ParallelDirected>>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                _ = row.ReadWrite<DirectedRelationEndpoints<ParallelDirected>>().Target;
        });
        world.MaintainRelations<ParallelDirected>();

        Assert.Equal(
            new[] { second, first },
            world.GetOrderedOutgoingRelations<ParallelDirected>(source)
                .Entries.ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void DeferredPlacementAndRetarget_OrderMatchesImmediateSequence()
    {
        var world = new World();
        var source = world.CreateEntity();
        var targetA = world.CreateEntity();
        var targetB = world.CreateEntity();
        var targetC = world.CreateEntity();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(source, targetA, new ParallelDirected());
        var second = world.CreateRelation(source, targetB, new ParallelDirected());

        world.RetargetRelationDeferred(
            first,
            source,
            targetA,
            new DirectedRelationPlacement(OutgoingIndex: 1));
        world.RetargetRelationDeferred(second, source, targetC);
        world.MaintainRelations<ParallelDirected>();

        Assert.Equal(
            new[] { first, second },
            world.GetOrderedOutgoingRelations<ParallelDirected>(source)
                .Entries.ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void MultipleDeferredPlacements_UseMutationSequenceNotEntityOrder()
    {
        var world = new World();
        var source = world.CreateEntity();
        var targetA = world.CreateEntity();
        var targetB = world.CreateEntity();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(source, targetA, new ParallelDirected());
        var second = world.CreateRelation(source, targetB, new ParallelDirected());

        world.RetargetRelationDeferred(
            second,
            source,
            targetB,
            new DirectedRelationPlacement(OutgoingIndex: 0));
        world.RetargetRelationDeferred(
            first,
            source,
            targetA,
            new DirectedRelationPlacement(OutgoingIndex: 0));
        world.MaintainRelations<ParallelDirected>();

        Assert.Equal(
            new[] { first, second },
            world.GetOrderedOutgoingRelations<ParallelDirected>(source)
                .Entries.ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void EndpointRefBodyFault_RollsBackEvenWhenFinalValueWouldBeValid()
    {
        var world = new World();
        var source = world.CreateEntity();
        var oldTarget = world.CreateEntity();
        var newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new ParallelDirected());
        var query = world.Query(
            world.QueryDefinition()
                .ReadWrite<DirectedRelationEndpoints<ParallelDirected>>());

        Assert.Throws<ApplicationException>(() => MutateThenFault());

        Assert.Equal(oldTarget, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray()).Edge);
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(newTarget).Entries.ToArray());

        void MutateThenFault()
        {
            world.ExecuteQuery(query, cursor =>
            {
                foreach (var row in cursor.Rows)
                {
                    row.ReadWrite<DirectedRelationEndpoints<ParallelDirected>>().Target = newTarget;
                    throw new ApplicationException("body fault");
                }
            });
        }
    }

    [Fact]
    public void UndirectedSelfEdge_HasOneIncidentMembership_AndFixedSlots()
    {
        var world = new World();
        var endpoint = world.CreateEntity();

        var edge = world.CreateRelation(endpoint, endpoint, new ParallelUndirected { Value = 3 });

        var endpoints = world.GetUndirectedRelationEndpoints(edge);
        Assert.Equal(endpoint, endpoints.EndpointA);
        Assert.Equal(endpoint, endpoints.EndpointB);
        Assert.Equal(edge, Assert.Single(world.GetIncidentRelations<ParallelUndirected>(endpoint).Entries.ToArray()).Edge);
    }

    [Fact]
    public void LocalOrder_IsIndependentPerEndpointRole_AndOldSnapshotStaysValid()
    {
        var world = new World();
        var source = world.CreateEntity();
        var targetA = world.CreateEntity();
        var targetB = world.CreateEntity();
        var targetC = world.CreateEntity();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);

        var first = world.CreateRelation(source, targetA, new ParallelDirected { Value = 1 });
        var second = world.CreateRelation(source, targetB, new ParallelDirected { Value = 2 });
        var oldSnapshot = world.GetOrderedOutgoingRelations<ParallelDirected>(source);
        var third = world.CreateRelation(source, targetC, new ParallelDirected { Value = 3 });
        world.ReorderRelationAdjacency(
            source,
            RelationAdjacencyRole.Outgoing,
            third,
            insertIndex: 0);

        Assert.Equal(new[] { first, second }, oldSnapshot.Entries.ToArray().Select(static item => item.Edge));
        Assert.Equal(
            new[] { third, first, second },
            world.GetOrderedOutgoingRelations<ParallelDirected>(source)
                .Entries.ToArray().Select(static item => item.Edge));
        Assert.Equal(RelationAdjacencyOrderPolicy.Unordered,
            world.GetIncomingRelations<ParallelDirected>(targetA).OrderPolicy);
    }

    [Fact]
    public async Task RelaxedSnapshots_ObserveGenerationAndPolicyFromOnePublishedRoot()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();

        // Register the type state without advancing its initial generation.
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Unordered);
        AssertSnapshot(world.GetOutgoingRelations<ParallelDirected>(source));

        const int transitionCount = 10_000;
        const int readerCount = 3;
        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readersReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int readyReaderCount = 0;
        int writerCompleted = 0;

        Task writer = Task.Run(async () =>
        {
            await start.Task;
            await readersReady.Task;
            try
            {
                for (uint generation = 2; generation <= transitionCount + 1; generation++)
                {
                    RelationAdjacencyOrderPolicy policy = (generation & 1) == 0
                        ? RelationAdjacencyOrderPolicy.Ordered
                        : RelationAdjacencyOrderPolicy.Unordered;
                    world.SetRelationAdjacencyOrder<ParallelDirected>(
                        source,
                        RelationAdjacencyRole.Outgoing,
                        policy);
                    if ((generation & 31) == 0)
                        Thread.Yield();
                }
            }
            finally
            {
                Volatile.Write(ref writerCompleted, 1);
            }
        });

        Task[] readers = Enumerable.Range(0, readerCount).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            if (Interlocked.Increment(ref readyReaderCount) == readerCount)
                readersReady.TrySetResult(true);

            int observations = 0;
            do
            {
                AssertSnapshot(world.GetOutgoingRelations<ParallelDirected>(source));
                if ((observations & 31) == 0)
                    Assert.Empty(
                        world.GetRelationEdgesBetween<ParallelDirected>(source, target).ToArray());
                if ((observations & 63) == 0)
                    Thread.Yield();
                observations++;
            }
            while (Volatile.Read(ref writerCompleted) == 0 || observations < transitionCount);
        })).ToArray();

        start.TrySetResult(true);
        await Task.WhenAll([writer, .. readers]).WaitAsync(TimeSpan.FromSeconds(30));

        RelationAdjacencySnapshot<ParallelDirected> final =
            world.GetOutgoingRelations<ParallelDirected>(source);
        Assert.Equal((uint)transitionCount + 1, final.Generation);
        Assert.Equal(RelationAdjacencyOrderPolicy.Unordered, final.OrderPolicy);

        static void AssertSnapshot(RelationAdjacencySnapshot<ParallelDirected> snapshot)
        {
            RelationAdjacencyOrderPolicy expected = (snapshot.Generation & 1) == 0
                ? RelationAdjacencyOrderPolicy.Ordered
                : RelationAdjacencyOrderPolicy.Unordered;
            Assert.True(
                snapshot.Generation >= 1 && snapshot.OrderPolicy == expected,
                $"Generation {snapshot.Generation} was published with {snapshot.OrderPolicy}; " +
                $"expected {expected} from the same immutable root.");
            Assert.Equal(0, snapshot.Count);
        }
    }

    [Fact]
    public async Task UnregisteredPayloadSnapshots_RacingFirstRegistration_SeeOnlyWholeGenerations()
    {
        const int worldCount = 128;
        const int readerCount = 3;
        var fixtures = new (World World, Entity Endpoint)[worldCount];
        for (int i = 0; i < fixtures.Length; i++)
        {
            var world = new World();
            fixtures[i] = (world, world.CreateEntity());
        }

        var start = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var readersReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        int readyReaderCount = 0;
        int writerCompleted = 0;

        Task writer = Task.Run(async () =>
        {
            await start.Task;
            await readersReady.Task;
            try
            {
                for (int i = 0; i < fixtures.Length; i++)
                {
                    (World world, Entity endpoint) = fixtures[i];
                    world.SetRelationAdjacencyOrder<FirstRegistrationRelation>(
                        endpoint,
                        RelationAdjacencyRole.Outgoing,
                        RelationAdjacencyOrderPolicy.Ordered);
                    Thread.Yield();
                }
            }
            finally
            {
                Volatile.Write(ref writerCompleted, 1);
            }
        });

        Task[] readers = Enumerable.Range(0, readerCount).Select(readerIndex => Task.Run(async () =>
        {
            await start.Task;
            if (Interlocked.Increment(ref readyReaderCount) == readerCount)
                readersReady.TrySetResult(true);

            int pass = 0;
            do
            {
                int offset = (readerIndex * 37 + pass * 17) % fixtures.Length;
                for (int i = 0; i < fixtures.Length; i++)
                {
                    (World world, Entity endpoint) = fixtures[(offset + i) % fixtures.Length];
                    AssertWholeSnapshot(
                        world.GetOutgoingRelations<FirstRegistrationRelation>(endpoint));
                }

                Thread.Yield();
                pass++;
            }
            while (Volatile.Read(ref writerCompleted) == 0 || pass < 4);
        })).ToArray();

        start.TrySetResult(true);
        await Task.WhenAll([writer, .. readers]).WaitAsync(TimeSpan.FromSeconds(30));

        for (int i = 0; i < fixtures.Length; i++)
        {
            RelationAdjacencySnapshot<FirstRegistrationRelation> snapshot =
                fixtures[i].World.GetOutgoingRelations<FirstRegistrationRelation>(
                    fixtures[i].Endpoint);
            Assert.Equal(2u, snapshot.Generation);
            Assert.Equal(RelationAdjacencyOrderPolicy.Ordered, snapshot.OrderPolicy);
            Assert.Equal(0, snapshot.Count);
        }

        static void AssertWholeSnapshot(
            RelationAdjacencySnapshot<FirstRegistrationRelation> snapshot)
        {
            bool initialEmpty =
                snapshot.Generation == 1 &&
                snapshot.OrderPolicy == RelationAdjacencyOrderPolicy.Unordered;
            bool publishedOrdered =
                snapshot.Generation == 2 &&
                snapshot.OrderPolicy == RelationAdjacencyOrderPolicy.Ordered;
            Assert.True(
                initialEmpty || publishedOrdered,
                $"Observed partially published first-registration state: generation " +
                $"{snapshot.Generation}, policy {snapshot.OrderPolicy}.");
            Assert.Equal(0, snapshot.Count);
        }
    }

    [Fact]
    public void UnregisteredEdgesBetweenLookup_DoesNotReadLivenessOrRegisterRelationState()
    {
        var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();

        Assert.False(world.RelationGraph.Any);
        Assert.Empty(
            world.GetRelationEdgesBetween<FirstRegistrationRelation>(first, second).ToArray());
        Assert.False(world.RelationGraph.Any);

        world.DestroyEntity(first);
        world.DestroyEntity(second);

        Assert.Empty(
            world.GetRelationEdgesBetween<FirstRegistrationRelation>(first, second).ToArray());
        Assert.False(world.RelationGraph.Any);
    }

    [Fact]
    public void DestroyingAnEdgeThatIsAlsoAnEndpoint_CleansItsIncidentEdges()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        var other = world.CreateEntity();
        var endpointEdge = world.CreateRelation(source, target, new ParallelDirected());
        var incidentEdge = world.CreateRelation(endpointEdge.Entity, other, new ParallelDirected());

        world.DestroyRelation(endpointEdge);

        Assert.False(world.IsAlive(endpointEdge.Entity));
        Assert.False(world.IsAlive(incidentEdge.Entity));
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(other).Entries.ToArray());
    }

    [Fact]
    public void DestroyingEdgeEndpoint_CleansAnotherEdgesCurrentOnlyReference()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        var otherSource = world.CreateEntity();
        var oldTarget = world.CreateEntity();
        var endpointEdge = world.CreateRelation(source, target, new ParallelDirected());
        var incidentEdge = world.CreateRelation(otherSource, oldTarget, new ParallelDirected());
        world.RetargetRelationDeferred(incidentEdge, otherSource, endpointEdge.Entity);

        world.DestroyRelation(endpointEdge);

        Assert.False(world.IsAlive(endpointEdge.Entity));
        Assert.False(world.IsAlive(incidentEdge.Entity));
        Assert.Empty(world.GetIncomingRelations<ParallelDirected>(oldTarget).Entries.ToArray());
    }

    [Fact]
    public void ThrowingPayloadHook_IsReportedAfterTypedDestroyCommitsGraphAndEntity()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        world.Hooks<HookedRelation>().OnRemove(
            static (DeferredWorld deferred, Entity entity, in HookedRelation value) =>
                throw new InvalidOperationException("hook fault"));
        var edge = world.CreateRelation(source, target, new HookedRelation());

        Assert.Throws<InvalidOperationException>(() => world.DestroyRelation(edge));

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(world.GetOutgoingRelations<HookedRelation>(source).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<HookedRelation>(target).Entries.ToArray());
    }

    [Fact]
    public void ThrowingHooksDuringEdgeEndpointCascade_DoNotResurrectDeadEdges()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        var other = world.CreateEntity();
        world.Hooks<HookedRelation>().OnRemove(
            static (DeferredWorld deferred, Entity entity, in HookedRelation value) =>
                throw new InvalidOperationException("hook fault"));
        var endpointEdge = world.CreateRelation(source, target, new HookedRelation());
        var incidentEdge = world.CreateRelation(endpointEdge.Entity, other, new HookedRelation());

        Assert.ThrowsAny<Exception>(() => world.DestroyRelation(endpointEdge));

        Assert.False(world.IsAlive(endpointEdge.Entity));
        Assert.False(world.IsAlive(incidentEdge.Entity));
        Assert.Empty(world.GetIncomingRelations<HookedRelation>(other).Entries.ToArray());
        Assert.Empty(world.GetOutgoingRelations<HookedRelation>(source).Entries.ToArray());
    }

    [Fact]
    public void DestroyingEmptyOrderedEndpoint_RetiresItsDerivedShardState()
    {
        var world = new World();
        var endpoint = world.CreateEntity();
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            endpoint,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        Assert.True(world.RelationGraph.HasEndpointState<ParallelDirected>(endpoint));

        world.DestroyEntity(endpoint);

        Assert.False(world.RelationGraph.HasEndpointState<ParallelDirected>(endpoint));
    }

    [Fact]
    public void NoOpOrderPolicyAndReorder_DoNotAdvanceGeneration()
    {
        var world = new World();
        var source = world.CreateEntity();
        var target = world.CreateEntity();
        uint initial = world.GetOutgoingRelations<ParallelDirected>(source).Generation;

        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Unordered);
        Assert.Equal(initial, world.GetOutgoingRelations<ParallelDirected>(source).Generation);

        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        uint ordered = world.GetOutgoingRelations<ParallelDirected>(source).Generation;
        world.SetRelationAdjacencyOrder<ParallelDirected>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        Assert.Equal(ordered, world.GetOutgoingRelations<ParallelDirected>(source).Generation);

        var edge = world.CreateRelation(source, target, new ParallelDirected());
        uint populated = world.GetOutgoingRelations<ParallelDirected>(source).Generation;
        world.ReorderRelationAdjacency(
            source,
            RelationAdjacencyRole.Outgoing,
            edge,
            insertIndex: 0);
        Assert.Equal(populated, world.GetOutgoingRelations<ParallelDirected>(source).Generation);
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct ParallelDirected : IComponent
    {
        public int Value;
    }

    [RelationSchema(RelationDirection.Undirected, RelationCardinality.Parallel)]
    private struct ParallelUndirected : IComponent
    {
        public int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair)]
    private struct UniquePairDirected : IComponent { }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueSource)]
    private struct UniqueSourceDirected : IComponent { }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private struct UniqueTargetDirected : IComponent { }

    [RelationSchema(
        RelationDirection.Directed,
        RelationCardinality.Parallel,
        AllowSelfEdge = false)]
    private struct NoSelfDirected : IComponent { }

    private struct QueryProbe : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct HookedRelation : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct FirstRegistrationRelation : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.OneToOne)]
    private struct OneToOneDirected : IComponent { }

    [RelationSchema(RelationDirection.Undirected, RelationCardinality.UniquePair)]
    private struct UniquePairUndirected : IComponent { }

    [RelationSchema(RelationDirection.Undirected, RelationCardinality.UniqueSource)]
    private struct InvalidUndirectedUniqueSource : IComponent { }
}
