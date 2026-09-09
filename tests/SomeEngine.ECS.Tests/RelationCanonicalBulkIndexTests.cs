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
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<FinalImageUnique>();
        relations.Retarget(existing, existingSource, projectedTarget, RelationMaintenanceTiming.Deferred);
        relations.Create(immediateSource, projectedTarget, new FinalImageUnique(), RelationMaintenanceTiming.Immediate);
        Assert.Throws<InvalidOperationException>(commands.Playback);

        Assert.Equal(entityCount, world.EntityCount);
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
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<FinalImageUnique>();
            relations.Retarget(edgeA, sourceA, targetB, RelationMaintenanceTiming.Deferred);
            relations.Retarget(edgeB, sourceB, targetA, RelationMaintenanceTiming.Deferred);
            commands.Playback();
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

        Assert.All(edges, edge => Assert.True(world.IsAlive(edge.Entity)));

        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<BulkLink>();
            for (int i = 0; i < Count; i++)
                relations.DestroyAllBetween(source, targets[i]);
            commands.Playback();
        }

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

        world.DestroyEntity(unrelated[0]);

        using (var cleanup = new CommandBuffer(world))
        {
            for (int i = 1; i < UnrelatedCount; i++)
                cleanup.DestroyEntity(unrelated[i]);
            cleanup.Playback();
        }

        world.DestroyEntity(newTargets[0]);
        Assert.False(world.IsAlive(edges[0].Entity));

        world.DestroyEntity(oldTargets[1]);
        Assert.False(world.IsAlive(edges[1].Entity));
        Assert.True(world.IsAlive(edges[2].Entity));
    }
}
