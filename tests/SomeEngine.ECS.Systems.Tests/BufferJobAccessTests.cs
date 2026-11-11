using ManualResetEventSlim = SomeEngine.Testing.TestSignal;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class BufferJobAccessTests : JobTest
{
    [Fact]
    public void BufferCallbackInsideJob_RejectsMissingLogicalResource()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = CreateBufferEntity<Element>(world);

        // Bind the optional World coordinator, but deliberately omit the logical buffer resource.

        JobHandle handle = Jobs.Schedule(
            new ReadBufferJob<Element>(world, entity),
            world.Execution!.StructureRead);

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void SameWorldSameBuffer_ReadOwnersCanRunConcurrently()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = CreateBufferEntity<Element>(world);
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        try
        {
            first = world.Execution!.Scheduler!.Schedule(new BlockingReadJob<Element>(world, entity, firstStarted, releaseFirst), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadRandom<Buffer<Element>>()));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle second = world.Execution!.Scheduler!.Schedule(new SignalReadJob<Element>(world, entity, secondStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadRandom<Buffer<Element>>()));
            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            second.Complete();
        }
        finally
        {
            releaseFirst.Set();
            first.Complete();
        }
    }

    [Fact]
    public void SameWorldSameBuffer_WriteOwnerBlocksReadersAndWriters()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = CreateBufferEntity<Element>(world);
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        releaseWriter.WaitCallback = () => events.Enqueue("released");
        using var readerStarted = new ManualResetEventSlim();
        readerStarted.SetCallback = () => events.Enqueue("readerStarted");
        using var laterWriterStarted = new ManualResetEventSlim();
        laterWriterStarted.SetCallback = () => events.Enqueue("laterWriterStarted");
        JobHandle writer = default;
        try
        {
            writer = world.Execution!.Scheduler!.Schedule(new BlockingWriteJob<Element>(world, entity, writerStarted, releaseWriter), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<Buffer<Element>>()));
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle reader = world.Execution!.Scheduler!.Schedule(new SignalJob(readerStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadRandom<Buffer<Element>>()));
            JobHandle laterWriter = world.Execution!.Scheduler!.Schedule(new SignalJob(laterWriterStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<Buffer<Element>>()));

            releaseWriter.Set();
            reader.Complete();
            laterWriter.Complete();
            Assert.True(readerStarted.IsSet);
            Assert.True(laterWriterStarted.IsSet);
        }
        finally
        {
            releaseWriter.Set();
            writer.Complete();
        }

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("readerStarted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "readerStarted"));
        Assert.Contains("laterWriterStarted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "laterWriterStarted"));
    }

    [Fact]
    public void DifferentBufferTypesAndWorlds_DoNotShareLogicalResource()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        var otherWorld = CreateJobWorld();
        Entity entity = CreateBufferEntity<Element>(world);
        Entity otherTypeEntity = CreateBufferEntity<OtherElement>(world);
        Entity otherWorldEntity = CreateBufferEntity<Element>(otherWorld);
        using var blockerStarted = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();
        using var otherTypeStarted = new ManualResetEventSlim();
        using var otherWorldStarted = new ManualResetEventSlim();
        JobHandle blocker = default;
        try
        {
            blocker = world.Execution!.Scheduler!.Schedule(new BlockingWriteJob<Element>(world, entity, blockerStarted, releaseBlocker), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<Buffer<Element>>()));
            Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle otherType = world.Execution!.Scheduler!.Schedule(new SignalWriteJob<OtherElement>(world, otherTypeEntity, otherTypeStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<Buffer<OtherElement>>()));
            JobHandle otherWorldHandle = otherWorld.Execution!.Scheduler!.Schedule(new SignalWriteJob<Element>(otherWorld, otherWorldEntity, otherWorldStarted), ((WorldJobs)otherWorld.Execution!).CollectAccess(new SystemAccesses().WriteRandom<Buffer<Element>>()));

            Assert.True(otherTypeStarted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(otherWorldStarted.Wait(TimeSpan.FromSeconds(5)));
            otherType.Complete();
            otherWorldHandle.Complete();
        }
        finally
        {
            releaseBlocker.Set();
            blocker.Complete();
        }
    }

    [Fact]
    public void FaultedBufferBody_ReleasesIterationAndSynchronousResourceOwner()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = CreateBufferEntity<Element>(world);


        Assert.Throws<ProbeException>((Action)(() =>
            { using (var buffer = world.GetBuffer<Element>(entity)) { throw new ProbeException(); } }));

        // Structural success proves iteration ownership was released; scheduled success proves
        // the synchronous logical storage owner was released.
        world.Add(entity, new ProbeComponent { Value = 1 });
        using var started = new ManualResetEventSlim();
        world.Execution!.Scheduler!.Schedule(new SignalWriteJob<Element>(world, entity, started), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<Buffer<Element>>()))
            .Complete();
        Assert.True(started.IsSet);
    }

    [Fact]
    public void RepeatedBufferAccessInvokesEveryCallback()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var unboundWorld = new World();
        Entity unboundEntity = CreateBufferEntity<Element>(unboundWorld);
        int unboundCallbacks = 0;
        for (int i = 0; i < 128; i++)
            ExecuteCountedRead(unboundWorld, unboundEntity, ref unboundCallbacks);
        for (int i = 0; i < 1_000; i++)
            ExecuteCountedRead(unboundWorld, unboundEntity, ref unboundCallbacks);

        var world = CreateJobWorld();
        Entity entity = CreateBufferEntity<Element>(world);

        int callbacks = 0;

        for (int i = 0; i < 128; i++)
            ExecuteCountedRead(world, entity, ref callbacks);
        for (int i = 0; i < 1_000; i++)
            ExecuteCountedRead(world, entity, ref callbacks);

        for (int i = 0; i < 128; i++)
            _ = world.Has<Buffer<Element>>(entity);
        for (int i = 0; i < 1_000; i++)
            _ = world.Has<Buffer<Element>>(entity);

        Assert.Equal(1_128, callbacks);
    }

    [Fact]
    public void StructureWriteCanBorrowItsBufferAfterALaterBufferJobRegisters()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = CreateBufferEntity<Element>(world);

        using var hookStarted = new ManualResetEventSlim();
        using var laterRegistered = new ManualResetEventSlim();
        using var nestedBorrowCompleted = new ManualResetEventSlim();
        using var laterJobStarted = new ManualResetEventSlim();
        Exception? mutationFault = null;
        Thread? mutationThread = null;
        JobHandle later = default;
        try
        {
            mutationThread = new Thread(() =>
            {
                try
                {
                    using WorldStructureWriteScope structure = world.OpenStructureWrite();
                    world.Add(entity, new ProbeComponent { Value = 1 });
                    hookStarted.Set();
                    Assert.True(laterRegistered.Wait(TimeSpan.FromSeconds(5)));
                    using (DynamicBuffer<Element> buffer = world.GetBuffer<Element>(entity)) buffer.Add(new Element { Value = 7 });
                    nestedBorrowCompleted.Set();
                }
                catch (Exception exception)
                {
                    mutationFault = exception;
                }
            });
            mutationThread.Start();
            Assert.True(hookStarted.Wait(TimeSpan.FromSeconds(5)));

            later = world.Execution!.Scheduler!.Schedule(new SignalWriteJob<Element>(world, entity, laterJobStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<Buffer<Element>>()));
            laterRegistered.Set();

            Assert.True(nestedBorrowCompleted.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(mutationThread.Join(TimeSpan.FromSeconds(5)));
            later.Complete();
            Assert.True(laterJobStarted.IsSet);
            Assert.Null(mutationFault);
        }
        finally
        {
            laterRegistered.Set();
            mutationThread?.Join(TimeSpan.FromSeconds(5));
            later.Complete();
        }
    }

    private Entity CreateBufferEntity<T>(World world)
        where T : struct, IBufferElement
    {
        Entity entity = world.Create();
        world.AddBuffer<T>(entity);
        return entity;
    }

    private void ExecuteCountedRead(World world, Entity entity, ref int callbacks)
    {
        using (var buffer = world.ReadBuffer<Element>(entity))
        {
            callbacks++;
        }
    }

    private readonly struct ReadBufferJob<T> : IJob
        where T : struct, IBufferElement
    {
        private readonly World _world;
        private readonly Entity _entity;

        internal ReadBufferJob(World world, Entity entity)
        {
            _world = world;
            _entity = entity;
        }

        public void Execute()
        {
            using (var buffer = _world.ReadBuffer<T>(_entity))
            {

            }
        }
    }

    private readonly struct BlockingReadJob<T> : IJob
        where T : struct, IBufferElement
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingReadJob(
            World world,
            Entity entity,
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _world = world;
            _entity = entity;
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            var state = new BlockingState(_started, _release);
            using (var buffer = _world.ReadBuffer<T>(_entity))
            {
                state.Block();
            }
        }
    }

    private readonly struct BlockingWriteJob<T> : IJob
        where T : struct, IBufferElement
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingWriteJob(
            World world,
            Entity entity,
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _world = world;
            _entity = entity;
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            var state = new BlockingState(_started, _release);
            using (var buffer = _world.GetBuffer<T>(_entity))
            {
                state.Block();
            }
        }
    }

    private readonly struct SignalJob : IJob
    {
        private readonly ManualResetEventSlim _started;

        internal SignalJob(ManualResetEventSlim started)
        {
            _started = started;
        }

        public void Execute()
        {
            _started.Set();
        }
    }

    private readonly struct SignalReadJob<T> : IJob
        where T : struct, IBufferElement
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly ManualResetEventSlim _started;

        internal SignalReadJob(World world, Entity entity, ManualResetEventSlim started)
        {
            _world = world;
            _entity = entity;
            _started = started;
        }

        public void Execute()
        {
            ManualResetEventSlim state = _started;
            using (var buffer = _world.ReadBuffer<T>(_entity))
            {
                state.Set();
            }
        }
    }

    private readonly struct SignalWriteJob<T> : IJob
        where T : struct, IBufferElement
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly ManualResetEventSlim _started;

        internal SignalWriteJob(World world, Entity entity, ManualResetEventSlim started)
        {
            _world = world;
            _entity = entity;
            _started = started;
        }

        public void Execute()
        {
            ManualResetEventSlim state = _started;
            using (var buffer = _world.GetBuffer<T>(_entity))
            {
                state.Set();
            }
        }
    }

    private readonly struct BlockingState
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingState(ManualResetEventSlim started, ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        internal void Block()
        {
            _started.Set();
            _release.Wait();
        }
    }

    private sealed class ProbeException : Exception;

    private struct ProbeComponent : IComponent
    {
        internal int Value;
    }

    private struct Element : IBufferElement
    {
        internal int Value;
    }

    private struct OtherElement : IBufferElement;
}
