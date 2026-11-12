using System.Collections.Concurrent;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Job;
using ManualResetEventSlim = SomeEngine.Testing.TestSignal;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class TopologyFinalizerAndHierarchyPropagationTests : JobTest
{
    [Fact]
    public void ParallelParentWorkPublishesOneCompletedParentBatch()
    {
        var scene = CreateParents();
        using var arrived = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var state = new ConcurrentWork(arrived, release);
        JobHandle edit = ScheduleParents(scene.World, scene.Query, new BlockingReplace(state, scene.NewParent));
        try
        {
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(new HashSet<Entity> { scene.First, scene.Second }, Children(scene.World, scene.OldParent).ToHashSet());
            Assert.Empty(Children(scene.World, scene.NewParent));
        }
        finally { release.Set(); edit.Complete(); }
        Assert.True(state.Maximum >= 2);
        Assert.Equal(scene.NewParent, Hierarchy<Domain>.GetParent(scene.World, scene.First));
        Assert.Equal(scene.NewParent, Hierarchy<Domain>.GetParent(scene.World, scene.Second));
        Assert.Empty(Children(scene.World, scene.OldParent));
        Assert.Equal(new HashSet<Entity> { scene.First, scene.Second }, Children(scene.World, scene.NewParent).ToHashSet());
    }

    [Fact]
    public void NoopParentWorkPreservesAnInterveningParentWriteAndVersion()
    {
        var scene = CreateParents();
        scene.World.Set(scene.First, new Selected());
        Query query = scene.World.Query(new QueryDescription().With<Parent<Domain>>().With<Selected>());
        Entity competing = scene.World.Create();
        using var arrived = new CountdownEvent(1);
        using var release = new ManualResetEventSlim();
        ulong before = scene.World.Version, edited = before;
        JobHandle edit = ScheduleParents(scene.World, query, new BlockingNoop(new(arrived, release)));
        try
        {
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
            Assert.Equal(before, scene.World.Version);
            Hierarchy<Domain>.SetParent(scene.World, scene.First, competing);
            edited = scene.World.Version;
        }
        finally { release.Set(); edit.Complete(); }
        Assert.Equal(edited, scene.World.Version);
        Assert.Equal(competing, Hierarchy<Domain>.GetParent(scene.World, scene.First));
    }

    [Fact]
    public void ParentUpdatesRemainVisibleAfterInterveningWrites()
    {
        var scene = CreateParents();
        scene.World.Set(scene.First, new Selected());
        Query query = scene.World.Query(new QueryDescription().With<Parent<Domain>>().With<Selected>());
        Entity intermediate = scene.World.Create();
        using var arrived = new CountdownEvent(1);
        using var release = new ManualResetEventSlim();
        JobHandle edit = ScheduleParents(scene.World, query, new BlockingReplace(new(arrived, release), scene.NewParent));
        ulong intervening = 0;
        try
        {
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
            Hierarchy<Domain>.SetParent(scene.World, scene.First, intermediate);
            Hierarchy<Domain>.SetParent(scene.World, scene.First, scene.OldParent);
            intervening = scene.World.Version;
        }
        finally { release.Set(); edit.Complete(); }
        Assert.Equal([scene.First], Entities(scene.World.Query(new QueryDescription().WithChanged<Parent<Domain>>(intervening))));
    }

    [Fact]
    public void ParentWorkFailurePreservesLiveParentsAndChildren()
    {
        var scene = CreateParents();
        JobHandle edit = ScheduleParents(scene.World, scene.Query, new FaultingReplace(scene.NewParent));
        Assert.Throws<ParentWorkException>(edit.Complete);
        Assert.Equal(scene.OldParent, Hierarchy<Domain>.GetParent(scene.World, scene.First));
        Assert.Equal(scene.OldParent, Hierarchy<Domain>.GetParent(scene.World, scene.Second));
        Assert.Equal(new HashSet<Entity> { scene.First, scene.Second }, Children(scene.World, scene.OldParent).ToHashSet());
        Assert.Empty(Children(scene.World, scene.NewParent));
    }

    [Fact]
    public void InvalidParentBatchPreservesEveryParentAndInverseEntry()
    {
        var scene = CreateParents();
        JobHandle edit = ScheduleParents(scene.World, scene.Query, new CycleParents(scene.First, scene.Second));
        Exception failure = Assert.ThrowsAny<Exception>(edit.Complete);
        Assert.Contains(Failures(failure), error => error is ArgumentException);
        Assert.Equal(scene.OldParent, Hierarchy<Domain>.GetParent(scene.World, scene.First));
        Assert.Equal(scene.OldParent, Hierarchy<Domain>.GetParent(scene.World, scene.Second));
        Assert.Equal(new HashSet<Entity> { scene.First, scene.Second }, Children(scene.World, scene.OldParent).ToHashSet());
    }

    [Fact]
    public void FailedDependencyPreventsParentCaptureAndWork()
    {
        var scene = CreateParents();
        int[] visits = [0];
        JobHandle dependency = Jobs.Schedule(new FaultingDependency());
        JobHandle edit = ScheduleParents(scene.World, scene.Query, new CountParentRows(visits), dependency);
        Assert.Throws<ParentDependencyException>(edit.Complete);
        Assert.Equal(0, visits[0]);
        Assert.Equal(scene.OldParent, Hierarchy<Domain>.GetParent(scene.World, scene.First));
    }

    [Fact]
    public void ParentCaptureReadsTheCompletedDependencyValues()
    {
        var scene = CreateParents();
        scene.World.Set(scene.First, new Selected());
        Query query = scene.World.Query(new QueryDescription().With<Parent<Domain>>().With<Selected>());
        Entity dependencyParent = scene.World.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle dependency = Jobs.Schedule(new BlockingParentWrite(scene.World, scene.First, dependencyParent, entered, release),
            ((WorldJobs)scene.World.Execution!).StructureWrite);
        int[] observations = [0];
        JobHandle edit = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            edit = ScheduleParents(scene.World, query, new ObserveAndReplace(dependencyParent, scene.NewParent, observations), dependency);
            Assert.False(edit.IsCompleted);
        }
        finally { release.Set(); edit.Complete(); dependency.Complete(); }
        Assert.Equal(1, observations[0]);
        Assert.Equal(scene.NewParent, Hierarchy<Domain>.GetParent(scene.World, scene.First));
    }

    [Fact]
    public void ChangedParentPreimageRejectsTheCapturedBatch()
    {
        var scene = CreateParents();
        Entity competing = scene.World.Create();
        using var arrived = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        JobHandle edit = ScheduleParents(scene.World, scene.Query, new BlockingReplace(new(arrived, release), scene.NewParent));
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
        try { Hierarchy<Domain>.SetParent(scene.World, scene.First, competing); }
        finally { release.Set(); }
        Exception failure = Assert.ThrowsAny<Exception>(edit.Complete);
        Assert.Contains(Failures(failure), error => error is InvalidOperationException);
        Assert.Equal(competing, Hierarchy<Domain>.GetParent(scene.World, scene.First));
        Assert.Equal(scene.OldParent, Hierarchy<Domain>.GetParent(scene.World, scene.Second));
    }

    [Fact]
    public void UnrelatedParentWriteMergesWithTheCapturedQueryBatch()
    {
        var scene = CreateParents();
        scene.World.Set(scene.First, new Selected());
        Query query = scene.World.Query(new QueryDescription().With<Parent<Domain>>().With<Selected>());
        Entity unrelated = scene.World.Create();
        using var arrived = new CountdownEvent(1);
        using var release = new ManualResetEventSlim();
        JobHandle edit = ScheduleParents(scene.World, query, new BlockingReplace(new(arrived, release), scene.NewParent));
        try
        {
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
            Hierarchy<Domain>.SetParent(scene.World, scene.Second, unrelated);
        }
        finally { release.Set(); edit.Complete(); }
        Assert.Equal(scene.NewParent, Hierarchy<Domain>.GetParent(scene.World, scene.First));
        Assert.Equal(unrelated, Hierarchy<Domain>.GetParent(scene.World, scene.Second));
    }

    [Fact]
    public void EquivalentParentAbaPreservesTheCapturedPreimage()
    {
        var scene = CreateParents();
        scene.World.Set(scene.First, new Selected());
        Query query = scene.World.Query(new QueryDescription().With<Parent<Domain>>().With<Selected>());
        Entity intermediate = scene.World.Create();
        using var arrived = new CountdownEvent(1);
        using var release = new ManualResetEventSlim();
        JobHandle edit = ScheduleParents(scene.World, query, new BlockingReplace(new(arrived, release), scene.NewParent));
        try
        {
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
            Hierarchy<Domain>.SetParent(scene.World, scene.First, intermediate);
            Hierarchy<Domain>.SetParent(scene.World, scene.First, scene.OldParent);
        }
        finally { release.Set(); edit.Complete(); }
        Assert.Equal(scene.NewParent, Hierarchy<Domain>.GetParent(scene.World, scene.First));
    }

    [Fact]
    public void SameCountMembershipReplacementRejectsCapturedParentEdits()
    {
        var scene = CreateParents();
        scene.World.Set(scene.First, new Selected());
        Query query = scene.World.Query(new QueryDescription().With<Parent<Domain>>().With<Selected>());
        using var arrived = new CountdownEvent(1);
        using var release = new ManualResetEventSlim();
        JobHandle edit = ScheduleParents(scene.World, query, new BlockingReplace(new(arrived, release), scene.NewParent));
        Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
        try { scene.World.Remove<Selected>(scene.First); scene.World.Set(scene.Second, new Selected()); }
        finally { release.Set(); }
        Exception failure = Assert.ThrowsAny<Exception>(edit.Complete);
        Assert.Contains(Failures(failure), error => error is InvalidOperationException);
        Assert.Equal(scene.OldParent, Hierarchy<Domain>.GetParent(scene.World, scene.First));
        Assert.Equal(scene.OldParent, Hierarchy<Domain>.GetParent(scene.World, scene.Second));
    }

    [Fact]
    public void PropagationVisitsDisjointRootsAndOrganizationNodesInHierarchyOrder()
    {
        World world = CreateJobWorld();
        Entity orderedRoot = ValueNode(world), orderedChild = ValueNode(world), organization = world.Create();
        Entity leaf = ValueNode(world), unorderedRoot = ValueNode(world), first = ValueNode(world), second = ValueNode(world);
        Hierarchy<Domain>.SetOrdered(world, orderedRoot, true);
        Hierarchy<Domain>.SetParent(world, orderedChild, orderedRoot, 0);
        Hierarchy<Domain>.SetParent(world, organization, orderedRoot, 1);
        Hierarchy<Domain>.SetParent(world, leaf, organization);
        Hierarchy<Domain>.SetParent(world, first, unorderedRoot);
        Hierarchy<Domain>.SetParent(world, second, unorderedRoot);
        using var arrived = new CountdownEvent(2);
        using var release = new ManualResetEventSlim();
        var state = new PropagationRecords(arrived, release);
        var events = new ConcurrentQueue<string>();
        release.WaitCallback = () => events.Enqueue("released");
        using var writerStarted = new ManualResetEventSlim();
        writerStarted.SetCallback = () => events.Enqueue("writer");
        JobHandle propagation = HierarchyPropagation<Domain>.Schedule(world,
            [leaf, orderedChild, orderedRoot, second, unorderedRoot, orderedRoot], new RecordPropagation(state),
            new SystemAccesses().WriteRandom<PropagationValue>(), rootsPerRange: 1);
        JobHandle writer = default;
        try
        {
            Assert.True(arrived.Wait(TimeSpan.FromSeconds(5)));
            writer = Jobs.Schedule(new Signal(writerStarted), ((WorldJobs)world.Execution!).StructureWrite);
        }
        finally { release.Set(); propagation.Complete(); writer.Complete(); }
        Assert.True(state.Maximum >= 2);
        Assert.Equal(new Visit(orderedRoot, 1), state.Visited[organization]);
        Assert.Equal(2, world.ReadComponentData<PropagationValue>(leaf).Depth);
        Assert.Equal(1, world.ReadComponentData<PropagationValue>(orderedChild).Depth);
        Assert.Equal(1, world.ReadComponentData<PropagationValue>(first).Depth);
        Assert.Equal(1, world.ReadComponentData<PropagationValue>(second).Depth);
        Assert.Equal([orderedRoot, orderedChild, organization, leaf], state.Order[orderedRoot].ToArray());
        Assert.Equal(new HashSet<Entity> { unorderedRoot, first, second }, state.Order[unorderedRoot].ToHashSet());
        string[] sequence = events.ToArray();
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "writer"));
    }

    [Fact]
    public void PropagationSchedulingReturnsBeforeItsMaintenanceDependencyCompletes()
    {
        World world = CreateJobWorld();
        Entity root = ValueNode(world);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle blocker = Jobs.Schedule(new WaitForRelease(entered, release));
        JobHandle maintenance = Maintain(world, blocker);
        var state = new PropagationRecords();
        JobHandle propagation = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            propagation = HierarchyPropagation<Domain>.Schedule(world, [root], new RecordPropagation(state),
                new SystemAccesses().WriteRandom<PropagationValue>(), maintenance);
            Assert.False(propagation.IsCompleted);
        }
        finally { release.Set(); propagation.Complete(); blocker.Complete(); }
        Assert.True(state.Visited.ContainsKey(root));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MissingPropagationComponentReportsItsComponentErrorBeforeRangeSafety(bool write)
    {
        World world = CreateJobWorld();
        Entity root = world.Create();
        ulong before = world.Version;
        JobHandle propagation = HierarchyPropagation<Domain>.Schedule(world, [root], new MissingComponent(write),
            new SystemAccesses().WriteRandom<PropagationValue>());
        Assert.Throws<InvalidOperationException>(propagation.Complete);
        Assert.Equal(before, world.Version);
    }

    [Fact]
    public void WritablePropagationReadsExternalAncestorsWithoutChangingThem()
    {
        World world = CreateJobWorld();
        Entity ancestor = world.Create(new PropagationValue { Depth = 73 });
        Entity root = ValueNode(world), child = ValueNode(world);
        Hierarchy<Domain>.SetParent(world, root, ancestor);
        Hierarchy<Domain>.SetParent(world, child, root);
        HierarchyPropagation<Domain>.Schedule(world, [root], new CopyAncestor(ancestor),
            new SystemAccesses().WriteRandom<PropagationValue>()).Complete();
        Assert.Equal(73, world.ReadComponentData<PropagationValue>(root).Depth);
        Assert.Equal(73, world.ReadComponentData<PropagationValue>(child).Depth);
        Assert.Equal(73, world.ReadComponentData<PropagationValue>(ancestor).Depth);
    }

    [Fact]
    public void PropagationWritesShareOneVersionAfterInterveningClockChanges()
    {
        World world = CreateJobWorld();
        Entity[] roots = Enumerable.Range(0, 12).Select(_ => ValueNode(world)).ToArray();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var state = new VersionedWrites(entered, release);
        JobHandle propagation = HierarchyPropagation<Domain>.Schedule(world, roots, new WriteAfterSignal(state),
            new SystemAccesses().WriteRandom<PropagationValue>(), rootsPerRange: 1);
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            world.AcquireSystemVersion(); world.AcquireSystemVersion(); world.AcquireSystemVersion();
        }
        finally { release.Set(); propagation.Complete(); }
        ulong version = world.ComponentDataWriteVersion<PropagationValue>(roots[0]);
        Assert.All(roots, entity =>
        {
            Assert.True(world.ReadComponentData<PropagationValue>(entity).Depth >= 100);
            Assert.Equal(version, world.ComponentDataWriteVersion<PropagationValue>(entity));
        });
    }

    [Fact]
    public void ReadOnlyPropagationPreservesTheWorldVersion()
    {
        World world = CreateJobWorld();
        Entity root = ValueNode(world), child = ValueNode(world);
        Hierarchy<Domain>.SetParent(world, child, root);
        int[] visits = [0];
        ulong before = world.Version;
        HierarchyPropagation<Domain>.Schedule(world, [root], new ReadPropagation(visits),
            new SystemAccesses().ReadRandom<PropagationValue>()).Complete();
        Assert.Equal(2, visits[0]);
        Assert.Equal(before, world.Version);
    }

    [Fact]
    public void UnusedPropagationWriteDeclarationPreservesTheWorldVersion()
    {
        World world = CreateJobWorld();
        Entity root = ValueNode(world), child = ValueNode(world);
        Hierarchy<Domain>.SetParent(world, child, root);
        int[] visits = [0];
        ulong before = world.Version;
        HierarchyPropagation<Domain>.Schedule(world, [root], new CountPropagation(visits),
            new SystemAccesses().WriteRandom<PropagationValue>()).Complete();
        Assert.Equal(2, visits[0]);
        Assert.Equal(before, world.Version);
    }

    [Fact]
    public void EmptyPropagationRootsPreserveTheWorldVersion()
    {
        World world = CreateJobWorld();
        int[] visits = [0];
        ulong before = world.Version;
        HierarchyPropagation<Domain>.Schedule(world, [], new CountPropagation(visits),
            new SystemAccesses().WriteRandom<PropagationValue>()).Complete();
        Assert.Equal(0, visits[0]);
        Assert.Equal(before, world.Version);
    }

    [Fact]
    public void WritablePropagationRejectsReadingAnotherActiveRoot()
    {
        World world = CreateJobWorld();
        Entity first = ValueNode(world), second = ValueNode(world);
        JobHandle propagation = HierarchyPropagation<Domain>.Schedule(world, [first, second],
            new ReadOtherRoot(first, second), new SystemAccesses().WriteRandom<PropagationValue>());
        Assert.Throws<InvalidOperationException>(propagation.Complete);
    }

    [Fact]
    public void ExplicitMaintenanceDependencyDeliversFreshDeferredChildren()
    {
        World world = CreateJobWorld();
        Entity oldParent = world.Create(), newParent = world.Create(), child = ValueNode(world);
        Hierarchy<Domain>.SetParent(world, child, oldParent);
        JobHandle writer = Jobs.Schedule(new ReparentDeferred(world, child, newParent), ((WorldJobs)world.Execution!).StructureWrite);
        JobHandle maintenance = Maintain(world, writer);
        var state = new PropagationRecords();
        HierarchyPropagation<Domain>.Schedule(world, [newParent], new RecordPropagation(state),
            new SystemAccesses().WriteRandom<PropagationValue>(), maintenance).Complete();
        Assert.Equal(newParent, Hierarchy<Domain>.GetParent(world, child));
        Assert.Equal([child], Children(world, newParent));
        Assert.True(state.Visited.ContainsKey(newParent));
        Assert.True(state.Visited.ContainsKey(child));
        Assert.Equal(1, world.ReadComponentData<PropagationValue>(child).Depth);
    }

    [Fact]
    public void PropagationRequiresItsDeclaredComponentReadAccess()
    {
        World world = CreateJobWorld();
        Entity root = ValueNode(world);
        int[] visits = [0];
        JobHandle propagation = HierarchyPropagation<Domain>.Schedule(world, [root], new ReadPropagation(visits), new SystemAccesses());
        Assert.Throws<JobResourceSafetyException>(propagation.Complete);
        Assert.Equal(0, visits[0]);
    }

    [Fact]
    public void DeferredReparentKeepsAppliedChildrenUntilTheNextMaintenance()
    {
        World world = CreateJobWorld();
        Entity oldParent = world.Create(), newParent = world.Create(), child = ValueNode(world);
        Hierarchy<Domain>.SetParent(world, child, oldParent);
        Maintain(world).Complete();
        Hierarchy<Domain>.SetParentDeferred(world, child, newParent);
        Assert.Equal(newParent, Hierarchy<Domain>.GetParent(world, child));
        Assert.Equal([child], Children(world, oldParent));
        Assert.Empty(Children(world, newParent));
        JobHandle maintenance = Maintain(world);
        var state = new PropagationRecords();
        HierarchyPropagation<Domain>.Schedule(world, [newParent], new RecordPropagation(state),
            new SystemAccesses().WriteRandom<PropagationValue>(), maintenance).Complete();
        Assert.Equal([child], Children(world, newParent));
        Assert.Empty(Children(world, oldParent));
        Assert.Equal(1, world.ReadComponentData<PropagationValue>(child).Depth);
    }

    [Fact]
    public void ManagedPropagationStorageIsRejectedBeforeAnyComponentWrite()
    {
        World world = CreateJobWorld();
        Entity root = world.Create(new PropagationValue { Depth = 7 }, new ManagedPropagationValue { Text = "shared" });
        ulong before = world.Version;
        Assert.Throws<InvalidOperationException>(() => HierarchyPropagation<Domain>.Schedule(world, [root],
            new WriteThenManagedRead(), new SystemAccesses().WriteRandom<PropagationValue>().ReadRandom<ManagedPropagationValue>()));
        Assert.Equal(7, world.ReadComponentData<PropagationValue>(root).Depth);
        Assert.Equal(before, world.Version);
    }

    [Fact]
    public void ChangedQueriesObserveCompletedPropagationWrites()
    {
        World world = CreateJobWorld();
        Entity root = ValueNode(world);
        ulong before = world.Version;
        HierarchyPropagation<Domain>.Schedule(world, [root], new SetPropagation(42),
            new SystemAccesses().WriteRandom<PropagationValue>()).Complete();
        Query changed = world.Query(new QueryDescription().WithChanged<PropagationValue>(before));
        Assert.Equal([root], Entities(changed));
        Assert.Equal(42, world.ReadComponentData<PropagationValue>(root).Depth);
    }

    private readonly record struct ParentScene(World World, Entity OldParent, Entity NewParent,
        Entity First, Entity Second, Query Query);

    private ParentScene CreateParents()
    {
        World world = CreateJobWorld();
        Entity oldParent = world.Create(), newParent = world.Create(), first = world.Create(), second = world.Create();
        Hierarchy<Domain>.SetParent(world, first, oldParent);
        Hierarchy<Domain>.SetParent(world, second, oldParent);
        return new(world, oldParent, newParent, first, second, world.Query(new QueryDescription().With<Parent<Domain>>()));
    }

    private static Entity ValueNode(World world) => world.Create(new PropagationValue());
    private static Entity[] Children(World world, Entity parent)
    {
        using ChildrenRead children = Hierarchy<Domain>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }
    private static Entity[] Entities(Query query)
    {
        var entities = new List<Entity>();
        using QueryScope rows = query.OpenRead();
        foreach (QueryChunk chunk in rows)
            foreach (Entity entity in chunk.Entities) entities.Add(entity);
        return [.. entities];
    }
    private static IEnumerable<Exception> Failures(Exception failure)
        => failure is AggregateException aggregate ? aggregate.Flatten().InnerExceptions : [failure];

    private JobHandle Maintain(World world, JobHandle dependency = default)
        => Jobs.Schedule(new MaintainHierarchy(world), ((WorldJobs)world.Execution!).StructureWrite, dependency: dependency);

    private JobHandle ScheduleParents<TWork>(World world, Query query, TWork work, JobHandle dependency = default)
        where TWork : struct, IRangeWork<ParentRow>
    {
        var data = new ParentData(world, query);
        JobHandle captured = Jobs.Schedule(new CaptureParents(data), ((WorldJobs)world.Execution!).CollectReadAccess(query), dependency: dependency);
        return Jobs.Schedule(new ChangeParents<TWork>(Jobs, data, work),
            JobResourceAccess.Write(Jobs.GetResourceToken(data)), dependency: captured);
    }

    private readonly record struct ParentRow(Entity Child, Entity Before, Entity[] Results, int Index);
    private sealed class ParentData(World world, Query query)
    {
        internal readonly World World = world;
        internal readonly Query Query = query;
        internal ParentRow[] Rows = [];
        internal Entity[] Results = [];
        internal CapturedParentEdit[] Changes = [];
        internal int Generation;
        internal ulong Membership;
    }
    private readonly struct CaptureParents(ParentData data) : IJob
    {
        public void Execute()
        {
            var rows = new List<(Entity Entity, Entity Parent)>();
            using QueryScope source = data.Query.OpenRead();
            data.Generation = data.World.DataGeneration;
            data.Membership = data.Query.MembershipVersion;
            foreach (QueryChunk chunk in source)
            {
                ReadOnlySpan<Parent<Domain>> parents = chunk.Read<Parent<Domain>>();
                for (int row = 0; row < chunk.Count; row++) rows.Add((chunk.Entities[row], parents[row].Value));
            }
            data.Results = new Entity[rows.Count];
            data.Rows = new ParentRow[rows.Count];
            for (int index = 0; index < rows.Count; index++)
            {
                var row = rows[index];
                data.Results[index] = row.Parent;
                data.Rows[index] = new(row.Entity, row.Parent, data.Results, index);
            }
        }
    }
    private readonly struct ChangeParents<TWork>(Scheduler scheduler, ParentData data, TWork work) : IJob
        where TWork : struct, IRangeWork<ParentRow>
    {
        public void Execute()
        {
            scheduler.ExecuteRanges(data.Rows, data.Rows.Length, work);
            var changes = new List<CapturedParentEdit>();
            foreach (ParentRow row in data.Rows)
                if (row.Before != row.Results[row.Index]) changes.Add(new(row.Child, row.Before, row.Results[row.Index]));
            if (changes.Count == 0) return;
            data.Changes = changes.ToArray();
            scheduler.Schedule(new ApplyParents(data), ((WorldJobs)data.World.Execution!).StructureWrite);
        }
    }
    private readonly struct ApplyParents(ParentData data) : IJob
    {
        public void Execute()
        {
            using var commands = new CommandBuffer(data.World);
            commands.EditParents<Domain>(data.Query, data.Changes, data.Generation, data.Membership);
            commands.Playback();
        }
    }

    private sealed class ConcurrentWork(CountdownEvent arrived, ManualResetEventSlim release)
    {
        internal int Active, Maximum;
        internal void Enter()
        {
            int active = Interlocked.Increment(ref Active);
            int observed = Volatile.Read(ref Maximum);
            while (active > observed)
            {
                int previous = Interlocked.CompareExchange(ref Maximum, active, observed);
                if (previous == observed) break;
                observed = previous;
            }
            arrived.Signal();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            Interlocked.Decrement(ref Active);
        }
    }
    private readonly struct BlockingReplace(ConcurrentWork state, Entity parent) : IRangeWork<ParentRow>
    {
        public void Execute(in ParentRow row) { row.Results[row.Index] = parent; state.Enter(); }
    }
    private readonly struct BlockingNoop(ConcurrentWork state) : IRangeWork<ParentRow>
    {
        public void Execute(in ParentRow row) => state.Enter();
    }
    private readonly struct FaultingReplace(Entity parent) : IRangeWork<ParentRow>
    {
        public void Execute(in ParentRow row)
        {
            row.Results[row.Index] = parent;
            if (row.Index == 0) throw new ParentWorkException();
        }
    }
    private readonly struct CycleParents(Entity first, Entity second) : IRangeWork<ParentRow>
    {
        public void Execute(in ParentRow row) => row.Results[row.Index] = row.Child == first ? second : first;
    }
    private readonly struct CountParentRows(int[] visits) : IRangeWork<ParentRow>
    {
        public void Execute(in ParentRow row) => Interlocked.Increment(ref visits[0]);
    }
    private readonly struct ObserveAndReplace(Entity expected, Entity replacement, int[] visits) : IRangeWork<ParentRow>
    {
        public void Execute(in ParentRow row)
        {
            Assert.Equal(expected, row.Before);
            Interlocked.Increment(ref visits[0]);
            row.Results[row.Index] = replacement;
        }
    }
    private readonly struct BlockingParentWrite(World world, Entity child, Entity parent,
        ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            Hierarchy<Domain>.SetParent(world, child, parent);
        }
    }
    private readonly struct FaultingDependency : IJob
    {
        public void Execute() => throw new ParentDependencyException();
    }
    private readonly struct MaintainHierarchy(World world) : IJob
    {
        public void Execute() => Hierarchy<Domain>.Maintain(world);
    }
    private readonly struct ReparentDeferred(World world, Entity child, Entity parent) : IJob
    {
        public void Execute() => Hierarchy<Domain>.SetParentDeferred(world, child, parent);
    }
    private readonly struct Signal(ManualResetEventSlim signal) : IJob
    {
        public void Execute() => signal.Set();
    }
    private readonly struct WaitForRelease(ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }
    }

    private readonly record struct Visit(Entity Root, int Depth);
    private sealed class PropagationRecords(CountdownEvent? arrived = null, ManualResetEventSlim? release = null)
    {
        internal readonly ConcurrentDictionary<Entity, Visit> Visited = new();
        internal readonly ConcurrentDictionary<Entity, ConcurrentQueue<Entity>> Order = new();
        private int _active;
        internal int Maximum;
        internal void EnterRoot()
        {
            int active = Interlocked.Increment(ref _active);
            int observed = Volatile.Read(ref Maximum);
            while (active > observed)
            {
                int previous = Interlocked.CompareExchange(ref Maximum, active, observed);
                if (previous == observed) break;
                observed = previous;
            }
            arrived?.Signal();
            if (release is not null && !release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            Interlocked.Decrement(ref _active);
        }
    }
    private readonly struct RecordPropagation(PropagationRecords data) : IHierarchyPropagationJob<Domain>
    {
        public void Execute(ref HierarchyPropagationContext<Domain> context)
        {
            Entity root = context.Entity;
            int depth = 0;
            for (Entity parent = context.Parent; parent != Entity.Null; parent = context.GetParent(parent))
            { root = parent; depth++; }
            if (depth == 0) data.EnterRoot();
            data.Visited[context.Entity] = new(root, depth);
            data.Order.GetOrAdd(root, static _ => new()).Enqueue(context.Entity);
            if (context.Has<PropagationValue>()) context.Write(new PropagationValue { Depth = depth });
        }
    }
    private readonly struct MissingComponent(bool write) : IHierarchyPropagationJob<Domain>
    {
        public void Execute(ref HierarchyPropagationContext<Domain> context)
        {
            Assert.False(context.Has<PropagationValue>());
            if (write) context.Write(new PropagationValue { Depth = 1 });
            else _ = context.Read<PropagationValue>();
        }
    }
    private readonly struct CopyAncestor(Entity ancestor) : IHierarchyPropagationJob<Domain>
    {
        public void Execute(ref HierarchyPropagationContext<Domain> context) => context.Write(context.Read<PropagationValue>(ancestor));
    }
    private sealed class VersionedWrites(ManualResetEventSlim entered, ManualResetEventSlim release)
    {
        internal int First;
        internal void Execute(ref HierarchyPropagationContext<Domain> context)
        {
            context.Write(new PropagationValue { Depth = 100 + context.Entity.Id });
            if (Interlocked.CompareExchange(ref First, 1, 0) == 0) entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
        }
    }
    private readonly struct WriteAfterSignal(VersionedWrites data) : IHierarchyPropagationJob<Domain>
    {
        public void Execute(ref HierarchyPropagationContext<Domain> context) => data.Execute(ref context);
    }
    private readonly struct ReadPropagation(int[] visits) : IHierarchyPropagationJob<Domain>
    {
        public void Execute(ref HierarchyPropagationContext<Domain> context)
        {
            _ = context.Read<PropagationValue>();
            Interlocked.Increment(ref visits[0]);
        }
    }
    private readonly struct CountPropagation(int[] visits) : IHierarchyPropagationJob<Domain>
    {
        public void Execute(ref HierarchyPropagationContext<Domain> context) => Interlocked.Increment(ref visits[0]);
    }
    private readonly struct ReadOtherRoot(Entity first, Entity second) : IHierarchyPropagationJob<Domain>
    {
        public void Execute(ref HierarchyPropagationContext<Domain> context)
        { if (context.Entity == first) _ = context.Read<PropagationValue>(second); }
    }
    private readonly struct WriteThenManagedRead : IHierarchyPropagationJob<Domain>
    {
        public void Execute(ref HierarchyPropagationContext<Domain> context)
        { context.Write(new PropagationValue { Depth = 99 }); _ = context.Read<ManagedPropagationValue>(); }
    }
    private readonly struct SetPropagation(int value) : IHierarchyPropagationJob<Domain>
    {
        public void Execute(ref HierarchyPropagationContext<Domain> context) => context.Write(new PropagationValue { Depth = value });
    }
    private struct PropagationValue : IAddedComponent { internal int Depth; }
    private struct ManagedPropagationValue { internal string? Text; }
    private readonly struct Selected : ITag;
    private readonly struct Domain : IHierarchyDomain;
    private sealed class ParentWorkException : Exception;
    private sealed class ParentDependencyException : Exception;
}
