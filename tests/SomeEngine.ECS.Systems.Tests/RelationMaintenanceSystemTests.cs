using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class RelationMaintenanceSystemTests : JobTest
{
    [Fact]
    public void CapturedIncomingEntriesKeepTheirAppliedValuesAndFreshReadsFollowMaintenance()
    {
        World world = CreateJobWorld();
        Entity source = world.Create(), oldTarget = world.Create(), newTarget = world.Create();
        RelationEdge<DirectedA> edge = world.CreateRelation(source, oldTarget, default(DirectedA));
        Entity[] earlier = Incoming<DirectedA>(world, oldTarget);
        Jobs.Schedule(new Retarget(world, edge, source, newTarget), world.Execution!.StructureWrite).Complete();
        Assert.Equal(newTarget, world.ReadComponentData<RelationEndpoints<DirectedA>>(edge.Entity).Target);
        Assert.Equal([edge.Entity], Incoming<DirectedA>(world, oldTarget));
        Assert.Empty(Incoming<DirectedA>(world, newTarget));
        var relaxed = new EdgeResult();
        var fresh = new EdgeResult();
        JobHandle copied = Jobs.Schedule(new DeliverEdges(earlier, relaxed));
        JobHandle maintained = Jobs.Schedule(new Maintain<DirectedA>(world), world.Execution.StructureWrite);
        Jobs.Schedule(new ReadIncoming(world, newTarget, fresh), world.Execution.StructureRead, maintained).Complete();
        copied.Complete();
        Assert.Equal([edge.Entity], relaxed.Values);
        Assert.Equal([edge.Entity], fresh.Values);
        Assert.Empty(Incoming<DirectedA>(world, oldTarget));
    }

    [Fact]
    public void CapturedAdjacencyCanBeReadWhileTheNextMaintenanceFinishes()
    {
        World world = CreateJobWorld();
        Entity source = world.Create(), oldTarget = world.Create(), newTarget = world.Create();
        RelationEdge<DirectedA> edge = world.CreateRelation(source, oldTarget, default(DirectedA));
        Entity[] earlier = Incoming<DirectedA>(world, oldTarget);
        world.RetargetRelationDeferred(edge, source, newTarget);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var result = new EdgeResult();
        JobHandle reading = Jobs.Schedule(new HoldEdges(earlier, result, entered, release));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Jobs.Schedule(new Maintain<DirectedA>(world), world.Execution!.StructureWrite).Complete();
            Assert.Empty(Incoming<DirectedA>(world, oldTarget));
            Assert.Equal([edge.Entity], Incoming<DirectedA>(world, newTarget));
            release.Set();
            reading.Complete();
            Assert.Equal([edge.Entity], result.Values);
        }
        finally { release.Set(); reading.Complete(); }
    }

    [Fact]
    public void StructureWritesSerializePayloadsWithinAWorldAndAllowAnotherWorldToRun()
    {
        World first = CreateJobWorld(), second = CreateJobWorld();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        JobHandle holding = Jobs.Schedule(new Hold(entered, release, events), first.Execution!.StructureWrite);
        JobHandle same = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            same = Jobs.Schedule(new RecordMaintenance<DirectedB>(first, events, "first maintained"), first.Execution.StructureWrite);
            Jobs.Schedule(new RecordMaintenance<DirectedA>(second, events, "second maintained"), second.Execution!.StructureWrite).Complete();
            release.Set();
            same.Complete();
            holding.Complete();
            Assert.Equal(["second maintained", "first write ended", "first maintained"], events);
        }
        finally { release.Set(); holding.Complete(); same.Complete(); }
    }

    [Fact]
    public void EndpointMutationRequiresWriteAccessToItsOwnWorld()
    {
        World world = CreateJobWorld(), other = CreateJobWorld();
        Entity source = world.Create(), oldTarget = world.Create(), newTarget = world.Create();
        RelationEdge<DirectedA> edge = world.CreateRelation(source, oldTarget, default(DirectedA));
        var mutation = new Retarget(world, edge, source, newTarget);
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(mutation).Complete());
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(mutation, other.Execution!.StructureWrite).Complete());
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(mutation, world.Execution!.StructureRead).Complete());
        Assert.Equal(oldTarget, world.ReadComponentData<RelationEndpoints<DirectedA>>(edge.Entity).Target);
        Jobs.Schedule(mutation, world.Execution!.StructureWrite).Complete();
        Assert.Equal(newTarget, world.ReadComponentData<RelationEndpoints<DirectedA>>(edge.Entity).Target);
    }

    [Fact]
    public void EndpointMutationRejectsAWholeWorldWriteSharedByParallelWorkItems()
    {
        World world = CreateJobWorld();
        Entity source = world.Create(), oldTarget = world.Create(), newTarget = world.Create();
        RelationEdge<DirectedA> edge = world.CreateRelation(source, oldTarget, default(DirectedA));
        JobHandle work = Jobs.ScheduleParallel(new ParallelRetarget(world, edge, source, newTarget), 2, 1, world.Execution!.StructureWrite);
        Assert.Throws<JobResourceSafetyException>(work.Complete);
        Assert.Equal(oldTarget, world.ReadComponentData<RelationEndpoints<DirectedA>>(edge.Entity).Target);
    }

    [Fact]
    public void CapturedIncomingEntriesCanBeConsumedAfterTheWorldChanges()
    {
        World world = CreateJobWorld();
        Entity source = world.Create(), first = world.Create(), second = world.Create();
        RelationEdge<DirectedA> edge = world.CreateRelation(source, first, default(DirectedA));
        Entity[] captured = Incoming<DirectedA>(world, first);
        world.RetargetRelationImmediate(edge, source, second);
        var result = new EdgeResult();
        Jobs.Schedule(new DeliverEdges(captured, result)).Complete();
        Assert.Equal([edge.Entity], result.Values);
        Assert.Empty(Incoming<DirectedA>(world, first));
        Assert.Equal([edge.Entity], Incoming<DirectedA>(world, second));
    }

    [Fact]
    public void ReplacingTheDefaultSchedulerKeepsTheWorldsExplicitScheduler()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity source = world.Create(), target = world.Create();
        Span<RelationEdge<DirectedA>> created = stackalloc RelationEdge<DirectedA>[1];
        Relations<DirectedA>.Edit(world, [EndpointEdit.Create(source, target)], created, deferred: true);
        int workers = JobSystem.WorkerCount;
        JobSafetyMode safety = JobSystem.SafetyMode;
        ManagedPayloadPolicy payload = JobSystem.ManagedPayloadPolicy;
        try
        {
            JobSystem.Initialize(new JobConfiguration { WorkerCount = 1 });
            world.Execution!.Scheduler!.Schedule(new Maintain<DirectedA>(world), world.Execution.StructureWrite).Complete();
            Assert.Same(scheduler, world.Execution.Scheduler);
            Assert.Equal([created[0].Entity], Incoming<DirectedA>(world, target));
            JobSystem.Initialize(new JobConfiguration { WorkerCount = 2 });
            world.Execution.Scheduler!.Schedule(new Maintain<DirectedA>(world), world.Execution.StructureWrite).Complete();
            Assert.Same(scheduler, world.Execution.Scheduler);
            Assert.Equal([created[0].Entity], Incoming<DirectedA>(world, target));
        }
        finally { JobSystem.Initialize(new JobConfiguration { WorkerCount = workers, SafetyMode = safety, ManagedPayloadPolicy = payload }); }
    }

    [Fact]
    public void TheManagedPayloadPolicyAppliesToConcreteMaintenanceJobs()
    {
        World world = CreateJobWorld();
        Jobs.ManagedPayloadPolicy = ManagedPayloadPolicy.Reject;
        try
        {
            Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(new Maintain<DirectedA>(world), world.Execution!.StructureWrite));
            Assert.Equal(ManagedPayloadPolicy.Reject, Jobs.ManagedPayloadPolicy);
        }
        finally { Jobs.ManagedPayloadPolicy = ManagedPayloadPolicy.Allow; }
    }

    private static Entity[] Incoming<T>(World world, Entity target) where T : struct
    {
        using RelationRead entries = Relations<T>.Incoming(world, target);
        Entity[] values = new Entity[entries.Count];
        for (int index = 0; index < values.Length; index++) values[index] = entries.Entries[index].Edge;
        return values;
    }
    private sealed class EdgeResult { internal Entity[] Values = []; }
    private readonly struct DeliverEdges(Entity[] values, EdgeResult result) : IJob
    { public void Execute() => result.Values = values; }
    private readonly struct ReadIncoming(World world, Entity target, EdgeResult result) : IJob
    { public void Execute() => result.Values = Incoming<DirectedA>(world, target); }
    private readonly struct Maintain<T>(World world) : IJob where T : struct
    { public void Execute() => Relations<T>.Maintain(world); }
    private readonly struct Retarget(World world, RelationEdge<DirectedA> edge, Entity source, Entity target) : IJob
    { public void Execute() => Relations<DirectedA>.RetargetDeferred(world, edge, source, target); }
    private readonly struct ParallelRetarget(World world, RelationEdge<DirectedA> edge, Entity source, Entity target) : IJobParallelFor
    { public void Execute(int index) => Relations<DirectedA>.RetargetDeferred(world, edge, source, target); }
    private readonly struct HoldEdges(Entity[] values, EdgeResult result, ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            result.Values = values;
        }
    }
    private readonly struct Hold(ManualResetEventSlim entered, ManualResetEventSlim release,
        System.Collections.Concurrent.ConcurrentQueue<string> events) : IJob
    {
        public void Execute()
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            events.Enqueue("first write ended");
        }
    }
    private readonly struct RecordMaintenance<T>(World world, System.Collections.Concurrent.ConcurrentQueue<string> events, string value) : IJob where T : struct
    { public void Execute() { Relations<T>.Maintain(world); events.Enqueue(value); } }
    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)] private struct DirectedA : IComponent;
    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)] private struct DirectedB : IComponent;
}
