using ManualResetEventSlim = SomeEngine.Testing.TestSignal;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class ComponentJobAccessTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(ComponentEntry.Read)]
    [InlineData(ComponentEntry.Replace)]
    [InlineData(ComponentEntry.Enable)]
    [InlineData(ComponentEntry.Disable)]
    [InlineData(ComponentEntry.IsEnabled)]
    [InlineData(ComponentEntry.GetByIndex)]
    public void DirectComponentEntriesUseTheirDataAndStructureDeclarations(
        ComponentEntry entry)
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);
        JobHandle handle = jobs.Scheduler.Schedule(
            new ComponentEntryJob(world, entity, entry),
            world.Execution!.StructureRead);

        if (entry is ComponentEntry.GetByIndex or ComponentEntry.IsEnabled)
        {
            handle.Complete();
            Assert.True(world.IsEnabled<EnabledA>(entity));
            Assert.Equal(1, CountIndexed(world, 4));
        }
        else Assert.Throws<JobResourceSafetyException>(handle.Complete);
        Assert.Equal(1, world.ReadComponentData<TableA>(entity).Value);
    }

    [Fact]
    public void DirectComponentEntryInsideJob_RejectsMissingTopologyCapability()
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);
        JobHandle handle = jobs.Scheduler.Schedule(
            new ReadJob<TableA>(world, entity),
            ((WorldJobs)world.Execution!).Read<TableA>());

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void ReadCapability_DoesNotAuthorizeComponentWrite()
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);
        JobResourceAccess[] accesses = [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<TableA>()];
        JobHandle handle = jobs.Scheduler.Schedule(
            new ReplaceJob<TableA>(world, entity, new TableA { Value = 7 }),
            accesses);

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void HasInsideJob_RequiresOnlyTopologyRead()
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);
        var capture = new BoolCapture();
        JobHandle handle = jobs.Scheduler.Schedule(
            new HasJob<TableA>(world, entity, capture),
            world.Execution!.StructureRead);

        handle.Complete();

        Assert.True(capture.Value);
    }

    [Fact]
    public void SameWorldSameComponent_ReadOwnersRunConcurrently()
    {
        using var jobs = new TestJobs(workerCount: 4);
        World world = CreateWorld(jobs, out Entity entity);
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        try
        {
            first = world.Execution!.Scheduler!.Schedule(new BlockingReadJob<TableA>(
                    world,
                    entity,
                    firstStarted,
                    releaseFirst), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<TableA>()]);
            Assert.True(firstStarted.Wait(StartTimeout));

            JobHandle second = world.Execution!.Scheduler!.Schedule(new SignalReadJob<TableA>(world, entity, secondStarted), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<TableA>()]);
            Assert.True(secondStarted.Wait(StartTimeout));
            second.Complete();
        }
        finally
        {
            releaseFirst.Set();
            first.Complete();
        }
    }

    [Fact]
    public void SameWorldSameComponent_WriteOwnerBlocksReadersAndWriters()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var jobs = new TestJobs(workerCount: 4);
        World world = CreateWorld(jobs, out Entity entity);
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        releaseWriter.WaitCallback = () => events.Enqueue("released");
        using var readerStarted = new ManualResetEventSlim();
        readerStarted.SetCallback = () => events.Enqueue("readerStarted");
        using var laterWriterStarted = new ManualResetEventSlim();
        laterWriterStarted.SetCallback = () => events.Enqueue("laterWriterStarted");
        JobHandle writer = default;
        JobHandle reader = default;
        JobHandle laterWriter = default;
        try
        {
            writer = world.Execution!.Scheduler!.Schedule(new BlockingReplaceJob<TableA>(
                    world,
                    entity,
                    new TableA { Value = 2 },
                    writerStarted,
                    releaseWriter), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableA>()));
            Assert.True(writerStarted.Wait(StartTimeout));

            reader = world.Execution!.Scheduler!.Schedule(new SignalReadJob<TableA>(world, entity, readerStarted), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<TableA>()]);
            laterWriter = world.Execution!.Scheduler!.Schedule(new SignalReplaceJob<TableA>(
                    world,
                    entity,
                    new TableA { Value = 3 },
                    laterWriterStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableA>()));

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
            reader.Complete();
            laterWriter.Complete();
        }

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("readerStarted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "readerStarted"));
        Assert.Contains("laterWriterStarted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "laterWriterStarted"));
    }

    [Fact]
    public void DifferentComponentTypesAndWorlds_DoNotShareTypedResource()
    {
        using var jobs = new TestJobs(workerCount: 4);
        World world = CreateWorld(jobs, out Entity entity);
        World otherWorld = CreateWorld(jobs, out Entity otherWorldEntity);
        using var blockerStarted = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();
        using var otherTypeStarted = new ManualResetEventSlim();
        using var otherWorldStarted = new ManualResetEventSlim();
        JobHandle blocker = default;
        try
        {
            blocker = world.Execution!.Scheduler!.Schedule(new BlockingReplaceJob<TableA>(
                    world,
                    entity,
                    new TableA { Value = 2 },
                    blockerStarted,
                    releaseBlocker), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableA>()));
            Assert.True(blockerStarted.Wait(StartTimeout));

            JobHandle otherType = world.Execution!.Scheduler!.Schedule(new SignalReplaceJob<TableB>(
                    world,
                    entity,
                    new TableB { Value = 4 },
                    otherTypeStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableB>()));
            JobHandle otherWorldHandle = otherWorld.Execution!.Scheduler!.Schedule(new SignalReplaceJob<TableA>(
                    otherWorld,
                    otherWorldEntity,
                    new TableA { Value = 5 },
                    otherWorldStarted), ((WorldJobs)otherWorld.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableA>()));

            Assert.True(otherTypeStarted.Wait(StartTimeout));
            Assert.True(otherWorldStarted.Wait(StartTimeout));
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
    public void ScheduledWriter_BlocksDirectReadUntilScheduledOwnerReturns()
    {
        using var attempted = new ManualResetEventSlim();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var jobs = new TestJobs(workerCount: 4);
        World world = CreateWorld(jobs, out Entity entity);
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        releaseWriter.WaitCallback = () => events.Enqueue("released");
        using var directCompleted = new ManualResetEventSlim();
        directCompleted.SetCallback = () => events.Enqueue("directCompleted");
        Exception? directFault = null;
        JobHandle writer = world.Execution!.Scheduler!.Schedule(new BlockingReplaceJob<TableA>(
                world,
                entity,
                new TableA { Value = 6 },
                writerStarted,
                releaseWriter), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableA>()));
        Thread direct = new(() =>
        {
            attempted.Set();
            try
            {
                _ = world.ReadComponentData<TableA>(entity);
            }
            catch (Exception exception)
            {
                directFault = exception;
            }
            finally
            {
                directCompleted.Set();
            }
        });
        try
        {
            Assert.True(writerStarted.Wait(StartTimeout));
            direct.Start();
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));

            releaseWriter.Set();
            writer.Complete();
            Assert.True(direct.Join(StartTimeout));
            Assert.True(directCompleted.IsSet);
            Assert.Null(directFault);
        }
        finally
        {
            releaseWriter.Set();
            writer.Complete();
            if (direct.ThreadState != ThreadState.Unstarted)
                direct.Join(StartTimeout);
        }

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("directCompleted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "directCompleted"));
    }

    [Fact]
    public void ExplicitValueWriteScopeBlocksAScheduledReaderUntilItEnds()
    {
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var jobs = new TestJobs(workerCount: 4);
        World world = CreateWorld(jobs, out Entity entity);

        using var hookStarted = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        releaseHook.WaitCallback = () => events.Enqueue("released");
        using var scheduledStarted = new ManualResetEventSlim();
        scheduledStarted.SetCallback = () => events.Enqueue("scheduledStarted");
        Exception? directFault = null;
        Thread direct = new(() =>
        {
            try
            {
                using WriteLookup<TableA> write = world.OpenWrite<TableA>();
                hookStarted.Set();
                Assert.True(releaseHook.Wait(StartTimeout));
                write.Set(entity, new TableA { Value = 7 });
            }
            catch (Exception exception)
            {
                directFault = exception;
            }
        });
        JobHandle scheduled = default;
        try
        {
            direct.Start();
            Assert.True(hookStarted.Wait(StartTimeout));
            scheduled = world.Execution!.Scheduler!.Schedule(new SignalReadJob<TableA>(world, entity, scheduledStarted), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<TableA>()]);

            releaseHook.Set();
            Assert.True(direct.Join(StartTimeout));
            scheduled.Complete();
            Assert.True(scheduledStarted.IsSet);
            Assert.Null(directFault);
        }
        finally
        {
            releaseHook.Set();
            direct.Join(StartTimeout);
            scheduled.Complete();
        }

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("scheduledStarted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "scheduledStarted"));
    }

    [Fact]
    public void UnrelatedMembershipObservationKeepsAValueWriteWithinItsDeclaredData()
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);
        using EventReader<MembershipChange> reader = world.WatchMembership(world.Query<TableB>());
        jobs.Scheduler.Schedule(new ReplaceJob<TableA>(world, entity, new TableA { Value = 21 }),
            ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableA>())).Complete();
        Assert.Equal(21, world.ReadComponentData<TableA>(entity).Value);
        using EventRead<MembershipChange> changes = reader.Open();
        Assert.Equal(0, changes.Count);
    }

    [Fact]
    public void ReplacingAValuePreservesItsQueryMembership()
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);
        using EventReader<MembershipChange> reader = world.WatchMembership(world.Query<TableA>());
        jobs.Scheduler.Schedule(new ReplaceJob<TableA>(world, entity, new TableA { Value = 22 }),
            ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableA>())).Complete();
        Assert.Equal(22, world.ReadComponentData<TableA>(entity).Value);
        using EventRead<MembershipChange> changes = reader.Open();
        Assert.Equal(0, changes.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AConcreteStageDeclaresItsStructureWrites(bool add)
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);
        if (!add) world.Add(entity, default(EditProbe));
        using EventReader<MembershipChange> reader = world.WatchMembership(world.Query<EditProbe>());
        JobResourceAccess[] values = ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableA>());
        JobHandle rejected = add
            ? jobs.Scheduler.Schedule(new ReplaceAndAdd(world, entity, 23), values)
            : jobs.Scheduler.Schedule(new ReplaceAndRemove(world, entity, 23), values);
        Assert.Throws<JobResourceSafetyException>(rejected.Complete);
        Assert.Equal(1, world.ReadComponentData<TableA>(entity).Value);
        JobHandle complete = add
            ? jobs.Scheduler.Schedule(new ReplaceAndAdd(world, entity, 24), world.Execution!.StructureWrite)
            : jobs.Scheduler.Schedule(new ReplaceAndRemove(world, entity, 24), world.Execution!.StructureWrite);
        complete.Complete();
        Assert.Equal(24, world.ReadComponentData<TableA>(entity).Value);
        using EventRead<MembershipChange> changes = reader.Open();
        Assert.Equal(1, changes.Count);
        Assert.Equal(add, world.Has<EditProbe>(entity));
    }

    [Fact]
    public void OrdinaryTableQuery_UsesMatchingTypedReadAndWriteCapabilities()
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);
        Query readQuery = world.Query(
            new QueryDescription().WithStored<TableA>());
        Query writeQuery = world.Query(
            new QueryDescription().WithStored<TableA>());
        var readCapture = new IntCapture();

        world.Execution!.Scheduler!.Schedule(new ReadQueryJob<TableA>(world, readQuery, readCapture), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<TableA>()]).Complete();
        Assert.Equal(1, readCapture.Value);

        JobResourceAccess[] readOnly = [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<TableA>()];
        JobHandle underDeclared = jobs.Scheduler.Schedule(
            new WriteQueryJob<TableA>(
                world,
                writeQuery,
                new TableA { Value = 8 }),
            readOnly);
        Assert.Throws<JobResourceSafetyException>(() => underDeclared.Complete());

        world.Execution!.Scheduler!.Schedule(new WriteQueryJob<TableA>(
                world,
                writeQuery,
                new TableA { Value = 9 }), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<TableA>())).Complete();
        Assert.Equal(9, world.ReadComponentData<TableA>(entity).Value);
    }

    [Fact]
    public void BufferQueryWithDeclaredAccessDoesNotThrow()
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);
        world.AddBuffer<Element>(entity);
        Query query = world.Query(
            new QueryDescription().WithStored<Buffer<Element>>());

        world.Execution!.Scheduler!.Schedule(new EmptyQueryJob(world, query), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().ReadRandom<Buffer<Element>>())).Complete();
    }

    [Fact]
    public void RelationshipSchedulers_UseTheSameTableComponentKeys()
    {
        using var jobs = new TestJobs(workerCount: 2);
        var world = jobs.CreateWorld();
        Entity parent = world.Create();
        Entity child = world.Create();
        Entity target = world.Create();
        Hierarchy<Domain>.SetParent(world, child, parent);
        RelationEdge<Payload> edge = world.CreateRelation(
            child,
            target,
            new Payload { Value = 10 });
        var parentCapture = new EntityCapture();
        var endpointCapture = new EndpointsCapture();

        world.Execution!.Scheduler!.Schedule(new ParentViaRelationshipAccessJob(world, child, parentCapture), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<Parent<Domain>>()]).Complete();
        world.Execution!.Scheduler!.Schedule(new EndpointsViaRelationshipAccessJob(world, edge, endpointCapture), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<RelationEndpoints<Payload>>()]).Complete();

        Assert.Equal(parent, parentCapture.Value);
        Assert.Equal(child, endpointCapture.Source);
        Assert.Equal(target, endpointCapture.Target);
    }

    [Fact]
    public void RepeatedDirectComponentAccessDoesNotThrow()
    {
        using var jobs = new TestJobs(workerCount: 2);
        World world = CreateWorld(jobs, out Entity entity);



        {
            for (int i = 0; i < 128; i++)
                ExerciseDirectEntries(world, entity, i);
            for (int i = 0; i < 1_000; i++)
                ExerciseDirectEntries(world, entity, i);

        }
    }

    [Fact]
    public void Clock_AcquireIsUniqueAndMonotonicAcrossThreads()
    {
        using var jobs = new TestJobs(workerCount: 2);
        var world = jobs.CreateWorld();
        const int threadCount = 8;
        const int ticksPerThread = 4_096;
        var ticks = new ulong[threadCount * ticksPerThread];
        var threads = new Thread[threadCount];
        using var start = new ManualResetEventSlim();

        for (int threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            int offset = threadIndex * ticksPerThread;
            threads[threadIndex] = new Thread(() =>
            {
                start.Wait();
                for (int i = 0; i < ticksPerThread; i++)
                    ticks[offset + i] = world.AcquireSystemVersion();
            });
            threads[threadIndex].Start();
        }

        start.Set();
        for (int i = 0; i < threads.Length; i++)
            Assert.True(threads[i].Join(StartTimeout));

        Array.Sort(ticks);
        for (int i = 0; i < ticks.Length; i++)
            Assert.Equal(unchecked((ulong)(i + 1)), ticks[i]);
        Assert.Equal((ulong)ticks.Length, world.Version);
    }

    private static World CreateWorld(TestJobs jobs, out Entity entity)
    {
        var world = jobs.CreateWorld();
        entity = world.Create(new TableA { Value = 1 });
        world.Add(entity, new TableB { Value = 2 });
        world.Add(entity, new EnabledA { Value = 3 });
        world.Add(entity, new IndexedA { Key = 4 });
        _ = CountIndexed(world, 4);
        return world;
    }

    private static int CountIndexed(World world, int key)
    {
        using var members = world.Index<IndexedA, int>().Find(key);
        return members.Entities.Length;
    }

    private static void ExerciseDirectEntries(World world, Entity entity, int iteration)
    {
        _ = world.ReadComponentData<TableA>(entity);
        world.Set(entity, new TableA { Value = iteration });
        world.Disable<EnabledA>(entity);
        world.Enable<EnabledA>(entity);
        _ = world.IsEnabled<EnabledA>(entity);
        _ = CountIndexed(world, 4);
    }

    public enum ComponentEntry
    {
        Read,
        Replace,
        Enable,
        Disable,
        IsEnabled,
        GetByIndex,
    }

    private readonly struct ComponentEntryJob : IJob
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly ComponentEntry _entry;

        internal ComponentEntryJob(World world, Entity entity, ComponentEntry entry)
        {
            _world = world;
            _entity = entity;
            _entry = entry;
        }

        public void Execute()
        {
            switch (_entry)
            {
                case ComponentEntry.Read:
                    _ = _world.ReadComponentData<TableA>(_entity);
                    break;
                case ComponentEntry.Replace:
                    _world.Set(_entity, new TableA { Value = 11 });
                    break;
                case ComponentEntry.Enable:
                    _world.Enable<EnabledA>(_entity);
                    break;
                case ComponentEntry.Disable:
                    _world.Disable<EnabledA>(_entity);
                    break;
                case ComponentEntry.IsEnabled:
                    _ = _world.IsEnabled<EnabledA>(_entity);
                    break;
                case ComponentEntry.GetByIndex:
                    _ = CountIndexed(_world, 4);
                    break;
                default:
                    throw new ArgumentOutOfRangeException();
            }
        }
    }

    private readonly struct ReadJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly Entity _entity;

        internal ReadJob(World world, Entity entity)
        {
            _world = world;
            _entity = entity;
        }

        public void Execute() => _ = _world.ReadComponentData<T>(_entity);
    }

    private readonly struct ReplaceJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly T _value;

        internal ReplaceJob(World world, Entity entity, in T value)
        {
            _world = world;
            _entity = entity;
            _value = value;
        }

        public void Execute() => _world.Set(_entity, in _value);
    }

    private readonly struct HasJob<T> : IJob
        where T : struct
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly BoolCapture _capture;

        internal HasJob(World world, Entity entity, BoolCapture capture)
        {
            _world = world;
            _entity = entity;
            _capture = capture;
        }

        public void Execute() => _capture.Value = _world.Has<T>(_entity);
    }

    private readonly struct BlockingReadJob<T> : IJob
        where T : struct, IComponent
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
            _ = _world.ReadComponentData<T>(_entity);
            _started.Set();
            _release.Wait();
        }
    }

    private readonly struct SignalReadJob<T> : IJob
        where T : struct, IComponent
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
            _ = _world.ReadComponentData<T>(_entity);
            _started.Set();
        }
    }

    private readonly struct BlockingReplaceJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly T _value;
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingReplaceJob(
            World world,
            Entity entity,
            in T value,
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _world = world;
            _entity = entity;
            _value = value;
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            _world.Set(_entity, in _value);
            _started.Set();
            _release.Wait();
        }
    }

    private readonly struct SignalReplaceJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly T _value;
        private readonly ManualResetEventSlim _started;

        internal SignalReplaceJob(
            World world,
            Entity entity,
            in T value,
            ManualResetEventSlim started)
        {
            _world = world;
            _entity = entity;
            _value = value;
            _started = started;
        }

        public void Execute()
        {
            _world.Set(_entity, in _value);
            _started.Set();
        }
    }

    private readonly struct ReadQueryJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly Query _query;
        private readonly IntCapture _capture;

        internal ReadQueryJob(World world, Query query, IntCapture capture)
        {
            _world = world;
            _query = query;
            _capture = capture;
        }

        public void Execute()
        {
            int result = 0;
            using QueryScope rows = _query.OpenRead();
            foreach (QueryChunk chunk in rows) result += chunk.Read<T>().Length;
            _capture.Value = result;
        }
    }

    private readonly struct WriteQueryJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly Query _query;
        private readonly T _value;

        internal WriteQueryJob(World world, Query query, in T value)
        {
            _world = world;
            _query = query;
            _value = value;
        }

        public void Execute()
        {
            using QueryScope rows = _query.Open();
            foreach (QueryChunk chunk in rows) chunk.Write<T>().Fill(_value);
        }
    }

    private readonly struct EmptyQueryJob : IJob
    {
        private readonly World _world;
        private readonly Query _query;

        internal EmptyQueryJob(World world, Query query)
        {
            _world = world;
            _query = query;
        }

        public void Execute() { using QueryScope rows = _query.OpenRead(); }
    }

    private readonly struct ParentViaRelationshipAccessJob : IJob
    {
        private readonly World _world;
        private readonly Entity _child;
        private readonly EntityCapture _capture;

        internal ParentViaRelationshipAccessJob(
            World world,
            Entity child,
            EntityCapture capture)
        {
            _world = world;
            _child = child;
            _capture = capture;
        }

        public void Execute() =>
            _capture.Value = Hierarchy<Domain>.GetParent(_world, _child);
    }

    private readonly struct EndpointsViaRelationshipAccessJob : IJob
    {
        private readonly World _world;
        private readonly RelationEdge<Payload> _edge;
        private readonly EndpointsCapture _capture;

        internal EndpointsViaRelationshipAccessJob(
            World world,
            RelationEdge<Payload> edge,
            EndpointsCapture capture)
        {
            _world = world;
            _edge = edge;
            _capture = capture;
        }

        public void Execute()
        {
            RelationEndpoints<Payload> endpoints =
                _world.ReadComponentData<RelationEndpoints<Payload>>(_edge.Entity);
            _capture.Source = endpoints.Source;
            _capture.Target = endpoints.Target;
        }
    }

    private sealed class BoolCapture
    {
        internal bool Value;
    }

    private sealed class IntCapture
    {
        internal int Value;
    }

    private sealed class EntityCapture
    {
        internal Entity Value;
    }

    private sealed class EndpointsCapture
    {
        internal Entity Source;
        internal Entity Target;
    }

    private sealed class TestJobs : IDisposable
    {
        private readonly List<World> _worlds = [];

        internal TestJobs(int workerCount)
        {
            Scheduler = new Scheduler(new JobConfiguration
            {
                WorkerCount = workerCount,
                SafetyMode = JobSafetyMode.Checked,
                ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
            });
        }

        internal Scheduler Scheduler { get; }

        internal World CreateWorld()
        {
            var world = new World(execution: new WorldJobs(Scheduler));
            _worlds.Add(world);
            return world;
        }

        public void Dispose()
        {
            foreach (var world in _worlds)
                world.Dispose();
            Scheduler.Dispose();
        }
    }

    private struct EditProbe : IComponent;
    private readonly struct ReplaceAndAdd(World world, Entity entity, int value) : IJob
    {
        public void Execute()
        {
            using WorldStructureWriteScope structure = world.OpenStructureWrite();
            world.Set(entity, new TableA { Value = value });
            world.Add(entity, default(EditProbe));
        }
    }
    private readonly struct ReplaceAndRemove(World world, Entity entity, int value) : IJob
    {
        public void Execute()
        {
            using WorldStructureWriteScope structure = world.OpenStructureWrite();
            world.Set(entity, new TableA { Value = value });
            world.Remove<EditProbe>(entity);
        }
    }

    private readonly struct Domain : IHierarchyDomain;

    private struct TableA : IComponent
    {
        internal int Value;
    }

    private struct TableB : IComponent
    {
        internal int Value;
    }

    private struct EnabledA : IEnableableComponent
    {
        internal int Value;
    }

    private struct IndexedA : IIndexedComponent<int>
    {
        internal int Key;

        public readonly int GetKey() => Key;
    }

    private struct Element : IBufferElement;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct Payload : IComponent
    {
        internal int Value;
    }
}
