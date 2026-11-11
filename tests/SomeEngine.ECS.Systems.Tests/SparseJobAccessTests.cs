using ManualResetEventSlim = SomeEngine.Testing.TestSignal;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class SparseJobAccessTests : JobTest
{
    [Fact]
    public void SparseCallbackInsideJob_RejectsMissingLogicalResource()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateWorldWith<SparseA>();

        JobHandle handle = Jobs.Schedule(
            new ReadSparseJob<SparseA>(world),
            world.Execution!.StructureRead);

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void SparseCallbackInsideJob_RejectsMissingTopologyReadResource()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateWorldWith<SparseA>();
        JobHandle handle = Jobs.Schedule(
            new ReadSparseJob<SparseA>(world),
            ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadSparse<SparseA>()).Except(world.Execution!.StructureRead).ToArray());

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void SameWorldSameSparseType_ReadOwnersRunConcurrently()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateWorldWith<SparseA>();
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        try
        {
            first = world.Execution!.Scheduler!.Schedule(new BlockingReadJob<SparseA>(world, firstStarted, releaseFirst), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadSparse<SparseA>()));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle second = world.Execution!.Scheduler!.Schedule(new SignalReadJob<SparseA>(world, secondStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadSparse<SparseA>()));
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
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateWorldWith<SparseA>();
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
            writer = world.Execution!.Scheduler!.Schedule(new BlockingWriteJob<SparseA>(world, writerStarted, releaseWriter), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteSparse<SparseA>()));
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle reader = world.Execution!.Scheduler!.Schedule(new SignalJob(readerStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadSparse<SparseA>()));
            JobHandle laterWriter = world.Execution!.Scheduler!.Schedule(new SignalJob(laterWriterStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteSparse<SparseA>()));

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
    public void DifferentSparseTypesAndWorlds_DoNotShareLogicalResource()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
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
            blocker = world.Execution!.Scheduler!.Schedule(new BlockingWriteJob<SparseA>(world, blockerStarted, releaseBlocker), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteSparse<SparseA>()));
            Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5)));

            JobHandle otherType = world.Execution!.Scheduler!.Schedule(new SignalWriteJob<SparseB>(world, otherTypeStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteSparse<SparseB>()));
            JobHandle otherWorldHandle = otherWorld.Execution!.Scheduler!.Schedule(new SignalWriteJob<SparseA>(otherWorld, otherWorldStarted), ((WorldJobs)otherWorld.Execution!).CollectAccess(new SystemAccesses().WriteSparse<SparseA>()));

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
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateWorldWith<SparseA>();

        using var directStarted = new ManualResetEventSlim();
        using var releaseDirect = new ManualResetEventSlim();
        releaseDirect.WaitCallback = () => events.Enqueue("released");
        using var scheduledStarted = new ManualResetEventSlim();
        scheduledStarted.SetCallback = () => events.Enqueue("scheduledStarted");
        Exception? directFault = null;
        Thread thread = new(() =>
        {
            try
            {
                var state = new BlockingState(directStarted, releaseDirect);
                {
                    using var range = world.OpenSparseWrite<SparseA>();
                    ReadOnlySpan<Entity> entities = range.Entities;
                    Span<SparseA> values = range.Values;
                    state.Block();
                }
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
            scheduled = world.Execution!.Scheduler!.Schedule(new SignalJob(scheduledStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadSparse<SparseA>()));

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

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("scheduledStarted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "scheduledStarted"));
    }

    [Fact]
    public void ScheduledSparseWriter_BlocksDirectReadSparseUntilOwnerReturns()
    {
        using var attempted = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateWorldWith<SparseA>();
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        releaseWriter.WaitCallback = () => events.Enqueue("released");
        using var directCompleted = new ManualResetEventSlim();
        directCompleted.SetCallback = () => events.Enqueue("directCompleted");
        Entity sparseEntity = FirstSparseEntity<SparseA>(world);
        Exception? directFault = null;
        JobHandle writer = world.Execution!.Scheduler!.Schedule(new BlockingWriteJob<SparseA>(world, writerStarted, releaseWriter), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteSparse<SparseA>()));
        Thread direct = new(() =>
        {
            attempted.Set();
            try
            {
                _ = world.ReadComponentData<SparseA>(sparseEntity);
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
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));

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

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("directCompleted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "directCompleted"));
    }

    [Fact]
    public void FaultedSparseBody_ReleasesIterationAndSynchronousResourceOwner()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateWorldWith<SparseA>();


        Assert.Throws<ProbeException>((Action)(() =>
            { using var range = world.OpenSparseWrite<SparseA>(); ReadOnlySpan<Entity> entities = range.Entities; Span<SparseA> values = range.Values; throw new ProbeException(); }));

        AddSparse<SparseB>(world);
        using var started = new ManualResetEventSlim();
        world.Execution!.Scheduler!.Schedule(new SignalWriteJob<SparseA>(world, started), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteSparse<SparseA>()))
            .Complete();
        Assert.True(started.IsSet);
    }

    [Fact]
    public void RepeatedSparseAccessPreservesCallbackResults()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateWorldWith<SparseA>();

        Entity sparseEntity = FirstSparseEntity<SparseA>(world);
        int callbacks = 0;
        {
            for (int i = 0; i < 128; i++)
            {
                ExecuteCountedRead(world, ref callbacks);
                ExecuteCountedWrite(world, ref callbacks);
                _ = world.ReadComponentData<SparseA>(sparseEntity);
            }
            for (int i = 0; i < 1_000; i++)
            {
                ExecuteCountedRead(world, ref callbacks);
                ExecuteCountedWrite(world, ref callbacks);
                _ = world.ReadComponentData<SparseA>(sparseEntity);
            }

        }

        Assert.Equal(2_256, callbacks);
    }

    [Fact]
    public void StructureWriteCanBorrowSparseDataAfterALaterSparseJobRegisters()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateWorldWith<SparseA>();
        Entity entity = world.Create();

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
                    using (SparseWriteScope<SparseA> values = world.OpenSparseWrite<SparseA>()) values.Values[0].Value++;
                    nestedBorrowCompleted.Set();
                }
                catch (Exception exception)
                {
                    mutationFault = exception;
                }
            });
            mutationThread.Start();
            Assert.True(hookStarted.Wait(TimeSpan.FromSeconds(5)));

            later = world.Execution!.Scheduler!.Schedule(new SignalWriteJob<SparseA>(world, laterJobStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteSparse<SparseA>()));
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

    private World CreateWorldWith<T>()
        where T : struct, ISparseComponent
    {
        var world = CreateJobWorld();
        AddSparse<T>(world);
        return world;
    }

    private void AddSparse<T>(World world)
        where T : struct, ISparseComponent
    {
        Entity entity = world.Create();
        world.SetSparse(entity, default(T));
    }

    private void ExecuteCountedRead(World world, ref int callbacks)
    {
        {
            using var range = world.OpenSparseRead<SparseA>();
            ReadOnlySpan<Entity> entities = range.Entities;
            ReadOnlySpan<SparseA> values = range.Values;
            callbacks++;
        }
    }

    private void ExecuteCountedWrite(World world, ref int callbacks)
    {
        {
            using var range = world.OpenSparseWrite<SparseA>();
            ReadOnlySpan<Entity> entities = range.Entities;
            Span<SparseA> values = range.Values;
            callbacks++;
        }
    }

    private Entity FirstSparseEntity<T>(World world)
        where T : struct, ISparseComponent
    {
        Entity entity = default;
        {
            using var range = world.OpenSparseRead<T>();
            ReadOnlySpan<Entity> entities = range.Entities;
            ReadOnlySpan<T> values = range.Values;
            entity = entities[0];
        }
        return entity;
    }

    private readonly struct ReadSparseJob<T> : IJob
        where T : struct, ISparseComponent
    {
        private readonly World _world;

        internal ReadSparseJob(World world) => _world = world;

        public void Execute() { using var range = _world.OpenSparseRead<T>(); ReadOnlySpan<Entity> entities = range.Entities; ReadOnlySpan<T> values = range.Values;   }
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
            {
                using var range = _world.OpenSparseRead<T>();
                ReadOnlySpan<Entity> entities = range.Entities;
                ReadOnlySpan<T> values = range.Values;
                state.Block();
            }
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
            {
                using var range = _world.OpenSparseWrite<T>();
                ReadOnlySpan<Entity> entities = range.Entities;
                Span<T> values = range.Values;
                state.Block();
            }
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
            {
                using var range = _world.OpenSparseRead<T>();
                ReadOnlySpan<Entity> entities = range.Entities;
                ReadOnlySpan<T> values = range.Values;
                state.Set();
            }
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
            {
                using var range = _world.OpenSparseWrite<T>();
                ReadOnlySpan<Entity> entities = range.Entities;
                Span<T> values = range.Values;
                state.Set();
            }
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
