using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class BufferJobAccessTests
{
    [Fact]
    public void BufferCallbackInsideJob_RejectsMissingLogicalResource()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = CreateBufferEntity<Element>(world);

        // Bind the optional World coordinator, but deliberately omit the logical buffer resource.
        _ = BufferJobAccess<Element>.Read(world);
        JobHandle handle = JobSystem.Schedule(
            new ReadBufferJob<Element>(world, entity),
            RelationshipJobAccess.TopologyRead(world));

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void SameWorldSameBuffer_ReadOwnersCanRunConcurrently()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        Entity entity = CreateBufferEntity<Element>(world);
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        try
        {
            first = BufferJobAccess<Element>.ScheduleRead(
                world,
                new BlockingReadJob<Element>(world, entity, firstStarted, releaseFirst));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle second = BufferJobAccess<Element>.ScheduleRead(
                world,
                new SignalReadJob<Element>(world, entity, secondStarted));
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
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        Entity entity = CreateBufferEntity<Element>(world);
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var laterWriterStarted = new ManualResetEventSlim();
        JobHandle writer = default;
        try
        {
            writer = BufferJobAccess<Element>.ScheduleWrite(
                world,
                new BlockingWriteJob<Element>(world, entity, writerStarted, releaseWriter));
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle reader = BufferJobAccess<Element>.ScheduleRead(
                world,
                new SignalJob(readerStarted));
            JobHandle laterWriter = BufferJobAccess<Element>.ScheduleWrite(
                world,
                new SignalJob(laterWriterStarted));
            Assert.False(readerStarted.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.False(laterWriterStarted.Wait(TimeSpan.FromMilliseconds(100)));

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
    }

    [Fact]
    public void DifferentBufferTypesAndWorlds_DoNotShareLogicalResource()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        var otherWorld = new World();
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
            blocker = BufferJobAccess<Element>.ScheduleWrite(
                world,
                new BlockingWriteJob<Element>(world, entity, blockerStarted, releaseBlocker));
            Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle otherType = BufferJobAccess<OtherElement>.ScheduleWrite(
                world,
                new SignalWriteJob<OtherElement>(world, otherTypeEntity, otherTypeStarted));
            JobHandle otherWorldHandle = BufferJobAccess<Element>.ScheduleWrite(
                otherWorld,
                new SignalWriteJob<Element>(otherWorld, otherWorldEntity, otherWorldStarted));

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
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = CreateBufferEntity<Element>(world);
        _ = BufferJobAccess<Element>.Write(world);

        Assert.Throws<ProbeException>(() =>
            world.ExecuteBufferWrite<Element>(entity, static _ => throw new ProbeException()));

        // Structural success proves iteration ownership was released; scheduled success proves
        // the synchronous logical storage owner was released.
        world.Add(entity, new ProbeComponent { Value = 1 });
        using var started = new ManualResetEventSlim();
        BufferJobAccess<Element>
            .ScheduleWrite(world, new SignalWriteJob<Element>(world, entity, started))
            .Complete();
        Assert.True(started.IsSet);
    }

    [Fact]
    public void WarmedBufferAdmission_DoesNotAllocatePerCallback()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var unboundWorld = new World();
        Entity unboundEntity = CreateBufferEntity<Element>(unboundWorld);
        int unboundCallbacks = 0;
        for (int i = 0; i < 128; i++)
            ExecuteCountedRead(unboundWorld, unboundEntity, ref unboundCallbacks);
        long unboundBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            ExecuteCountedRead(unboundWorld, unboundEntity, ref unboundCallbacks);
        long unboundAllocated = GC.GetAllocatedBytesForCurrentThread() - unboundBefore;

        var world = new World();
        Entity entity = CreateBufferEntity<Element>(world);
        _ = BufferJobAccess<Element>.Read(world);
        int callbacks = 0;

        for (int i = 0; i < 128; i++)
            ExecuteCountedRead(world, entity, ref callbacks);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            ExecuteCountedRead(world, entity, ref callbacks);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        for (int i = 0; i < 128; i++)
            _ = world.HasBuffer<Element>(entity);
        long hasBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1_000; i++)
            _ = world.HasBuffer<Element>(entity);
        long hasAllocated = GC.GetAllocatedBytesForCurrentThread() - hasBefore;

        Assert.Equal(1_128, callbacks);
        Assert.True(
            unboundAllocated == 0 && allocated == 0 && hasAllocated == 0,
            $"unbound={unboundAllocated}, buffer={allocated}, topology={hasAllocated}");
    }

    [Fact]
    public void TopologyWriteHook_CanBorrowBufferAfterLaterBufferOwnerWasRegistered()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        Entity entity = CreateBufferEntity<Element>(world);
        _ = BufferJobAccess<Element>.Write(world);
        using var hookStarted = new ManualResetEventSlim();
        using var laterRegistered = new ManualResetEventSlim();
        using var nestedBorrowCompleted = new ManualResetEventSlim();
        using var laterJobStarted = new ManualResetEventSlim();
        Exception? mutationFault = null;
        Thread? mutationThread = null;
        JobHandle later = default;
        try
        {
            world.Hooks<ProbeComponent>().OnAdd(
                (DeferredWorld _, Entity _, in ProbeComponent _) =>
                {
                    hookStarted.Set();
                    laterRegistered.Wait();
                    world.ExecuteBufferWrite<Element>(
                        entity,
                        static buffer => buffer.Add(new Element { Value = 7 }));
                    nestedBorrowCompleted.Set();
                });

            mutationThread = new Thread(() =>
            {
                try
                {
                    world.Add(entity, new ProbeComponent { Value = 1 });
                }
                catch (Exception exception)
                {
                    mutationFault = exception;
                }
            });
            mutationThread.Start();
            Assert.True(hookStarted.Wait(TimeSpan.FromSeconds(5)));

            later = BufferJobAccess<Element>.ScheduleWrite(
                world,
                new SignalWriteJob<Element>(world, entity, laterJobStarted));
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

    private static Entity CreateBufferEntity<T>(World world)
        where T : struct, IBufferElement
    {
        Entity entity = world.CreateEntity();
        world.AddBuffer<T>(entity);
        return entity;
    }

    private static void ExecuteCountedRead(World world, Entity entity, ref int callbacks)
    {
        world.ExecuteBufferRead<Element, int>(
            entity,
            ref callbacks,
            static (BufferView<Element> _, ref int count) => count++);
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
            _world.ExecuteBufferRead<T>(_entity, static _ => { });
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
            _world.ExecuteBufferRead<T, BlockingState>(
                _entity,
                ref state,
                static (BufferView<T> _, ref BlockingState blocking) => blocking.Block());
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
            _world.ExecuteBufferWrite<T, BlockingState>(
                _entity,
                ref state,
                static (DynamicBuffer<T> _, ref BlockingState blocking) => blocking.Block());
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
            _world.ExecuteBufferRead<T, ManualResetEventSlim>(
                _entity,
                ref state,
                static (BufferView<T> _, ref ManualResetEventSlim started) => started.Set());
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
            _world.ExecuteBufferWrite<T, ManualResetEventSlim>(
                _entity,
                ref state,
                static (DynamicBuffer<T> _, ref ManualResetEventSlim started) => started.Set());
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

    private sealed class JobRuntimeScope : IDisposable
    {
        private readonly JobSafetyMode _safety = JobSystem.SafetyMode;
        private readonly ManagedPayloadPolicy _payload = JobSystem.ManagedPayloadPolicy;

        internal JobRuntimeScope(int workerCount)
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                WorkerCount = workerCount,
                SafetyMode = _safety,
                ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
            });
        }

        public void Dispose()
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = _safety,
                ManagedPayloadPolicy = _payload,
            });
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
