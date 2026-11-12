using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class WorldJobAdmissionTests : JobTest
{
    [Fact]
    public void HelpingJobRequiresItsOwnStructureWriteDeclaration()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 0, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateJobWorld();
        JobHandle work = Jobs.Schedule(new CreateEntity(world));
        using WorldStructureWriteScope structure = world.OpenStructureWrite();
        Assert.Throws<JobResourceSafetyException>(work.Complete);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void HelpingJobRequiresItsOwnComponentWriteDeclaration()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 0, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateJobWorld();
        Entity entity = world.Create(new ProbeValue(7));
        JobHandle work = Jobs.Schedule(new SetValue(world, entity, 19));
        using WorldStructureWriteScope structure = world.OpenStructureWrite();
        Assert.Throws<JobResourceSafetyException>(work.Complete);
        Assert.Equal(7, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public void HelpingJobRequiresItsOwnComponentReadDeclaration()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 0, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateJobWorld();
        Entity entity = world.Create(new ProbeValue(7));
        JobHandle work = Jobs.Schedule(new ReadValue(world, entity));
        using WorldStructureWriteScope structure = world.OpenStructureWrite();
        Assert.Throws<JobResourceSafetyException>(work.Complete);
        Assert.Equal(7, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public void HelpingJobRequiresItsOwnStructureReadDeclaration()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 0, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateJobWorld();
        Entity entity = world.Create(new ProbeValue(7));
        JobHandle work = Jobs.Schedule(new ReadLife(world, entity));
        using QueryScope rows = world.Query<ProbeValue>().OpenRead();
        Assert.Throws<JobResourceSafetyException>(work.Complete);
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void FreshWorldRemainsUsableBySynchronousCallersWithoutTypedAccessBinding()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create(new ProbeValue(3)), destination = world.Create();
        world.Set(entity, new ProbeValue(5));
        using var commands = new CommandBuffer(world);
        commands.Add(destination, new ProbeValue(7));
        commands.Playback();
        Assert.Equal(5, world.ReadComponentData<ProbeValue>(entity).Value);
        Assert.Equal(7, world.ReadComponentData<ProbeValue>(destination).Value);
    }

    [Fact]
    public void CapturedWorldRequiresDeclaredAccessBeforeReplacingAValue()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create(new ProbeValue(11));
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(new SetValue(world, entity, 29)).Complete());
        Assert.Equal(1, world.EntityCount);
        Assert.Equal(11, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public void CapturedWorldCreateRequiresDeclaredAccess()
    {
        World world = CreateJobWorld();
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(new CreateEntity(world)).Complete());
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void AnIndependentProducerBufferRecordsBeforeItsWorldChanges()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create();
        using var commands = new CommandBuffer(world);
        Jobs.Schedule(new RecordValue(commands, entity, 17), JobResourceAccess.Write(Jobs.GetResourceToken(commands))).Complete();
        Assert.Equal(1, commands.Count);
        Assert.False(world.Has<ProbeValue>(entity));
        commands.Playback();
        Assert.Equal(17, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public async Task SynchronousWorldRejectsGeneratedSchedulingDuringAndAfterAQuery()
    {
        using var world = new World();
        Entity entity = world.Create(new ProbeValue(1));
        Query query = world.Query<ProbeValue>();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task reading = Task.Run(() =>
        {
            using QueryScope rows = query.OpenRead();
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            Assert.Throws<InvalidOperationException>(() => new SystemRunner(world));
        }
        finally { release.Set(); await reading.WaitAsync(TimeSpan.FromSeconds(10)); }
        Assert.Throws<InvalidOperationException>(() => new SystemRunner(world));
        world.Set(entity, new ProbeValue(2));
        Assert.Equal(2, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public void SynchronousQueryDoesNotGiveRawJobsAccessToItsWorld()
    {
        using var world = new World();
        world.Create(new ProbeValue(1));
        using (QueryScope rows = world.Query<ProbeValue>().OpenRead())
            Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(new ReadCount(world)).Complete());
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void SynchronousQueryKeepsItsWorldConstructionMode()
    {
        using var world = new World();
        Entity entity = world.Create(new ProbeValue(1));
        using (QueryScope rows = world.Query<ProbeValue>().OpenRead())
        {
            Assert.Null(world.Execution);
            Assert.Throws<InvalidOperationException>(() => new SystemRunner(world));
        }
        Assert.Null(world.Execution);
        world.Set(entity, new ProbeValue(2));
        Assert.Equal(2, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public void RejectedPlaybackKeepsItsRecordedCommands()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create();
        using var commands = new CommandBuffer(world);
        commands.Add(entity, new ProbeValue(17));
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(new Playback(commands)).Complete());
        Assert.Equal(1, commands.Count);
        Assert.False(world.Has<ProbeValue>(entity));
        commands.Playback();
        Assert.Equal(17, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public void AnotherWorldsStructurePermissionKeepsPlaybackUnchanged()
    {
        World world = CreateJobWorld(), other = CreateJobWorld();
        Entity entity = world.Create();
        using var commands = new CommandBuffer(world);
        commands.Add(entity, new ProbeValue(19));
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(new Playback(commands), other.Execution!.StructureWrite).Complete());
        Assert.Equal(1, commands.Count);
        Assert.False(world.Has<ProbeValue>(entity));
        Jobs.Schedule(new Playback(commands), world.Execution!.StructureWrite).Complete();
        Assert.Equal(19, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public void ACompletedProducerSequenceRejectsFurtherJobRecording()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create();
        using var commands = new CommandBuffer(world);
        commands.Add(entity, new ProbeValue(3));
        commands.Playback();
        Assert.Throws<InvalidOperationException>(() => Jobs.Schedule(new RecordValue(commands, entity, 4)).Complete());
        Assert.Equal(3, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public void MultiWorkItemJobsRequireSeparateStructureWriters()
    {
        World world = CreateJobWorld();
        JobHandle work = Jobs.ScheduleParallel(new ParallelCreate(world), 2, 1, world.Execution!.StructureWrite);
        Assert.Throws<JobResourceSafetyException>(work.Complete);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void MultiWorkItemJobsRequireSeparateComponentWriteRanges()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create(new ProbeValue(3));
        JobHandle work = Jobs.ScheduleParallel(new ParallelSet(world, entity), 2, 1,
            ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<ProbeValue>()));
        Assert.Throws<JobResourceSafetyException>(work.Complete);
        Assert.Equal(3, world.ReadComponentData<ProbeValue>(entity).Value);
    }

    [Fact]
    public void MultiWorkItemReadOnlyWorldAccessRemainsAvailable()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create(new ProbeValue(13));
        int[] values = new int[4];
        JobResourceAccess[] accesses = [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<ProbeValue>()];
        Jobs.ScheduleParallel(new ParallelRead(world, entity, values), values.Length, 1, accesses).Complete();
        Assert.Equal([13, 13, 13, 13], values);
    }

    [Fact]
    public void OneWorkItemParallelJobCanUseItsStructureWritePermission()
    {
        World world = CreateJobWorld();
        Jobs.ScheduleParallel(new ParallelCreate(world), 1, 8, world.Execution!.StructureWrite).Complete();
        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void AConcreteStageRecordsTheNextSequenceForExplicitPlayback()
    {
        World world = CreateJobWorld();
        Entity trigger = world.Create(), destination = world.Create();
        using var next = new CommandBuffer(world);
        Jobs.Schedule(new RecordNext(world, trigger, destination, next), world.Execution!.StructureWrite).Complete();
        Assert.Equal(17, world.ReadComponentData<ProbeValue>(trigger).Value);
        Assert.False(world.Has<ResultValue>(destination));
        next.Playback();
        Assert.Equal(18, world.ReadComponentData<ResultValue>(destination).Value);
    }

    [Fact]
    public void AFollowingStageRequiresItsOwnWriteDeclaration()
    {
        World world = CreateJobWorld();
        Entity trigger = world.Create(), destination = world.Create();
        JobHandle first = Jobs.Schedule(new AddProbe(world, trigger, 17), world.Execution!.StructureWrite);
        JobHandle second = Jobs.Schedule(new AddResult(world, destination), world.Execution.StructureRead, first);
        Assert.Throws<JobResourceSafetyException>(second.Complete);
        Assert.Equal(17, world.ReadComponentData<ProbeValue>(trigger).Value);
        Assert.False(world.Has<ResultValue>(destination));
    }

    [Fact]
    public void AClosedFollowingBufferRejectsRecordingAfterTheFirstStageCommits()
    {
        World world = CreateJobWorld();
        Entity trigger = world.Create(), destination = world.Create();
        using var next = new CommandBuffer(world);
        next.Playback();
        JobHandle first = Jobs.Schedule(new AddProbe(world, trigger, 17), world.Execution!.StructureWrite);
        JobHandle second = Jobs.Schedule(new RecordValue(next, destination, 1), dependency: first);
        Assert.Throws<InvalidOperationException>(second.Complete);
        Assert.True(world.Has<ProbeValue>(trigger));
        Assert.False(world.Has<ProbeValue>(destination));
    }

    [Fact]
    public void CapturingAWorldDoesNotRetainACompletedJobsPermission()
    {
        World world = CreateJobWorld();
        Entity trigger = world.Create(), destination = world.Create();
        var captured = new CapturedWorld();
        using var next = new CommandBuffer(world);
        Jobs.Schedule(new CaptureAndRecord(world, trigger, destination, captured, next), world.Execution!.StructureWrite).Complete();
        Assert.Same(world, captured.World);
        Assert.Throws<JobResourceSafetyException>(() => Jobs.Schedule(new AddResult(captured.World!, destination)).Complete());
        Assert.True(world.Has<ProbeValue>(trigger));
        Assert.False(world.Has<ResultValue>(destination));
        next.Playback();
        Assert.Equal(1, world.ReadComponentData<ResultValue>(destination).Value);
    }

    [Fact]
    public void DeferredEntitiesRemainBoundToTheirRecordingBatch()
    {
        World world = CreateJobWorld();
        using var target = new CommandBuffer(world);
        using var first = new CommandBatch(world, 1);
        DeferredEntity foreign = first[0].Create();
        Assert.Throws<ArgumentException>(() => Jobs.Schedule(new RecordDeferred(target, default)).Complete());
        Assert.Throws<ArgumentException>(() => Jobs.Schedule(new RecordDeferred(target, foreign)).Complete());
        first.Clear();
        Assert.Throws<ArgumentException>(() => Jobs.Schedule(new RecordDeferred(first[0], foreign)).Complete());
        Assert.Equal(0, target.Count);
        Assert.Equal(0, first[0].Count);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void AFaultedCandidateKeepsTheSourceWorldAndItsRecordedSequence()
    {
        World world = CreateJobWorld();
        Entity trigger = world.Create(), destination = world.Create();
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World(execution: new WorldJobs(Jobs));
        checkpoint.Restore(candidate);
        using var next = new CommandBuffer(candidate);
        var failure = new StageFault();
        JobHandle work = Jobs.Schedule(new RecordThenFail(candidate, trigger, destination, next, failure), candidate.Execution!.StructureWrite);
        Assert.Same(failure, Assert.Throws<StageFault>(work.Complete));
        Assert.False(world.Has<ProbeValue>(trigger));
        Assert.False(world.Has<ResultValue>(destination));
        Assert.Equal(1, next.Count);
    }

    [Fact]
    public async Task IndependentSequencesRecordWhileAnEarlierPlaybackWaitsForTheWorld()
    {
        World world = CreateJobWorld();
        Entity earlier = world.Create(), later = world.Create();
        using var first = new CommandBuffer(world);
        using var second = new CommandBuffer(world);
        first.Add(earlier, new ProbeValue(1));
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim();
        JobHandle holding = Jobs.Schedule(new HoldStructure(entered, release), world.Execution!.StructureWrite);
        Task playback = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            playback = Task.Run(() => { attempted.Set(); first.Playback(); });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            Jobs.Schedule(new RecordValue(second, later, 2), JobResourceAccess.Write(Jobs.GetResourceToken(second))).Complete();
            Assert.Equal(1, second.Count);
            release.Set();
            holding.Complete();
            await playback.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, world.ReadComponentData<ProbeValue>(earlier).Value);
            Assert.False(world.Has<ProbeValue>(later));
            second.Playback();
            Assert.Equal(2, world.ReadComponentData<ProbeValue>(later).Value);
        }
        finally { release.Set(); holding.Complete(); await playback.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public void EmptyPlaybackKeepsAnotherActiveProducerBufferAvailable()
    {
        World world = CreateJobWorld();
        Entity destination = world.Create();
        using var empty = new CommandBuffer(world);
        using var next = new CommandBuffer(world);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle recording = Jobs.Schedule(new WaitAndRecord(next, destination, entered, release));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            empty.Playback();
            release.Set();
            recording.Complete();
            next.Playback();
            Assert.Equal(5, world.ReadComponentData<ProbeValue>(destination).Value);
        }
        finally { release.Set(); recording.Complete(); }
    }

    [Fact]
    public async Task DirectHierarchyMutationFollowsTheExistingStructureWriter()
    {
        World world = CreateJobWorld();
        Entity parent = world.Create(), child = world.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var attempted = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        JobHandle holding = Jobs.Schedule(new HoldAndRecord(entered, release, events), world.Execution!.StructureWrite);
        Task mutation = Task.CompletedTask;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            mutation = Task.Run(() =>
            {
                attempted.Set();
                Hierarchy<Tree>.SetParent(world, child, parent);
                events.Enqueue("hierarchy changed");
            });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            release.Set();
            holding.Complete();
            await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(parent, Hierarchy<Tree>.GetParent(world, child));
            Assert.Equal(["writer finished", "hierarchy changed"], events);
        }
        finally { release.Set(); holding.Complete(); await mutation.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [Fact]
    public async Task AnExplicitStructureRangeCoversItsOperationsUntilItEnds()
    {
        World world = CreateJobWorld();
        Entity entity = world.Create();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        Task mutation = Task.Run(() =>
        {
            using WorldStructureWriteScope structure = world.OpenStructureWrite();
            world.Add(entity, new ProbeValue(1));
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            events.Enqueue("range ended");
        });
        JobHandle later = default;
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            later = Jobs.Schedule(new RecordSignal(events), world.Execution!.StructureWrite);
            release.Set();
            await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            later.Complete();
            Assert.Equal(1, world.ReadComponentData<ProbeValue>(entity).Value);
            Assert.Equal(["range ended", "later writer"], events);
        }
        finally { release.Set(); await mutation.WaitAsync(TimeSpan.FromSeconds(10)); later.Complete(); }
    }

    private readonly record struct ProbeValue(int Value) : IComponent;
    private readonly record struct ResultValue(int Value) : IComponent;
    private readonly struct Tree : IHierarchyDomain;
    private sealed class CapturedWorld { internal World? World; }
    private sealed class StageFault : Exception;
    private readonly struct CreateEntity(World world) : IJob { public void Execute() => world.Create(); }
    private readonly struct SetValue(World world, Entity entity, int value) : IJob { public void Execute() => world.Set(entity, new ProbeValue(value)); }
    private readonly struct ReadCount(World world) : IJob { public void Execute() => _ = world.EntityCount; }
    private readonly struct ReadValue(World world, Entity entity) : IJob { public void Execute() => _ = world.ReadComponentData<ProbeValue>(entity); }
    private readonly struct ReadLife(World world, Entity entity) : IJob { public void Execute() => _ = world.IsAlive(entity); }
    private readonly struct Playback(CommandBuffer commands) : IJob { public void Execute() => commands.Playback(); }
    private readonly struct RecordValue(CommandBuffer commands, Entity entity, int value) : IJob
    { public void Execute() => commands.Add(entity, new ProbeValue(value)); }
    private readonly struct ParallelCreate(World world) : IJobParallelFor { public void Execute(int index) => world.Create(); }
    private readonly struct ParallelSet(World world, Entity entity) : IJobParallelFor
    { public void Execute(int index) => world.Set(entity, new ProbeValue(41)); }
    private readonly struct ParallelRead(World world, Entity entity, int[] values) : IJobParallelFor
    { public void Execute(int index) => values[index] = world.ReadComponentData<ProbeValue>(entity).Value; }
    private readonly struct AddProbe(World world, Entity entity, int value) : IJob { public void Execute() => world.Add(entity, new ProbeValue(value)); }
    private readonly struct AddResult(World world, Entity entity) : IJob { public void Execute() => world.Add(entity, new ResultValue(1)); }
    private readonly struct RecordNext(World world, Entity trigger, Entity destination, CommandBuffer next) : IJob
    {
        public void Execute()
        {
            world.Add(trigger, new ProbeValue(17));
            next.Add(destination, new ResultValue(18));
        }
    }
    private readonly struct CaptureAndRecord(World world, Entity trigger, Entity destination, CapturedWorld captured, CommandBuffer next) : IJob
    {
        public void Execute()
        {
            world.Add(trigger, new ProbeValue(17));
            captured.World = world;
            next.Add(destination, new ResultValue(1));
        }
    }
    private readonly struct RecordDeferred(CommandBuffer commands, DeferredEntity entity) : IJob
    { public void Execute() => commands.Add(entity, new ProbeValue(1)); }
    private readonly struct RecordThenFail(World world, Entity trigger, Entity destination, CommandBuffer next, StageFault failure) : IJob
    {
        public void Execute()
        {
            world.Add(trigger, new ProbeValue(17));
            next.Add(destination, new ResultValue(1));
            throw failure;
        }
    }
    private readonly struct HoldStructure(ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    { public void Execute() { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(10))); } }
    private readonly struct WaitAndRecord(CommandBuffer commands, Entity entity, ManualResetEventSlim entered, ManualResetEventSlim release) : IJob
    {
        public void Execute()
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            commands.Add(entity, new ProbeValue(5));
        }
    }
    private readonly struct HoldAndRecord(ManualResetEventSlim entered, ManualResetEventSlim release,
        System.Collections.Concurrent.ConcurrentQueue<string> events) : IJob
    {
        public void Execute()
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            events.Enqueue("writer finished");
        }
    }
    private readonly struct RecordSignal(System.Collections.Concurrent.ConcurrentQueue<string> events) : IJob
    { public void Execute() => events.Enqueue("later writer"); }
}
