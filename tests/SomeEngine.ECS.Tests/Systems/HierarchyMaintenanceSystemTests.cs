using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class HierarchyMaintenanceSystemTests : JobTest
{
    [Fact]
    public void CapturedChildrenKeepTheirAppliedValuesAndFreshReadsFollowMaintenance()
    {
        World world = CreateJobWorld();
        Entity oldParent = world.Create(), newParent = world.Create(), child = world.Create();
        Hierarchy<DomainA>.SetParent(world, child, oldParent);
        Entity[] earlier = Children<DomainA>(world, oldParent);
        Jobs.Schedule(new SetParent<DomainA>(world, child, newParent), world.Execution!.StructureWrite).Complete();
        Assert.Equal(newParent, Hierarchy<DomainA>.GetParent(world, child));
        Assert.Equal([child], Children<DomainA>(world, oldParent));
        Assert.Empty(Children<DomainA>(world, newParent));
        var captured = new ChildrenResult();
        var fresh = new ChildrenResult();
        JobHandle copied = Jobs.Schedule(new CopyChildren(earlier, captured));
        JobHandle maintenance = Jobs.Schedule(new Maintain<DomainA>(world), world.Execution.StructureWrite);
        JobHandle read = Jobs.Schedule(new ReadChildren<DomainA>(world, newParent, fresh), world.Execution.StructureRead, maintenance);
        copied.Complete();
        read.Complete();
        Assert.Equal([child], captured.Values);
        Assert.Equal([child], fresh.Values);
        Assert.Empty(Children<DomainA>(world, oldParent));
    }

    [Fact]
    public void StructureWritesSerializeDomainsWithinAWorldAndAllowAnotherWorldToRun()
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
            same = Jobs.Schedule(new RecordMaintenance<DomainB>(first, events, "first maintained"), first.Execution.StructureWrite);
            Jobs.Schedule(new RecordMaintenance<DomainA>(second, events, "second maintained"), second.Execution!.StructureWrite).Complete();
            release.Set();
            same.Complete();
            holding.Complete();
            Assert.Equal(["second maintained", "first write ended", "first maintained"], events);
        }
        finally { release.Set(); holding.Complete(); same.Complete(); }
    }

    [Fact]
    public void ParentMutationRequiresWriteAccessToItsOwnWorld()
    {
        World world = CreateJobWorld(), other = CreateJobWorld();
        Entity parent = world.Create(), child = world.Create();
        var mutation = new SetParent<DomainA>(world, child, parent);
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(mutation).Complete());
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(mutation, other.Execution!.StructureWrite).Complete());
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(mutation, world.Execution!.StructureRead).Complete());
        Assert.Equal(Entity.Null, Hierarchy<DomainA>.GetParent(world, child));
        Jobs.Schedule(mutation, world.Execution!.StructureWrite).Complete();
        Assert.Equal(parent, Hierarchy<DomainA>.GetParent(world, child));
    }

    [Fact]
    public void ParentMutationRejectsAWholeWorldWriteSharedByParallelWorkItems()
    {
        World world = CreateJobWorld();
        Entity parent = world.Create(), child = world.Create();
        JobHandle work = Jobs.ScheduleParallel(new ParallelSetParent(world, child, parent), 2, 1, world.Execution!.StructureWrite);
        Assert.Throws<JobResourceSafetyException>(work.Complete);
        Assert.Equal(Entity.Null, Hierarchy<DomainA>.GetParent(world, child));
    }

    [Fact]
    public void CapturedChildrenCanBeConsumedAfterTheWorldChanges()
    {
        World world = CreateJobWorld();
        Entity first = world.Create(), second = world.Create(), child = world.Create();
        Hierarchy<DomainA>.SetParent(world, child, first);
        Entity[] captured = Children<DomainA>(world, first);
        Hierarchy<DomainA>.SetParent(world, child, second);
        var result = new ChildrenResult();
        Jobs.Schedule(new CopyChildren(captured, result)).Complete();
        Assert.Equal([child], result.Values);
        Assert.Empty(Children<DomainA>(world, first));
        Assert.Equal([child], Children<DomainA>(world, second));
    }

    [Fact]
    public void ReplacingTheDefaultSchedulerKeepsTheWorldsExplicitScheduler()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity parent = world.Create(), child = world.Create();
        int workers = JobSystem.WorkerCount;
        JobSafetyMode safety = JobSystem.SafetyMode;
        ManagedPayloadPolicy payload = JobSystem.ManagedPayloadPolicy;
        try
        {
            Hierarchy<DomainA>.SetParentDeferred(world, child, parent);
            JobSystem.Initialize(new JobConfiguration { WorkerCount = 1 });
            world.Execution!.Scheduler!.Schedule(new Maintain<DomainA>(world), world.Execution.StructureWrite).Complete();
            Assert.Same(scheduler, world.Execution.Scheduler);
            Assert.Equal([child], Children<DomainA>(world, parent));
            JobSystem.Initialize(new JobConfiguration { WorkerCount = 2 });
            world.Execution.Scheduler!.Schedule(new Maintain<DomainA>(world), world.Execution.StructureWrite).Complete();
            Assert.Same(scheduler, world.Execution.Scheduler);
            Assert.Equal([child], Children<DomainA>(world, parent));
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
            Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(new Maintain<DomainA>(world), world.Execution!.StructureWrite));
            Assert.Equal(ManagedPayloadPolicy.Reject, Jobs.ManagedPayloadPolicy);
        }
        finally { Jobs.ManagedPayloadPolicy = ManagedPayloadPolicy.Allow; }
    }

    private static Entity[] Children<T>(World world, Entity parent) where T : struct, IHierarchyDomain
    {
        using ChildrenRead children = Hierarchy<T>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }
    private sealed class ChildrenResult { internal Entity[] Values = []; }
    private readonly struct CopyChildren(Entity[] values, ChildrenResult result) : IJob
    { public void Execute() => result.Values = values.ToArray(); }
    private readonly struct ReadChildren<T>(World world, Entity parent, ChildrenResult result) : IJob where T : struct, IHierarchyDomain
    { public void Execute() => result.Values = Children<T>(world, parent); }
    private readonly struct SetParent<T>(World world, Entity child, Entity parent) : IJob where T : struct, IHierarchyDomain
    { public void Execute() => Hierarchy<T>.SetParentDeferred(world, child, parent); }
    private readonly struct Maintain<T>(World world) : IJob where T : struct, IHierarchyDomain
    { public void Execute() => Hierarchy<T>.Maintain(world); }
    private readonly struct ParallelSetParent(World world, Entity child, Entity parent) : IJobParallelFor
    { public void Execute(int index) => Hierarchy<DomainA>.SetParentDeferred(world, child, parent); }
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
    private readonly struct RecordMaintenance<T>(World world, System.Collections.Concurrent.ConcurrentQueue<string> events, string value) : IJob
        where T : struct, IHierarchyDomain
    { public void Execute() { Hierarchy<T>.Maintain(world); events.Enqueue(value); } }
    private readonly struct DomainA : IHierarchyDomain;
    private readonly struct DomainB : IHierarchyDomain;
}
