using System.Collections.Concurrent;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Registry;
using DefaultHierarchy = SomeEngine.ECS.Hierarchy.Hierarchy;

namespace SomeEngine.ECS.Tests;

public class HierarchyModelTests
{
    private readonly struct SceneDomain : IHierarchyDomain;

    private readonly struct UiDomain : IHierarchyDomain;

    private readonly struct NeverRegisteredDomain : IHierarchyDomain;

    private readonly struct ComponentIdRegisteredDomain : IHierarchyDomain;

    private struct SceneNode : IAddedComponent
    {
        public int Value;
    }

    [Fact]
    public void DestroySubtreeRemovesEveryDescendant()
    {
        const int ChildCount = 1_024;
        var world = new World();
        Entity root = world.Create();
        var children = new Entity[ChildCount];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = world.Create();
            Hierarchy<SceneDomain>.SetParentDeferred(world, children[i], root);
        }

        Hierarchy<SceneDomain>.DestroySubtree(world, root);
        world.ClearRemoved<Parent<SceneDomain>>(world.Version);
        world.ClearRemoved<Children<SceneDomain>>(world.Version);

        Assert.False(world.IsAlive(root));
        for (int i = 0; i < children.Length; i++)
            Assert.False(world.IsAlive(children[i]));
    }

    [Fact]
    public void OrdinaryDestroy_UsesAppliedAndDirtyLocalIndicesWithoutLosingChildren()
    {
        var world = new World();
        Entity doomedParent = world.Create();
        Entity oldParent = world.Create();
        Entity appliedChild = world.Create();
        Entity deferredChild = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, appliedChild, doomedParent);
        Hierarchy<SceneDomain>.SetParent(world, deferredChild, oldParent);
        Hierarchy<SceneDomain>.SetParentDeferred(world, deferredChild, doomedParent);

        world.Destroy(doomedParent);

        Assert.True(world.IsAlive(appliedChild));
        Assert.True(world.IsAlive(deferredChild));
        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, appliedChild));
        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, deferredChild));
        Assert.Empty(ReadChildren<SceneDomain>(world, oldParent));
    }

    [Fact]
    public void UnrelatedOrdinaryDestroys_DoNotRescanLargeDirtyParentSet()
    {
        const int DirtyCount = 1_024;
        const int DestroyCount = 128;
        var world = new World();
        Entity dirtyParent = world.Create();
        for (int i = 0; i < DirtyCount; i++)
        {
            Entity child = world.Create();
            Hierarchy<SceneDomain>.SetParentDeferred(world, child, dirtyParent);
        }

        var unrelated = new Entity[DestroyCount];
        for (int i = 0; i < unrelated.Length; i++)
            unrelated[i] = world.Create();

        for (int i = 0; i < unrelated.Length; i++)
            world.Destroy(unrelated[i]);

        Hierarchy<SceneDomain>.Maintain(world);
        Assert.Equal(DirtyCount, ReadChildren<SceneDomain>(world, dirtyParent).Length);
        Assert.Equal(DirtyCount + 1, world.EntityCount);
        Assert.True(world.IsAlive(dirtyParent));
    }

    [Fact]
    public void TypedDomains_AreIndependent_AndHaveNoMembershipComponent()
    {
        var world = new World();
        Entity sceneParent = world.Create();
        Entity uiParent = world.Create();
        Entity child = world.Create();

        Hierarchy<SceneDomain>.SetParent(world, child, sceneParent);
        Hierarchy<UiDomain>.SetParent(world, child, uiParent);

        Assert.Equal(sceneParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(uiParent, Hierarchy<UiDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, sceneParent).ToArray());
        Assert.Equal(new[] { child }, ReadChildren<UiDomain>(world, uiParent).ToArray());
        Assert.True(world.Has<Parent<SceneDomain>>(child));
        Assert.True(world.Has<Parent<UiDomain>>(child));
    }

    [Fact]
    public void Root_IsWorkloadCandidateWithoutParent_NotGlobalMembership()
    {
        var world = new World();
        Entity root = world.Create(new SceneNode { Value = 1 });
        Entity child = world.Create(new SceneNode { Value = 2 });
        _ = world.Create(); // Unrelated entity is not a SceneNode candidate.
        Hierarchy<SceneDomain>.SetParent(world, child, root);

        var roots = world.Query(
            new QueryDescription().WithStored<SceneNode>().Without<Parent<SceneDomain>>());

        var found = new List<Entity>();
        {
            using QueryScope cursor = (roots).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                found.Add(rowChunk.Entities[row]);

        };

        Assert.Equal(new[] { root }, found);
    }

    [Fact]
    public void ParentAndChildren_UseNativeAddedChangedRemovedFacts()
    {
        var world = new World();
        Entity firstParent = world.Create();
        Entity secondParent = world.Create();
        Entity child = world.Create();
        Entity sibling = world.Create();

        ulong beforeAdd = world.AcquireSystemVersion();
        Hierarchy<SceneDomain>.SetParent(world, child, firstParent);

        Assert.Equal(
            new[] { child },
            QueryEntities(
                world.Query(new QueryDescription().WithStored<Parent<SceneDomain>>().WithAdded<Parent<SceneDomain>>(0)),
                beforeAdd));
        Assert.Equal(
            new[] { firstParent },
            QueryEntities(
                world.Query(new QueryDescription().WithStored<Children<SceneDomain>>().WithAdded<Children<SceneDomain>>(0)),
                beforeAdd));

        ulong beforeChildrenChange = world.AcquireSystemVersion();
        Hierarchy<SceneDomain>.SetParent(world, sibling, firstParent);
        Assert.Equal(
            new[] { firstParent },
            QueryEntities(
                world.Query(new QueryDescription().WithStored<Children<SceneDomain>>().WithChanged<Children<SceneDomain>>(0)),
                beforeChildrenChange));

        ulong beforeParentChange = world.AcquireSystemVersion();
        Hierarchy<SceneDomain>.SetParent(world, child, secondParent);
        Assert.Equal(
            new[] { child },
            QueryEntities(
                world.Query(new QueryDescription().WithStored<Parent<SceneDomain>>().WithChanged<Parent<SceneDomain>>(0)),
                beforeParentChange));

        Hierarchy<SceneDomain>.Detach(world, child);
        Parent<SceneDomain>? removedParent = null;
        var removedParentQuery = world.Query(
            new QueryDescription().WithStored<Removed<Parent<SceneDomain>>>());
        {
            using QueryScope cursor = (removedParentQuery).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
            {
                Assert.Equal(child, rowChunk.Entities[row]);
                removedParent = rowChunk.Read<Removed<Parent<SceneDomain>>>()[row].Value;
            }

        };
        Assert.True(removedParent.HasValue);
        Assert.Equal(secondParent, removedParent.Value.Value);

        Assert.Equal(
            new[] { secondParent },
            QueryEntities(world.Query(
                new QueryDescription().WithStored<Removed<Children<SceneDomain>>>())));

        Entity[] QueryEntities(Query query, ulong? lastVersion = null)
        {
            var entities = new List<Entity>();
            using QueryScope scope = query.Since(lastVersion ?? 0).OpenRead();
            foreach (QueryChunk chunk in scope)
                foreach (Entity entity in chunk.Entities) entities.Add(entity);
            entities.Sort(static (left, right) =>
            {
                int index = left.Id.CompareTo(right.Id);
                return index != 0 ? index : left.Generation.CompareTo(right.Generation);
            });
            return entities.ToArray();
        }
    }

    [Fact]
    public void ImmediateAndDeferred_UseEquivalentTransitionSemantics()
    {
        var immediate = new World();
        Entity immediateOld = immediate.Create();
        Entity immediateNew = immediate.Create();
        Entity immediateChild = immediate.Create();
        Hierarchy<SceneDomain>.SetParent(immediate, immediateChild, immediateOld);
        Hierarchy<SceneDomain>.SetParent(immediate, immediateChild, immediateNew);

        var deferred = new World();
        Entity deferredOld = deferred.Create();
        Entity deferredNew = deferred.Create();
        Entity deferredChild = deferred.Create();
        Hierarchy<SceneDomain>.SetParent(deferred, deferredChild, deferredOld);
        Hierarchy<SceneDomain>.SetParentDeferred(deferred, deferredChild, deferredNew);

        Assert.Equal(new[] { deferredChild }, ReadChildren<SceneDomain>(deferred, deferredOld).ToArray());
        Assert.Empty(ReadChildren<SceneDomain>(deferred, deferredNew));
        Assert.Equal(deferredNew, Hierarchy<SceneDomain>.GetParent(deferred, deferredChild));

        Hierarchy<SceneDomain>.Maintain(deferred);

        Assert.Empty(ReadChildren<SceneDomain>(immediate, immediateOld));
        Assert.Empty(ReadChildren<SceneDomain>(deferred, deferredOld));
        Assert.Equal(
            ReadChildren<SceneDomain>(immediate, immediateNew).Length,
            ReadChildren<SceneDomain>(deferred, deferredNew).Length);
        Assert.Equal(deferredNew, Hierarchy<SceneDomain>.GetParent(deferred, deferredChild));
    }

    [Fact]
    public void DeferredMultipleWrites_CompareFinalParentWithAppliedParent()
    {
        var world = new World();
        Entity a = world.Create();
        Entity b = world.Create();
        Entity c = world.Create();
        Entity child = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, a);

        Hierarchy<SceneDomain>.SetParentDeferred(world, child, b);
        Hierarchy<SceneDomain>.SetParentDeferred(world, child, c);

        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, a).ToArray());
        Hierarchy<SceneDomain>.Maintain(world);

        Assert.Empty(ReadChildren<SceneDomain>(world, a));
        Assert.Empty(ReadChildren<SceneDomain>(world, b));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, c).ToArray());
    }

    [Fact]
    public void InvalidTypedMutations_AreRejectedBeforeCanonicalPublication()
    {
        var world = new World();
        Entity root = world.Create();
        Entity child = world.Create();
        Entity grandchild = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, root);
        Hierarchy<SceneDomain>.SetParent(world, grandchild, child);

        Assert.Throws<ArgumentException>(
            () => Hierarchy<SceneDomain>.SetParentDeferred(world, root, grandchild));
        Assert.Throws<ArgumentException>(
            () => Hierarchy<SceneDomain>.SetParent(world, root, root));

        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, root));
        Assert.Equal(root, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(child, Hierarchy<SceneDomain>.GetParent(world, grandchild));
    }

    [Fact]
    public void InvalidOwnerBoundParentWrite_RollsBackAtValidation()
    {
        var world = new World();
        Entity root = world.Create();
        Entity child = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, root);

        Assert.Throws<ArgumentException>(() => WriteInvalidSelfParent(world, child));

        Assert.Equal(root, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, root).ToArray());
    }

    [Fact]
    public void CandidateBodyFailurePreservesSourceParentAndChildren()
    {
        using var world = new World();
        Entity oldParent = world.Create();
        Entity newParent = world.Create();
        Entity child = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, oldParent);
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        Assert.Throws<ApplicationException>((Action)(() =>
        {
            Hierarchy<SceneDomain>.SetParentDeferred(candidate, child, newParent);
            throw new ApplicationException();
        }));
        Assert.Equal(oldParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, oldParent));
        Assert.Empty(ReadChildren<SceneDomain>(world, newParent));
    }

    [Fact]
    public void OwnerBoundInvalidWrite_AfterTypedDeferred_RestoresDeferredCanonicalParent()
    {
        var world = new World();
        Entity oldParent = world.Create();
        Entity deferredParent = world.Create();
        Entity child = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, oldParent);
        Hierarchy<SceneDomain>.SetParentDeferred(world, child, deferredParent);

        Assert.Throws<ArgumentException>(() => WriteInvalidSelfParent(world, child));

        Assert.Equal(deferredParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, oldParent).ToArray());
        Assert.Empty(ReadChildren<SceneDomain>(world, deferredParent));

        Hierarchy<SceneDomain>.Maintain(world);

        Assert.Empty(ReadChildren<SceneDomain>(world, oldParent));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, deferredParent).ToArray());
    }

    [Fact]
    public void FailedCandidatePreservesSourceDeferredParent()
    {
        using var world = new World();
        Entity appliedParent = world.Create();
        Entity deferredParent = world.Create();
        Entity faultingParent = world.Create();
        Entity child = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, appliedParent);
        Hierarchy<SceneDomain>.SetParentDeferred(world, child, deferredParent);
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        Assert.Throws<ApplicationException>((Action)(() =>
        {
            Hierarchy<SceneDomain>.SetParentDeferred(candidate, child, faultingParent);
            throw new ApplicationException();
        }));
        Assert.Equal(deferredParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, appliedParent));
        Hierarchy<SceneDomain>.Maintain(world);
        Assert.Empty(ReadChildren<SceneDomain>(world, appliedParent));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, deferredParent));
    }

    [Fact]
    public void SelectedChildCanBeEditedAfterItsReadScopeEnds()
    {
        using var world = new World();
        Entity oldParent = world.Create();
        Entity newParent = world.Create();
        Entity child = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, oldParent);
        Entity selected;
        {
            using QueryScope scope = world.Query(new QueryDescription().WithStored<Parent<SceneDomain>>()).OpenRead();
            selected = Entity.Null;
            foreach (QueryChunk chunk in scope) { selected = chunk.Entities[0]; break; }
        }
        Hierarchy<SceneDomain>.SetParentDeferred(world, selected, newParent);
        Assert.Equal(newParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, oldParent));
        Assert.Empty(ReadChildren<SceneDomain>(world, newParent));
        Hierarchy<SceneDomain>.Maintain(world);
        Assert.Empty(ReadChildren<SceneDomain>(world, oldParent));
        Assert.Equal(new[] { child }, ReadChildren<SceneDomain>(world, newParent));
    }

    [Fact]
    public void ParentLocalOrder_MixesOrderedAndUnorderedWithoutTwoEngines()
    {
        var world = new World();
        Entity orderedParent = world.Create();
        Entity unorderedParent = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();
        Entity third = world.Create();
        Entity unorderedChild = world.Create();

        Hierarchy<SceneDomain>.SetOrdered(
            world,
            orderedParent,
            true);
        Hierarchy<SceneDomain>.SetParent(world, first, orderedParent);
        Hierarchy<SceneDomain>.SetParent(world, second, orderedParent);
        Hierarchy<SceneDomain>.SetParent(world, third, orderedParent, insertAt: 1);
        Hierarchy<SceneDomain>.SetParent(world, unorderedChild, unorderedParent);

        Assert.True(Hierarchy<SceneDomain>.IsOrdered(world, orderedParent));
        Assert.False(Hierarchy<SceneDomain>.IsOrdered(world, unorderedParent));
        Assert.Equal(
            new[] { first, third, second },
            ReadChildren<SceneDomain>(world, orderedParent).ToArray());

        Hierarchy<SceneDomain>.SetParent(world, second, Hierarchy<SceneDomain>.GetParent(world, second), 0);
        Assert.Equal(
            new[] { second, first, third },
            ReadChildren<SceneDomain>(world, orderedParent).ToArray());
        Assert.Equal(new[] { unorderedChild }, ReadChildren<SceneDomain>(world, unorderedParent).ToArray());
    }

    [Fact]
    public void OrderedDeferredInsertions_UseStableProjectedFifoImage()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();
        Hierarchy<SceneDomain>.SetOrdered(world, parent, true);

        Hierarchy<SceneDomain>.SetParentDeferred(world, first, parent, insertAt: 0);
        Hierarchy<SceneDomain>.SetParentDeferred(world, second, parent, insertAt: 1);
        Hierarchy<SceneDomain>.Maintain(world);

        Assert.Equal(
            new[] { first, second },
            ReadChildren<SceneDomain>(world, parent).ToArray());
    }

    [Fact]
    public void InvalidOrderedDeferredImage_DoesNotPartiallyPublishAndCanBeCorrected()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();
        Entity replacement = world.Create();
        Hierarchy<SceneDomain>.SetOrdered(world, parent, true);
        Hierarchy<SceneDomain>.SetParent(world, first, parent);
        Hierarchy<SceneDomain>.SetParent(world, second, parent);
        var before = ReadChildren<SceneDomain>(world, parent);

        Hierarchy<SceneDomain>.DetachDeferred(world, first);
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Hierarchy<SceneDomain>.SetParentDeferred(world, replacement, parent, insertAt: 2));
        var afterFailure = ReadChildren<SceneDomain>(world, parent);
        Assert.Equal(new[] { first, second }, afterFailure.ToArray());

        Hierarchy<SceneDomain>.SetParentDeferred(world, replacement, parent, insertAt: 1);
        Hierarchy<SceneDomain>.Maintain(world);

        Assert.Equal(new[] { second, replacement }, ReadChildren<SceneDomain>(world, parent).ToArray());
        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, first));
    }

    [Fact]
    public void PolicyConversion_UsesExplicitPermutationAndCanDiscardOrder()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity a = world.Create();
        Entity b = world.Create();
        Entity c = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, a, parent);
        Hierarchy<SceneDomain>.SetParent(world, b, parent);
        Hierarchy<SceneDomain>.SetParent(world, c, parent);

        Hierarchy<SceneDomain>.SetOrdered(
            world,
            parent,
            true,
            [c, a, b]);
        Assert.Equal(new[] { c, a, b }, ReadChildren<SceneDomain>(world, parent).ToArray());

        Hierarchy<SceneDomain>.SetOrdered(world, parent, false);
        Assert.False(Hierarchy<SceneDomain>.IsOrdered(world, parent));
        Assert.Equal(3, ReadChildren<SceneDomain>(world, parent).Length);
    }

    [Fact]
    public void OrdinaryDestroyHook_OrphansDirectChildrenAndPreservesGrandchildren()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity child = world.Create();
        Entity grandchild = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, parent);
        Hierarchy<SceneDomain>.SetParent(world, grandchild, child);

        world.Destroy(parent);
        world.ClearRemoved<Parent<SceneDomain>>(world.Version);
        world.ClearRemoved<Children<SceneDomain>>(world.Version);

        Assert.False(world.IsAlive(parent));
        Assert.True(world.IsAlive(child));
        Assert.True(world.IsAlive(grandchild));
        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(child, Hierarchy<SceneDomain>.GetParent(world, grandchild));
        Assert.Equal(new[] { grandchild }, ReadChildren<SceneDomain>(world, child).ToArray());
    }

    [Fact]
    public void DestroySubtree_IsTheExplicitCascadeOperation()
    {
        var world = new World();
        Entity root = world.Create();
        Entity child = world.Create();
        Entity grandchild = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, root);
        Hierarchy<SceneDomain>.SetParent(world, grandchild, child);

        Hierarchy<SceneDomain>.DestroySubtree(world, root);
        world.ClearRemoved<Parent<SceneDomain>>(world.Version);
        world.ClearRemoved<Children<SceneDomain>>(world.Version);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(grandchild));
    }

    [Fact]
    public void ChildrenView_IsReadOnlyAndOwnsItsCapturedGeneration()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();
        Hierarchy<SceneDomain>.SetOrdered(world, parent, true);
        Hierarchy<SceneDomain>.SetParent(world, first, parent);
        var captured = ReadChildren<SceneDomain>(world, parent);

        Hierarchy<SceneDomain>.SetParent(world, second, parent);

        Assert.Equal(new[] { first }, captured.ToArray());
        Assert.Equal(new[] { first, second }, ReadChildren<SceneDomain>(world, parent).ToArray());
    }

    [Fact]
    public void RepeatedChildrenReadsPreserveContents()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity child = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, parent);

        // Warm generic statics, the domain dictionary lookup, and the test loop before measuring.
        Assert.Single(ReadChildren<SceneDomain>(world, parent));
        int observed = ReadRepeatedly();
        Assert.Equal(64, observed);

        observed = ReadRepeatedly();

        Assert.Equal(64, observed);

        int ReadRepeatedly()
        {
            int count = 0;
            for (int i = 0; i < 64; i++)
                count += ReadChildren<SceneDomain>(world, parent).Length;
            return count;
        }
    }

    [Fact]
    public async Task ChildrenReadScopePrecedesConcurrentChildEdit()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 1 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity parent = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, first, parent);
        using var started = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        Task writer;
        using (ChildrenRead children = Hierarchy<SceneDomain>.GetChildren(world, parent))
        {
            writer = Task.Run(() =>
            {
                started.Set();
                Hierarchy<SceneDomain>.SetParent(world, second, parent);
                events.Enqueue("write");
            });
            started.Wait();
            Assert.Equal(new[] { first }, children.Entities.ToArray());
            events.Enqueue("read");
        }
        await writer;
        Assert.Equal(new[] { "read", "write" }, events.ToArray());
        Assert.Equal(new[] { first, second }, ReadChildren<SceneDomain>(world, parent));
    }

    [Fact]
    public async Task ConcurrentUnknownDomainReads_DoNotRegisterOrRaceWithKnownDomainRegistration()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 1 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity parent = world.Create();
        Entity child = world.Create();
        using var start = new ManualResetEventSlim();

        Task writer = Task.Run(() =>
        {
            start.Wait();
            for (int iteration = 0; iteration < 512; iteration++)
            {
                Hierarchy<SceneDomain>.SetParent(world, child, parent);
                Hierarchy<SceneDomain>.Detach(world, child);
            }
        });

        Task[] readers = Enumerable.Range(0, 4)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                for (int iteration = 0; iteration < 4_096; iteration++)
                {
                    Entity[] view =
                        ReadChildren<NeverRegisteredDomain>(world, parent);
                    Assert.Empty(view);
                }
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(readers.Append(writer));

    }

    [Fact]
    public void UnknownDomainScalarReadsReturnEmptyParentAndDefaultOrder()
    {
        var world = new World();
        Entity entity = world.Create();

        Assert.Equal(Entity.Null, Hierarchy<NeverRegisteredDomain>.GetParent(world, entity));
        Assert.False(Hierarchy<NeverRegisteredDomain>.IsOrdered(world, entity));
    }

    [Fact]
    public void RelationshipComponents_BlockPublicMutationSurfaces()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity child = world.Create();
        Hierarchy<SceneDomain>.SetParent(world, child, parent);

        Assert.Throws<InvalidOperationException>(
            () => world.Set(child, new Parent<SceneDomain>(parent)));
        Assert.Throws<InvalidOperationException>(
            () => world.Set(parent, default(Children<SceneDomain>)));
        Assert.Throws<InvalidOperationException>(() => world.Remove<Children<SceneDomain>>(parent));
    }

    [Fact]
    public void DefaultFacade_RoutesToGenericDefaultDomain()
    {
        var world = new World();
        Entity parent = world.Create();
        Entity child = world.Create();

        DefaultHierarchy.SetParent(world, child, parent);

        Assert.True(world.Has<Parent<DefaultHierarchyDomain>>(child));
        Assert.True(world.Has<Children<DefaultHierarchyDomain>>(parent));
        Assert.Equal(
            ReadChildren<DefaultHierarchyDomain>(world, parent).ToArray(),
            ReadChildren<DefaultHierarchyDomain>(world, parent).ToArray());
    }

    private static void WriteInvalidSelfParent(World world, Entity child)
        => Hierarchy<SceneDomain>.SetParentDeferred(world, child, child);
}
