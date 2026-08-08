using System.Collections;
using System.Reflection;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Owners;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Registry;
using OwnerHierarchy = SomeEngine.ECS.Owners.Hierarchy;

namespace SomeEngine.ECS.Tests;

public sealed class DetachedRelationshipStateCloneTests
{
    [Fact]
    public void HierarchyCloneDetached_PreservesPendingState_ThenEvolvesInIsolation()
    {
        HierarchyFixture source = CreateHierarchyFixture();
        HierarchyFixture candidate = CreateHierarchyFixture();

        OwnerHierarchy clone = source.World.Hierarchy.CloneDetached();
        AssertHierarchyContainersDetached(source.World.Hierarchy, clone);
        BindHierarchyClone(candidate.World, clone);

        var sourceDomain = source.World.Hierarchy.Domain<CloneDomain>();
        var cloneDomain = clone.Domain<CloneDomain>();
        var sourceAlt = source.World.Hierarchy.Domain<AlternateDomain>();
        var cloneAlt = clone.Domain<AlternateDomain>();

        object sourceBacking = sourceDomain.BackingIdentity;
        object sourceAltBacking = sourceAlt.BackingIdentity;
        Assert.Same(sourceBacking, cloneDomain.BackingIdentity);
        Assert.Same(sourceAltBacking, cloneAlt.BackingIdentity);
        Assert.Equal(0, cloneDomain.DetachCount);
        Assert.Equal(0, cloneAlt.DetachCount);
        AssertHierarchyDomainEquivalent(sourceDomain, cloneDomain, source.AllParents);
        AssertHierarchyDomainEquivalent(sourceAlt, cloneAlt, [source.AlternateParent]);

        sourceDomain.Maintain();
        cloneDomain.Maintain();
        sourceAlt.Maintain();
        cloneAlt.Maintain();

        Assert.NotSame(sourceBacking, sourceDomain.BackingIdentity);
        Assert.NotSame(sourceBacking, cloneDomain.BackingIdentity);
        Assert.NotSame(sourceDomain.BackingIdentity, cloneDomain.BackingIdentity);
        Assert.Equal(1, cloneDomain.DetachCount);
        Assert.NotSame(sourceAltBacking, sourceAlt.BackingIdentity);
        Assert.NotSame(sourceAltBacking, cloneAlt.BackingIdentity);
        Assert.NotSame(sourceAlt.BackingIdentity, cloneAlt.BackingIdentity);
        Assert.Equal(1, cloneAlt.DetachCount);

        AssertHierarchyDomainEquivalent(sourceDomain, cloneDomain, source.AllParents);
        AssertHierarchyDomainEquivalent(sourceAlt, cloneAlt, [source.AlternateParent]);
        Assert.Equal(
            new[] { source.MovingChild, source.SecondChild },
            sourceDomain.GetChildren(source.SecondParent).ToArray());
        Assert.Empty(sourceAlt.GetChildren(source.AlternateParent));

        HierarchyChildrenSnapshot<CloneDomain> pinnedSource =
            sourceDomain.GetChildren(source.SecondParent);
        TopologyOrderDiagnostics sourceDiagnostics = sourceDomain.OrderDiagnostics;

        cloneDomain.Reorder(candidate.SecondChild, insertIndex: 0);

        Assert.Equal(
            new[] { source.MovingChild, source.SecondChild },
            sourceDomain.GetChildren(source.SecondParent).ToArray());
        Assert.Equal(pinnedSource.Generation, sourceDomain.GetChildren(source.SecondParent).Generation);
        Assert.Equal(sourceDiagnostics, sourceDomain.OrderDiagnostics);
        Assert.Equal(
            new[] { candidate.SecondChild, candidate.MovingChild },
            cloneDomain.GetChildren(candidate.SecondParent).ToArray());
        Assert.Equal(
            new[] { source.MovingChild, source.SecondChild },
            pinnedSource.ToArray());
    }

    [Fact]
    public void HierarchyCloneDetached_RejectsActiveEditAndDestroyScratch()
    {
        HierarchyFixture fixture = CreateHierarchyFixture();
        OwnerHierarchy hierarchy = fixture.World.Hierarchy;

        hierarchy.BeginEdit();
        try
        {
            Assert.Throws<InvalidOperationException>(() => hierarchy.CloneDetached());
        }
        finally
        {
            hierarchy.EndEdit();
        }

        hierarchy.BeginTerminalDestroy(Array.Empty<Entity>());
        try
        {
            Assert.Throws<InvalidOperationException>(() => hierarchy.CloneDetached());
        }
        finally
        {
            hierarchy.EndTerminalDestroy();
        }

        var destroying = (HashSet<Entity>)Field(hierarchy, "_destroyingEntities");
        destroying.Add(fixture.FirstParent);
        try
        {
            Assert.Throws<InvalidOperationException>(() => hierarchy.CloneDetached());
        }
        finally
        {
            destroying.Clear();
        }

        _ = hierarchy.CloneDetached();
    }

    [Fact]
    public void RelationGraphCloneDetached_PreservesTrackerPreimageAndGeneration_ThenEvolvesInIsolation()
    {
        RelationFixture source = CreateRelationFixture();
        RelationFixture candidate = CreateRelationFixture();
        RelationGraph? clone = null;

        RewriteEndpointWithCloneCapture(source, () =>
        {
            clone = source.World.RelationGraph.CloneDetached();
            AssertRelationContainersDetached(source.World.RelationGraph, clone);
            Assert.True(TrackerPreimageCount<CloneRelation>(clone) > 0);
        });
        Assert.NotNull(clone);
        Assert.Equal(0, TrackerPreimageCount<CloneRelation>(source.World.RelationGraph));
        object sharedBacking = source.World.RelationGraph.StateBackingIdentity<CloneRelation>()!;
        Assert.Same(sharedBacking, clone!.StateBackingIdentity<CloneRelation>());
        Assert.Equal(0, clone.StateDetachCount<CloneRelation>());

        RewriteEndpointWithCloneCapture(candidate, callback: null);
        BindRelationClone(candidate.World, clone);

        // The clone was taken while the raw owner still held the old endpoint preimage. Replay
        // owner release against the candidate's matching canonical final image.
        clone.ValidateDeferredWrites(candidate.World);
        clone.CommitDeferredWrites();
        Assert.Equal(0, TrackerPreimageCount<CloneRelation>(clone));

        source.World.MaintainRelations<CloneRelation>();
        clone.Maintain<CloneRelation>(candidate.World);

        Assert.NotSame(
            sharedBacking,
            source.World.RelationGraph.StateBackingIdentity<CloneRelation>());
        Assert.NotSame(sharedBacking, clone.StateBackingIdentity<CloneRelation>());
        Assert.NotSame(
            source.World.RelationGraph.StateBackingIdentity<CloneRelation>(),
            clone.StateBackingIdentity<CloneRelation>());
        Assert.Equal(1, clone.StateDetachCount<CloneRelation>());

        AssertRelationEquivalent(
            source.World.RelationGraph,
            source.World,
            clone,
            candidate.World,
            source.Source,
            source.TargetA,
            source.TargetB,
            source.TargetC);

        RelationAdjacencySnapshot<CloneRelation> pinnedSource =
            source.World.RelationGraph.Snapshot<CloneRelation>(
                source.Source,
                RelationAdjacencyRole.Outgoing);
        RelationEdge<CloneRelation>[] sourceOrderBefore = Edges(pinnedSource);
        TopologyOrderDiagnostics sourceDiagnostics =
            source.World.RelationGraph.OrderDiagnostics<CloneRelation>();

        clone.Reorder(
            candidate.World,
            candidate.Source,
            RelationAdjacencyRole.Outgoing,
            candidate.First,
            insertIndex: 0);

        Assert.Equal(
            sourceOrderBefore,
            Edges(source.World.RelationGraph.Snapshot<CloneRelation>(
                source.Source,
                RelationAdjacencyRole.Outgoing)));
        Assert.Equal(sourceDiagnostics, source.World.RelationGraph.OrderDiagnostics<CloneRelation>());
        Assert.Equal(
            new[] { candidate.First, candidate.Second },
            Edges(clone.Snapshot<CloneRelation>(
                candidate.Source,
                RelationAdjacencyRole.Outgoing)));
        Assert.Equal(sourceOrderBefore, Edges(pinnedSource));
    }

    [Fact]
    public void RelationGraphMetadata_PublishesPayloadAndEndpointTrackersByComponentId()
    {
        RelationFixture fixture = CreateRelationFixture();
        RelationGraph source = fixture.World.RelationGraph;
        RelationGraph clone = source.CloneDetached();

        object sourceStateTable = Field(source, "_states");
        object cloneStateTable = Field(clone, "_states");
        var sourceStateSlots = (Array)Field(sourceStateTable, "_slots");
        var cloneStateSlots = (Array)Field(cloneStateTable, "_slots");
        int payloadComponentId = ComponentMetadata<CloneRelation>.Id;

        Assert.NotSame(sourceStateTable, cloneStateTable);
        Assert.NotSame(sourceStateSlots, cloneStateSlots);
        Assert.NotNull(sourceStateSlots.GetValue(payloadComponentId));
        Assert.NotNull(cloneStateSlots.GetValue(payloadComponentId));
        Assert.NotSame(
            sourceStateSlots.GetValue(payloadComponentId),
            cloneStateSlots.GetValue(payloadComponentId));

        object sourceTrackerTable = Field(source, "_endpointTrackers");
        object cloneTrackerTable = Field(clone, "_endpointTrackers");
        var sourceTrackerSlots = (Array)Field(sourceTrackerTable, "_slots");
        var cloneTrackerSlots = (Array)Field(cloneTrackerTable, "_slots");
        int endpointComponentId = ComponentMetadata<DirectedRelationEndpoints<CloneRelation>>.Id;

        Assert.NotSame(sourceTrackerTable, cloneTrackerTable);
        Assert.NotSame(sourceTrackerSlots, cloneTrackerSlots);
        Assert.NotNull(sourceTrackerSlots.GetValue(endpointComponentId));
        Assert.NotNull(cloneTrackerSlots.GetValue(endpointComponentId));
        Assert.NotSame(
            sourceTrackerSlots.GetValue(endpointComponentId),
            cloneTrackerSlots.GetValue(endpointComponentId));
    }

    [Fact]
    public void RelationGraphCloneDetached_RejectsCommandBatchAndDestroyScratch()
    {
        RelationFixture fixture = CreateRelationFixture();
        RelationGraph graph = fixture.World.RelationGraph;

        graph.BeginCommandBatch();
        try
        {
            Assert.Throws<InvalidOperationException>(() => graph.CloneDetached());
        }
        finally
        {
            graph.EndCommandBatch(fixture.World, completed: false);
        }

        var destroying = (HashSet<Entity>)Field(graph, "_destroyingEdges");
        destroying.Add(fixture.First.Entity);
        try
        {
            Assert.Throws<InvalidOperationException>(() => graph.CloneDetached());
        }
        finally
        {
            destroying.Clear();
        }

        _ = graph.CloneDetached();
    }

    private static HierarchyFixture CreateHierarchyFixture()
    {
        var world = new World();
        Entity firstParent = world.CreateEntity();
        Entity secondParent = world.CreateEntity();
        Entity unorderedParent = world.CreateEntity();
        Entity firstChild = world.CreateEntity();
        Entity movingChild = world.CreateEntity();
        Entity secondChild = world.CreateEntity();
        Entity unorderedChild = world.CreateEntity();
        Entity alternateParent = world.CreateEntity();
        Entity alternateChild = world.CreateEntity();

        SomeEngine.ECS.Hierarchy.Hierarchy<CloneDomain>.SetChildOrderPolicy(
            world,
            firstParent,
            ChildOrderPolicy.Ordered);
        SomeEngine.ECS.Hierarchy.Hierarchy<CloneDomain>.SetChildOrderPolicy(
            world,
            secondParent,
            ChildOrderPolicy.Ordered);
        SomeEngine.ECS.Hierarchy.Hierarchy<CloneDomain>.SetParent(world, firstChild, firstParent);
        SomeEngine.ECS.Hierarchy.Hierarchy<CloneDomain>.SetParent(world, movingChild, firstParent);
        SomeEngine.ECS.Hierarchy.Hierarchy<CloneDomain>.SetParent(world, secondChild, secondParent);
        SomeEngine.ECS.Hierarchy.Hierarchy<CloneDomain>.SetParent(world, unorderedChild, unorderedParent);
        SomeEngine.ECS.Hierarchy.Hierarchy<AlternateDomain>.SetParent(
            world,
            alternateChild,
            alternateParent);

        SomeEngine.ECS.Hierarchy.Hierarchy<CloneDomain>.SetParentDeferred(
            world,
            movingChild,
            secondParent,
            insertIndex: 0);
        SomeEngine.ECS.Hierarchy.Hierarchy<AlternateDomain>.DetachDeferred(world, alternateChild);

        HierarchyDomainStore<CloneDomain> domain = world.Hierarchy.Domain<CloneDomain>();
        domain.RequireChildrenNormalization(firstParent);
        domain.RequireScan();
        world.Hierarchy.Domain<AlternateDomain>().RequireScan();

        return new HierarchyFixture(
            world,
            firstParent,
            secondParent,
            unorderedParent,
            firstChild,
            movingChild,
            secondChild,
            unorderedChild,
            alternateParent,
            alternateChild);
    }

    private static RelationFixture CreateRelationFixture()
    {
        var world = new World();
        Entity source = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        Entity targetC = world.CreateEntity();
        Entity uniqueA = world.CreateEntity();
        Entity uniqueB = world.CreateEntity();
        Entity oneToOneA = world.CreateEntity();
        Entity oneToOneB = world.CreateEntity();
        Entity incidentA = world.CreateEntity();
        Entity incidentB = world.CreateEntity();

        world.SetRelationAdjacencyOrder<CloneRelation>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        RelationEdge<CloneRelation> first = world.CreateRelation(
            source,
            targetA,
            new CloneRelation(1));
        RelationEdge<CloneRelation> second = world.CreateRelation(
            source,
            targetB,
            new CloneRelation(2));

        _ = world.CreateRelation(uniqueA, uniqueB, new UniquePairCloneRelation());
        _ = world.CreateRelation(oneToOneA, oneToOneB, new OneToOneCloneRelation());
        _ = world.CreateRelation(incidentA, incidentB, new IncidentCloneRelation());

        return new RelationFixture(
            world,
            source,
            targetA,
            targetB,
            targetC,
            first,
            second);
    }

    private static void RewriteEndpointWithCloneCapture(
        RelationFixture fixture,
        Action? callback)
    {
        var query = fixture.World.Query(
            fixture.World.QueryDefinition()
                .ReadWrite<DirectedRelationEndpoints<CloneRelation>>());
        fixture.World.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                if (row.Entity != fixture.First.Entity)
                    continue;
                row.ReadWrite<DirectedRelationEndpoints<CloneRelation>>().Target = fixture.TargetC;
                callback?.Invoke();
            }
        });
    }

    private static void BindHierarchyClone(World world, OwnerHierarchy hierarchy)
    {
        WorldStructureRoot root = world.PublishedStructureRoot;
        hierarchy.Bind(root.Entities, root.Tables, root.Components, root.Clock);
        root.Components.Bind(
            world,
            root.Entities,
            root.Tables,
            root.Indices,
            root.RelationGraph,
            world.HookStore,
            root.Clock,
            root.Iteration,
            hierarchy);
    }

    private static void BindRelationClone(World world, RelationGraph graph)
    {
        WorldStructureRoot root = world.PublishedStructureRoot;
        root.Components.Bind(
            world,
            root.Entities,
            root.Tables,
            root.Indices,
            graph,
            world.HookStore,
            root.Clock,
            root.Iteration,
            root.Hierarchy);
    }

    private static void AssertHierarchyContainersDetached(
        OwnerHierarchy source,
        OwnerHierarchy clone)
    {
        Assert.NotSame(Field(source, "_domains"), Field(clone, "_domains"));
        Assert.NotSame(Field(source, "_parentComponents"), Field(clone, "_parentComponents"));
        Assert.NotSame(Field(source, "_childrenComponents"), Field(clone, "_childrenComponents"));

        AssertHierarchyStoreContainersShared(
            source.Domain<CloneDomain>(),
            clone.Domain<CloneDomain>());
        AssertHierarchyStoreContainersShared(
            source.Domain<AlternateDomain>(),
            clone.Domain<AlternateDomain>());
    }

    private static void AssertHierarchyStoreContainersShared<TDomain>(
        HierarchyDomainStore<TDomain> source,
        HierarchyDomainStore<TDomain> clone)
        where TDomain : IHierarchyDomain
    {
        string[] mutableFields =
        [
            "_appliedParents",
            "_preimages",
            "_dirty",
            "_pendingPlacements",
            "_policies",
            "_ordered",
            "_unordered",
            "_publishedChildren",
            "_normalizationParents",
        ];
        for (int i = 0; i < mutableFields.Length; i++)
        {
            object sourceValue = Field(source, mutableFields[i]);
            object cloneValue = Field(clone, mutableFields[i]);
            Assert.Same(sourceValue, cloneValue);
            Assert.Equal(CollectionCount(sourceValue), CollectionCount(cloneValue));
        }
        Assert.Same(source.BackingIdentity, clone.BackingIdentity);
        Assert.Equal(0, clone.DetachCount);
        Assert.Same(Field(source, "_orderDiagnostics"), Field(clone, "_orderDiagnostics"));

        string[] scalarFields =
        [
            "_scanNeeded",
            "_normalizeAllChildren",
            "_generation",
            "_deferredSequence",
        ];
        for (int i = 0; i < scalarFields.Length; i++)
            Assert.Equal(Field(source, scalarFields[i]), Field(clone, scalarFields[i]));
        Assert.Equal(source.OrderDiagnostics, clone.OrderDiagnostics);

        var sourceOrdered = (IDictionary)Field(source, "_ordered");
        var cloneOrdered = (IDictionary)Field(clone, "_ordered");
        foreach (DictionaryEntry pair in sourceOrdered)
        {
            object sourceShard = pair.Value!;
            object cloneShard = cloneOrdered[pair.Key]!;
            Assert.Same(sourceShard, cloneShard);
            Assert.Same(Field(sourceShard, "_items"), Field(cloneShard, "_items"));
            Assert.Same(Field(sourceShard, "_indices"), Field(cloneShard, "_indices"));
        }

        var sourceUnordered = (IDictionary)Field(source, "_unordered");
        var cloneUnordered = (IDictionary)Field(clone, "_unordered");
        foreach (DictionaryEntry pair in sourceUnordered)
        {
            object sourceShard = pair.Value!;
            object cloneShard = cloneUnordered[pair.Key]!;
            Assert.Same(sourceShard, cloneShard);
            Assert.Same(Field(sourceShard, "_items"), Field(cloneShard, "_items"));
            Assert.Same(Field(sourceShard, "_indices"), Field(cloneShard, "_indices"));
        }

        var sourcePublished = (IDictionary)Field(source, "_publishedChildren");
        var clonePublished = (IDictionary)Field(clone, "_publishedChildren");
        Assert.Same(sourcePublished, clonePublished);
        foreach (DictionaryEntry pair in sourcePublished)
        {
            var sourceEntry = (PublishedChildren)pair.Value!;
            var cloneEntry = (PublishedChildren)clonePublished[pair.Key]!;
            Assert.Equal(sourceEntry.Memory, cloneEntry.Memory);
            Assert.Equal(sourceEntry.Generation, cloneEntry.Generation);
        }
    }

    private static void AssertHierarchyDomainEquivalent<TDomain>(
        HierarchyDomainStore<TDomain> source,
        HierarchyDomainStore<TDomain> clone,
        IEnumerable<Entity> parents)
        where TDomain : IHierarchyDomain
    {
        Assert.Equal(source.OrderDiagnostics, clone.OrderDiagnostics);
        foreach (Entity parent in parents)
        {
            HierarchyChildrenSnapshot<TDomain> sourceChildren = source.GetChildren(parent);
            HierarchyChildrenSnapshot<TDomain> cloneChildren = clone.GetChildren(parent);
            Assert.Equal(sourceChildren.Generation, cloneChildren.Generation);
            Assert.Equal(sourceChildren.ToArray(), cloneChildren.ToArray());
            Assert.Equal(source.GetOrderPolicy(parent), clone.GetOrderPolicy(parent));
        }
    }

    private static void AssertRelationContainersDetached(RelationGraph source, RelationGraph clone)
    {
        object sourceStateTable = Field(source, "_states");
        object cloneStateTable = Field(clone, "_states");
        var sourceStates = (Array)Invoke(sourceStateTable, "SnapshotValues");
        var cloneStates = (Array)Invoke(cloneStateTable, "SnapshotValues");
        Assert.NotSame(sourceStateTable, cloneStateTable);
        Assert.NotSame(Field(sourceStateTable, "_slots"), Field(cloneStateTable, "_slots"));
        Assert.Equal(sourceStates.Length, cloneStates.Length);

        foreach (object sourceState in sourceStates)
        {
            Type payloadType = (Type)Property(sourceState, "PayloadType");
            object cloneState = StateForPayload(cloneStates, payloadType);
            Assert.NotSame(sourceState, cloneState);
            Assert.NotSame(Field(sourceState, "_dirtyEdges"), Field(cloneState, "_dirtyEdges"));
            Assert.NotSame(
                Field(sourceState, "_pendingPlacements"),
                Field(cloneState, "_pendingPlacements"));
            Assert.NotSame(
                Field(sourceState, "_orderDiagnostics"),
                Field(cloneState, "_orderDiagnostics"));
            Assert.Equal(Field(sourceState, "_dirtySequence"), Field(cloneState, "_dirtySequence"));

            object sourceGeneration = Field(sourceState, "_generation");
            object cloneGeneration = Field(cloneState, "_generation");
            Assert.Same(sourceGeneration, cloneGeneration);
            Assert.Same(
                Property(sourceState, "BackingIdentity"),
                Property(cloneState, "BackingIdentity"));
            Assert.Equal(0, Property(cloneState, "DetachCount"));
            Assert.Equal(Property(sourceGeneration, "Id"), Property(cloneGeneration, "Id"));
        }

        object sourceTrackerTable = Field(source, "_endpointTrackers");
        object cloneTrackerTable = Field(clone, "_endpointTrackers");
        var sourceTrackers = (Array)Field(sourceTrackerTable, "_slots");
        var cloneTrackers = (Array)Field(cloneTrackerTable, "_slots");
        Assert.NotSame(sourceTrackerTable, cloneTrackerTable);
        Assert.NotSame(sourceTrackers, cloneTrackers);
        Assert.Equal(sourceTrackers.Length, cloneTrackers.Length);
        for (int i = 0; i < sourceTrackers.Length; i++)
        {
            object? sourceTracker = sourceTrackers.GetValue(i);
            object? cloneTracker = cloneTrackers.GetValue(i);
            if (sourceTracker is null)
            {
                Assert.Null(cloneTracker);
                continue;
            }

            Assert.NotNull(cloneTracker);
            Assert.NotSame(sourceTracker, cloneTracker);
            object? sourcePreimages = NullableField(sourceTracker, "_preimages");
            object? clonePreimages = NullableField(cloneTracker!, "_preimages");
            if (sourcePreimages is null)
            {
                Assert.Null(clonePreimages);
            }
            else
            {
                Assert.NotSame(sourcePreimages, clonePreimages);
                Assert.NotNull(clonePreimages);
                Assert.Equal(
                    CollectionCount(sourcePreimages),
                    CollectionCount(clonePreimages));
            }
        }
    }

    private static void AssertRelationEquivalent(
        RelationGraph sourceGraph,
        World sourceWorld,
        RelationGraph cloneGraph,
        World cloneWorld,
        params Entity[] endpoints)
    {
        Assert.Equal(
            sourceGraph.OrderDiagnostics<CloneRelation>(),
            cloneGraph.OrderDiagnostics<CloneRelation>());
        RelationAdjacencyRole[] roles =
        [
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyRole.Incoming,
        ];
        foreach (Entity endpoint in endpoints)
        {
            for (int i = 0; i < roles.Length; i++)
            {
                RelationAdjacencySnapshot<CloneRelation> source =
                    sourceGraph.Snapshot<CloneRelation>(endpoint, roles[i]);
                RelationAdjacencySnapshot<CloneRelation> clone =
                    cloneGraph.Snapshot<CloneRelation>(endpoint, roles[i]);
                Assert.Equal(source.Generation, clone.Generation);
                Assert.Equal(source.OrderPolicy, clone.OrderPolicy);
                Assert.Equal(Edges(source), Edges(clone));
                Assert.Equal(
                    source.Entries.ToArray().Select(static entry => entry.OtherEndpoint),
                    clone.Entries.ToArray().Select(static entry => entry.OtherEndpoint));
            }
        }
    }

    private static int TrackerPreimageCount<T>(RelationGraph graph)
        where T : struct, IComponent
    {
        object trackerTable = Field(graph, "_endpointTrackers");
        var trackers = (Array)Field(trackerTable, "_slots");
        for (int i = 0; i < trackers.Length; i++)
        {
            object? tracker = trackers.GetValue(i);
            if (tracker is null)
                continue;
            if ((Type)Property(tracker, "PayloadType") == typeof(T))
            {
                object? preimages = NullableField(tracker, "_preimages");
                return preimages is null ? 0 : CollectionCount(preimages);
            }
        }
        return 0;
    }

    private static object StateForPayload(Array states, Type payloadType)
    {
        foreach (object state in states)
        {
            if ((Type)Property(state, "PayloadType") == payloadType)
                return state;
        }

        throw new InvalidOperationException($"Missing cloned relation state for {payloadType.FullName}.");
    }

    private static RelationEdge<T>[] Edges<T>(RelationAdjacencySnapshot<T> snapshot)
        where T : struct, IComponent =>
        snapshot.Entries.ToArray().Select(static entry => entry.Edge).ToArray();

    private static object Field(object instance, string name) =>
        instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .GetValue(instance)!;

    private static object? NullableField(object instance, string name) =>
        instance.GetType().GetField(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .GetValue(instance);

    private static object Property(object instance, string name) =>
        instance.GetType().GetProperty(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .GetValue(instance)!;

    private static object Invoke(object instance, string name) =>
        instance.GetType().GetMethod(
            name,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!
        .Invoke(instance, null)!;

    private static int CollectionCount(object collection) =>
        (int)collection.GetType().GetProperty("Count")!.GetValue(collection)!;

    private readonly record struct HierarchyFixture(
        World World,
        Entity FirstParent,
        Entity SecondParent,
        Entity UnorderedParent,
        Entity FirstChild,
        Entity MovingChild,
        Entity SecondChild,
        Entity UnorderedChild,
        Entity AlternateParent,
        Entity AlternateChild)
    {
        internal Entity[] AllParents => [FirstParent, SecondParent, UnorderedParent];
    }

    private readonly record struct RelationFixture(
        World World,
        Entity Source,
        Entity TargetA,
        Entity TargetB,
        Entity TargetC,
        RelationEdge<CloneRelation> First,
        RelationEdge<CloneRelation> Second);

    private readonly struct CloneDomain : IHierarchyDomain;

    private readonly struct AlternateDomain : IHierarchyDomain;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private readonly record struct CloneRelation(int Value) : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniquePair)]
    private readonly struct UniquePairCloneRelation : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.OneToOne)]
    private readonly struct OneToOneCloneRelation : IComponent;

    [RelationSchema(RelationDirection.Undirected, RelationCardinality.OneToOne)]
    private readonly struct IncidentCloneRelation : IComponent;
}
