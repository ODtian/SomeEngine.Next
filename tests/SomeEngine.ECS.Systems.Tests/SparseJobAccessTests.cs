using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hooks;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class SparseJobAccessTests
{
    [Fact]
    public void SparseCallbackInsideJob_RejectsMissingLogicalResource()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = CreateWorldWith<SparseA>();
        _ = SparseJobAccess<SparseA>.Read(world);
        JobHandle handle = JobSystem.Schedule(
            new ReadSparseJob<SparseA>(world),
            RelationshipJobAccess.TopologyRead(world));

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void SparseCallbackInsideJob_RejectsMissingTopologyReadResource()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = CreateWorldWith<SparseA>();
        JobHandle handle = JobSystem.Schedule(
            new ReadSparseJob<SparseA>(world),
            SparseJobAccess<SparseA>.Read(world));

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void SameWorldSameSparseType_ReadOwnersRunConcurrently()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = CreateWorldWith<SparseA>();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        try
        {
            first = SparseJobAccess<SparseA>.ScheduleRead(
                world,
                new BlockingReadJob<SparseA>(world, firstStarted, releaseFirst));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle second = SparseJobAccess<SparseA>.ScheduleRead(
                world,
                new SignalReadJob<SparseA>(world, secondStarted));
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
    public void SameWorldSameSparseType_WriteOwnerBlocksReadersAndWriters()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = CreateWorldWith<SparseA>();
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var laterWriterStarted = new ManualResetEventSlim();
        JobHandle writer = default;
        try
        {
            writer = SparseJobAccess<SparseA>.ScheduleWrite(
                world,
                new BlockingWriteJob<SparseA>(world, writerStarted, releaseWriter));
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle reader = SparseJobAccess<SparseA>.ScheduleRead(
                world,
                new SignalJob(readerStarted));
            JobHandle laterWriter = SparseJobAccess<SparseA>.ScheduleWrite(
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
    public void DifferentSparseTypesAndWorlds_DoNotShareLogicalResource()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = CreateWorldWith<SparseA>();
        AddSparse<SparseB>(world);
        var otherWorld = CreateWorldWith<SparseA>();
        using var blockerStarted = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();
        using var otherTypeStarted = new ManualResetEventSlim();
        using var otherWorldStarted = new ManualResetEventSlim();
        JobHandle blocker = default;
        try
        {
            blocker = SparseJobAccess<SparseA>.ScheduleWrite(
                world,
                new BlockingWriteJob<SparseA>(world, blockerStarted, releaseBlocker));
            Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle otherType = SparseJobAccess<SparseB>.ScheduleWrite(
                world,
                new SignalWriteJob<SparseB>(world, otherTypeStarted));
            JobHandle otherWorldHandle = SparseJobAccess<SparseA>.ScheduleWrite(
                otherWorld,
                new SignalWriteJob<SparseA>(otherWorld, otherWorldStarted));

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
    public void DirectSparseOwner_BlocksLaterScheduledConflictUntilCallbackReturns()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = CreateWorldWith<SparseA>();
        _ = SparseJobAccess<SparseA>.Write(world);
        using var directStarted = new ManualResetEventSlim();
        using var releaseDirect = new ManualResetEventSlim();
        using var scheduledStarted = new ManualResetEventSlim();
        Exception? directFault = null;
        Thread thread = new(() =>
        {
            try
            {
                var state = new BlockingState(directStarted, releaseDirect);
                world.ExecuteSparseWrite<SparseA, BlockingState>(
                    ref state,
                    static (
                        ReadOnlySpan<Entity> _,
                        Span<SparseA> _,
                        ref BlockingState blocking) => blocking.Block());
            }
            catch (Exception exception)
            {
                directFault = exception;
            }
        });
        JobHandle scheduled = default;
        try
        {
            thread.Start();
            Assert.True(directStarted.Wait(TimeSpan.FromSeconds(5)));
            scheduled = SparseJobAccess<SparseA>.ScheduleRead(
                world,
                new SignalJob(scheduledStarted));
            Assert.False(scheduledStarted.Wait(TimeSpan.FromMilliseconds(100)));

            releaseDirect.Set();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            scheduled.Complete();
            Assert.True(scheduledStarted.IsSet);
            Assert.Null(directFault);
        }
        finally
        {
            releaseDirect.Set();
            thread.Join(TimeSpan.FromSeconds(5));
            scheduled.Complete();
        }
    }

    [Fact]
    public void ScheduledSparseWriter_BlocksDirectReadSparseUntilOwnerReturns()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = CreateWorldWith<SparseA>();
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var directCompleted = new ManualResetEventSlim();
        Entity sparseEntity = FirstSparseEntity<SparseA>(world);
        Exception? directFault = null;
        JobHandle writer = SparseJobAccess<SparseA>.ScheduleWrite(
            world,
            new BlockingWriteJob<SparseA>(world, writerStarted, releaseWriter));
        Thread direct = new(() =>
        {
            try
            {
                _ = world.ReadSparse<SparseA>(sparseEntity);
                directCompleted.Set();
            }
            catch (Exception exception)
            {
                directFault = exception;
            }
        });
        try
        {
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));
            direct.Start();
            Assert.False(directCompleted.Wait(TimeSpan.FromMilliseconds(100)));

            releaseWriter.Set();
            writer.Complete();
            Assert.True(direct.Join(TimeSpan.FromSeconds(5)));
            Assert.True(directCompleted.IsSet);
            Assert.Null(directFault);
        }
        finally
        {
            releaseWriter.Set();
            writer.Complete();
            if (direct.ThreadState != ThreadState.Unstarted)
                direct.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void FaultedSparseBody_ReleasesIterationAndSynchronousResourceOwner()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = CreateWorldWith<SparseA>();
        _ = SparseJobAccess<SparseA>.Write(world);

        Assert.Throws<ProbeException>(() =>
            world.ExecuteSparseWrite<SparseA>(static (_, _) => throw new ProbeException()));

        AddSparse<SparseB>(world);
        using var started = new ManualResetEventSlim();
        SparseJobAccess<SparseA>
            .ScheduleWrite(world, new SignalWriteJob<SparseA>(world, started))
            .Complete();
        Assert.True(started.IsSet);
    }

    [Fact]
    public void WarmedSparseAdmission_DoesNotAllocatePerCallbackOrCopyRead()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = CreateWorldWith<SparseA>();
        _ = SparseJobAccess<SparseA>.Write(world);
        Entity sparseEntity = FirstSparseEntity<SparseA>(world);
        int callbacks = 0;
        {
            for (int i = 0; i < 128; i++)
            {
                ExecuteCountedRead(world, ref callbacks);
                ExecuteCountedWrite(world, ref callbacks);
                _ = world.ReadSparse<SparseA>(sparseEntity);
            }

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++)
            {
                ExecuteCountedRead(world, ref callbacks);
                ExecuteCountedWrite(world, ref callbacks);
                _ = world.ReadSparse<SparseA>(sparseEntity);
            }
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
        }

        Assert.Equal(2_256, callbacks);
    }

    [Fact]
    public void TopologyWriteHook_CanBorrowSparseAfterLaterSparseOwnerWasRegistered()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = CreateWorldWith<SparseA>();
        Entity entity = world.CreateEntity();
        _ = SparseJobAccess<SparseA>.Write(world);
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
                    world.ExecuteSparseWrite<SparseA>(
                        static (_, values) => values[0].Value++);
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

            later = SparseJobAccess<SparseA>.ScheduleWrite(
                world,
                new SignalWriteJob<SparseA>(world, laterJobStarted));
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

    private static World CreateWorldWith<T>()
        where T : struct, ISparseComponent
    {
        var world = new World();
        AddSparse<T>(world);
        return world;
    }

    private static void AddSparse<T>(World world)
        where T : struct, ISparseComponent
    {
        Entity entity = world.CreateEntity();
        world.AddSparse(entity, default(T));
    }

    private static void ExecuteCountedRead(World world, ref int callbacks)
    {
        world.ExecuteSparseRead<SparseA, int>(
            ref callbacks,
            static (
                ReadOnlySpan<Entity> _,
                ReadOnlySpan<SparseA> _,
                ref int count) => count++);
    }

    private static void ExecuteCountedWrite(World world, ref int callbacks)
    {
        world.ExecuteSparseWrite<SparseA, int>(
            ref callbacks,
            static (
                ReadOnlySpan<Entity> _,
                Span<SparseA> _,
                ref int count) => count++);
    }

    private static Entity FirstSparseEntity<T>(World world)
        where T : struct, ISparseComponent
    {
        Entity entity = default;
        world.ExecuteSparseRead<T, Entity>(
            ref entity,
            static (
                ReadOnlySpan<Entity> entities,
                ReadOnlySpan<T> _,
                ref Entity result) => result = entities[0]);
        return entity;
    }

    private readonly struct ReadSparseJob<T> : IJob
        where T : struct, ISparseComponent
    {
        private readonly World _world;

        internal ReadSparseJob(World world) => _world = world;

        public void Execute() => _world.ExecuteSparseRead<T>(static (_, _) => { });
    }

    private readonly struct BlockingReadJob<T> : IJob
        where T : struct, ISparseComponent
    {
        private readonly World _world;
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingReadJob(
            World world,
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _world = world;
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            var state = new BlockingState(_started, _release);
            _world.ExecuteSparseRead<T, BlockingState>(
                ref state,
                static (
                    ReadOnlySpan<Entity> _,
                    ReadOnlySpan<T> _,
                    ref BlockingState blocking) => blocking.Block());
        }
    }

    private readonly struct BlockingWriteJob<T> : IJob
        where T : struct, ISparseComponent
    {
        private readonly World _world;
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingWriteJob(
            World world,
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _world = world;
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            var state = new BlockingState(_started, _release);
            _world.ExecuteSparseWrite<T, BlockingState>(
                ref state,
                static (
                    ReadOnlySpan<Entity> _,
                    Span<T> _,
                    ref BlockingState blocking) => blocking.Block());
        }
    }

    private readonly struct SignalReadJob<T> : IJob
        where T : struct, ISparseComponent
    {
        private readonly World _world;
        private readonly ManualResetEventSlim _started;

        internal SignalReadJob(World world, ManualResetEventSlim started)
        {
            _world = world;
            _started = started;
        }

        public void Execute()
        {
            ManualResetEventSlim state = _started;
            _world.ExecuteSparseRead<T, ManualResetEventSlim>(
                ref state,
                static (
                    ReadOnlySpan<Entity> _,
                    ReadOnlySpan<T> _,
                    ref ManualResetEventSlim started) => started.Set());
        }
    }

    private readonly struct SignalWriteJob<T> : IJob
        where T : struct, ISparseComponent
    {
        private readonly World _world;
        private readonly ManualResetEventSlim _started;

        internal SignalWriteJob(World world, ManualResetEventSlim started)
        {
            _world = world;
            _started = started;
        }

        public void Execute()
        {
            ManualResetEventSlim state = _started;
            _world.ExecuteSparseWrite<T, ManualResetEventSlim>(
                ref state,
                static (
                    ReadOnlySpan<Entity> _,
                    Span<T> _,
                    ref ManualResetEventSlim started) => started.Set());
        }
    }

    private readonly struct SignalJob : IJob
    {
        private readonly ManualResetEventSlim _started;

        internal SignalJob(ManualResetEventSlim started) => _started = started;

        public void Execute() => _started.Set();
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

    private struct SparseA : ISparseComponent
    {
        internal int Value;
    }

    private struct SparseB : ISparseComponent;

    private struct ProbeComponent : IComponent
    {
        internal int Value;
    }
}
