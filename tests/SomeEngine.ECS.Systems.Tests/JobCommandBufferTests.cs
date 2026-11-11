using System.Collections.Concurrent;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.Job;
using ManualResetEventSlim = SomeEngine.Testing.TestSignal;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class JobCommandBufferTests : JobTest
{
    [Fact]
    public void ParallelProducersPublishEntitiesInProducerIndexOrder()
    {
        World world = CreateJobWorld();
        using var commands = new CommandBatch(world, 32);
        Jobs.ExecuteParallel(new CreateByIndex(commands), commands.ProducerCount, 1);
        Jobs.Schedule(new PlayCommands(commands), ((WorldJobs)world.Execution!).StructureWrite).Complete();

        var rows = new List<(Entity Entity, int Value)>();
        using QueryScope source = world.Query(new QueryDescription().With<JobCommandValue>()).OpenRead();
        foreach (QueryChunk chunk in source)
        {
            ReadOnlySpan<JobCommandValue> values = chunk.Read<JobCommandValue>();
            for (int row = 0; row < chunk.Count; row++) rows.Add((chunk.Entities[row], values[row].Value));
        }
        Assert.Equal(32, rows.Count);
        Assert.Equal(Enumerable.Range(0, 32), rows.OrderBy(row => row.Entity.Id).Select(row => row.Value));
    }

    [Fact]
    public void RelationshipProducersPublishEveryEdgeAndDeferredResult()
    {
        const int Count = 1024;
        World world = CreateJobWorld();
        Entity source = world.Create();
        Entity[] targets = Enumerable.Range(0, Count).Select(_ => world.Create()).ToArray();
        var created = new DeferredEntity[Count];
        using var commands = new CommandBatch(world, Count);
        Jobs.ExecuteParallel(new CreateRelations(commands, source, targets, created), Count, 16);
        Jobs.Schedule(new PlayCommands(commands), ((WorldJobs)world.Execution!).StructureWrite).Complete();

        using RelationRead outgoing = Relations<JobCommandLink>.Outgoing(world, source);
        Assert.Equal(Count, outgoing.Entries.Length);
        Assert.All(created, edge => Assert.True(edge.TryResolve(out _)));
        Assert.Equal(Enumerable.Range(0, Count), outgoing.Entries.ToArray()
            .Select(edge => world.ReadComponentData<JobCommandLink>(edge.Edge).Value).Order());
    }

    [Fact]
    public void ProducerEndpointEditsValidateTheCombinedUniqueTargetSwap()
    {
        World world = CreateJobWorld();
        Entity sourceA = world.Create(), sourceB = world.Create();
        Entity targetA = world.Create(), targetB = world.Create();
        RelationEdge<JobCommandUniqueTarget> edgeA = Relations<JobCommandUniqueTarget>.CreateEdge(world, sourceA, targetA, default);
        RelationEdge<JobCommandUniqueTarget> edgeB = Relations<JobCommandUniqueTarget>.CreateEdge(world, sourceB, targetB, default);
        var edits = new EndpointCommandEdit[2];
        Jobs.ExecuteParallel(new SwapTargets(edits, edgeA.Entity, edgeB.Entity, sourceA, sourceB, targetA, targetB), 2, 1);
        using var commands = new CommandBatch(world, 1);
        commands[0].EditEndpoints<JobCommandUniqueTarget>(edits, deferred: true);
        Jobs.Schedule(new PlayCommands(commands), ((WorldJobs)world.Execution!).StructureWrite).Complete();

        Assert.Equal(targetB, world.ReadComponentData<RelationEndpoints<JobCommandUniqueTarget>>(edgeA.Entity).Target);
        Assert.Equal(targetA, world.ReadComponentData<RelationEndpoints<JobCommandUniqueTarget>>(edgeB.Entity).Target);
        using (RelationRead incoming = Relations<JobCommandUniqueTarget>.Incoming(world, targetA))
            Assert.Equal(edgeA.Entity, Assert.Single(incoming.Entries.ToArray()).Edge);
        Relations<JobCommandUniqueTarget>.Maintain(world);
        using (RelationRead incoming = Relations<JobCommandUniqueTarget>.Incoming(world, targetB))
            Assert.Equal(edgeA.Entity, Assert.Single(incoming.Entries.ToArray()).Edge);
        using (RelationRead incoming = Relations<JobCommandUniqueTarget>.Incoming(world, targetA))
            Assert.Equal(edgeB.Entity, Assert.Single(incoming.Entries.ToArray()).Edge);
    }

    [Fact]
    public void FailedCandidateRelationshipsKeepTheSourceWorldUnchanged()
    {
        World world = CreateJobWorld();
        Entity sourceA = world.Create(), sourceB = world.Create(), target = world.Create();
        int before = world.EntityCount;
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World(execution: new WorldJobs(Jobs));
        checkpoint.Restore(candidate);
        using var commands = new CommandBatch(candidate, 2);
        var created = new DeferredEntity[2];
        commands[0].EditEndpoints<JobCommandUniqueTarget>([EndpointCommandEdit.Create(sourceA, target)], created.AsSpan(0, 1));
        commands[1].EditEndpoints<JobCommandUniqueTarget>([EndpointCommandEdit.Create(sourceB, target)], created.AsSpan(1, 1));

        Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(new PlayCommands(commands),
            ((WorldJobs)candidate.Execution!).StructureWrite).Complete());

        Assert.Equal(before, world.EntityCount);
        using (RelationRead outgoing = Relations<JobCommandUniqueTarget>.Outgoing(world, sourceA)) Assert.Empty(outgoing.Entries.ToArray());
        using (RelationRead outgoing = Relations<JobCommandUniqueTarget>.Outgoing(world, sourceB)) Assert.Empty(outgoing.Entries.ToArray());
        using (RelationRead incoming = Relations<JobCommandUniqueTarget>.Incoming(world, target)) Assert.Empty(incoming.Entries.ToArray());
    }

    [Fact]
    public void ProducerFailureDiscardsEveryRecordedSegmentBeforePlayback()
    {
        World world = CreateJobWorld();
        using var commands = new CommandBatch(world, 8);
        var created = new DeferredEntity[8];
        JobHandle producer = Jobs.ScheduleParallel(new FaultingCreate(commands, created, 3), 8, 1);
        JobHandle playback = Jobs.Schedule(new PlayCommands(commands), ((WorldJobs)world.Execution!).StructureWrite, dependency: producer);
        int[] cleaned = [0];
        JobHandle cleanup = Jobs.ScheduleFinally(new ClearCommands(commands, playback.GetAwaiter(), cleaned), default, playback);

        Assert.ThrowsAny<Exception>(cleanup.Complete);

        Assert.Equal(1, cleaned[0]);
        Assert.Equal(0, world.EntityCount);
        Assert.All(created, entity => Assert.False(entity.TryResolve(out _)));
    }

    [Fact]
    public void FailedCandidatePlaybackPreservesTheSourceAndItsMembershipLog()
    {
        World world = CreateJobWorld();
        Entity original = world.Create();
        Query members = world.Query(new QueryDescription().With<JobCommandValue>());
        using EventReader<MembershipChange> changes = world.WatchMembership(members);
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World(execution: new WorldJobs(Jobs));
        checkpoint.Restore(candidate);
        using var commands = new CommandBatch(candidate, 2);
        DeferredEntity created = commands[0].Create(new JobCommandValue { Value = 7 });
        commands[1].Set(Entity.Null, new JobCommandValue { Value = 9 });

        Assert.Throws<ArgumentException>(() => Jobs.Schedule(new PlayCommands(commands),
            ((WorldJobs)candidate.Execution!).StructureWrite).Complete());

        Assert.Equal(1, world.EntityCount);
        Assert.True(world.IsAlive(original));
        Assert.True(created.TryResolve(out Entity candidateEntity));
        Assert.False(world.IsAlive(candidateEntity));
        using EventRead<MembershipChange> read = changes.Open();
        Assert.Equal(0, read.Count);
    }

    [Fact]
    public void EmptyProducerBatchPreservesTheWorldVersion()
    {
        World world = CreateJobWorld();
        ulong before = world.Version;
        using var commands = new CommandBatch(world, 1);
        Jobs.Schedule(new PlayCommands(commands), ((WorldJobs)world.Execution!).StructureWrite).Complete();
        Assert.Equal(0, world.EntityCount);
        Assert.Equal(before, world.Version);
    }

    [Fact]
    public void CommandProducerCannotMutateWorldWithoutDeclaredStructureAccess()
    {
        World world = CreateJobWorld();
        JobHandle producer = Jobs.Schedule(new CreateWithoutStructureAccess(world));
        Assert.Throws<JobResourceSafetyException>(producer.Complete);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void CommandProducerCannotPlayItsBatchWithoutDeclaredStructureAccess()
    {
        World world = CreateJobWorld();
        using var commands = new CommandBatch(world, 1);
        commands[0].Create(new JobCommandValue { Value = 7 });
        JobHandle producer = Jobs.Schedule(new PlayCommands(commands));
        Assert.Throws<JobResourceSafetyException>(producer.Complete);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void PlaybackDependencyIncludesCommandsRecordedByProducerChildren()
    {
        World world = CreateJobWorld();
        using var commands = new CommandBatch(world, 1);
        var created = new DeferredEntity[1];
        JobHandle producer = Jobs.Schedule(new StartChildProducer(Jobs, commands[0], created));
        Jobs.Schedule(new PlayCommands(commands), ((WorldJobs)world.Execution!).StructureWrite, dependency: producer).Complete();
        Assert.True(created[0].TryResolve(out Entity entity));
        Assert.Equal(7, world.ReadComponentData<JobCommandValue>(entity).Value);
    }

    [Fact]
    public void FaultedProducerDependencyStillClearsEverySegment()
    {
        World world = CreateJobWorld();
        using var commands = new CommandBatch(world, 1);
        commands[0].Create(new JobCommandValue());
        JobHandle dependency = Jobs.Schedule(new ThrowingDependency());
        JobHandle playback = Jobs.Schedule(new PlayCommands(commands), ((WorldJobs)world.Execution!).StructureWrite, dependency: dependency);
        int[] cleaned = [0];
        JobHandle cleanup = Jobs.ScheduleFinally(new ClearCommands(commands, playback.GetAwaiter(), cleaned), default, playback);
        Assert.Throws<ProducerDependencyException>(cleanup.Complete);
        Assert.Equal(1, cleaned[0]);
        Assert.Equal(0, commands[0].Count);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void FaultedStructurePredecessorRunsCleanupAndPreservesTheSource()
    {
        World world = CreateJobWorld();
        using var commands = new CommandBatch(world, 1);
        JobHandle predecessor = Jobs.Schedule(new ThrowingDependency(), ((WorldJobs)world.Execution!).StructureWrite);
        JobHandle playback = Jobs.Schedule(new PlayCommands(commands), ((WorldJobs)world.Execution!).StructureWrite, dependency: predecessor);
        int[] cleaned = [0];
        JobHandle cleanup = Jobs.ScheduleFinally(new ClearCommands(commands, playback.GetAwaiter(), cleaned), default, playback);
        Assert.Throws<ProducerDependencyException>(cleanup.Complete);
        Assert.Equal(1, cleaned[0]);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void SchedulingFailureLeavesRecordedCommandsAvailableForPlayback()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 0, MaxCompletionStates = 1 });
        World world = CreateJobWorld();
        using var commands = new CommandBatch(world, 1);
        DeferredEntity created = commands[0].Create(new JobCommandValue { Value = 7 });
        var released = new TaskCompletionSource();
        JobHandle blocker = Jobs.CreateExternalFenceHandle(released.Task);
        try
        {
            Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(new PlayCommands(commands),
                ((WorldJobs)world.Execution!).StructureWrite));
        }
        finally { released.SetResult(); blocker.Complete(); }
        commands.Playback();
        Assert.True(created.TryResolve(out Entity entity));
        Assert.Equal(7, world.ReadComponentData<JobCommandValue>(entity).Value);
    }

    [Fact]
    public void PlaybackRunsAfterTheExplicitProducerDependency()
    {
        World world = CreateJobWorld();
        using var commands = new CommandBatch(world, 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle producer = Jobs.Schedule(new BlockingCreate(commands[0], entered, release));
        JobHandle playback = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            playback = Jobs.Schedule(new PlayCommands(commands), ((WorldJobs)world.Execution!).StructureWrite, dependency: producer);
            Assert.Equal(0, world.EntityCount);
        }
        finally { release.Set(); playback.Complete(); producer.Complete(); }
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void DisposalFollowsEveryProducerCompletion()
    {
        World world = CreateJobWorld();
        var commands = new CommandBatch(world, 1);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new ConcurrentQueue<string>();
        JobHandle producer = Jobs.Schedule(new BlockingCreate(commands[0], entered, release, events));
        JobHandle disposal = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            disposal = Jobs.ScheduleFinally(new DisposeCommands(commands, producer.GetAwaiter(), events), default, producer);
        }
        finally { release.Set(); disposal.Complete(); producer.Complete(); }
        Assert.Equal(["recorded", "disposed"], events.ToArray());
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void ZeroProducerBatchPreservesTheWorldVersion()
    {
        World world = CreateJobWorld();
        ulong before = world.Version;
        using var commands = new CommandBatch(world, 0);
        commands.Playback();
        Assert.Equal(0, world.EntityCount);
        Assert.Equal(before, world.Version);
    }

    [Fact]
    public void ZeroProducerSchedulePreservesItsExplicitDependency()
    {
        World world = CreateJobWorld();
        using var commands = new CommandBatch(world, 0);
        JobHandle dependency = Jobs.Schedule(new ThrowingDependency());
        JobHandle producer = Jobs.ScheduleParallel(new CreateByIndex(commands), 0, 1, dependency);
        Assert.Throws<ProducerDependencyException>(producer.Complete);
        Assert.Equal(0, world.EntityCount);
    }

    private readonly struct CreateByIndex(CommandBatch commands) : IJobParallelFor
    {
        public void Execute(int index) => commands[index].Create(new JobCommandValue { Value = index });
    }

    private readonly struct CreateRelations(CommandBatch commands, Entity source, Entity[] targets,
        DeferredEntity[] created) : IJobParallelFor
    {
        public void Execute(int index)
        {
            CommandBuffer buffer = commands[index];
            buffer.EditEndpoints<JobCommandLink>([EndpointCommandEdit.Create(source, targets[index])], created.AsSpan(index, 1));
            buffer.Set(created[index], new JobCommandLink { Value = index });
        }
    }

    private readonly struct SwapTargets(EndpointCommandEdit[] edits, Entity first, Entity second,
        Entity sourceA, Entity sourceB, Entity targetA, Entity targetB) : IJobParallelFor
    {
        public void Execute(int index) => edits[index] = index == 0
            ? new(first, sourceA, targetB) : new(second, sourceB, targetA);
    }

    private readonly struct PlayCommands(CommandBatch commands) : IJob
    {
        public void Execute() => commands.Playback();
    }

    private readonly struct FaultingCreate(CommandBatch commands, DeferredEntity[] created, int failureIndex) : IJobParallelFor
    {
        public void Execute(int index)
        {
            created[index] = commands[index].Create(new JobCommandValue { Value = index });
            if (index == failureIndex) throw new ProducerProbeException();
        }
    }

    private readonly struct ClearCommands(CommandBatch commands, JobHandleAwaiter result, int[] cleaned) : IJob
    {
        public void Execute()
        {
            try { result.GetResult(); }
            finally { commands.Clear(); cleaned[0]++; }
        }
    }

    private readonly struct DisposeCommands(CommandBatch commands, JobHandleAwaiter result,
        ConcurrentQueue<string> events) : IJob
    {
        public void Execute()
        {
            try { result.GetResult(); }
            finally { commands.Dispose(); events.Enqueue("disposed"); }
        }
    }

    private readonly struct CreateWithoutStructureAccess(World world) : IJob
    {
        public void Execute() => world.Create(new JobCommandValue());
    }

    private readonly struct StartChildProducer(Scheduler scheduler, CommandBuffer commands, DeferredEntity[] created) : IJob
    {
        public void Execute() => scheduler.Schedule(new CaptureCreate(commands, created));
    }

    private readonly struct CaptureCreate(CommandBuffer commands, DeferredEntity[] created) : IJob
    {
        public void Execute() => created[0] = commands.Create(new JobCommandValue { Value = 7 });
    }

    private readonly struct BlockingCreate(CommandBuffer commands, ManualResetEventSlim entered,
        ManualResetEventSlim release, ConcurrentQueue<string>? events = null) : IJob
    {
        public void Execute()
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            commands.Create(new JobCommandValue());
            events?.Enqueue("recorded");
        }
    }

    private readonly struct ThrowingDependency : IJob
    {
        public void Execute() => throw new ProducerDependencyException();
    }

    private struct JobCommandValue { internal int Value; }
    private struct JobCommandLink { internal int Value; }
    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private struct JobCommandUniqueTarget;
    private sealed class ProducerProbeException : Exception;
    private sealed class ProducerDependencyException : Exception;
}
