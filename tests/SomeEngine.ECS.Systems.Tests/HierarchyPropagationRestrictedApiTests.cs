using System.Collections.Concurrent;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class HierarchyPropagationRestrictedApiTests : JobTest
{
    [Fact]
    public void ExplicitSuccessorRunsAfterMaintenanceAndPropagation()
    {
        World world = CreateJobWorld();
        var jobs = (WorldJobs)world.Execution!;
        Entity root = world.Create(new EscapeValue());
        var stages = new ConcurrentQueue<string>();
        using var blockerStarted = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();
        JobHandle blocker = Jobs.Schedule(new BlockingJob(blockerStarted, releaseBlocker, stages));
        Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(3)));
        JobHandle successor;
        try
        {
            JobHandle maintenance = Jobs.Schedule(new MaintainParents(world, stages), jobs.StructureWrite,
                dependency: blocker);
            JobHandle propagation = HierarchyPropagation<EscapeDomain>.Schedule(world, [root],
                new RecordPropagation(stages), new SystemAccesses(), maintenance);
            successor = Jobs.Schedule(new RecordSuccessor(stages), jobs.StructureWrite, dependency: propagation);
        }
        finally { releaseBlocker.Set(); }
        successor.Complete();
        Assert.Equal(["blocker", "maintenance", "propagation", "successor"], stages.ToArray());
    }

    [Fact]
    public void WritableRootReadRejectsAnotherPropagationRootBeforeWriting()
    {
        World world = CreateJobWorld();
        Entity first = world.Create(new EscapeValue { Value = 11 });
        Entity second = world.Create(new EscapeValue { Value = 22 });
        ulong before = world.Version;
        JobHandle propagation = HierarchyPropagation<EscapeDomain>.Schedule(world, [first, second],
            new ReadOtherRoot(first, second), new SystemAccesses().WriteRandom<EscapeValue>());
        Assert.Throws<InvalidOperationException>(propagation.Complete);
        Assert.Equal(11, world.ReadComponentData<EscapeValue>(first).Value);
        Assert.Equal(22, world.ReadComponentData<EscapeValue>(second).Value);
        Assert.Equal(before, world.Version);
    }

    [Fact]
    public void OrdinaryWorldWriteRequiresSingleWorkItemAccessDuringParallelPropagation()
    {
        World world = CreateJobWorld();
        Entity first = world.Create(new EscapeValue { Value = 11 });
        Entity second = world.Create(new EscapeValue { Value = 22 });
        ulong before = world.Version;
        JobHandle propagation = HierarchyPropagation<EscapeDomain>.Schedule(world, [first, second],
            new WriteOtherRoot(world, first, second), new SystemAccesses().WriteRandom<EscapeValue>());
        Assert.Throws<JobResourceSafetyException>(propagation.Complete);
        Assert.Equal(11, world.ReadComponentData<EscapeValue>(first).Value);
        Assert.Equal(22, world.ReadComponentData<EscapeValue>(second).Value);
        Assert.Equal(before, world.Version);
    }

    [Fact]
    public void PrebuiltQueryRequiresItsComponentReadDeclaration()
    {
        World world = CreateJobWorld();
        Entity root = world.Create(new EscapeValue { Value = 11 }, new OtherValue { Value = 22 });
        Query query = world.Query(new QueryDescription().With<OtherValue>());
        var result = new PropagationResult();
        JobHandle propagation = HierarchyPropagation<EscapeDomain>.Schedule(world, [root],
            new ReadUndeclaredQuery(query, result), new SystemAccesses().ReadRandom<EscapeValue>());
        Assert.Throws<JobResourceSafetyException>(propagation.Complete);
        Assert.Empty(result.Entities);
        Assert.Equal(22, world.ReadComponentData<OtherValue>(root).Value);
    }

    [Fact]
    public void PropagationRecordsCommandsForPlaybackAfterItsReadScopeEnds()
    {
        World world = CreateJobWorld();
        Entity first = world.Create(new EscapeValue { Value = 11 });
        Entity second = world.Create(new EscapeValue { Value = 22 });
        using var commands = new CommandBuffer(world);
        JobHandle propagation = HierarchyPropagation<EscapeDomain>.Schedule(world, [first, second],
            new RecordCommands(commands, first, second), new SystemAccesses());
        propagation.Complete();
        Assert.Equal(22, world.ReadComponentData<EscapeValue>(second).Value);
        Assert.Equal(1, commands.Count);
        commands.Playback();
        Assert.Equal(999, world.ReadComponentData<EscapeValue>(second).Value);
    }

    [Fact]
    public void PropagationStructureReadCanReadAnotherRootsChildren()
    {
        World world = CreateJobWorld();
        Entity first = world.Create(new EscapeValue { Value = 11 });
        Entity second = world.Create(new EscapeValue { Value = 22 });
        Entity child = world.Create(new EscapeValue { Value = 33 });
        Hierarchy<EscapeDomain>.SetParent(world, child, second);
        var result = new PropagationResult();
        JobHandle propagation = HierarchyPropagation<EscapeDomain>.Schedule(world, [first],
            new ReadChildren(world, second, result), new SystemAccesses());
        propagation.Complete();
        Assert.Equal([child], result.Entities);
        Assert.Equal(second, Hierarchy<EscapeDomain>.GetParent(world, child));
    }

    private readonly struct ReadOtherRoot(Entity trigger, Entity other) : IHierarchyPropagationJob<EscapeDomain>
    {
        public void Execute(ref HierarchyPropagationContext<EscapeDomain> context)
        {
            if (context.Entity == trigger) _ = context.Read<EscapeValue>(other);
        }
    }

    private readonly struct WriteOtherRoot(World world, Entity trigger, Entity other) : IHierarchyPropagationJob<EscapeDomain>
    {
        public void Execute(ref HierarchyPropagationContext<EscapeDomain> context)
        {
            if (context.Entity == trigger) world.Set(other, new EscapeValue { Value = 999 });
        }
    }

    private readonly struct ReadUndeclaredQuery(Query query, PropagationResult result) : IHierarchyPropagationJob<EscapeDomain>
    {
        public void Execute(ref HierarchyPropagationContext<EscapeDomain> context)
        {
            using QueryScope rows = query.OpenRead();
            foreach (QueryChunk chunk in rows) result.Entities = chunk.Entities.ToArray();
        }
    }

    private readonly struct RecordCommands(CommandBuffer commands, Entity trigger, Entity target) : IHierarchyPropagationJob<EscapeDomain>
    {
        public void Execute(ref HierarchyPropagationContext<EscapeDomain> context)
        {
            if (context.Entity == trigger) commands.Set(target, new EscapeValue { Value = 999 });
        }
    }

    private readonly struct ReadChildren(World world, Entity parent, PropagationResult result) : IHierarchyPropagationJob<EscapeDomain>
    {
        public void Execute(ref HierarchyPropagationContext<EscapeDomain> context)
        {
            using ChildrenRead children = Hierarchy<EscapeDomain>.GetChildren(world, parent);
            result.Entities = children.Entities.ToArray();
        }
    }

    private readonly struct BlockingJob(ManualResetEventSlim started, ManualResetEventSlim release,
        ConcurrentQueue<string> stages) : IJob
    {
        public void Execute()
        {
            started.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            stages.Enqueue("blocker");
        }
    }

    private readonly struct MaintainParents(World world, ConcurrentQueue<string> stages) : IJob
    {
        public void Execute()
        {
            Hierarchy<EscapeDomain>.Maintain(world);
            stages.Enqueue("maintenance");
        }
    }

    private readonly struct RecordPropagation(ConcurrentQueue<string> stages) : IHierarchyPropagationJob<EscapeDomain>
    {
        public void Execute(ref HierarchyPropagationContext<EscapeDomain> context) => stages.Enqueue("propagation");
    }

    private readonly struct RecordSuccessor(ConcurrentQueue<string> stages) : IJob
    {
        public void Execute() => stages.Enqueue("successor");
    }

    private sealed class PropagationResult
    {
        internal Entity[] Entities = [];
    }

    private struct EscapeValue : ITrackedComponent
    {
        public int Value;
    }

    private struct OtherValue : IComponent
    {
        public int Value;
    }

    private readonly struct EscapeDomain : IHierarchyDomain;
}
