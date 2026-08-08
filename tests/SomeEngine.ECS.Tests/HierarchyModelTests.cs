using System.Collections.Concurrent;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using DefaultHierarchy = SomeEngine.ECS.Hierarchy.Hierarchy;

namespace SomeEngine.ECS.Tests;

public class HierarchyModelTests
{
    private readonly struct SceneDomain : IHierarchyDomain;

    private readonly struct UiDomain : IHierarchyDomain;

    private readonly struct NeverRegisteredDomain : IHierarchyDomain;

    private readonly struct ComponentIdRegisteredDomain : IHierarchyDomain;

    private struct SceneNode : IComponent
    {
        public int Value;
    }

    [Fact]
    public void ComponentIdFirstAccess_UsesTypedRegistrationForParentAndChildren()
    {
        var parentFirst = new World();
        Entity parentEntity = parentFirst.CreateEntity();
        Assert.False(parentFirst.Hierarchy.TryDomain<ComponentIdRegisteredDomain>(out _));

        parentFirst.Hierarchy.TrackParent(
            parentEntity,
            ComponentMetadata<Parent<ComponentIdRegisteredDomain>>.Id);

        Assert.True(
            parentFirst.Hierarchy.TryDomain<ComponentIdRegisteredDomain>(out var parentStore));
        Assert.Equal(
            ComponentMetadata<Parent<ComponentIdRegisteredDomain>>.Id,
            parentStore.ParentComponentId);
        Assert.Equal(
            ComponentMetadata<Children<ComponentIdRegisteredDomain>>.Id,
            parentStore.ChildrenComponentId);

        var childrenFirst = new World();
        Entity childrenEntity = childrenFirst.CreateEntity();
        Assert.False(childrenFirst.Hierarchy.TryDomain<ComponentIdRegisteredDomain>(out _));

        childrenFirst.Hierarchy.TrackParent(
            childrenEntity,
            ComponentMetadata<Children<ComponentIdRegisteredDomain>>.Id);

        Assert.True(
            childrenFirst.Hierarchy.TryDomain<ComponentIdRegisteredDomain>(out var childrenStore));
        Assert.Equal(parentStore.ParentComponentId, childrenStore.ParentComponentId);
        Assert.Equal(parentStore.ChildrenComponentId, childrenStore.ChildrenComponentId);
    }

    [Fact]
    public void LargeSubtreeDestroy_UsesConstantCanonicalParentPasses()
    {
        const int ChildCount = 1_024;
        var world = new World();
        Entity root = world.CreateEntity();
        var children = new Entity[ChildCount];
        for (int i = 0; i < children.Length; i++)
        {
            children[i] = world.CreateEntity();
            Hierarchy<SceneDomain>.SetParentDeferred(world, children[i], root);
        }

        var store = world.Hierarchy.Domain<SceneDomain>();
        long before = store.CanonicalParentFullScanCount;
        long dirtyLookupsBefore = store.DirtyParentLookupCount;
        long dirtyRebuildVisitsBefore = store.DirtyParentIndexRebuildEntityVisits;

        Hierarchy<SceneDomain>.DestroySubtree(world, root);

        Assert.InRange(store.CanonicalParentFullScanCount - before, 1, 2);
        Assert.Equal(dirtyLookupsBefore, store.DirtyParentLookupCount);
        Assert.Equal(dirtyRebuildVisitsBefore, store.DirtyParentIndexRebuildEntityVisits);
        Assert.False(world.IsAlive(root));
        for (int i = 0; i < children.Length; i++)
            Assert.False(world.IsAlive(children[i]));
    }

    [Fact]
    public void OrdinaryDestroy_UsesAppliedAndDirtyLocalIndicesWithoutLosingChildren()
    {
        var world = new World();
        Entity doomedParent = world.CreateEntity();
        Entity oldParent = world.CreateEntity();
        Entity appliedChild = world.CreateEntity();
        Entity deferredChild = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, appliedChild, doomedParent);
        Hierarchy<SceneDomain>.SetParent(world, deferredChild, oldParent);
        Hierarchy<SceneDomain>.SetParentDeferred(world, deferredChild, doomedParent);

        var store = world.Hierarchy.Domain<SceneDomain>();
        long before = store.CanonicalParentFullScanCount;

        world.DestroyEntity(doomedParent);

        Assert.Equal(before, store.CanonicalParentFullScanCount);
        Assert.True(world.IsAlive(appliedChild));
        Assert.True(world.IsAlive(deferredChild));
        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, appliedChild));
        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, deferredChild));
        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(world, oldParent));
    }

    [Fact]
    public void UnrelatedOrdinaryDestroys_DoNotRescanLargeDirtyParentSet()
    {
        const int DirtyCount = 1_024;
        const int DestroyCount = 128;
        var world = new World();
        Entity dirtyParent = world.CreateEntity();
        for (int i = 0; i < DirtyCount; i++)
        {
            Entity child = world.CreateEntity();
            Hierarchy<SceneDomain>.SetParentDeferred(world, child, dirtyParent);
        }

        var unrelated = new Entity[DestroyCount];
        for (int i = 0; i < unrelated.Length; i++)
            unrelated[i] = world.CreateEntity();

        var store = world.Hierarchy.Domain<SceneDomain>();
        long rebuildVisitsBefore = store.DirtyParentIndexRebuildEntityVisits;
        long lookupCountBefore = store.DirtyParentLookupCount;
        long lookupVisitsBefore = store.DirtyParentLookupEntityVisits;

        for (int i = 0; i < unrelated.Length; i++)
            world.DestroyEntity(unrelated[i]);

        Assert.InRange(
            store.DirtyParentIndexRebuildEntityVisits - rebuildVisitsBefore,
            0,
            DirtyCount);
        Assert.Equal(DestroyCount, store.DirtyParentLookupCount - lookupCountBefore);
        Assert.Equal(0, store.DirtyParentLookupEntityVisits - lookupVisitsBefore);
        Assert.True(world.IsAlive(dirtyParent));
    }

    [Fact]
    public void TypedDomains_AreIndependent_AndHaveNoMembershipComponent()
    {
        var world = new World();
        Entity sceneParent = world.CreateEntity();
        Entity uiParent = world.CreateEntity();
        Entity child = world.CreateEntity();

        Hierarchy<SceneDomain>.SetParent(world, child, sceneParent);
        Hierarchy<UiDomain>.SetParent(world, child, uiParent);

        Assert.Equal(sceneParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(uiParent, Hierarchy<UiDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, sceneParent).ToArray());
        Assert.Equal(new[] { child }, Hierarchy<UiDomain>.GetChildren(world, uiParent).ToArray());
        Assert.True(world.Has<Parent<SceneDomain>>(child));
        Assert.True(world.Has<Parent<UiDomain>>(child));
        Assert.DoesNotContain(
            typeof(World).Assembly.GetTypes(),
            static type => type.Name.StartsWith("HierarchyNode", StringComparison.Ordinal));
    }

    [Fact]
    public void Root_IsWorkloadCandidateWithoutParent_NotGlobalMembership()
    {
        var world = new World();
        Entity root = world.CreateEntity(new SceneNode { Value = 1 });
        Entity child = world.CreateEntity(new SceneNode { Value = 2 });
        _ = world.CreateEntity(); // Unrelated entity is not a SceneNode candidate.
        Hierarchy<SceneDomain>.SetParent(world, child, root);

        var roots = world.Query(
            world.QueryDefinition()
                .Read<SceneNode>()
                .None<Parent<SceneDomain>>());

        var found = new List<Entity>();
        world.ExecuteQuery(roots, cursor =>
        {
            foreach (var row in cursor.Rows)
                found.Add(row.Entity);
        });

        Assert.Equal(new[] { root }, found);
    }

    [Fact]
    public void ParentAndChildren_UseNativeAddedChangedRemovedFacts()
    {
        var world = new World();
        Entity firstParent = world.CreateEntity();
        Entity secondParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Entity sibling = world.CreateEntity();

        uint beforeAdd = world.AcquireSystemTick();
        Hierarchy<SceneDomain>.SetParent(world, child, firstParent);

        Assert.Equal(
            new[] { child },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<Parent<SceneDomain>>()
                    .Added<Parent<SceneDomain>>()),
                beforeAdd));
        Assert.Equal(
            new[] { firstParent },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<Children<SceneDomain>>()
                    .Added<Children<SceneDomain>>()),
                beforeAdd));

        uint beforeChildrenChange = world.AcquireSystemTick();
        Hierarchy<SceneDomain>.SetParent(world, sibling, firstParent);
        Assert.Equal(
            new[] { firstParent },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<Children<SceneDomain>>()
                    .Changed<Children<SceneDomain>>()),
                beforeChildrenChange));

        uint beforeParentChange = world.AcquireSystemTick();
        Hierarchy<SceneDomain>.SetParent(world, child, secondParent);
        Assert.Equal(
            new[] { child },
            QueryEntities(
                world.Query(world.QueryDefinition()
                    .Read<Parent<SceneDomain>>()
                    .Changed<Parent<SceneDomain>>()),
                beforeParentChange));

        Hierarchy<SceneDomain>.Detach(world, child);
        Parent<SceneDomain>? removedParent = null;
        var removedParentQuery = world.Query(
            world.QueryDefinition().Removed<Parent<SceneDomain>>());
        world.ExecuteQuery(removedParentQuery, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.Equal(child, row.Entity);
                removedParent = row.Read<Removed<Parent<SceneDomain>>>().Value;
            }
        });
        Assert.True(removedParent.HasValue);
        Assert.Equal(secondParent, removedParent.Value.Value);

        Assert.Equal(
            new[] { secondParent },
            QueryEntities(world.Query(
                world.QueryDefinition().Removed<Children<SceneDomain>>())));

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
    public void ImmediateAndDeferred_UseEquivalentTransitionSemantics()
    {
        var immediate = new World();
        Entity immediateOld = immediate.CreateEntity();
        Entity immediateNew = immediate.CreateEntity();
        Entity immediateChild = immediate.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(immediate, immediateChild, immediateOld);
        Hierarchy<SceneDomain>.SetParent(immediate, immediateChild, immediateNew);

        var deferred = new World();
        Entity deferredOld = deferred.CreateEntity();
        Entity deferredNew = deferred.CreateEntity();
        Entity deferredChild = deferred.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(deferred, deferredChild, deferredOld);
        Hierarchy<SceneDomain>.SetParentDeferred(deferred, deferredChild, deferredNew);

        Assert.Equal(new[] { deferredChild }, Hierarchy<SceneDomain>.GetChildren(deferred, deferredOld).ToArray());
        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(deferred, deferredNew));
        Assert.Equal(deferredNew, Hierarchy<SceneDomain>.GetParent(deferred, deferredChild));

        Hierarchy<SceneDomain>.Maintain(deferred);

        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(immediate, immediateOld));
        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(deferred, deferredOld));
        Assert.Equal(
            Hierarchy<SceneDomain>.GetChildren(immediate, immediateNew).Count,
            Hierarchy<SceneDomain>.GetChildren(deferred, deferredNew).Count);
        Assert.Equal(deferredNew, Hierarchy<SceneDomain>.GetParent(deferred, deferredChild));
    }

    [Fact]
    public void DeferredMultipleWrites_CompareFinalParentWithAppliedParent()
    {
        var world = new World();
        Entity a = world.CreateEntity();
        Entity b = world.CreateEntity();
        Entity c = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, a);

        Hierarchy<SceneDomain>.SetParentDeferred(world, child, b);
        Hierarchy<SceneDomain>.SetParentDeferred(world, child, c);

        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, a).ToArray());
        Hierarchy<SceneDomain>.Maintain(world);

        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(world, a));
        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(world, b));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, c).ToArray());
    }

    [Fact]
    public void InvalidTypedMutations_AreRejectedBeforeCanonicalPublication()
    {
        var world = new World();
        Entity root = world.CreateEntity();
        Entity child = world.CreateEntity();
        Entity grandchild = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, root);
        Hierarchy<SceneDomain>.SetParent(world, grandchild, child);

        Assert.Throws<InvalidOperationException>(
            () => Hierarchy<SceneDomain>.SetParentDeferred(world, root, grandchild));
        Assert.Throws<InvalidOperationException>(
            () => Hierarchy<SceneDomain>.SetParent(world, root, root));

        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, root));
        Assert.Equal(root, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(child, Hierarchy<SceneDomain>.GetParent(world, grandchild));
    }

    [Fact]
    public void InvalidOwnerBoundParentWrite_RollsBackAtValidation()
    {
        var world = new World();
        Entity root = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, root);

        Assert.Throws<InvalidOperationException>(() => WriteInvalidSelfParent(world, child));

        Assert.Equal(root, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, root).ToArray());
    }

    [Fact]
    public void OwnerBoundParentWrite_BodyFaultRollsBackAValidPartialEdit()
    {
        var world = new World();
        Entity oldParent = world.CreateEntity();
        Entity newParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, oldParent);
        var query = world.Query(
            world.QueryDefinition().ReadWrite<Parent<SceneDomain>>());

        Assert.Throws<ApplicationException>(() => MutateThenFault());

        Assert.Equal(oldParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, oldParent).ToArray());
        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(world, newParent));

        void MutateThenFault()
        {
            world.ExecuteQuery(query, cursor =>
            {
                foreach (var row in cursor.Rows)
                {
                    row.ReadWrite<Parent<SceneDomain>>().Value = newParent;
                    throw new ApplicationException("body fault");
                }
            });
        }
    }

    [Fact]
    public void OwnerBoundInvalidWrite_AfterTypedDeferred_RestoresDeferredCanonicalParent()
    {
        var world = new World();
        Entity oldParent = world.CreateEntity();
        Entity deferredParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, oldParent);
        Hierarchy<SceneDomain>.SetParentDeferred(world, child, deferredParent);

        Assert.Throws<InvalidOperationException>(() => WriteInvalidSelfParent(world, child));

        Assert.Equal(deferredParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, oldParent).ToArray());
        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(world, deferredParent));

        Hierarchy<SceneDomain>.Maintain(world);

        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(world, oldParent));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, deferredParent).ToArray());
    }

    [Fact]
    public void LaterOwnerBodyFault_RestoresPreviousUnmaintainedCanonicalParent()
    {
        var world = new World();
        Entity appliedParent = world.CreateEntity();
        Entity firstDeferredParent = world.CreateEntity();
        Entity faultingParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, appliedParent);
        var query = world.Query(
            world.QueryDefinition().ReadWrite<Parent<SceneDomain>>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                row.ReadWrite<Parent<SceneDomain>>().Value = firstDeferredParent;
        });

        Assert.Throws<ApplicationException>(() => MutateThenFault());

        Assert.Equal(firstDeferredParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, appliedParent).ToArray());
        Hierarchy<SceneDomain>.Maintain(world);
        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(world, appliedParent));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, firstDeferredParent).ToArray());

        void MutateThenFault()
        {
            world.ExecuteQuery(query, cursor =>
            {
                foreach (var row in cursor.Rows)
                {
                    row.ReadWrite<Parent<SceneDomain>>().Value = faultingParent;
                    throw new ApplicationException("body fault");
                }
            });
        }
    }

    [Fact]
    public void RuntimeOwnedQueryBreak_CommitsDeferredCanonicalParent()
    {
        var world = new World();
        Entity oldParent = world.CreateEntity();
        Entity newParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, oldParent);
        var query = world.Query(
            world.QueryDefinition().ReadWrite<Parent<SceneDomain>>());

        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                row.ReadWrite<Parent<SceneDomain>>().Value = newParent;
                break;
            }
        });

        Assert.Equal(newParent, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, oldParent).ToArray());
        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(world, newParent));

        Hierarchy<SceneDomain>.Maintain(world);
        Assert.Empty(Hierarchy<SceneDomain>.GetChildren(world, oldParent));
        Assert.Equal(new[] { child }, Hierarchy<SceneDomain>.GetChildren(world, newParent).ToArray());
    }

    [Fact]
    public void ParentLocalOrder_MixesOrderedAndUnorderedWithoutTwoEngines()
    {
        var world = new World();
        Entity orderedParent = world.CreateEntity();
        Entity unorderedParent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity third = world.CreateEntity();
        Entity unorderedChild = world.CreateEntity();

        Hierarchy<SceneDomain>.SetChildOrderPolicy(
            world,
            orderedParent,
            ChildOrderPolicy.Ordered);
        Hierarchy<SceneDomain>.SetParent(world, first, orderedParent);
        Hierarchy<SceneDomain>.SetParent(world, second, orderedParent);
        Hierarchy<SceneDomain>.SetParent(world, third, orderedParent, insertIndex: 1);
        Hierarchy<SceneDomain>.SetParent(world, unorderedChild, unorderedParent);

        Assert.Equal(
            ChildOrderPolicy.Ordered,
            Hierarchy<SceneDomain>.GetChildOrderPolicy(world, orderedParent));
        Assert.Equal(
            ChildOrderPolicy.Unordered,
            Hierarchy<SceneDomain>.GetChildOrderPolicy(world, unorderedParent));
        Assert.Equal(
            new[] { first, third, second },
            Hierarchy<SceneDomain>.GetChildren(world, orderedParent).ToArray());

        Hierarchy<SceneDomain>.Reorder(world, second, 0);
        Assert.Equal(
            new[] { second, first, third },
            Hierarchy<SceneDomain>.GetChildren(world, orderedParent).ToArray());
        Assert.Equal(new[] { unorderedChild }, Hierarchy<SceneDomain>.GetChildren(world, unorderedParent).ToArray());
    }

    [Fact]
    public void OrderedDeferredInsertions_UseStableProjectedFifoImage()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Hierarchy<SceneDomain>.SetChildOrderPolicy(world, parent, ChildOrderPolicy.Ordered);

        Hierarchy<SceneDomain>.SetParentDeferred(world, first, parent, insertIndex: 0);
        Hierarchy<SceneDomain>.SetParentDeferred(world, second, parent, insertIndex: 1);
        Hierarchy<SceneDomain>.Maintain(world);

        Assert.Equal(
            new[] { first, second },
            Hierarchy<SceneDomain>.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void InvalidOrderedDeferredImage_DoesNotPartiallyPublishAndCanBeCorrected()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Entity replacement = world.CreateEntity();
        Hierarchy<SceneDomain>.SetChildOrderPolicy(world, parent, ChildOrderPolicy.Ordered);
        Hierarchy<SceneDomain>.SetParent(world, first, parent);
        Hierarchy<SceneDomain>.SetParent(world, second, parent);
        var before = Hierarchy<SceneDomain>.GetChildren(world, parent);

        Hierarchy<SceneDomain>.DetachDeferred(world, first);
        Hierarchy<SceneDomain>.SetParentDeferred(world, replacement, parent, insertIndex: 2);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Hierarchy<SceneDomain>.Maintain(world));
        var afterFailure = Hierarchy<SceneDomain>.GetChildren(world, parent);
        Assert.Equal(before.Generation, afterFailure.Generation);
        Assert.Equal(new[] { first, second }, afterFailure.ToArray());

        Hierarchy<SceneDomain>.SetParentDeferred(world, replacement, parent, insertIndex: 1);
        Hierarchy<SceneDomain>.Maintain(world);

        Assert.Equal(new[] { second, replacement }, Hierarchy<SceneDomain>.GetChildren(world, parent).ToArray());
        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, first));
    }

    [Fact]
    public void PolicyConversion_UsesExplicitPermutationAndCanDiscardOrder()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity a = world.CreateEntity();
        Entity b = world.CreateEntity();
        Entity c = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, a, parent);
        Hierarchy<SceneDomain>.SetParent(world, b, parent);
        Hierarchy<SceneDomain>.SetParent(world, c, parent);

        Hierarchy<SceneDomain>.SetChildOrderPolicy(
            world,
            parent,
            ChildOrderPolicy.Ordered,
            [c, a, b]);
        Assert.Equal(new[] { c, a, b }, Hierarchy<SceneDomain>.GetChildren(world, parent).ToArray());

        Hierarchy<SceneDomain>.SetChildOrderPolicy(world, parent, ChildOrderPolicy.Unordered);
        Assert.Equal(ChildOrderPolicy.Unordered, Hierarchy<SceneDomain>.GetChildOrderPolicy(world, parent));
        Assert.Equal(3, Hierarchy<SceneDomain>.GetChildren(world, parent).Count);
    }

    [Fact]
    public void OrdinaryDestroyHook_OrphansDirectChildrenAndPreservesGrandchildren()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Entity grandchild = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, parent);
        Hierarchy<SceneDomain>.SetParent(world, grandchild, child);

        world.DestroyEntity(parent);

        Assert.False(world.IsAlive(parent));
        Assert.True(world.IsAlive(child));
        Assert.True(world.IsAlive(grandchild));
        Assert.Equal(Entity.Null, Hierarchy<SceneDomain>.GetParent(world, child));
        Assert.Equal(child, Hierarchy<SceneDomain>.GetParent(world, grandchild));
        Assert.Equal(new[] { grandchild }, Hierarchy<SceneDomain>.GetChildren(world, child).ToArray());
    }

    [Fact]
    public void DestroySubtree_IsTheExplicitCascadeOperation()
    {
        var world = new World();
        Entity root = world.CreateEntity();
        Entity child = world.CreateEntity();
        Entity grandchild = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, root);
        Hierarchy<SceneDomain>.SetParent(world, grandchild, child);

        Hierarchy<SceneDomain>.DestroySubtree(world, root);

        Assert.False(world.IsAlive(root));
        Assert.False(world.IsAlive(child));
        Assert.False(world.IsAlive(grandchild));
    }

    [Fact]
    public void ChildrenView_IsReadOnlyAndOwnsItsCapturedGeneration()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Hierarchy<SceneDomain>.SetChildOrderPolicy(world, parent, ChildOrderPolicy.Ordered);
        Hierarchy<SceneDomain>.SetParent(world, first, parent);
        var captured = Hierarchy<SceneDomain>.GetChildren(world, parent);

        Hierarchy<SceneDomain>.SetParent(world, second, parent);

        Assert.Equal(new[] { first }, captured.ToArray());
        Assert.Equal(new[] { first, second }, Hierarchy<SceneDomain>.GetChildren(world, parent).ToArray());
    }

    [Fact]
    public void ChildrenView_WarmedUnchangedGeneration_DoesNotAllocatePerRead()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, parent);

        // Warm generic statics, the domain dictionary lookup, and the test loop before measuring.
        Assert.Equal(1, Hierarchy<SceneDomain>.GetChildren(world, parent).Span.Length);
        int observed = ReadRepeatedly();
        Assert.Equal(64, observed);

        long before = GC.GetAllocatedBytesForCurrentThread();
        observed = ReadRepeatedly();
        long after = GC.GetAllocatedBytesForCurrentThread();

        Assert.Equal(64, observed);
        Assert.Equal(0, after - before);

        int ReadRepeatedly()
        {
            int count = 0;
            for (int i = 0; i < 64; i++)
                count += Hierarchy<SceneDomain>.GetChildren(world, parent).Span.Length;
            return count;
        }
    }

    [Fact]
    public async Task RelaxedChildrenPublication_IsCoherentDuringImmediateAndDeferredWriters()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity otherParent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();

        Hierarchy<SceneDomain>.SetChildOrderPolicy(world, parent, ChildOrderPolicy.Ordered);
        Hierarchy<SceneDomain>.SetParent(world, first, parent, insertIndex: 0);
        Hierarchy<SceneDomain>.SetParent(world, second, parent, insertIndex: 1);

        var expected = new ConcurrentDictionary<ulong, Entity[]>();
        var observed = new ConcurrentQueue<(ulong Generation, Entity[] Children)>();
        RecordExpected();

        using var start = new ManualResetEventSlim();
        int writerComplete = 0;
        Task reader = Task.Run(() =>
        {
            start.Wait();
            int captures = 0;
            while (Volatile.Read(ref writerComplete) == 0)
            {
                if (captures++ < 100_000)
                {
                    HierarchyChildrenSnapshot<SceneDomain> view =
                        Hierarchy<SceneDomain>.GetChildren(world, parent);
                    observed.Enqueue((view.Generation, view.ToArray()));
                }
                else
                {
                    Thread.Yield();
                }
            }

            HierarchyChildrenSnapshot<SceneDomain> final =
                Hierarchy<SceneDomain>.GetChildren(world, parent);
            observed.Enqueue((final.Generation, final.ToArray()));
        });

        Task writer = Task.Run(() =>
        {
            start.Wait();
            try
            {
                for (int iteration = 0; iteration < 256; iteration++)
                {
                    Hierarchy<SceneDomain>.Reorder(world, first, iteration & 1);
                    RecordExpected();

                    Hierarchy<SceneDomain>.SetParent(world, second, otherParent);
                    RecordExpected();
                    Hierarchy<SceneDomain>.SetParent(world, second, parent, insertIndex: 1);
                    RecordExpected();

                    Hierarchy<SceneDomain>.SetParentDeferred(world, second, otherParent);
                    Hierarchy<SceneDomain>.Maintain(world);
                    RecordExpected();
                    Hierarchy<SceneDomain>.SetParentDeferred(world, second, parent, insertIndex: 1);
                    Hierarchy<SceneDomain>.Maintain(world);
                    RecordExpected();

                    Entity transient = world.CreateEntity();
                    Hierarchy<SceneDomain>.SetParent(world, transient, parent, insertIndex: 0);
                    RecordExpected();
                    world.DestroyEntity(transient);
                    RecordExpected();
                }
            }
            finally
            {
                Volatile.Write(ref writerComplete, 1);
            }
        });

        start.Set();
        await Task.WhenAll(reader, writer);

        Assert.NotEmpty(observed);
        foreach (var observation in observed)
        {
            Assert.True(
                expected.TryGetValue(observation.Generation, out Entity[]? children),
                $"Reader observed unpublished hierarchy generation {observation.Generation}.");
            Assert.Equal(children, observation.Children);
        }

        void RecordExpected()
        {
            HierarchyChildrenSnapshot<SceneDomain> view =
                Hierarchy<SceneDomain>.GetChildren(world, parent);
            Assert.True(expected.TryAdd(view.Generation, view.ToArray()));
        }
    }

    [Fact]
    public async Task ConcurrentUnknownDomainReads_DoNotRegisterOrRaceWithKnownDomainRegistration()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();
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
                    HierarchyChildrenSnapshot<NeverRegisteredDomain> view =
                        Hierarchy<NeverRegisteredDomain>.GetChildren(world, parent);
                    Assert.Equal(0UL, view.Generation);
                    Assert.Empty(view);
                }
            }))
            .ToArray();

        start.Set();
        await Task.WhenAll(readers.Append(writer));

        Assert.False(world.Hierarchy.TryDomain<NeverRegisteredDomain>(out _));
        Assert.True(world.Hierarchy.TryDomain<SceneDomain>(out _));
    }

    [Fact]
    public void UnknownDomainScalarReads_DoNotMaterializePersistentDomainState()
    {
        var world = new World();
        Entity entity = world.CreateEntity();

        Assert.False(world.Hierarchy.Any);
        Assert.Equal(Entity.Null, Hierarchy<NeverRegisteredDomain>.GetParent(world, entity));
        Assert.Equal(
            ChildOrderPolicy.Unordered,
            Hierarchy<NeverRegisteredDomain>.GetChildOrderPolicy(world, entity));
        Assert.False(world.Hierarchy.Any);
        Assert.False(world.Hierarchy.TryDomain<NeverRegisteredDomain>(out _));
    }

    [Fact]
    public void RelationshipComponents_BlockPublicMutationSurfaces()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<SceneDomain>.SetParent(world, child, parent);

        Assert.Throws<InvalidOperationException>(
            () => world.Replace(child, new Parent<SceneDomain>(parent)));
        Assert.Throws<InvalidOperationException>(
            () => world.Replace(parent, default(Children<SceneDomain>)));
        Assert.Throws<InvalidOperationException>(() => world.Remove<Children<SceneDomain>>(parent));
    }

    [Fact]
    public void DefaultFacade_RoutesToGenericDefaultDomain()
    {
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();

        DefaultHierarchy.SetParent(world, child, parent);

        Assert.True(world.Has<Parent<DefaultHierarchyDomain>>(child));
        Assert.True(world.Has<Children<DefaultHierarchyDomain>>(parent));
        Assert.Equal(
            Hierarchy<DefaultHierarchyDomain>.GetChildren(world, parent).ToArray(),
            DefaultHierarchy.GetChildren(world, parent).ToArray());
    }

    private static void WriteInvalidSelfParent(World world, Entity child)
    {
        var query = world.Query(
            world.QueryDefinition().ReadWrite<Parent<SceneDomain>>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                if (row.Entity == child)
                    row.ReadWrite<Parent<SceneDomain>>().Value = child;
            }
        });
    }
}
