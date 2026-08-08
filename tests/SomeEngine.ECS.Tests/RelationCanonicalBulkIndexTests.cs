using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class RelationCanonicalBulkIndexTests
{
    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly struct BulkLink : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private readonly struct FinalImageUnique : IComponent;

    [Fact]
    public void IntermediateOwnerValidation_CannotHideLaterImmediateFinalImageConflict()
    {
        var world = new World();
        Entity existingSource = world.CreateEntity();
        Entity immediateSource = world.CreateEntity();
        Entity appliedTarget = world.CreateEntity();
        Entity projectedTarget = world.CreateEntity();
        RelationEdge<FinalImageUnique> existing = world.CreateRelation(
            existingSource,
            appliedTarget,
            new FinalImageUnique());
        int entityCount = world.EntityCount;
        RelationCommandBatchValidationDiagnostics before =
            world.RelationGraph.StateCommandBatchValidationDiagnostics<FinalImageUnique>();

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            var graph = world.RelationGraph;
            graph.BeginCommandBatch();
            graph.Retarget(
                world,
                existing,
                existingSource,
                projectedTarget,
                RelationMaintenanceTiming.Deferred);

            graph.ValidateAndTrackDeferred(world, new[] { existing });
            Assert.Equal(
                before,
                graph.StateCommandBatchValidationDiagnostics<FinalImageUnique>());

            _ = graph.Create(
                world,
                immediateSource,
                projectedTarget,
                new FinalImageUnique(),
                timing: RelationMaintenanceTiming.Immediate);

            Assert.Throws<InvalidOperationException>(() =>
                graph.EndCommandBatch(world, completed: true));
        }

        Assert.Equal(entityCount, world.EntityCount);
        Assert.Equal(
            before,
            world.RelationGraph.StateCommandBatchValidationDiagnostics<FinalImageUnique>());
        Assert.Equal(
            appliedTarget,
            world.GetDirectedRelationEndpoints(existing).Target);
        Assert.Equal(existing, Assert.Single(
            world.GetIncomingRelations<FinalImageUnique>(appliedTarget).Entries.ToArray()).Edge);
        Assert.Empty(world.GetIncomingRelations<FinalImageUnique>(projectedTarget).Entries.ToArray());
    }

    [Fact]
    public void IntermediateOwnerValidation_DoesNotRejectHalfOfDeferredFinalImageSwap()
    {
        var world = new World();
        Entity sourceA = world.CreateEntity();
        Entity sourceB = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        RelationEdge<FinalImageUnique> edgeA = world.CreateRelation(
            sourceA,
            targetA,
            new FinalImageUnique());
        RelationEdge<FinalImageUnique> edgeB = world.CreateRelation(
            sourceB,
            targetB,
            new FinalImageUnique());
        RelationCommandBatchValidationDiagnostics before =
            world.RelationGraph.StateCommandBatchValidationDiagnostics<FinalImageUnique>();

        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            var graph = world.RelationGraph;
            graph.BeginCommandBatch();
            graph.Retarget(
                world,
                edgeA,
                sourceA,
                targetB,
                RelationMaintenanceTiming.Deferred);

            graph.ValidateAndTrackDeferred(world, new[] { edgeA });
            Assert.Equal(
                before,
                graph.StateCommandBatchValidationDiagnostics<FinalImageUnique>());

            graph.Retarget(
                world,
                edgeB,
                sourceB,
                targetA,
                RelationMaintenanceTiming.Deferred);
            graph.EndCommandBatch(world, completed: true);

            RelationCommandBatchValidationDiagnostics afterFinalization =
                graph.StateCommandBatchValidationDiagnostics<FinalImageUnique>();
            Assert.Equal(1, afterFinalization.FullScanCount - before.FullScanCount);
            Assert.Equal(2, afterFinalization.TransitionVisitCount - before.TransitionVisitCount);
            mutation.Commit();
        }

        Assert.Equal(targetB, world.GetDirectedRelationEndpoints(edgeA).Target);
        Assert.Equal(targetA, world.GetDirectedRelationEndpoints(edgeB).Target);
        Assert.Equal(edgeA, Assert.Single(
            world.GetIncomingRelations<FinalImageUnique>(targetA).Entries.ToArray()).Edge);
        Assert.Equal(edgeB, Assert.Single(
            world.GetIncomingRelations<FinalImageUnique>(targetB).Entries.ToArray()).Edge);

        world.MaintainRelations<FinalImageUnique>();
        Assert.Equal(edgeB, Assert.Single(
            world.GetIncomingRelations<FinalImageUnique>(targetA).Entries.ToArray()).Edge);
        Assert.Equal(edgeA, Assert.Single(
            world.GetIncomingRelations<FinalImageUnique>(targetB).Entries.ToArray()).Edge);
    }

    [Fact]
    public void PairBulkStar_BuildsCanonicalIndexOnceAndVisitsOnlyMatchedBuckets()
    {
        const int Count = 4096;
        var world = new World();
        Entity source = world.CreateEntity();
        var targets = new Entity[Count];
        var pending = new DeferredRelationEdge<BulkLink>[Count];
        using (var seed = new CommandBuffer(world))
        {
            var relations = seed.Relations<BulkLink>();
            for (int i = 0; i < Count; i++)
            {
                targets[i] = world.CreateEntity();
                pending[i] = relations.Create(
                    source,
                    targets[i],
                    new BulkLink(),
                    RelationMaintenanceTiming.Immediate);
            }
            seed.Playback();
        }

        var edges = new RelationEdge<BulkLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        RelationCanonicalLookupDiagnostics before =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();
        using (var failing = new CommandBuffer(world))
        {
            var relations = failing.Relations<BulkLink>();
            for (int i = 0; i < Count; i++)
                relations.DestroyAllBetween(source, targets[i]);
            relations.SetAdjacencyOrder(
                source,
                RelationAdjacencyRole.Incident,
                RelationAdjacencyOrderPolicy.Ordered);

            Assert.Throws<InvalidOperationException>(() => failing.Playback());
        }

        Assert.Equal(
            before,
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>());
        Assert.All(edges, edge => Assert.True(world.IsAlive(edge.Entity)));

        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<BulkLink>();
            for (int i = 0; i < Count; i++)
                relations.DestroyAllBetween(source, targets[i]);
            commands.Playback();
        }

        RelationCanonicalLookupDiagnostics after =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();
        Assert.Equal(1, after.BulkIndexBuildCount - before.BulkIndexBuildCount);
        Assert.Equal(Count, after.BulkIndexBuildEdgeVisits - before.BulkIndexBuildEdgeVisits);
        Assert.Equal(Count, after.BetweenLookupCount - before.BetweenLookupCount);
        Assert.Equal(Count, after.BetweenBucketVisits - before.BetweenBucketVisits);
        Assert.All(edges, edge => Assert.False(world.IsAlive(edge.Entity)));
    }

    [Fact]
    public void BuiltIndex_TracksDeferredMoveAndAtBulkUsesCanonicalRoleBucket()
    {
        var world = new World();
        Entity oldSource = world.CreateEntity();
        Entity oldTarget = world.CreateEntity();
        Entity newSource = world.CreateEntity();
        Entity newTarget = world.CreateEntity();
        RelationEdge<BulkLink> edge = world.CreateRelation(
            oldSource,
            oldTarget,
            new BulkLink());
        RelationCanonicalLookupDiagnostics before =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();

        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<BulkLink>();
            relations.DestroyAllBetween(oldSource, newTarget);
            relations.Retarget(
                edge,
                newSource,
                newTarget,
                RelationMaintenanceTiming.Deferred);
            relations.DestroyAllBetween(oldSource, oldTarget);
            relations.DestroyAllOutgoing(newSource);
            commands.Playback();
        }

        RelationCanonicalLookupDiagnostics after =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();
        Assert.Equal(1, after.BulkIndexBuildCount - before.BulkIndexBuildCount);
        Assert.Equal(1, after.BulkIndexBuildEdgeVisits - before.BulkIndexBuildEdgeVisits);
        Assert.Equal(2, after.BetweenLookupCount - before.BetweenLookupCount);
        Assert.Equal(0, after.BetweenBucketVisits - before.BetweenBucketVisits);
        Assert.Equal(1, after.AtLookupCount - before.AtLookupCount);
        Assert.Equal(1, after.AtBucketVisits - before.AtBucketVisits);
        Assert.False(world.IsAlive(edge.Entity));
    }

    [Fact]
    public void BuiltIndex_TracksImmediateCancellationOfDeferredCanonicalMove()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity appliedTarget = world.CreateEntity();
        Entity projectedTarget = world.CreateEntity();
        Entity unrelatedTarget = world.CreateEntity();
        RelationEdge<BulkLink> edge = world.CreateRelation(
            source,
            appliedTarget,
            new BulkLink());
        RelationCanonicalLookupDiagnostics before =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();

        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<BulkLink>();
            relations.DestroyAllBetween(source, unrelatedTarget);
            relations.Retarget(
                edge,
                source,
                projectedTarget,
                RelationMaintenanceTiming.Deferred);
            relations.Retarget(
                edge,
                source,
                appliedTarget,
                RelationMaintenanceTiming.Immediate);
            relations.DestroyAllBetween(source, projectedTarget);
            relations.DestroyAllBetween(source, appliedTarget);
            commands.Playback();
        }

        RelationCanonicalLookupDiagnostics after =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();
        Assert.Equal(1, after.BulkIndexBuildCount - before.BulkIndexBuildCount);
        Assert.Equal(1, after.BulkIndexBuildEdgeVisits - before.BulkIndexBuildEdgeVisits);
        Assert.Equal(3, after.BetweenLookupCount - before.BetweenLookupCount);
        Assert.Equal(1, after.BetweenBucketVisits - before.BetweenBucketVisits);
        Assert.False(world.IsAlive(edge.Entity));
    }

    [Fact]
    public void DirtyEndpointIndex_MakesUnrelatedCleanupLocalAndPreservesCurrentOrAppliedSemantics()
    {
        const int Count = 4096;
        const int UnrelatedCount = 1024;
        var world = new World();
        var sources = new Entity[Count];
        var oldTargets = new Entity[Count];
        var newTargets = new Entity[Count];
        var unrelated = new Entity[UnrelatedCount];
        var pending = new DeferredRelationEdge<BulkLink>[Count];
        for (int i = 0; i < Count; i++)
        {
            sources[i] = world.CreateEntity();
            oldTargets[i] = world.CreateEntity();
            newTargets[i] = world.CreateEntity();
        }
        for (int i = 0; i < UnrelatedCount; i++)
            unrelated[i] = world.CreateEntity();

        using (var seed = new CommandBuffer(world))
        {
            var relations = seed.Relations<BulkLink>();
            for (int i = 0; i < Count; i++)
            {
                pending[i] = relations.Create(
                    sources[i],
                    oldTargets[i],
                    new BulkLink(),
                    RelationMaintenanceTiming.Immediate);
            }
            seed.Playback();
        }

        var edges = new RelationEdge<BulkLink>[Count];
        using (var retarget = new CommandBuffer(world))
        {
            var relations = retarget.Relations<BulkLink>();
            for (int i = 0; i < Count; i++)
            {
                edges[i] = pending[i].Resolve();
                relations.Retarget(
                    edges[i],
                    sources[i],
                    newTargets[i],
                    RelationMaintenanceTiming.Deferred);
            }
            retarget.Playback();
        }

        RelationCanonicalLookupDiagnostics beforeDirect =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();
        world.DestroyEntity(unrelated[0]);
        RelationCanonicalLookupDiagnostics afterDirect =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();
        Assert.Equal(
            1,
            afterDirect.CleanupLookupCount - beforeDirect.CleanupLookupCount);
        Assert.Equal(
            0,
            afterDirect.CleanupAppliedEntryVisits - beforeDirect.CleanupAppliedEntryVisits);
        Assert.Equal(
            0,
            afterDirect.CleanupDirtyEntryVisits - beforeDirect.CleanupDirtyEntryVisits);

        using (var cleanup = new CommandBuffer(world))
        {
            for (int i = 1; i < UnrelatedCount; i++)
                cleanup.DestroyEntity(unrelated[i]);
            cleanup.Playback();
        }
        RelationCanonicalLookupDiagnostics afterUnrelated =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();
        Assert.Equal(
            UnrelatedCount - 1,
            afterUnrelated.CleanupLookupCount - afterDirect.CleanupLookupCount);
        Assert.Equal(
            0,
            afterUnrelated.CleanupAppliedEntryVisits - afterDirect.CleanupAppliedEntryVisits);
        Assert.Equal(
            0,
            afterUnrelated.CleanupDirtyEntryVisits - afterDirect.CleanupDirtyEntryVisits);

        RelationCanonicalLookupDiagnostics beforeCurrent = afterUnrelated;
        world.DestroyEntity(newTargets[0]);
        RelationCanonicalLookupDiagnostics afterCurrent =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();
        Assert.Equal(
            0,
            afterCurrent.CleanupAppliedEntryVisits - beforeCurrent.CleanupAppliedEntryVisits);
        Assert.Equal(
            1,
            afterCurrent.CleanupDirtyEntryVisits - beforeCurrent.CleanupDirtyEntryVisits);
        Assert.False(world.IsAlive(edges[0].Entity));

        RelationCanonicalLookupDiagnostics beforeApplied = afterCurrent;
        world.DestroyEntity(oldTargets[1]);
        RelationCanonicalLookupDiagnostics afterApplied =
            world.RelationGraph.StateCanonicalLookupDiagnostics<BulkLink>();
        Assert.Equal(
            1,
            afterApplied.CleanupAppliedEntryVisits - beforeApplied.CleanupAppliedEntryVisits);
        Assert.Equal(
            0,
            afterApplied.CleanupDirtyEntryVisits - beforeApplied.CleanupDirtyEntryVisits);
        Assert.False(world.IsAlive(edges[1].Entity));
        Assert.True(world.IsAlive(edges[2].Entity));
    }
}
