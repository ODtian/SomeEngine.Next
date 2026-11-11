using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public sealed class RelationCanonicalBulkIndexTests
{
    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly struct BulkLink : IAddedComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private readonly struct FinalImageUnique : IAddedComponent;

    [Fact]
    public void IntermediateOwnerValidation_CannotHideLaterImmediateFinalImageConflict()
    {
        var world = new World();
        Entity existingSource = world.Create();
        Entity immediateSource = world.Create();
        Entity appliedTarget = world.Create();
        Entity projectedTarget = world.Create();
        RelationEdge<FinalImageUnique> existing = world.CreateRelation(
            existingSource,
            appliedTarget,
            new FinalImageUnique());
        int entityCount = world.EntityCount;
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using var commands = new CommandBuffer(candidate);
        commands.RetargetRelation<FinalImageUnique>(existing, existingSource, projectedTarget, true);
        commands.CreateRelation<FinalImageUnique>(immediateSource, projectedTarget, new FinalImageUnique(), false);
        Assert.Throws<InvalidOperationException>(commands.Playback);

        Assert.Equal(entityCount, world.EntityCount);
        Assert.Equal(
            appliedTarget,
            ReadEndpoints(world, existing).Target);
        Assert.Equal(existing, Assert.Single(
            ReadIncoming<FinalImageUnique>(world, appliedTarget).ToArray()).Edge);
        Assert.Empty(ReadIncoming<FinalImageUnique>(world, projectedTarget).ToArray());
    }

    [Fact]
    public void IntermediateOwnerValidation_DoesNotRejectHalfOfDeferredFinalImageSwap()
    {
        var world = new World();
        Entity sourceA = world.Create();
        Entity sourceB = world.Create();
        Entity targetA = world.Create();
        Entity targetB = world.Create();
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
            commands.EditEndpoints<FinalImageUnique>([new(edgeA.Entity, sourceA, targetB), new(edgeB.Entity, sourceB, targetA)], deferred: true);
                commands.Playback();
        }

        Assert.Equal(targetB, ReadEndpoints(world, edgeA).Target);
        Assert.Equal(targetA, ReadEndpoints(world, edgeB).Target);
        Assert.Equal(edgeA, Assert.Single(
            ReadIncoming<FinalImageUnique>(world, targetA).ToArray()).Edge);
        Assert.Equal(edgeB, Assert.Single(
            ReadIncoming<FinalImageUnique>(world, targetB).ToArray()).Edge);

        world.MaintainRelations<FinalImageUnique>();
        Assert.Equal(edgeB, Assert.Single(
            ReadIncoming<FinalImageUnique>(world, targetA).ToArray()).Edge);
        Assert.Equal(edgeA, Assert.Single(
            ReadIncoming<FinalImageUnique>(world, targetB).ToArray()).Edge);
    }

    [Fact]
    public void PairBulkStar_BuildsCanonicalIndexOnceAndVisitsOnlyMatchedBuckets()
    {
        const int Count = 4096;
        var world = new World();
        Entity source = world.Create();
        var targets = new Entity[Count];
        var pending = new DeferredRelationEdge<BulkLink>[Count];
        using (var seed = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                targets[i] = world.Create();
                pending[i] = seed.CreateRelation<BulkLink>(source, targets[i], new BulkLink(), false);
            }
            seed.Playback();
        }

        var edges = new RelationEdge<BulkLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using (var failing = new CommandBuffer(candidate))
        {
            for (int i = 0; i < Count; i++)
                failing.DestroyRelationsBetween<BulkLink>(source, targets[i]);
            failing.SetRelationOrder<BulkLink>(source, RelationAdjacencyRole.Incident, true);

            Assert.Throws<ArgumentException>(() => failing.Playback());
        }

        Assert.All(edges, edge => Assert.True(world.IsAlive(edge.Entity)));

        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
                commands.DestroyRelationsBetween<BulkLink>(source, targets[i]);
            commands.Playback();
        }

        Assert.All(edges, edge =>
        {
            world.Remove<Removed<RelationEndpoints<BulkLink>>>(edge.Entity);
            Assert.False(world.IsAlive(edge.Entity));
        });
    }

    [Fact]
    public void BuiltIndex_TracksDeferredMoveAndAtBulkUsesCanonicalRoleBucket()
    {
        var world = new World();
        Entity oldSource = world.Create();
        Entity oldTarget = world.Create();
        Entity newSource = world.Create();
        Entity newTarget = world.Create();
        RelationEdge<BulkLink> edge = world.CreateRelation(
            oldSource,
            oldTarget,
            new BulkLink());

        using (var commands = new CommandBuffer(world))
        {
            commands.DestroyRelationsBetween<BulkLink>(oldSource, newTarget);
            commands.RetargetRelation<BulkLink>(edge, newSource, newTarget, true);
            commands.DestroyRelationsBetween<BulkLink>(oldSource, oldTarget);
            commands.DestroyRelationsAt<BulkLink>(newSource, RelationAdjacencyRole.Outgoing);
            commands.Playback();
        }

        world.Remove<Removed<RelationEndpoints<BulkLink>>>(edge.Entity);

        Assert.False(world.IsAlive(edge.Entity));
    }

    [Fact]
    public void BuiltIndex_TracksImmediateCancellationOfDeferredCanonicalMove()
    {
        var world = new World();
        Entity source = world.Create();
        Entity appliedTarget = world.Create();
        Entity projectedTarget = world.Create();
        Entity unrelatedTarget = world.Create();
        RelationEdge<BulkLink> edge = world.CreateRelation(
            source,
            appliedTarget,
            new BulkLink());

        using (var commands = new CommandBuffer(world))
        {
            commands.DestroyRelationsBetween<BulkLink>(source, unrelatedTarget);
            commands.RetargetRelation<BulkLink>(edge, source, projectedTarget, true);
            commands.RetargetRelation<BulkLink>(edge, source, appliedTarget, false);
            commands.DestroyRelationsBetween<BulkLink>(source, projectedTarget);
            commands.DestroyRelationsBetween<BulkLink>(source, appliedTarget);
            commands.Playback();
        }

        world.Remove<Removed<RelationEndpoints<BulkLink>>>(edge.Entity);

        Assert.False(world.IsAlive(edge.Entity));
    }

    [Fact]
    public void EndpointCleanupUsesCurrentReferencesAndAppliesRetargetedEdges()
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
            sources[i] = world.Create();
            oldTargets[i] = world.Create();
            newTargets[i] = world.Create();
        }
        for (int i = 0; i < UnrelatedCount; i++)
            unrelated[i] = world.Create();

        using (var seed = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                pending[i] = seed.CreateRelation<BulkLink>(sources[i], oldTargets[i], new BulkLink(), false);
            }
            seed.Playback();
        }

        var edges = new RelationEdge<BulkLink>[Count];
        using (var retarget = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                edges[i] = pending[i].Resolve();
                retarget.RetargetRelation<BulkLink>(edges[i], sources[i], newTargets[i], true);
            }
            retarget.Playback();
        }

        world.Destroy(unrelated[0]);

        using (var cleanup = new CommandBuffer(world))
        {
            for (int i = 1; i < UnrelatedCount; i++)
                cleanup.Destroy(unrelated[i]);
            cleanup.Playback();
        }

        world.Destroy(newTargets[0]);
        world.Remove<Removed<RelationEndpoints<BulkLink>>>(edges[0].Entity);
        Assert.False(world.IsAlive(edges[0].Entity));

        world.Destroy(oldTargets[1]);
        Assert.True(world.IsAlive(edges[1].Entity));
        Assert.Equal(newTargets[1], ReadEndpoints(world, edges[1]).Target);
        Assert.Empty(ReadIncoming<BulkLink>(world, oldTargets[1]));
        Assert.Equal(edges[1], Assert.Single(ReadIncoming<BulkLink>(world, newTargets[1])).Edge);
        Assert.True(world.IsAlive(edges[2].Entity));
    }
}
