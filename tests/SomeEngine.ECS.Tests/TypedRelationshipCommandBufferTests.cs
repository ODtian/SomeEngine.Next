using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;

namespace SomeEngine.ECS.Tests;

public class TypedRelationshipCommandBufferTests
{
    private readonly struct SceneDomain : IHierarchyDomain;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct CommandLink : IAddedComponent
    {
        public int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private struct CommandUniqueTarget : IAddedComponent;

    [Fact]
    public void HighFanOutImmediateCreate_CopiesNoSourceEntriesAndFreezesEachShardOnce()
    {
        const int Count = 4096;
        var world = new World();
        Entity source = world.Create();
        var targets = new Entity[Count];
        for (int i = 0; i < Count; i++)
            targets[i] = world.Create();

        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                _ = commands.CreateRelation<CommandLink>(source, targets[i], new CommandLink { Value = i }, false);
            }
            commands.Playback();
        }

        Assert.Equal(Count, ReadOutgoing<CommandLink>(world, source).Length);
    }

    [Fact]
    public void HighFanOutDirtyRetarget_FinalValidationCopiesEachWorkspaceOnce()
    {
        const int Count = 4096;
        var world = new World();
        Entity source = world.Create();
        Entity extraTarget = world.Create();
        var oldTargets = new Entity[Count];
        var newTargets = new Entity[Count];
        var pending = new DeferredRelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
        {
            oldTargets[i] = world.Create();
            newTargets[i] = world.Create();
        }

        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
                pending[i] = commands.CreateRelation<CommandLink>(source, oldTargets[i], new CommandLink());
            commands.Playback();
        }

        var edges = new RelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        DeferredRelationEdge<CommandLink> extra;
        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                commands.RetargetRelation<CommandLink>(edges[i], source, newTargets[i], true);
            }
            extra = commands.CreateRelation<CommandLink>(source, extraTarget, new CommandLink(), false);
            commands.Playback();
        }

        Assert.Equal(Count + 1, ReadOutgoing<CommandLink>(world, source).Length);
        Assert.Equal(newTargets[0], ReadEndpoints(world, edges[0]).Target);
        _ = extra.Resolve();

        world.MaintainRelations<CommandLink>();
        Assert.Equal(edges[0], Assert.Single(
            ReadIncoming<CommandLink>(world, newTargets[0]).ToArray()).Edge);
    }

    [Fact]
    public void LargeSameTypeBatches_CopyRelationGenerationOncePerAtomicPlayback()
    {
        const int Count = 384;
        var world = new World();
        var sources = new Entity[Count];
        var originalTargets = new Entity[Count];
        var replacementTargets = new Entity[Count];
        for (int i = 0; i < Count; i++)
        {
            sources[i] = world.Create();
            originalTargets[i] = world.Create();
            replacementTargets[i] = world.Create();
        }

        var pending = new DeferredRelationEdge<CommandLink>[Count];
        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                pending[i] = commands.CreateRelation<CommandLink>(sources[i], originalTargets[i], new CommandLink { Value = i });
            }
            commands.Playback();
        }

        var edges = new RelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                commands.RetargetRelation<CommandLink>(edges[i], sources[i], replacementTargets[i], false);
            }
            commands.Playback();
        }

        for (int i = 0; i < Count; i++)
        {
            Assert.Equal(
                replacementTargets[i],
                ReadEndpoints(world, edges[i]).Target);
        }

        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
                commands.DestroyRelation<CommandLink>(edges[i]);
            commands.Playback();
        }
        for (int i = 0; i < Count; i++)
        {
            world.Remove<Removed<RelationEndpoints<CommandLink>>>(edges[i].Entity);
            Assert.False(world.IsAlive(edges[i].Entity));
        }
    }

    [Fact]
    public void ImmediateCommandsThatConsumeAllDeferredWrites_CopyGenerationOnce()
    {
        const int Count = 128;
        var world = new World();
        var sources = new Entity[Count];
        var originalTargets = new Entity[Count];
        var finalTargets = new Entity[Count];
        var createSources = new Entity[Count];
        var createTargets = new Entity[Count];
        var edges = new RelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
        {
            sources[i] = world.Create();
            originalTargets[i] = world.Create();
            finalTargets[i] = world.Create();
            createSources[i] = world.Create();
            createTargets[i] = world.Create();
            edges[i] = world.CreateRelation(sources[i], originalTargets[i], new CommandLink());
        }

        using var commands = new CommandBuffer(world);
        for (int i = 0; i < Count; i++)
        {
            commands.RetargetRelation<CommandLink>(edges[i], sources[i], finalTargets[i], true);
        }
        for (int i = 0; i < Count; i++)
        {
            commands.RetargetRelation<CommandLink>(edges[i], sources[i], finalTargets[i], false);
            _ = commands.CreateRelation<CommandLink>(createSources[i], createTargets[i], new CommandLink(), false);
        }

        commands.Playback();

        for (int i = 0; i < Count; i++)
            Assert.Equal(finalTargets[i], ReadEndpoints(world, edges[i]).Target);
    }

    [Fact]
    public void MixedRelationshipCommandPlaybackDoesNotThrow()
    {
        const int Count = 4096;
        var world = new World();
        Entity dirtySource = world.Create();
        Entity immediateSource = world.Create();
        var oldTargets = new Entity[Count];
        var newTargets = new Entity[Count];
        var immediateTargets = new Entity[Count];
        for (int i = 0; i < Count; i++)
        {
            oldTargets[i] = world.Create();
            newTargets[i] = world.Create();
            immediateTargets[i] = world.Create();
        }

        var pending = new DeferredRelationEdge<CommandLink>[Count];
        using (var seed = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                pending[i] = seed.CreateRelation<CommandLink>(dirtySource, oldTargets[i], new CommandLink(), false);
            }
            seed.Playback();
        }

        var edges = new RelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                commands.RetargetRelation<CommandLink>(edges[i], dirtySource, newTargets[i], true);
            }
            for (int i = 0; i < Count; i++)
            {
                _ = commands.CreateRelation<CommandLink>(immediateSource, immediateTargets[i], new CommandLink(), false);
            }
            commands.Playback();
        }

    }

    [Fact]
    public void ImmediateCommandsThatConsumeAllDeferredWrites_RequireNoFinalImageScan()
    {
        const int Count = 1024;
        var world = new World();
        Entity source = world.Create();
        var oldTargets = new Entity[Count];
        var newTargets = new Entity[Count];
        for (int i = 0; i < Count; i++)
        {
            oldTargets[i] = world.Create();
            newTargets[i] = world.Create();
        }

        var pending = new DeferredRelationEdge<CommandLink>[Count];
        using (var seed = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                pending[i] = seed.CreateRelation<CommandLink>(source, oldTargets[i], new CommandLink(), false);
            }
            seed.Playback();
        }

        var edges = new RelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                commands.RetargetRelation<CommandLink>(edges[i], source, newTargets[i], true);
            }
            for (int i = 0; i < Count; i++)
            {
                commands.RetargetRelation<CommandLink>(edges[i], source, newTargets[i], false);
            }
            commands.Playback();
        }

        for (int i = 0; i < Count; i++)
            Assert.Equal(newTargets[i], ReadEndpoints(world, edges[i]).Target);
    }

    [Fact]
    public void CancelDeferredUniqueTarget_AllowsFollowingCreateToReuseProjectedTarget()
    {
        var world = new World();
        Entity source = world.Create();
        Entity newSource = world.Create();
        Entity appliedTarget = world.Create();
        Entity projectedTarget = world.Create();
        RelationEdge<CommandUniqueTarget> edge = world.CreateRelation(
            source,
            appliedTarget,
            new CommandUniqueTarget());

        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandUniqueTarget>(edge, source, projectedTarget, true);
        commands.RetargetRelation<CommandUniqueTarget>(edge, source, appliedTarget, false);
        DeferredRelationEdge<CommandUniqueTarget> reused = commands.CreateRelation<CommandUniqueTarget>(newSource, projectedTarget, new CommandUniqueTarget(), false);

        commands.Playback();

        Assert.Equal(appliedTarget, ReadEndpoints(world, edge).Target);
        Assert.Equal(reused.Resolve(), Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, projectedTarget).ToArray()).Edge);
    }

    [Fact]
    public void DestroyDeferredUniqueTarget_AllowsFollowingCreateToReuseProjectedTarget()
    {
        var world = new World();
        Entity source = world.Create();
        Entity newSource = world.Create();
        Entity appliedTarget = world.Create();
        Entity projectedTarget = world.Create();
        RelationEdge<CommandUniqueTarget> edge = world.CreateRelation(
            source,
            appliedTarget,
            new CommandUniqueTarget());

        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandUniqueTarget>(edge, source, projectedTarget, true);
        commands.DestroyRelation<CommandUniqueTarget>(edge);
        DeferredRelationEdge<CommandUniqueTarget> reused = commands.CreateRelation<CommandUniqueTarget>(newSource, projectedTarget, new CommandUniqueTarget(), false);

        commands.Playback();

        world.Remove<Removed<RelationEndpoints<CommandUniqueTarget>>>(edge.Entity);

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Equal(reused.Resolve(), Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, projectedTarget).ToArray()).Edge);
    }

    [Fact]
    public void DirtyProjection_MirrorsOrderPolicyAndReorderBeforePlacedCreate()
    {
        var world = new World();
        Entity source = world.Create();
        Entity firstTarget = world.Create();
        Entity secondTarget = world.Create();
        Entity deferredTarget = world.Create();
        Entity createdTarget = world.Create();
        RelationEdge<CommandLink> first = world.CreateRelation(source, firstTarget, new CommandLink());
        RelationEdge<CommandLink> second = world.CreateRelation(source, secondTarget, new CommandLink());

        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandLink>(first, source, deferredTarget, true);
        commands.SetRelationOrder<CommandLink>(source, RelationAdjacencyRole.Outgoing, true);
        commands.ReorderRelation<CommandLink>(source, RelationAdjacencyRole.Outgoing, second, insertAt: 0);
        DeferredRelationEdge<CommandLink> created = commands.CreateRelation<CommandLink>(source, createdTarget, new CommandLink(), false, outgoingIndex: 1);

        commands.Playback();

        Assert.Equal(
            new[] { second, created.Resolve(), first },
            ReadOutgoing<CommandLink>(world, source).ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void HierarchyImmediatePlayback_ResolvesDeferredEntitiesAndUpdatesChildren()
    {
        var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity parent = commands.Create();
        DeferredEntity child = commands.Create();

        commands.EditParents<SceneDomain>([new(child, parent, -1)], deferred: false);

        Assert.Equal(0, world.EntityCount);
        Assert.False(child.TryResolve(out _));
        commands.Playback();

        Entity liveParent = parent.Resolve();
        Entity liveChild = child.Resolve();
        Assert.Equal(
            liveParent,
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetParent(world, liveChild));
        Assert.Equal(
            new[] { liveChild },
            ReadChildren<SceneDomain>(world, liveParent).ToArray());
    }

    [Fact]
    public void HierarchyDeferredPlayback_ChangesParentButLeavesLastAppliedChildren()
    {
        var world = new World();
        Entity oldParent = world.Create();
        Entity newParent = world.Create();
        Entity child = world.Create();
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetParent(world, child, oldParent);

        using var commands = new CommandBuffer(world);
        commands.EditParents<SceneDomain>([new(child, newParent, -1)], deferred: true);
        commands.Playback();

        Assert.Equal(newParent, SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(
            new[] { child },
            ReadChildren<SceneDomain>(world, oldParent).ToArray());
        Assert.Empty(ReadChildren<SceneDomain>(world, newParent));

        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.Maintain(world);
        Assert.Empty(ReadChildren<SceneDomain>(world, oldParent));
        Assert.Equal(
            new[] { child },
            ReadChildren<SceneDomain>(world, newParent).ToArray());
    }

    [Fact]
    public void LaterInvalidHierarchyCommand_LeavesImmediateMutationAndGenerationUnpublished()
    {
        var world = new World();
        Entity oldParent = world.Create();
        Entity newParent = world.Create();
        Entity child = world.Create();
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetParent(world, child, oldParent);
        var oldChildren = ReadChildren<SceneDomain>(world, oldParent);
        var newChildren = ReadChildren<SceneDomain>(world, newParent);

        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using var commands = new CommandBuffer(candidate);
        commands.EditParents<SceneDomain>([new(child, newParent, -1)], deferred: false);
        commands.EditParents<SceneDomain>([new(newParent, newParent, -1)], deferred: false);

        Assert.Throws<ArgumentException>(() => commands.Playback());

        Assert.Equal(oldParent, SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetParent(world, child));
        var oldAfter = ReadChildren<SceneDomain>(world, oldParent);
        var newAfter = ReadChildren<SceneDomain>(world, newParent);
        Assert.Equal(oldChildren, oldAfter);
        Assert.Equal(newChildren, newAfter);
        Assert.Equal(new[] { child }, oldAfter.ToArray());
        Assert.Empty(newAfter);
    }

    [Fact]
    public void HierarchyOrderCommandsAndDestroySubtree_CallTypedKernel()
    {
        var world = new World();
        Entity root = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();

        using (var commands = new CommandBuffer(world))
        {
            commands.SetHierarchyOrder<SceneDomain>(root, true);
            commands.EditParents<SceneDomain>([new(first, root, -1)], deferred: false);
            commands.EditParents<SceneDomain>([new(second, root, 0)], deferred: false);
            commands.ReorderChild<SceneDomain>(first, insertAt: 0);
            commands.Playback();
        }

        Assert.Equal(
            new[] { first, second },
            ReadChildren<SceneDomain>(world, root).ToArray());

        using (var commands = new CommandBuffer(world))
        {
            commands.DestroySubtree<SceneDomain>(root);
            commands.Playback();
        }

        world.ClearRemoved<Parent<SceneDomain>>(world.Version);
        world.ClearRemoved<Children<SceneDomain>>(world.Version);
        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(first));
        Assert.False(world.IsAlive(second));
    }

    [Fact]
    public void RelationCreate_ReturnsDeferredHandleAndUsesDeferredEndpointMapping()
    {
        var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity source = commands.Create();
        DeferredEntity originalTarget = commands.Create();
        DeferredEntity finalTarget = commands.Create();

        DeferredRelationEdge<CommandLink> pending = commands.CreateRelation<CommandLink>(source, originalTarget, new CommandLink { Value = 42 });
        commands.RetargetRelation<CommandLink>(pending, source, finalTarget, false);

        Assert.False(pending.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => pending.Resolve());

        commands.Playback();

        RelationEdge<CommandLink> edge = pending.Resolve();
        Entity liveSource = source.Resolve();
        Entity liveFinalTarget = finalTarget.Resolve();
        Assert.True(pending.TryResolve(out _));
        Assert.True(world.IsAlive(edge.Entity));
        Assert.Equal(42, world.Read<CommandLink>(edge.Entity).Value);
        var endpoints = ReadEndpoints(world, edge);
        Assert.Equal(liveSource, endpoints.Source);
        Assert.Equal(liveFinalTarget, endpoints.Target);
        Assert.Throws<InvalidOperationException>(() => commands.Playback());
    }

    [Fact]
    public void ClearingUnplayedCreate_InvalidatesDeferredEdgeAndEndpointsWithoutWorldMutation()
    {
        var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity source = commands.Create();
        DeferredEntity target = commands.Create();
        DeferredRelationEdge<CommandLink> pending = commands.CreateRelation<CommandLink>(source, target, new CommandLink { Value = 1 });

        commands.Clear();

        Assert.Equal(0, world.EntityCount);
        Assert.False(source.TryResolve(out _));
        Assert.False(target.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => source.Resolve());
        Assert.Throws<InvalidOperationException>(() => target.Resolve());
        Assert.False(pending.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => pending.Resolve());
    }

    [Fact]
    public void MixedLiveAndDeferredEntities_AreResolvedAcrossHierarchyAndRelationCommands()
    {
        var world = new World();
        Entity liveParent = world.Create();
        Entity liveChild = world.Create();
        Entity liveTarget = world.Create();
        using var commands = new CommandBuffer(world);
        DeferredEntity deferredParent = commands.Create();
        DeferredEntity deferredChild = commands.Create();

        commands.EditParents<SceneDomain>([new(deferredChild, liveParent, -1)], deferred: false);
        commands.EditParents<SceneDomain>([new(liveChild, deferredParent, -1)], deferred: false);

        DeferredRelationEdge<CommandLink> edge = commands.CreateRelation<CommandLink>(deferredChild, liveTarget, new CommandLink { Value = 9 });
        commands.RetargetRelation<CommandLink>(edge, liveParent, deferredParent);

        commands.Playback();

        Entity resolvedParent = deferredParent.Resolve();
        Entity resolvedChild = deferredChild.Resolve();
        Assert.Equal(
            liveParent,
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetParent(world, resolvedChild));
        Assert.Equal(
            resolvedParent,
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetParent(world, liveChild));
        var endpoints = ReadEndpoints(world, edge.Resolve());
        Assert.Equal(liveParent, endpoints.Source);
        Assert.Equal(resolvedParent, endpoints.Target);
    }

    [Fact]
    public void RelationDeferredRetarget_PlaybackLeavesAppliedAdjacencyUntilMaintain()
    {
        var world = new World();
        Entity oldSource = world.Create();
        Entity newSource = world.Create();
        Entity target = world.Create();
        RelationEdge<CommandLink> edge = world.CreateRelation(
            oldSource,
            target,
            new CommandLink { Value = 3 });

        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandLink>(edge, newSource, target, true);
        commands.Playback();

        Assert.Equal(newSource, ReadEndpoints(world, edge).Source);
        Assert.Equal(edge, Assert.Single(ReadOutgoing<CommandLink>(world, oldSource).ToArray()).Edge);
        Assert.Empty(ReadOutgoing<CommandLink>(world, newSource).ToArray());

        world.MaintainRelations<CommandLink>();

        Assert.Empty(ReadOutgoing<CommandLink>(world, oldSource).ToArray());
        Assert.Equal(edge, Assert.Single(ReadOutgoing<CommandLink>(world, newSource).ToArray()).Edge);
    }

    [Fact]
    public void RelationOrderReorderDestroyAndBulkDestroy_UseEdgeIdentity()
    {
        var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        RelationEdge<CommandLink> first = world.CreateRelation(
            source,
            target,
            new CommandLink { Value = 1 });
        RelationEdge<CommandLink> second = world.CreateRelation(
            source,
            target,
            new CommandLink { Value = 2 });

        using (var commands = new CommandBuffer(world))
        {
            commands.SetRelationOrder<CommandLink>(source, RelationAdjacencyRole.Outgoing, true);
            commands.ReorderRelation<CommandLink>(source, RelationAdjacencyRole.Outgoing, second, insertAt: 0);
            commands.DestroyRelation<CommandLink>(first);
            commands.Playback();
        }

        var ordered = ReadOutgoing<CommandLink>(world, source);
        Assert.Equal(second, Assert.Single(ordered).Edge);
        world.Remove<Removed<RelationEndpoints<CommandLink>>>(first.Entity);
        Assert.False(world.IsAlive(first.Entity));

        using (var commands = new CommandBuffer(world))
        {
            commands.DestroyRelationsBetween<CommandLink>(source, target);
            commands.Playback();
        }

        world.Remove<Removed<RelationEndpoints<CommandLink>>>(second.Entity);

        Assert.False(world.IsAlive(second.Entity));
        Assert.Empty(ReadPairEdges<CommandLink>(world, source, target).ToArray());
    }

    [Fact]
    public void CreatedDeferredEdge_CanBeDestroyedLaterInSamePlayback()
    {
        var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        using var commands = new CommandBuffer(world);
        DeferredRelationEdge<CommandLink> pending = commands.CreateRelation<CommandLink>(source, target, new CommandLink { Value = 9 });
        commands.DestroyRelation<CommandLink>(pending);

        commands.Playback();

        RelationEdge<CommandLink> identity = pending.Resolve();
        world.Remove<Removed<RelationEndpoints<CommandLink>>>(identity.Entity);
        Assert.False(world.IsAlive(identity.Entity));
        Assert.Empty(ReadPairEdges<CommandLink>(world, source, target).ToArray());
    }

    [Fact]
    public void DeferredRetargetThenDestroyInSamePlayback_DoesNotValidateDeadPreimage()
    {
        var world = new World();
        Entity source = world.Create();
        Entity oldTarget = world.Create();
        Entity newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new CommandLink());
        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandLink>(edge, source, newTarget, true);
        commands.DestroyRelation<CommandLink>(edge);

        commands.Playback();

        world.Remove<Removed<RelationEndpoints<CommandLink>>>(edge.Entity);

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(ReadIncoming<CommandLink>(world, oldTarget).ToArray());
        Assert.Empty(ReadIncoming<CommandLink>(world, newTarget).ToArray());
    }

    [Fact]
    public void LaterInvalidRelationCommand_RollsBackStagedEndpointsAndLeavesAdjacencyApplied()
    {
        var world = new World();
        Entity source = world.Create();
        Entity oldTarget = world.Create();
        Entity newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new CommandLink());
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using var commands = new CommandBuffer(candidate);
        commands.RetargetRelation<CommandLink>(edge, source, newTarget, true);
        commands.SetRelationOrder<CommandLink>(source, RelationAdjacencyRole.Incident, true);

        Assert.Throws<ArgumentException>(() => commands.Playback());

        Assert.Equal(oldTarget, ReadEndpoints(world, edge).Target);
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<CommandLink>(world, oldTarget).ToArray()).Edge);
        Assert.Empty(ReadIncoming<CommandLink>(world, newTarget).ToArray());
        world.MaintainRelations<CommandLink>();
        Assert.Equal(oldTarget, ReadEndpoints(world, edge).Target);
    }

    [Fact]
    public void LaterInvalidRelationCommand_LeavesImmediateRetargetUnpublished()
    {
        var world = new World();
        Entity source = world.Create();
        Entity oldTarget = world.Create();
        Entity newTarget = world.Create();
        var edge = world.CreateRelation(source, oldTarget, new CommandLink());
        var oldGeneration = ReadIncoming<CommandLink>(world, oldTarget);
        var newGeneration = ReadIncoming<CommandLink>(world, newTarget);
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        using var commands = new CommandBuffer(candidate);
        commands.RetargetRelation<CommandLink>(edge, source, newTarget, false);
        commands.SetRelationOrder<CommandLink>(source, RelationAdjacencyRole.Incident, true);

        Assert.Throws<ArgumentException>(() => commands.Playback());

        Assert.Equal(oldTarget, ReadEndpoints(world, edge).Target);
        Assert.Equal(oldGeneration, ReadIncoming<CommandLink>(world, oldTarget));
        Assert.Equal(newGeneration, ReadIncoming<CommandLink>(world, newTarget));
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<CommandLink>(world, oldTarget).ToArray()).Edge);
        Assert.Empty(ReadIncoming<CommandLink>(world, newTarget).ToArray());
    }

    [Fact]
    public void FailedPlaybackRetainsItsCreatedRelationPrefix()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        using var commands = new CommandBuffer(world);
        var created = commands.CreateRelation(source, target, new CommandLink { Value = 7 });
        commands.SetRelationOrder<CommandLink>(source, RelationAdjacencyRole.Incident, true);
        Assert.Throws<ArgumentException>(commands.Playback);
        Assert.True(created.TryResolve(out RelationEdge<CommandLink> edge));
        Assert.Equal(7, world.Read<CommandLink>(edge.Entity).Value);
        Assert.Equal(edge, Assert.Single(ReadPairEdges<CommandLink>(world, source, target)));
    }

    [Fact]
    public void DeferredCreateAndPlacement_PreserveLastAppliedViewsUntilMaintenance()
    {
        var world = new World();
        Entity source = world.Create();
        Entity targetA = world.Create();
        Entity targetB = world.Create();
        world.SetRelationAdjacencyOrder<CommandLink>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        RelationEdge<CommandLink> existing = world.CreateRelation(
            source,
            targetA,
            new CommandLink { Value = 1 });
        using var commands = new CommandBuffer(world);
        DeferredRelationEdge<CommandLink> pending = commands.CreateRelation<CommandLink>(source, targetB, new CommandLink { Value = 2 }, true, outgoingIndex: 0);

        commands.Playback();

        RelationEdge<CommandLink> created = pending.Resolve();
        Assert.Equal(
            new[] { existing },
            ReadOutgoing<CommandLink>(world, source).ToArray().Select(static entry => entry.Edge));

        world.MaintainRelations<CommandLink>();

        Assert.Equal(
            new[] { created, existing },
            ReadOutgoing<CommandLink>(world, source).ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void DeferredHierarchyPlacement_AppliesAtMaintenanceInRecordedOrder()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetOrdered(
            world,
            parent,
            true);
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetParent(world, first, parent);
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetParent(world, second, parent);
        using var commands = new CommandBuffer(world);

        commands.EditParents<SceneDomain>([new(first, parent, 1)], deferred: true);
        commands.Playback();

        Assert.Equal(
            new[] { first, second },
            ReadChildren<SceneDomain>(world, parent).ToArray());
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.Maintain(world);
        Assert.Equal(
            new[] { second, first },
            ReadChildren<SceneDomain>(world, parent).ToArray());
    }

    [Fact]
    public void DeferredUniqueTargetSwap_ValidatesOneFinalCommandImage()
    {
        var world = new World();
        Entity sourceA = world.Create();
        Entity sourceB = world.Create();
        Entity targetA = world.Create();
        Entity targetB = world.Create();
        var edgeA = world.CreateRelation(sourceA, targetA, new CommandUniqueTarget());
        var edgeB = world.CreateRelation(sourceB, targetB, new CommandUniqueTarget());
        using var commands = new CommandBuffer(world);

        commands.EditEndpoints<CommandUniqueTarget>([new(edgeA.Entity, sourceA, targetB), new(edgeB.Entity, sourceB, targetA)], deferred: true);
        commands.SetRelationOrder<CommandUniqueTarget>(sourceA, RelationAdjacencyRole.Outgoing, false);
        commands.Playback();

        Assert.Equal(targetB, ReadEndpoints(world, edgeA).Target);
        Assert.Equal(targetA, ReadEndpoints(world, edgeB).Target);
        world.MaintainRelations<CommandUniqueTarget>();
        Assert.Equal(edgeB, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, targetA).ToArray()).Edge);
        Assert.Equal(edgeA, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, targetB).ToArray()).Edge);
    }

    [Fact]
    public void UnrelatedImmediateCommand_DoesNotValidateHalfOfDeferredUniqueTargetSwap()
    {
        var world = new World();
        Entity sourceA = world.Create();
        Entity sourceB = world.Create();
        Entity unrelatedSource = world.Create();
        Entity targetA = world.Create();
        Entity targetB = world.Create();
        Entity unrelatedTarget = world.Create();
        var edgeA = world.CreateRelation(sourceA, targetA, new CommandUniqueTarget());
        var edgeB = world.CreateRelation(sourceB, targetB, new CommandUniqueTarget());

        using var commands = new CommandBuffer(world);
        commands.EditEndpoints<CommandUniqueTarget>([new(edgeA.Entity, sourceA, targetB), new(edgeB.Entity, sourceB, targetA)], deferred: true);
        DeferredRelationEdge<CommandUniqueTarget> unrelated = commands.CreateRelation<CommandUniqueTarget>(unrelatedSource, unrelatedTarget, new CommandUniqueTarget(), false);

        commands.Playback();

        // Canonical endpoints already expose the complete deferred result, while the inverse
        // views deliberately retain the last-applied image until explicit maintenance.
        Assert.Equal(targetB, ReadEndpoints(world, edgeA).Target);
        Assert.Equal(targetA, ReadEndpoints(world, edgeB).Target);
        Assert.Equal(edgeA, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, targetA).ToArray()).Edge);
        Assert.Equal(edgeB, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, targetB).ToArray()).Edge);
        Assert.Equal(unrelated.Resolve(), Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, unrelatedTarget).ToArray()).Edge);

        world.MaintainRelations<CommandUniqueTarget>();

        Assert.Equal(edgeB, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, targetA).ToArray()).Edge);
        Assert.Equal(edgeA, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, targetB).ToArray()).Edge);
    }

    [Fact]
    public void DeferredRetargetReleasesItsCurrentUniqueTargetBeforeMaintenance()
    {
        using var world = new World();
        Entity existingSource = world.Create();
        Entity newSource = world.Create();
        Entity appliedTarget = world.Create();
        Entity deferredTarget = world.Create();
        var existing = world.CreateRelation(existingSource, appliedTarget, new CommandUniqueTarget());
        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandUniqueTarget>(existing, existingSource, deferredTarget, deferred: true);
        var created = commands.CreateRelation(newSource, appliedTarget, new CommandUniqueTarget());
        commands.Playback();
        Assert.Equal(deferredTarget, ReadEndpoints(world, existing).Target);
        Assert.Equal(appliedTarget, ReadEndpoints(world, created.Resolve()).Target);
        Assert.Equal(2, ReadIncoming<CommandUniqueTarget>(world, appliedTarget).Length);
        world.MaintainRelations<CommandUniqueTarget>();
        Assert.Equal(created.Resolve(), Assert.Single(ReadIncoming<CommandUniqueTarget>(world, appliedTarget)).Edge);
        Assert.Equal(existing, Assert.Single(ReadIncoming<CommandUniqueTarget>(world, deferredTarget)).Edge);
    }

    [Fact]
    public void AlternatingRelationshipCommandPlaybackDoesNotThrow()
    {
        const int Count = 256;
        var world = new World();
        Entity dirtySource = world.Create();
        Entity immediateSource = world.Create();
        var edges = new RelationEdge<CommandLink>[Count];
        var deferredTargets = new Entity[Count];
        var immediateTargets = new Entity[Count];
        for (int i = 0; i < Count; i++)
        {
            Entity originalTarget = world.Create();
            deferredTargets[i] = world.Create();
            immediateTargets[i] = world.Create();
            edges[i] = world.CreateRelation(
                dirtySource,
                originalTarget,
                new CommandLink { Value = i });
        }

        using (var commands = new CommandBuffer(world))
        {
            for (int i = 0; i < Count; i++)
            {
                commands.RetargetRelation<CommandLink>(edges[i], dirtySource, deferredTargets[i], true);
                _ = commands.CreateRelation<CommandLink>(immediateSource, immediateTargets[i], new CommandLink(), false);
            }
            commands.Playback();
        }

    }

    [Fact]
    public void DeferredUniqueTargetSwap_RemainsAtomicAfterProjectionWasInitialized()
    {
        var world = new World();
        Entity sourceA = world.Create();
        Entity sourceB = world.Create();
        Entity dirtySource = world.Create();
        Entity immediateSource = world.Create();
        Entity targetA = world.Create();
        Entity targetB = world.Create();
        Entity dirtyOldTarget = world.Create();
        Entity dirtyNewTarget = world.Create();
        Entity immediateTarget = world.Create();
        var edgeA = world.CreateRelation(sourceA, targetA, new CommandUniqueTarget());
        var edgeB = world.CreateRelation(sourceB, targetB, new CommandUniqueTarget());
        var dirtyEdge = world.CreateRelation(
            dirtySource,
            dirtyOldTarget,
            new CommandUniqueTarget());

        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandUniqueTarget>(dirtyEdge, dirtySource, dirtyNewTarget, true);
        DeferredRelationEdge<CommandUniqueTarget> immediate = commands.CreateRelation<CommandUniqueTarget>(immediateSource, immediateTarget, new CommandUniqueTarget(), false);
        commands.EditEndpoints<CommandUniqueTarget>([new(edgeA.Entity, sourceA, targetB), new(edgeB.Entity, sourceB, targetA)], deferred: true);

        commands.Playback();
        world.MaintainRelations<CommandUniqueTarget>();

        Assert.Equal(edgeB, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, targetA).ToArray()).Edge);
        Assert.Equal(edgeA, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, targetB).ToArray()).Edge);
        Assert.Equal(dirtyEdge, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, dirtyNewTarget).ToArray()).Edge);
        Assert.Equal(immediate.Resolve(), Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, immediateTarget).ToArray()).Edge);
    }

    [Fact]
    public void PreexistingDeferredCanonicalTarget_RejectsConflictingImmediateCreate()
    {
        var world = new World();
        Entity existingSource = world.Create();
        Entity newSource = world.Create();
        Entity oldTarget = world.Create();
        Entity pendingTarget = world.Create();
        var existing = world.CreateRelation(
            existingSource,
            oldTarget,
            new CommandUniqueTarget());
        world.RetargetRelationDeferred(existing, existingSource, pendingTarget);

        using var commands = new CommandBuffer(world);
        DeferredRelationEdge<CommandUniqueTarget> conflicting =
            commands.CreateRelation<CommandUniqueTarget>(newSource, pendingTarget, new CommandUniqueTarget(), false);

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        Assert.False(conflicting.TryResolve(out _));
        Assert.Equal(pendingTarget, ReadEndpoints(world, existing).Target);
        world.MaintainRelations<CommandUniqueTarget>();
        Assert.Equal(existing, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, pendingTarget).ToArray()).Edge);
    }

    [Fact]
    public void PreexistingDeferredCanonicalTarget_RejectsConflictingImmediateRetarget()
    {
        var world = new World();
        Entity sourceA = world.Create();
        Entity sourceB = world.Create();
        Entity oldTargetA = world.Create();
        Entity pendingTarget = world.Create();
        Entity oldTargetB = world.Create();
        var pending = world.CreateRelation(sourceA, oldTargetA, new CommandUniqueTarget());
        var immediate = world.CreateRelation(sourceB, oldTargetB, new CommandUniqueTarget());
        world.RetargetRelationDeferred(pending, sourceA, pendingTarget);

        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandUniqueTarget>(immediate, sourceB, pendingTarget, false);

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        Assert.Equal(oldTargetB, ReadEndpoints(world, immediate).Target);
        Assert.Equal(immediate, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, oldTargetB).ToArray()).Edge);
        world.MaintainRelations<CommandUniqueTarget>();
        Assert.Equal(pending, Assert.Single(
            ReadIncoming<CommandUniqueTarget>(world, pendingTarget).ToArray()).Edge);
    }

    [Fact]
    public void DeferredPlacementThenImmediateRetarget_ConsumesPendingPlacement()
    {
        var world = new World();
        Entity source = world.Create();
        Entity targetA = world.Create();
        Entity targetB = world.Create();
        world.SetRelationAdjacencyOrder<CommandLink>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(source, targetA, new CommandLink());
        var second = world.CreateRelation(source, targetB, new CommandLink());

        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandLink>(second, source, targetB, true, outgoingIndex: 0);
        commands.RetargetRelation<CommandLink>(second, source, targetB, false);
        commands.ReorderRelation<CommandLink>(source, RelationAdjacencyRole.Outgoing, second, 1);
        commands.Playback();

        Assert.Equal(
            new[] { first, second },
            ReadOutgoing<CommandLink>(world, source).ToArray().Select(static entry => entry.Edge));
        world.MaintainRelations<CommandLink>();
        Assert.Equal(
            new[] { first, second },
            ReadOutgoing<CommandLink>(world, source).ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void DeferredPlacementThenDestroy_CannotReappearAtMaintenance()
    {
        var world = new World();
        Entity source = world.Create();
        Entity targetA = world.Create();
        Entity targetB = world.Create();
        world.SetRelationAdjacencyOrder<CommandLink>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var surviving = world.CreateRelation(source, targetA, new CommandLink());
        var destroyed = world.CreateRelation(source, targetB, new CommandLink());

        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandLink>(destroyed, source, targetB, true, outgoingIndex: 0);
        commands.DestroyRelation<CommandLink>(destroyed);
        commands.Playback();

        world.Remove<Removed<RelationEndpoints<CommandLink>>>(destroyed.Entity);

        Assert.False(world.IsAlive(destroyed.Entity));
        world.MaintainRelations<CommandLink>();
        Assert.Equal(surviving, Assert.Single(
            ReadOutgoing<CommandLink>(world, source).ToArray()).Edge);
    }

    [Fact]
    public void ImmediateAndDeferredTiming_RemainPerEdgeWithinOnePayloadType()
    {
        var world = new World();
        Entity deferredSource = world.Create();
        Entity deferredOld = world.Create();
        Entity deferredNew = world.Create();
        Entity immediateSource = world.Create();
        Entity immediateTarget = world.Create();
        var deferredEdge = world.CreateRelation(
            deferredSource,
            deferredOld,
            new CommandLink { Value = 1 });
        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandLink>(deferredEdge, deferredSource, deferredNew, true);
        DeferredRelationEdge<CommandLink> immediateEdge = commands.CreateRelation<CommandLink>(immediateSource, immediateTarget, new CommandLink { Value = 2 }, false);

        commands.Playback();

        Assert.Equal(deferredNew, ReadEndpoints(world, deferredEdge).Target);
        Assert.Equal(deferredEdge, Assert.Single(
            ReadIncoming<CommandLink>(world, deferredOld).ToArray()).Edge);
        Assert.Empty(ReadIncoming<CommandLink>(world, deferredNew).ToArray());
        Assert.Equal(immediateEdge.Resolve(), Assert.Single(
            ReadIncoming<CommandLink>(world, immediateTarget).ToArray()).Edge);
    }

    [Fact]
    public void DeferredCreateThenImmediateRetarget_AppliesTheRegisteredEdge()
    {
        var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        using var commands = new CommandBuffer(world);
        DeferredRelationEdge<CommandLink> pending = commands.CreateRelation<CommandLink>(source, target, new CommandLink(), true);
        commands.RetargetRelation<CommandLink>(pending, source, target, false);

        commands.Playback();

        RelationEdge<CommandLink> edge = pending.Resolve();
        Assert.Equal(edge, Assert.Single(
            ReadOutgoing<CommandLink>(world, source).ToArray()).Edge);
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<CommandLink>(world, target).ToArray()).Edge);
    }

    [Fact]
    public void ImmediateCreateThenDeferredRetarget_LeavesOriginalAppliedAdjacency()
    {
        var world = new World();
        Entity source = world.Create();
        Entity original = world.Create();
        Entity final = world.Create();
        using var commands = new CommandBuffer(world);
        DeferredRelationEdge<CommandLink> pending = commands.CreateRelation<CommandLink>(source, original, new CommandLink(), false);
        commands.RetargetRelation<CommandLink>(pending, source, final, true);

        commands.Playback();

        var edge = pending.Resolve();
        Assert.Equal(final, ReadEndpoints(world, edge).Target);
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<CommandLink>(world, original).ToArray()).Edge);
        Assert.Empty(ReadIncoming<CommandLink>(world, final).ToArray());
        world.MaintainRelations<CommandLink>();
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<CommandLink>(world, final).ToArray()).Edge);
    }

    [Fact]
    public void ImmediateRetargetThenDeferredRetarget_LeavesIntermediateAppliedAdjacency()
    {
        var world = new World();
        Entity source = world.Create();
        Entity first = world.Create();
        Entity intermediate = world.Create();
        Entity final = world.Create();
        var edge = world.CreateRelation(source, first, new CommandLink());
        using var commands = new CommandBuffer(world);
        commands.RetargetRelation<CommandLink>(edge, source, intermediate, false);
        commands.RetargetRelation<CommandLink>(edge, source, final, true);

        commands.Playback();

        Assert.Equal(final, ReadEndpoints(world, edge).Target);
        Assert.Empty(ReadIncoming<CommandLink>(world, first).ToArray());
        Assert.Equal(edge, Assert.Single(
            ReadIncoming<CommandLink>(world, intermediate).ToArray()).Edge);
        Assert.Empty(ReadIncoming<CommandLink>(world, final).ToArray());
    }

    [Fact]
    public void CreateThenOrderAndReorder_UsesTheNewAppliedEdgeInSamePlayback()
    {
        var world = new World();
        Entity source = world.Create();
        Entity targetA = world.Create();
        Entity targetB = world.Create();
        var existing = world.CreateRelation(source, targetA, new CommandLink());
        using var commands = new CommandBuffer(world);
        var created = commands.CreateRelation<CommandLink>(source, targetB, new CommandLink(), false);
        commands.SetRelationOrder<CommandLink>(source, RelationAdjacencyRole.Outgoing, true);
        commands.ReorderRelation<CommandLink>(source, RelationAdjacencyRole.Outgoing, created, insertAt: 0);

        commands.Playback();

        Assert.Equal(
            new[] { created.Resolve(), existing },
            ReadOutgoing<CommandLink>(world, source).ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void BulkDestroyUsesCurrentCommandImageForCreatedAndRetargetedEdges()
    {
        var world = new World();
        Entity source = world.Create();
        Entity oldTarget = world.Create();
        Entity newTarget = world.Create();
        var moved = world.CreateRelation(source, oldTarget, new CommandLink());
        using var commands = new CommandBuffer(world);
        var created = commands.CreateRelation<CommandLink>(source, newTarget, new CommandLink(), true);
        commands.DestroyRelationsBetween<CommandLink>(source, newTarget);
        commands.RetargetRelation<CommandLink>(moved, source, newTarget, false);
        commands.DestroyRelationsBetween<CommandLink>(source, oldTarget);

        commands.Playback();

        world.Remove<Removed<RelationEndpoints<CommandLink>>>(created.Resolve().Entity);

        Assert.False(world.IsAlive(created.Resolve().Entity));
        Assert.True(world.IsAlive(moved.Entity));
        Assert.Equal(newTarget, ReadEndpoints(world, moved).Target);
    }

    [Fact]
    public void OrderedEmptyEndpointDestroyedInSameBatch_DropsOverlayBeforeIndexReuse()
    {
        var world = new World();
        Entity endpoint = world.Create();

        using (var commands = new CommandBuffer(world))
        {
            commands.SetRelationOrder<CommandLink>(endpoint, RelationAdjacencyRole.Outgoing, true);
            commands.Destroy(endpoint);
            commands.Playback();
        }

        Assert.False(world.IsAlive(endpoint));
        Entity reused = world.Create();
        Assert.True(world.IsAlive(reused));
        Assert.NotEqual(endpoint, reused);

        Entity target = world.Create();
        RelationEdge<CommandLink> edge = world.CreateRelation(
            reused,
            target,
            new CommandLink());
        Assert.Equal(edge, Assert.Single(
            ReadOutgoing<CommandLink>(world, reused).ToArray()).Edge);
    }

    [Fact]
    public void EndpointDestroyUsesCurrentEndpointsAcrossMaintenanceTiming()
    {
        var immediateWorld = new World();
        Entity immediateSource = immediateWorld.Create();
        Entity immediateOld = immediateWorld.Create();
        Entity immediateNew = immediateWorld.Create();
        var immediateEdge = immediateWorld.CreateRelation(
            immediateSource,
            immediateOld,
            new CommandLink());
        using (var commands = new CommandBuffer(immediateWorld))
        {
            commands.RetargetRelation<CommandLink>(immediateEdge, immediateSource, immediateNew, false);
            commands.Destroy(immediateOld);
            commands.Playback();
        }
        Assert.True(immediateWorld.IsAlive(immediateEdge.Entity));

        var deferredWorld = new World();
        Entity deferredSource = deferredWorld.Create();
        Entity deferredOld = deferredWorld.Create();
        Entity deferredNew = deferredWorld.Create();
        var deferredEdge = deferredWorld.CreateRelation(
            deferredSource,
            deferredOld,
            new CommandLink());
        using (var commands = new CommandBuffer(deferredWorld))
        {
            commands.RetargetRelation<CommandLink>(deferredEdge, deferredSource, deferredNew, true);
            commands.Destroy(deferredOld);
            commands.Playback();
        }
        Assert.True(deferredWorld.IsAlive(deferredEdge.Entity));
        Assert.Equal(deferredNew, ReadEndpoints(deferredWorld, deferredEdge).Target);
        Assert.Empty(ReadIncoming<CommandLink>(deferredWorld, deferredOld));
        Assert.Equal(deferredEdge, Assert.Single(ReadIncoming<CommandLink>(deferredWorld, deferredNew)).Edge);
    }

    [Fact]
    public void CreateResultCountIsValidatedBeforeRecordingEndpointEdits()
    {
        using var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        using var commands = new CommandBuffer(world);
        Assert.Throws<ArgumentException>(() => commands.EditEndpoints<CommandLink>(
            [EndpointCommandEdit.Create(source, target)]));
        Assert.Equal(0, commands.Count);
    }
}
