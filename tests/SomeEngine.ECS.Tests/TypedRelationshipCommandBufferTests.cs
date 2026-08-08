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
    private struct CommandLink : IComponent
    {
        public int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private struct CommandUniqueTarget : IComponent;

    [Fact]
    public void HighFanOutImmediateCreate_CopiesNoSourceEntriesAndFreezesEachShardOnce()
    {
        const int Count = 4096;
        var world = new World();
        Entity source = world.CreateEntity();
        var targets = new Entity[Count];
        for (int i = 0; i < Count; i++)
            targets[i] = world.CreateEntity();

        RelationAdjacencyBatchDiagnostics before =
            world.RelationGraph.StateAdjacencyBatchDiagnostics<CommandLink>();
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
            {
                _ = relations.Create(
                    source,
                    targets[i],
                    new CommandLink { Value = i },
                    RelationMaintenanceTiming.Immediate);
            }
            commands.Playback();
        }

        RelationAdjacencyBatchDiagnostics after =
            world.RelationGraph.StateAdjacencyBatchDiagnostics<CommandLink>();
        Assert.Equal(0, after.SourceEntryCopies - before.SourceEntryCopies);
        Assert.Equal(Count * 2, after.FrozenEntries - before.FrozenEntries);
        Assert.Equal(Count + 1, after.FrozenShards - before.FrozenShards);
        Assert.Equal(Count, world.GetOutgoingRelations<CommandLink>(source).Entries.Length);
    }

    [Fact]
    public void HighFanOutDirtyRetarget_FinalValidationCopiesEachWorkspaceOnce()
    {
        const int Count = 4096;
        var world = new World();
        Entity source = world.CreateEntity();
        Entity extraTarget = world.CreateEntity();
        var oldTargets = new Entity[Count];
        var newTargets = new Entity[Count];
        var pending = new DeferredRelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
        {
            oldTargets[i] = world.CreateEntity();
            newTargets[i] = world.CreateEntity();
        }

        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
                pending[i] = relations.Create(source, oldTargets[i], new CommandLink());
            commands.Playback();
        }

        var edges = new RelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        RelationAdjacencyBatchDiagnostics beforeSynchronization =
            world.RelationGraph.StateAdjacencyBatchDiagnostics<CommandLink>();
        DeferredRelationEdge<CommandLink> extra;
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
            {
                relations.Retarget(
                    edges[i],
                    source,
                    newTargets[i],
                    RelationMaintenanceTiming.Deferred);
            }
            extra = relations.Create(
                source,
                extraTarget,
                new CommandLink(),
                RelationMaintenanceTiming.Immediate);
            commands.Playback();
        }

        RelationAdjacencyBatchDiagnostics afterSynchronization =
            world.RelationGraph.StateAdjacencyBatchDiagnostics<CommandLink>();
        Assert.Equal(
            Count * 4 + 3,
            afterSynchronization.SourceEntryCopies - beforeSynchronization.SourceEntryCopies);
        Assert.Equal(
            Count * 3 + 3,
            afterSynchronization.FrozenEntries - beforeSynchronization.FrozenEntries);
        Assert.Equal(
            Count * 2 + 3,
            afterSynchronization.FrozenShards - beforeSynchronization.FrozenShards);
        Assert.Equal(Count + 1, world.GetOutgoingRelations<CommandLink>(source).Entries.Length);
        Assert.Equal(newTargets[0], world.GetDirectedRelationEndpoints(edges[0]).Target);
        _ = extra.Resolve();

        RelationAdjacencyBatchDiagnostics beforeMaintenance = afterSynchronization;
        world.MaintainRelations<CommandLink>();
        RelationAdjacencyBatchDiagnostics afterMaintenance =
            world.RelationGraph.StateAdjacencyBatchDiagnostics<CommandLink>();
        Assert.Equal(
            Count * 2 + 1,
            afterMaintenance.SourceEntryCopies - beforeMaintenance.SourceEntryCopies);
        Assert.Equal(
            Count * 2 + 1,
            afterMaintenance.FrozenEntries - beforeMaintenance.FrozenEntries);
        Assert.Equal(
            Count * 2 + 1,
            afterMaintenance.FrozenShards - beforeMaintenance.FrozenShards);
        Assert.Equal(edges[0], Assert.Single(
            world.GetIncomingRelations<CommandLink>(newTargets[0]).Entries.ToArray()).Edge);
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
            sources[i] = world.CreateEntity();
            originalTargets[i] = world.CreateEntity();
            replacementTargets[i] = world.CreateEntity();
        }

        var pending = new DeferredRelationEdge<CommandLink>[Count];
        long beforeCreate = world.RelationGraph.StateFullCloneCount<CommandLink>();
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
            {
                pending[i] = relations.Create(
                    sources[i],
                    originalTargets[i],
                    new CommandLink { Value = i });
            }
            commands.Playback();
        }
        Assert.Equal(1, world.RelationGraph.StateFullCloneCount<CommandLink>() - beforeCreate);

        var edges = new RelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        long beforeRetarget = world.RelationGraph.StateFullCloneCount<CommandLink>();
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
            {
                relations.Retarget(
                    edges[i],
                    sources[i],
                    replacementTargets[i],
                    RelationMaintenanceTiming.Immediate);
            }
            commands.Playback();
        }
        Assert.Equal(1, world.RelationGraph.StateFullCloneCount<CommandLink>() - beforeRetarget);

        for (int i = 0; i < Count; i++)
        {
            Assert.Equal(
                replacementTargets[i],
                world.GetDirectedRelationEndpoints(edges[i]).Target);
        }

        long beforeDestroy = world.RelationGraph.StateFullCloneCount<CommandLink>();
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
                relations.Destroy(edges[i]);
            commands.Playback();
        }
        Assert.Equal(1, world.RelationGraph.StateFullCloneCount<CommandLink>() - beforeDestroy);
        for (int i = 0; i < Count; i++)
            Assert.False(world.IsAlive(edges[i].Entity));
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
            sources[i] = world.CreateEntity();
            originalTargets[i] = world.CreateEntity();
            finalTargets[i] = world.CreateEntity();
            createSources[i] = world.CreateEntity();
            createTargets[i] = world.CreateEntity();
            edges[i] = world.CreateRelation(sources[i], originalTargets[i], new CommandLink());
        }

        long before = world.RelationGraph.StateFullCloneCount<CommandLink>();
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        for (int i = 0; i < Count; i++)
        {
            relations.Retarget(
                edges[i],
                sources[i],
                finalTargets[i],
                RelationMaintenanceTiming.Deferred);
        }
        for (int i = 0; i < Count; i++)
        {
            relations.Retarget(
                edges[i],
                sources[i],
                finalTargets[i],
                RelationMaintenanceTiming.Immediate);
            _ = relations.Create(
                createSources[i],
                createTargets[i],
                new CommandLink(),
                RelationMaintenanceTiming.Immediate);
        }

        commands.Playback();

        Assert.Equal(1, world.RelationGraph.StateFullCloneCount<CommandLink>() - before);
        for (int i = 0; i < Count; i++)
            Assert.Equal(finalTargets[i], world.GetDirectedRelationEndpoints(edges[i]).Target);
    }

    [Fact]
    public void DirtyProjectionSynchronization_ScansOnceAcrossUnrelatedImmediateCommands()
    {
        const int Count = 4096;
        var world = new World();
        Entity dirtySource = world.CreateEntity();
        Entity immediateSource = world.CreateEntity();
        var oldTargets = new Entity[Count];
        var newTargets = new Entity[Count];
        var immediateTargets = new Entity[Count];
        for (int i = 0; i < Count; i++)
        {
            oldTargets[i] = world.CreateEntity();
            newTargets[i] = world.CreateEntity();
            immediateTargets[i] = world.CreateEntity();
        }

        var pending = new DeferredRelationEdge<CommandLink>[Count];
        using (var seed = new CommandBuffer(world))
        {
            var relations = seed.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
            {
                pending[i] = relations.Create(
                    dirtySource,
                    oldTargets[i],
                    new CommandLink(),
                    RelationMaintenanceTiming.Immediate);
            }
            seed.Playback();
        }

        var edges = new RelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        RelationCommandBatchValidationDiagnostics before =
            world.RelationGraph.StateCommandBatchValidationDiagnostics<CommandLink>();
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
            {
                relations.Retarget(
                    edges[i],
                    dirtySource,
                    newTargets[i],
                    RelationMaintenanceTiming.Deferred);
            }
            for (int i = 0; i < Count; i++)
            {
                _ = relations.Create(
                    immediateSource,
                    immediateTargets[i],
                    new CommandLink(),
                    RelationMaintenanceTiming.Immediate);
            }
            commands.Playback();
        }

        RelationCommandBatchValidationDiagnostics after =
            world.RelationGraph.StateCommandBatchValidationDiagnostics<CommandLink>();
        Assert.Equal(1, after.FullScanCount - before.FullScanCount);
        Assert.Equal(Count, after.TransitionVisitCount - before.TransitionVisitCount);
    }

    [Fact]
    public void ImmediateCommandsThatConsumeAllDeferredWrites_RequireNoFinalImageScan()
    {
        const int Count = 1024;
        var world = new World();
        Entity source = world.CreateEntity();
        var oldTargets = new Entity[Count];
        var newTargets = new Entity[Count];
        for (int i = 0; i < Count; i++)
        {
            oldTargets[i] = world.CreateEntity();
            newTargets[i] = world.CreateEntity();
        }

        var pending = new DeferredRelationEdge<CommandLink>[Count];
        using (var seed = new CommandBuffer(world))
        {
            var relations = seed.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
            {
                pending[i] = relations.Create(
                    source,
                    oldTargets[i],
                    new CommandLink(),
                    RelationMaintenanceTiming.Immediate);
            }
            seed.Playback();
        }

        var edges = new RelationEdge<CommandLink>[Count];
        for (int i = 0; i < Count; i++)
            edges[i] = pending[i].Resolve();

        RelationCommandBatchValidationDiagnostics before =
            world.RelationGraph.StateCommandBatchValidationDiagnostics<CommandLink>();
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
            {
                relations.Retarget(
                    edges[i],
                    source,
                    newTargets[i],
                    RelationMaintenanceTiming.Deferred);
            }
            for (int i = 0; i < Count; i++)
            {
                relations.Retarget(
                    edges[i],
                    source,
                    newTargets[i],
                    RelationMaintenanceTiming.Immediate);
            }
            commands.Playback();
        }

        RelationCommandBatchValidationDiagnostics after =
            world.RelationGraph.StateCommandBatchValidationDiagnostics<CommandLink>();
        Assert.Equal(0, after.FullScanCount - before.FullScanCount);
        Assert.Equal(0, after.TransitionVisitCount - before.TransitionVisitCount);
        for (int i = 0; i < Count; i++)
            Assert.Equal(newTargets[i], world.GetDirectedRelationEndpoints(edges[i]).Target);
    }

    [Fact]
    public void CancelDeferredUniqueTarget_AllowsFollowingCreateToReuseProjectedTarget()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity newSource = world.CreateEntity();
        Entity appliedTarget = world.CreateEntity();
        Entity projectedTarget = world.CreateEntity();
        RelationEdge<CommandUniqueTarget> edge = world.CreateRelation(
            source,
            appliedTarget,
            new CommandUniqueTarget());

        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandUniqueTarget>();
        relations.Retarget(edge, source, projectedTarget, RelationMaintenanceTiming.Deferred);
        relations.Retarget(edge, source, appliedTarget, RelationMaintenanceTiming.Immediate);
        DeferredRelationEdge<CommandUniqueTarget> reused = relations.Create(
            newSource,
            projectedTarget,
            new CommandUniqueTarget(),
            RelationMaintenanceTiming.Immediate);

        commands.Playback();

        Assert.Equal(appliedTarget, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Equal(reused.Resolve(), Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(projectedTarget).Entries.ToArray()).Edge);
    }

    [Fact]
    public void DestroyDeferredUniqueTarget_AllowsFollowingCreateToReuseProjectedTarget()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity newSource = world.CreateEntity();
        Entity appliedTarget = world.CreateEntity();
        Entity projectedTarget = world.CreateEntity();
        RelationEdge<CommandUniqueTarget> edge = world.CreateRelation(
            source,
            appliedTarget,
            new CommandUniqueTarget());

        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandUniqueTarget>();
        relations.Retarget(edge, source, projectedTarget, RelationMaintenanceTiming.Deferred);
        relations.Destroy(edge);
        DeferredRelationEdge<CommandUniqueTarget> reused = relations.Create(
            newSource,
            projectedTarget,
            new CommandUniqueTarget(),
            RelationMaintenanceTiming.Immediate);

        commands.Playback();

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Equal(reused.Resolve(), Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(projectedTarget).Entries.ToArray()).Edge);
    }

    [Fact]
    public void DirtyProjection_MirrorsOrderPolicyAndReorderBeforePlacedCreate()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity firstTarget = world.CreateEntity();
        Entity secondTarget = world.CreateEntity();
        Entity deferredTarget = world.CreateEntity();
        Entity createdTarget = world.CreateEntity();
        RelationEdge<CommandLink> first = world.CreateRelation(source, firstTarget, new CommandLink());
        RelationEdge<CommandLink> second = world.CreateRelation(source, secondTarget, new CommandLink());

        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        relations.Retarget(first, source, deferredTarget, RelationMaintenanceTiming.Deferred);
        relations.SetAdjacencyOrder(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        relations.Reorder(source, RelationAdjacencyRole.Outgoing, second, insertIndex: 0);
        DeferredRelationEdge<CommandLink> created = relations.Create(
            source,
            createdTarget,
            new CommandLink(),
            new DirectedRelationPlacement(OutgoingIndex: 1),
            RelationMaintenanceTiming.Immediate);

        commands.Playback();

        Assert.Equal(
            new[] { second, created.Resolve(), first },
            world.GetOrderedOutgoingRelations<CommandLink>(source)
                .Entries.ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void HierarchyImmediatePlayback_ResolvesDeferredEntitiesAndUpdatesChildren()
    {
        var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity parent = commands.CreateEntity();
        DeferredEntity child = commands.CreateEntity();

        commands.Hierarchy<SceneDomain>().SetParent(
            child,
            parent,
            HierarchyMaintenanceTiming.Immediate);

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
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, liveParent).ToArray());
    }

    [Fact]
    public void HierarchyDeferredPlayback_ChangesParentButLeavesLastAppliedChildren()
    {
        var world = new World();
        Entity oldParent = world.CreateEntity();
        Entity newParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetParent(world, child, oldParent);

        using var commands = new CommandBuffer(world);
        commands.Hierarchy<SceneDomain>().SetParent(
            child,
            newParent,
            HierarchyMaintenanceTiming.Deferred);
        commands.Playback();

        Assert.Equal(newParent, SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(
            new[] { child },
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, oldParent).ToArray());
        Assert.Empty(SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, newParent));

        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.Maintain(world);
        Assert.Empty(SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, oldParent));
        Assert.Equal(
            new[] { child },
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, newParent).ToArray());
    }

    [Fact]
    public void LaterInvalidHierarchyCommand_LeavesImmediateMutationAndGenerationUnpublished()
    {
        var world = new World();
        Entity oldParent = world.CreateEntity();
        Entity newParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetParent(world, child, oldParent);
        var oldChildren = SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, oldParent);
        var newChildren = SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, newParent);

        using var commands = new CommandBuffer(world);
        var hierarchy = commands.Hierarchy<SceneDomain>();
        hierarchy.SetParent(child, newParent, HierarchyMaintenanceTiming.Immediate);
        hierarchy.SetParent(newParent, newParent, HierarchyMaintenanceTiming.Immediate);

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        Assert.Equal(oldParent, SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetParent(world, child));
        var oldAfter = SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, oldParent);
        var newAfter = SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, newParent);
        Assert.Equal(oldChildren.Generation, oldAfter.Generation);
        Assert.Equal(newChildren.Generation, newAfter.Generation);
        Assert.Equal(new[] { child }, oldAfter.ToArray());
        Assert.Empty(newAfter);
    }

    [Fact]
    public void HierarchyOrderCommandsAndDestroySubtree_CallTypedKernel()
    {
        var world = new World();
        Entity root = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();

        using (var commands = new CommandBuffer(world))
        {
            var hierarchy = commands.Hierarchy<SceneDomain>();
            hierarchy.SetOrderPolicy(root, ChildOrderPolicy.Ordered);
            hierarchy.SetParent(first, root);
            hierarchy.SetParent(second, root, insertIndex: 0);
            hierarchy.Reorder(first, insertIndex: 0);
            commands.Playback();
        }

        Assert.Equal(
            new[] { first, second },
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, root).ToArray());

        using (var commands = new CommandBuffer(world))
        {
            commands.Hierarchy<SceneDomain>().DestroySubtree(root);
            commands.Playback();
        }

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(first));
        Assert.False(world.IsAlive(second));
    }

    [Fact]
    public void RelationCreate_ReturnsDeferredHandleAndUsesDeferredEndpointMapping()
    {
        var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity source = commands.CreateEntity();
        DeferredEntity originalTarget = commands.CreateEntity();
        DeferredEntity finalTarget = commands.CreateEntity();
        var relations = commands.Relations<CommandLink>();

        DeferredRelationEdge<CommandLink> pending = relations.Create(
            source,
            originalTarget,
            new CommandLink { Value = 42 });
        relations.Retarget(
            pending,
            source,
            finalTarget,
            RelationMaintenanceTiming.Immediate);

        Assert.False(pending.IsResolved);
        Assert.Throws<InvalidOperationException>(() => pending.Resolve());

        commands.Playback();

        RelationEdge<CommandLink> edge = pending.Resolve();
        Entity liveSource = source.Resolve();
        Entity liveFinalTarget = finalTarget.Resolve();
        Assert.True(pending.IsResolved);
        Assert.True(world.IsAlive(edge.Entity));
        Assert.Equal(42, world.Read<CommandLink>(edge.Entity).Value);
        var endpoints = world.GetDirectedRelationEndpoints(edge);
        Assert.Equal(liveSource, endpoints.Source);
        Assert.Equal(liveFinalTarget, endpoints.Target);
        Assert.Throws<InvalidOperationException>(() => commands.Playback());
    }

    [Fact]
    public void ClearingUnplayedCreate_InvalidatesDeferredEdgeAndEndpointsWithoutWorldMutation()
    {
        var world = new World();
        using var commands = new CommandBuffer(world);
        DeferredEntity source = commands.CreateEntity();
        DeferredEntity target = commands.CreateEntity();
        DeferredRelationEdge<CommandLink> pending = commands.Relations<CommandLink>().Create(
            source,
            target,
            new CommandLink { Value = 1 });

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
        Entity liveParent = world.CreateEntity();
        Entity liveChild = world.CreateEntity();
        Entity liveTarget = world.CreateEntity();
        using var commands = new CommandBuffer(world);
        DeferredEntity deferredParent = commands.CreateEntity();
        DeferredEntity deferredChild = commands.CreateEntity();

        var hierarchy = commands.Hierarchy<SceneDomain>();
        hierarchy.SetParent(deferredChild, liveParent);
        hierarchy.SetParent(liveChild, deferredParent);

        var relations = commands.Relations<CommandLink>();
        DeferredRelationEdge<CommandLink> edge = relations.Create(
            deferredChild,
            liveTarget,
            new CommandLink { Value = 9 });
        relations.Retarget(edge, liveParent, deferredParent);

        commands.Playback();

        Entity resolvedParent = deferredParent.Resolve();
        Entity resolvedChild = deferredChild.Resolve();
        Assert.Equal(
            liveParent,
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetParent(world, resolvedChild));
        Assert.Equal(
            resolvedParent,
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetParent(world, liveChild));
        var endpoints = world.GetDirectedRelationEndpoints(edge.Resolve());
        Assert.Equal(liveParent, endpoints.Source);
        Assert.Equal(resolvedParent, endpoints.Target);
    }

    [Fact]
    public void RelationDeferredRetarget_PlaybackLeavesAppliedAdjacencyUntilMaintain()
    {
        var world = new World();
        Entity oldSource = world.CreateEntity();
        Entity newSource = world.CreateEntity();
        Entity target = world.CreateEntity();
        RelationEdge<CommandLink> edge = world.CreateRelation(
            oldSource,
            target,
            new CommandLink { Value = 3 });

        using var commands = new CommandBuffer(world);
        commands.Relations<CommandLink>().Retarget(
            edge,
            newSource,
            target,
            RelationMaintenanceTiming.Deferred);
        commands.Playback();

        Assert.Equal(newSource, world.GetDirectedRelationEndpoints(edge).Source);
        Assert.Equal(edge, Assert.Single(world.GetOutgoingRelations<CommandLink>(oldSource).Entries.ToArray()).Edge);
        Assert.Empty(world.GetOutgoingRelations<CommandLink>(newSource).Entries.ToArray());

        world.MaintainRelations<CommandLink>();

        Assert.Empty(world.GetOutgoingRelations<CommandLink>(oldSource).Entries.ToArray());
        Assert.Equal(edge, Assert.Single(world.GetOutgoingRelations<CommandLink>(newSource).Entries.ToArray()).Edge);
    }

    [Fact]
    public void RelationOrderReorderDestroyAndBulkDestroy_UseEdgeIdentity()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
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
            var relations = commands.Relations<CommandLink>();
            relations.SetAdjacencyOrder(
                source,
                RelationAdjacencyRole.Outgoing,
                RelationAdjacencyOrderPolicy.Ordered);
            relations.Reorder(source, RelationAdjacencyRole.Outgoing, second, insertIndex: 0);
            relations.Destroy(first);
            commands.Playback();
        }

        var ordered = world.GetOrderedOutgoingRelations<CommandLink>(source);
        Assert.Equal(second, Assert.Single(ordered.Entries.ToArray()).Edge);
        Assert.False(world.IsAlive(first.Entity));

        using (var commands = new CommandBuffer(world))
        {
            commands.Relations<CommandLink>().DestroyAllBetween(source, target);
            commands.Playback();
        }

        Assert.False(world.IsAlive(second.Entity));
        Assert.Empty(world.GetRelationEdgesBetween<CommandLink>(source, target).ToArray());
    }

    [Fact]
    public void CreatedDeferredEdge_CanBeDestroyedLaterInSamePlayback()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        DeferredRelationEdge<CommandLink> pending = relations.Create(
            source,
            target,
            new CommandLink { Value = 9 });
        relations.Destroy(pending);

        commands.Playback();

        RelationEdge<CommandLink> identity = pending.Resolve();
        Assert.False(world.IsAlive(identity.Entity));
        Assert.Empty(world.GetRelationEdgesBetween<CommandLink>(source, target).ToArray());
    }

    [Fact]
    public void DeferredRetargetThenDestroyInSamePlayback_DoesNotValidateDeadPreimage()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity oldTarget = world.CreateEntity();
        Entity newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new CommandLink());
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        relations.Retarget(edge, source, newTarget, RelationMaintenanceTiming.Deferred);
        relations.Destroy(edge);

        commands.Playback();

        Assert.False(world.IsAlive(edge.Entity));
        Assert.Empty(world.GetIncomingRelations<CommandLink>(oldTarget).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<CommandLink>(newTarget).Entries.ToArray());
    }

    [Fact]
    public void LaterInvalidRelationCommand_RollsBackStagedEndpointsAndLeavesAdjacencyApplied()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity oldTarget = world.CreateEntity();
        Entity newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new CommandLink());
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        relations.Retarget(edge, source, newTarget, RelationMaintenanceTiming.Deferred);
        relations.SetAdjacencyOrder(
            source,
            RelationAdjacencyRole.Incident,
            RelationAdjacencyOrderPolicy.Ordered);

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        Assert.Equal(oldTarget, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<CommandLink>(oldTarget).Entries.ToArray()).Edge);
        Assert.Empty(world.GetIncomingRelations<CommandLink>(newTarget).Entries.ToArray());
        world.MaintainRelations<CommandLink>();
        Assert.Equal(oldTarget, world.GetDirectedRelationEndpoints(edge).Target);
    }

    [Fact]
    public void LaterInvalidRelationCommand_LeavesImmediateRetargetUnpublished()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity oldTarget = world.CreateEntity();
        Entity newTarget = world.CreateEntity();
        var edge = world.CreateRelation(source, oldTarget, new CommandLink());
        uint oldGeneration = world.GetIncomingRelations<CommandLink>(oldTarget).Generation;
        uint newGeneration = world.GetIncomingRelations<CommandLink>(newTarget).Generation;
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        relations.Retarget(edge, source, newTarget, RelationMaintenanceTiming.Immediate);
        relations.SetAdjacencyOrder(
            source,
            RelationAdjacencyRole.Incident,
            RelationAdjacencyOrderPolicy.Ordered);

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        Assert.Equal(oldTarget, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Equal(oldGeneration, world.GetIncomingRelations<CommandLink>(oldTarget).Generation);
        Assert.Equal(newGeneration, world.GetIncomingRelations<CommandLink>(newTarget).Generation);
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<CommandLink>(oldTarget).Entries.ToArray()).Edge);
        Assert.Empty(world.GetIncomingRelations<CommandLink>(newTarget).Entries.ToArray());
    }

    [Fact]
    public void FailedPlayback_InvalidatesCreatedDeferredRelationEdge()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        DeferredRelationEdge<CommandLink> pending = relations.Create(
            source,
            target,
            new CommandLink { Value = 7 },
            RelationMaintenanceTiming.Immediate);
        relations.SetAdjacencyOrder(
            source,
            RelationAdjacencyRole.Incident,
            RelationAdjacencyOrderPolicy.Ordered);

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        Assert.False(pending.IsResolved);
        Assert.False(pending.TryResolve(out _));
        Assert.Throws<InvalidOperationException>(() => pending.Resolve());
        Assert.Empty(world.GetRelationEdgesBetween<CommandLink>(source, target).ToArray());
    }

    [Fact]
    public void DeferredCreateAndPlacement_PreserveLastAppliedViewsUntilMaintenance()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        world.SetRelationAdjacencyOrder<CommandLink>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        RelationEdge<CommandLink> existing = world.CreateRelation(
            source,
            targetA,
            new CommandLink { Value = 1 });
        using var commands = new CommandBuffer(world);
        DeferredRelationEdge<CommandLink> pending = commands.Relations<CommandLink>().Create(
            source,
            targetB,
            new CommandLink { Value = 2 },
            new DirectedRelationPlacement(OutgoingIndex: 0),
            RelationMaintenanceTiming.Deferred);

        commands.Playback();

        RelationEdge<CommandLink> created = pending.Resolve();
        Assert.Equal(
            new[] { existing },
            world.GetOrderedOutgoingRelations<CommandLink>(source)
                .Entries.ToArray().Select(static entry => entry.Edge));

        world.MaintainRelations<CommandLink>();

        Assert.Equal(
            new[] { created, existing },
            world.GetOrderedOutgoingRelations<CommandLink>(source)
                .Entries.ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void DeferredHierarchyPlacement_AppliesAtMaintenanceInRecordedOrder()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetChildOrderPolicy(
            world,
            parent,
            ChildOrderPolicy.Ordered);
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetParent(world, first, parent);
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.SetParent(world, second, parent);
        using var commands = new CommandBuffer(world);

        commands.Hierarchy<SceneDomain>().SetParent(
            first,
            parent,
            insertIndex: 1,
            HierarchyMaintenanceTiming.Deferred);
        commands.Playback();

        Assert.Equal(
            new[] { first, second },
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, parent).ToArray());
        SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.Maintain(world);
        Assert.Equal(
            new[] { second, first },
            SomeEngine.ECS.Hierarchy.Hierarchy<SceneDomain>.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void DeferredUniqueTargetSwap_ValidatesOneFinalCommandImage()
    {
        var world = new World();
        Entity sourceA = world.CreateEntity();
        Entity sourceB = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        var edgeA = world.CreateRelation(sourceA, targetA, new CommandUniqueTarget());
        var edgeB = world.CreateRelation(sourceB, targetB, new CommandUniqueTarget());
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandUniqueTarget>();

        relations.Retarget(
            edgeA,
            sourceA,
            targetB,
            RelationMaintenanceTiming.Deferred);
        relations.SetAdjacencyOrder(
            sourceA,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Unordered);
        relations.Retarget(
            edgeB,
            sourceB,
            targetA,
            RelationMaintenanceTiming.Deferred);
        commands.Playback();

        Assert.Equal(targetB, world.GetDirectedRelationEndpoints(edgeA).Target);
        Assert.Equal(targetA, world.GetDirectedRelationEndpoints(edgeB).Target);
        world.MaintainRelations<CommandUniqueTarget>();
        Assert.Equal(edgeB, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(targetA).Entries.ToArray()).Edge);
        Assert.Equal(edgeA, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(targetB).Entries.ToArray()).Edge);
    }

    [Fact]
    public void UnrelatedImmediateCommand_DoesNotValidateHalfOfDeferredUniqueTargetSwap()
    {
        var world = new World();
        Entity sourceA = world.CreateEntity();
        Entity sourceB = world.CreateEntity();
        Entity unrelatedSource = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        Entity unrelatedTarget = world.CreateEntity();
        var edgeA = world.CreateRelation(sourceA, targetA, new CommandUniqueTarget());
        var edgeB = world.CreateRelation(sourceB, targetB, new CommandUniqueTarget());

        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandUniqueTarget>();
        relations.Retarget(
            edgeA,
            sourceA,
            targetB,
            RelationMaintenanceTiming.Deferred);
        DeferredRelationEdge<CommandUniqueTarget> unrelated = relations.Create(
            unrelatedSource,
            unrelatedTarget,
            new CommandUniqueTarget(),
            RelationMaintenanceTiming.Immediate);
        relations.Retarget(
            edgeB,
            sourceB,
            targetA,
            RelationMaintenanceTiming.Deferred);

        commands.Playback();

        // Canonical endpoints already expose the complete deferred result, while the inverse
        // views deliberately retain the last-applied image until explicit maintenance.
        Assert.Equal(targetB, world.GetDirectedRelationEndpoints(edgeA).Target);
        Assert.Equal(targetA, world.GetDirectedRelationEndpoints(edgeB).Target);
        Assert.Equal(edgeA, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(targetA).Entries.ToArray()).Edge);
        Assert.Equal(edgeB, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(targetB).Entries.ToArray()).Edge);
        Assert.Equal(unrelated.Resolve(), Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(unrelatedTarget).Entries.ToArray()).Edge);

        world.MaintainRelations<CommandUniqueTarget>();

        Assert.Equal(edgeB, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(targetA).Entries.ToArray()).Edge);
        Assert.Equal(edgeA, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(targetB).Entries.ToArray()).Edge);
    }

    [Fact]
    public void DeferredRetarget_DoesNotFreeAppliedUniqueTargetForImmediateCreate()
    {
        var world = new World();
        Entity existingSource = world.CreateEntity();
        Entity newSource = world.CreateEntity();
        Entity appliedTarget = world.CreateEntity();
        Entity deferredTarget = world.CreateEntity();
        var existing = world.CreateRelation(
            existingSource,
            appliedTarget,
            new CommandUniqueTarget());

        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandUniqueTarget>();
        relations.Retarget(
            existing,
            existingSource,
            deferredTarget,
            RelationMaintenanceTiming.Deferred);
        DeferredRelationEdge<CommandUniqueTarget> conflicting = relations.Create(
            newSource,
            appliedTarget,
            new CommandUniqueTarget(),
            RelationMaintenanceTiming.Immediate);

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        // Playback is atomic, and deferred timing never makes the old applied key available to
        // a later immediate command. The source World therefore retains its original image.
        Assert.False(conflicting.TryResolve(out _));
        Assert.Equal(appliedTarget, world.GetDirectedRelationEndpoints(existing).Target);
        Assert.Equal(existing, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(appliedTarget).Entries.ToArray()).Edge);
        Assert.Empty(world.GetIncomingRelations<CommandUniqueTarget>(deferredTarget).Entries.ToArray());
    }

    [Fact]
    public void AlternatingDeferredAndImmediateCommands_ValidateDirtyImageOnceAtBatchEnd()
    {
        const int Count = 256;
        var world = new World();
        Entity dirtySource = world.CreateEntity();
        Entity immediateSource = world.CreateEntity();
        var edges = new RelationEdge<CommandLink>[Count];
        var deferredTargets = new Entity[Count];
        var immediateTargets = new Entity[Count];
        for (int i = 0; i < Count; i++)
        {
            Entity originalTarget = world.CreateEntity();
            deferredTargets[i] = world.CreateEntity();
            immediateTargets[i] = world.CreateEntity();
            edges[i] = world.CreateRelation(
                dirtySource,
                originalTarget,
                new CommandLink { Value = i });
        }

        RelationCommandBatchValidationDiagnostics before =
            world.RelationGraph.StateCommandBatchValidationDiagnostics<CommandLink>();
        using (var commands = new CommandBuffer(world))
        {
            var relations = commands.Relations<CommandLink>();
            for (int i = 0; i < Count; i++)
            {
                relations.Retarget(
                    edges[i],
                    dirtySource,
                    deferredTargets[i],
                    RelationMaintenanceTiming.Deferred);
                _ = relations.Create(
                    immediateSource,
                    immediateTargets[i],
                    new CommandLink(),
                    RelationMaintenanceTiming.Immediate);
            }
            commands.Playback();
        }

        RelationCommandBatchValidationDiagnostics after =
            world.RelationGraph.StateCommandBatchValidationDiagnostics<CommandLink>();
        Assert.Equal(1, after.FullScanCount - before.FullScanCount);
        Assert.Equal(Count, after.TransitionVisitCount - before.TransitionVisitCount);
    }

    [Fact]
    public void DeferredUniqueTargetSwap_RemainsAtomicAfterProjectionWasInitialized()
    {
        var world = new World();
        Entity sourceA = world.CreateEntity();
        Entity sourceB = world.CreateEntity();
        Entity dirtySource = world.CreateEntity();
        Entity immediateSource = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        Entity dirtyOldTarget = world.CreateEntity();
        Entity dirtyNewTarget = world.CreateEntity();
        Entity immediateTarget = world.CreateEntity();
        var edgeA = world.CreateRelation(sourceA, targetA, new CommandUniqueTarget());
        var edgeB = world.CreateRelation(sourceB, targetB, new CommandUniqueTarget());
        var dirtyEdge = world.CreateRelation(
            dirtySource,
            dirtyOldTarget,
            new CommandUniqueTarget());

        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandUniqueTarget>();
        relations.Retarget(
            dirtyEdge,
            dirtySource,
            dirtyNewTarget,
            RelationMaintenanceTiming.Deferred);
        DeferredRelationEdge<CommandUniqueTarget> immediate = relations.Create(
            immediateSource,
            immediateTarget,
            new CommandUniqueTarget(),
            RelationMaintenanceTiming.Immediate);
        relations.Retarget(edgeA, sourceA, targetB, RelationMaintenanceTiming.Deferred);
        relations.Retarget(edgeB, sourceB, targetA, RelationMaintenanceTiming.Deferred);

        commands.Playback();
        world.MaintainRelations<CommandUniqueTarget>();

        Assert.Equal(edgeB, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(targetA).Entries.ToArray()).Edge);
        Assert.Equal(edgeA, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(targetB).Entries.ToArray()).Edge);
        Assert.Equal(dirtyEdge, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(dirtyNewTarget).Entries.ToArray()).Edge);
        Assert.Equal(immediate.Resolve(), Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(immediateTarget).Entries.ToArray()).Edge);
    }

    [Fact]
    public void PreexistingDeferredCanonicalTarget_RejectsConflictingImmediateCreate()
    {
        var world = new World();
        Entity existingSource = world.CreateEntity();
        Entity newSource = world.CreateEntity();
        Entity oldTarget = world.CreateEntity();
        Entity pendingTarget = world.CreateEntity();
        var existing = world.CreateRelation(
            existingSource,
            oldTarget,
            new CommandUniqueTarget());
        world.RetargetRelationDeferred(existing, existingSource, pendingTarget);

        using var commands = new CommandBuffer(world);
        DeferredRelationEdge<CommandUniqueTarget> conflicting =
            commands.Relations<CommandUniqueTarget>().Create(
                newSource,
                pendingTarget,
                new CommandUniqueTarget(),
                RelationMaintenanceTiming.Immediate);

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        Assert.False(conflicting.TryResolve(out _));
        Assert.Equal(pendingTarget, world.GetDirectedRelationEndpoints(existing).Target);
        world.MaintainRelations<CommandUniqueTarget>();
        Assert.Equal(existing, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(pendingTarget).Entries.ToArray()).Edge);
    }

    [Fact]
    public void PreexistingDeferredCanonicalTarget_RejectsConflictingImmediateRetarget()
    {
        var world = new World();
        Entity sourceA = world.CreateEntity();
        Entity sourceB = world.CreateEntity();
        Entity oldTargetA = world.CreateEntity();
        Entity pendingTarget = world.CreateEntity();
        Entity oldTargetB = world.CreateEntity();
        var pending = world.CreateRelation(sourceA, oldTargetA, new CommandUniqueTarget());
        var immediate = world.CreateRelation(sourceB, oldTargetB, new CommandUniqueTarget());
        world.RetargetRelationDeferred(pending, sourceA, pendingTarget);

        using var commands = new CommandBuffer(world);
        commands.Relations<CommandUniqueTarget>().Retarget(
            immediate,
            sourceB,
            pendingTarget,
            RelationMaintenanceTiming.Immediate);

        Assert.Throws<InvalidOperationException>(() => commands.Playback());

        Assert.Equal(oldTargetB, world.GetDirectedRelationEndpoints(immediate).Target);
        Assert.Equal(immediate, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(oldTargetB).Entries.ToArray()).Edge);
        world.MaintainRelations<CommandUniqueTarget>();
        Assert.Equal(pending, Assert.Single(
            world.GetIncomingRelations<CommandUniqueTarget>(pendingTarget).Entries.ToArray()).Edge);
    }

    [Fact]
    public void DeferredPlacementThenImmediateRetarget_ConsumesPendingPlacement()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        world.SetRelationAdjacencyOrder<CommandLink>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var first = world.CreateRelation(source, targetA, new CommandLink());
        var second = world.CreateRelation(source, targetB, new CommandLink());

        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        relations.Retarget(
            second,
            source,
            targetB,
            new DirectedRelationPlacement(OutgoingIndex: 0),
            RelationMaintenanceTiming.Deferred);
        relations.Retarget(
            second,
            source,
            targetB,
            RelationMaintenanceTiming.Immediate);
        commands.Playback();

        Assert.Equal(
            new[] { first, second },
            world.GetOrderedOutgoingRelations<CommandLink>(source)
                .Entries.ToArray().Select(static entry => entry.Edge));
        world.MaintainRelations<CommandLink>();
        Assert.Equal(
            new[] { first, second },
            world.GetOrderedOutgoingRelations<CommandLink>(source)
                .Entries.ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void DeferredPlacementThenDestroy_CannotReappearAtMaintenance()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        world.SetRelationAdjacencyOrder<CommandLink>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        var surviving = world.CreateRelation(source, targetA, new CommandLink());
        var destroyed = world.CreateRelation(source, targetB, new CommandLink());

        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        relations.Retarget(
            destroyed,
            source,
            targetB,
            new DirectedRelationPlacement(OutgoingIndex: 0),
            RelationMaintenanceTiming.Deferred);
        relations.Destroy(destroyed);
        commands.Playback();

        Assert.False(world.IsAlive(destroyed.Entity));
        world.MaintainRelations<CommandLink>();
        Assert.Equal(surviving, Assert.Single(
            world.GetOrderedOutgoingRelations<CommandLink>(source).Entries.ToArray()).Edge);
    }

    [Fact]
    public void ImmediateAndDeferredTiming_RemainPerEdgeWithinOnePayloadType()
    {
        var world = new World();
        Entity deferredSource = world.CreateEntity();
        Entity deferredOld = world.CreateEntity();
        Entity deferredNew = world.CreateEntity();
        Entity immediateSource = world.CreateEntity();
        Entity immediateTarget = world.CreateEntity();
        var deferredEdge = world.CreateRelation(
            deferredSource,
            deferredOld,
            new CommandLink { Value = 1 });
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        relations.Retarget(
            deferredEdge,
            deferredSource,
            deferredNew,
            RelationMaintenanceTiming.Deferred);
        DeferredRelationEdge<CommandLink> immediateEdge = relations.Create(
            immediateSource,
            immediateTarget,
            new CommandLink { Value = 2 },
            RelationMaintenanceTiming.Immediate);

        commands.Playback();

        Assert.Equal(deferredNew, world.GetDirectedRelationEndpoints(deferredEdge).Target);
        Assert.Equal(deferredEdge, Assert.Single(
            world.GetIncomingRelations<CommandLink>(deferredOld).Entries.ToArray()).Edge);
        Assert.Empty(world.GetIncomingRelations<CommandLink>(deferredNew).Entries.ToArray());
        Assert.Equal(immediateEdge.Resolve(), Assert.Single(
            world.GetIncomingRelations<CommandLink>(immediateTarget).Entries.ToArray()).Edge);
    }

    [Fact]
    public void DeferredCreateThenImmediateRetarget_AppliesTheRegisteredEdge()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        DeferredRelationEdge<CommandLink> pending = relations.Create(
            source,
            target,
            new CommandLink(),
            RelationMaintenanceTiming.Deferred);
        relations.Retarget(
            pending,
            source,
            target,
            RelationMaintenanceTiming.Immediate);

        commands.Playback();

        RelationEdge<CommandLink> edge = pending.Resolve();
        Assert.Equal(edge, Assert.Single(
            world.GetOutgoingRelations<CommandLink>(source).Entries.ToArray()).Edge);
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<CommandLink>(target).Entries.ToArray()).Edge);
    }

    [Fact]
    public void ImmediateCreateThenDeferredRetarget_LeavesOriginalAppliedAdjacency()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity original = world.CreateEntity();
        Entity final = world.CreateEntity();
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        DeferredRelationEdge<CommandLink> pending = relations.Create(
            source,
            original,
            new CommandLink(),
            RelationMaintenanceTiming.Immediate);
        relations.Retarget(
            pending,
            source,
            final,
            RelationMaintenanceTiming.Deferred);

        commands.Playback();

        var edge = pending.Resolve();
        Assert.Equal(final, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<CommandLink>(original).Entries.ToArray()).Edge);
        Assert.Empty(world.GetIncomingRelations<CommandLink>(final).Entries.ToArray());
        world.MaintainRelations<CommandLink>();
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<CommandLink>(final).Entries.ToArray()).Edge);
    }

    [Fact]
    public void ImmediateRetargetThenDeferredRetarget_LeavesIntermediateAppliedAdjacency()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity intermediate = world.CreateEntity();
        Entity final = world.CreateEntity();
        var edge = world.CreateRelation(source, first, new CommandLink());
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        relations.Retarget(edge, source, intermediate, RelationMaintenanceTiming.Immediate);
        relations.Retarget(edge, source, final, RelationMaintenanceTiming.Deferred);

        commands.Playback();

        Assert.Equal(final, world.GetDirectedRelationEndpoints(edge).Target);
        Assert.Empty(world.GetIncomingRelations<CommandLink>(first).Entries.ToArray());
        Assert.Equal(edge, Assert.Single(
            world.GetIncomingRelations<CommandLink>(intermediate).Entries.ToArray()).Edge);
        Assert.Empty(world.GetIncomingRelations<CommandLink>(final).Entries.ToArray());
    }

    [Fact]
    public void CreateThenOrderAndReorder_UsesTheNewAppliedEdgeInSamePlayback()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        var existing = world.CreateRelation(source, targetA, new CommandLink());
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        var created = relations.Create(
            source,
            targetB,
            new CommandLink(),
            RelationMaintenanceTiming.Immediate);
        relations.SetAdjacencyOrder(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        relations.Reorder(
            source,
            RelationAdjacencyRole.Outgoing,
            created,
            insertIndex: 0);

        commands.Playback();

        Assert.Equal(
            new[] { created.Resolve(), existing },
            world.GetOrderedOutgoingRelations<CommandLink>(source)
                .Entries.ToArray().Select(static entry => entry.Edge));
    }

    [Fact]
    public void BulkDestroyUsesCurrentCommandImageForCreatedAndRetargetedEdges()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity oldTarget = world.CreateEntity();
        Entity newTarget = world.CreateEntity();
        var moved = world.CreateRelation(source, oldTarget, new CommandLink());
        using var commands = new CommandBuffer(world);
        var relations = commands.Relations<CommandLink>();
        var created = relations.Create(
            source,
            newTarget,
            new CommandLink(),
            RelationMaintenanceTiming.Deferred);
        relations.DestroyAllBetween(source, newTarget);
        relations.Retarget(moved, source, newTarget, RelationMaintenanceTiming.Immediate);
        relations.DestroyAllBetween(source, oldTarget);

        commands.Playback();

        Assert.False(world.IsAlive(created.Resolve().Entity));
        Assert.True(world.IsAlive(moved.Entity));
        Assert.Equal(newTarget, world.GetDirectedRelationEndpoints(moved).Target);
    }

    [Fact]
    public void OrderedEmptyEndpointDestroyedInSameBatch_DropsOverlayBeforeIndexReuse()
    {
        var world = new World();
        Entity endpoint = world.CreateEntity();

        using (var commands = new CommandBuffer(world))
        {
            commands.Relations<CommandLink>().SetAdjacencyOrder(
                endpoint,
                RelationAdjacencyRole.Outgoing,
                RelationAdjacencyOrderPolicy.Ordered);
            commands.DestroyEntity(endpoint);
            commands.Playback();
        }

        Assert.False(world.IsAlive(endpoint));
        Entity reused = world.CreateEntity();
        Assert.Equal(new Entity(endpoint.Index, endpoint.Generation + 1), reused);
        Assert.False(world.RelationGraph.HasEndpointState<CommandLink>(reused));

        Entity target = world.CreateEntity();
        RelationEdge<CommandLink> edge = world.CreateRelation(
            reused,
            target,
            new CommandLink());
        Assert.Equal(edge, Assert.Single(
            world.GetOutgoingRelations<CommandLink>(reused).Entries.ToArray()).Edge);
    }

    [Fact]
    public void ImmediateVersusDeferredRetargetControlsFollowingEndpointDestroy()
    {
        var immediateWorld = new World();
        Entity immediateSource = immediateWorld.CreateEntity();
        Entity immediateOld = immediateWorld.CreateEntity();
        Entity immediateNew = immediateWorld.CreateEntity();
        var immediateEdge = immediateWorld.CreateRelation(
            immediateSource,
            immediateOld,
            new CommandLink());
        using (var commands = new CommandBuffer(immediateWorld))
        {
            commands.Relations<CommandLink>().Retarget(
                immediateEdge,
                immediateSource,
                immediateNew,
                RelationMaintenanceTiming.Immediate);
            commands.DestroyEntity(immediateOld);
            commands.Playback();
        }
        Assert.True(immediateWorld.IsAlive(immediateEdge.Entity));

        var deferredWorld = new World();
        Entity deferredSource = deferredWorld.CreateEntity();
        Entity deferredOld = deferredWorld.CreateEntity();
        Entity deferredNew = deferredWorld.CreateEntity();
        var deferredEdge = deferredWorld.CreateRelation(
            deferredSource,
            deferredOld,
            new CommandLink());
        using (var commands = new CommandBuffer(deferredWorld))
        {
            commands.Relations<CommandLink>().Retarget(
                deferredEdge,
                deferredSource,
                deferredNew,
                RelationMaintenanceTiming.Deferred);
            commands.DestroyEntity(deferredOld);
            commands.Playback();
        }
        Assert.False(deferredWorld.IsAlive(deferredEdge.Entity));
    }

    [Fact]
    public void InvalidMaintenanceTiming_IsRejectedBeforeRecording()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
        using var commands = new CommandBuffer(world);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => commands.Relations<CommandLink>().Create(
                source,
                target,
                new CommandLink(),
                (RelationMaintenanceTiming)byte.MaxValue));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => commands.Hierarchy<SceneDomain>().SetParent(
                source,
                target,
                (HierarchyMaintenanceTiming)byte.MaxValue));
        Assert.Equal(0, commands.CommandCount);
    }
}
