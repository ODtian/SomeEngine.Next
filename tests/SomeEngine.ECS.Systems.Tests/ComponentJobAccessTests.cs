using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class ComponentJobAccessTests
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MustRemainBlocked = TimeSpan.FromMilliseconds(100);

    [Theory]
    [InlineData(ComponentEntry.Read)]
    [InlineData(ComponentEntry.Replace)]
    [InlineData(ComponentEntry.Enable)]
    [InlineData(ComponentEntry.Disable)]
    [InlineData(ComponentEntry.IsEnabled)]
    [InlineData(ComponentEntry.GetByIndex)]
    public void DirectComponentEntryInsideJob_RejectsMissingTypedCapability(
        ComponentEntry entry)
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        JobHandle handle = JobSystem.Schedule(
            new ComponentEntryJob(world, entity, entry),
            RelationshipJobAccess.TopologyRead(world));

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void DirectComponentEntryInsideJob_RejectsMissingTopologyCapability()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        JobHandle handle = JobSystem.Schedule(
            new ReadJob<TableA>(world, entity),
            ComponentJobAccess<TableA>.Read(world));

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void ReadCapability_DoesNotAuthorizeComponentWrite()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[2];
        accesses[0] = ComponentJobAccess<TableA>.Read(world);
        accesses[1] = RelationshipJobAccess.TopologyRead(world);
        JobHandle handle = JobSystem.Schedule(
            new ReplaceJob<TableA>(world, entity, new TableA { Value = 7 }),
            accesses);

        Assert.Throws<JobResourceSafetyException>(() => handle.Complete());
    }

    [Fact]
    public void HasInsideJob_RequiresOnlyTopologyRead()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        var capture = new BoolCapture();
        JobHandle handle = JobSystem.Schedule(
            new HasJob<TableA>(world, entity, capture),
            RelationshipJobAccess.TopologyRead(world));

        handle.Complete();

        Assert.True(capture.Value);
    }

    [Fact]
    public void SameWorldSameComponent_ReadOwnersRunConcurrently()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        World world = CreateWorld(out Entity entity);
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        try
        {
            first = ComponentJobAccess<TableA>.ScheduleRead(
                world,
                new BlockingReadJob<TableA>(
                    world,
                    entity,
                    firstStarted,
                    releaseFirst));
            Assert.True(firstStarted.Wait(StartTimeout));

            JobHandle second = ComponentJobAccess<TableA>.ScheduleRead(
                world,
                new SignalReadJob<TableA>(world, entity, secondStarted));
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
        using var runtime = new JobRuntimeScope(workerCount: 4);
        World world = CreateWorld(out Entity entity);
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        using var laterWriterStarted = new ManualResetEventSlim();
        JobHandle writer = default;
        JobHandle reader = default;
        JobHandle laterWriter = default;
        try
        {
            writer = ComponentJobAccess<TableA>.ScheduleWrite(
                world,
                new BlockingReplaceJob<TableA>(
                    world,
                    entity,
                    new TableA { Value = 2 },
                    writerStarted,
                    releaseWriter));
            Assert.True(writerStarted.Wait(StartTimeout));

            reader = ComponentJobAccess<TableA>.ScheduleRead(
                world,
                new SignalReadJob<TableA>(world, entity, readerStarted));
            laterWriter = ComponentJobAccess<TableA>.ScheduleWrite(
                world,
                new SignalReplaceJob<TableA>(
                    world,
                    entity,
                    new TableA { Value = 3 },
                    laterWriterStarted));
            Assert.False(readerStarted.Wait(MustRemainBlocked));
            Assert.False(laterWriterStarted.Wait(MustRemainBlocked));

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
    }

    [Fact]
    public void DifferentComponentTypesAndWorlds_DoNotShareTypedResource()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        World world = CreateWorld(out Entity entity);
        World otherWorld = CreateWorld(out Entity otherWorldEntity);
        using var blockerStarted = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();
        using var otherTypeStarted = new ManualResetEventSlim();
        using var otherWorldStarted = new ManualResetEventSlim();
        JobHandle blocker = default;
        try
        {
            blocker = ComponentJobAccess<TableA>.ScheduleWrite(
                world,
                new BlockingReplaceJob<TableA>(
                    world,
                    entity,
                    new TableA { Value = 2 },
                    blockerStarted,
                    releaseBlocker));
            Assert.True(blockerStarted.Wait(StartTimeout));

            JobHandle otherType = ComponentJobAccess<TableB>.ScheduleWrite(
                world,
                new SignalReplaceJob<TableB>(
                    world,
                    entity,
                    new TableB { Value = 4 },
                    otherTypeStarted));
            JobHandle otherWorldHandle = ComponentJobAccess<TableA>.ScheduleWrite(
                otherWorld,
                new SignalReplaceJob<TableA>(
                    otherWorld,
                    otherWorldEntity,
                    new TableA { Value = 5 },
                    otherWorldStarted));

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
        using var runtime = new JobRuntimeScope(workerCount: 4);
        World world = CreateWorld(out Entity entity);
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var directCompleted = new ManualResetEventSlim();
        Exception? directFault = null;
        JobHandle writer = ComponentJobAccess<TableA>.ScheduleWrite(
            world,
            new BlockingReplaceJob<TableA>(
                world,
                entity,
                new TableA { Value = 6 },
                writerStarted,
                releaseWriter));
        Thread direct = new(() =>
        {
            try
            {
                _ = world.Read<TableA>(entity);
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
            Assert.False(directCompleted.Wait(MustRemainBlocked));

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
    }

    [Fact]
    public void DirectReplace_HoldsTypedOwnerThroughHookAndBlocksScheduledReader()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        World world = CreateWorld(out Entity entity);
        _ = ComponentJobAccess<TableA>.Write(world);
        using var hookStarted = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        using var scheduledStarted = new ManualResetEventSlim();
        Exception? directFault = null;
        world.Hooks<TableA>().OnReplace(
            (DeferredWorld _, Entity _, in TableA _) =>
            {
                hookStarted.Set();
                releaseHook.Wait();
            });
        Thread direct = new(() =>
        {
            try
            {
                world.Replace(entity, new TableA { Value = 7 });
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
            scheduled = ComponentJobAccess<TableA>.ScheduleRead(
                world,
                new SignalReadJob<TableA>(world, entity, scheduledStarted));
            Assert.False(scheduledStarted.Wait(MustRemainBlocked));

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
    }

    [Fact]
    public void UnrelatedComponentHook_DoesNotUpgradeValueReplaceToTopologyWrite()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        world.Hooks<TableB>().OnReplace(
            static (DeferredWorld _, Entity _, in TableB _) => { });

        ComponentJobAccess<TableA>.ScheduleWrite(
            world,
            new ReplaceJob<TableA>(world, entity, new TableA { Value = 21 })).Complete();

        Assert.Equal(21, world.Read<TableA>(entity).Value);
    }

    [Fact]
    public void SameComponentIrrelevantHookEvents_DoNotUpgradeValueReplaceToTopologyWrite()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        int callbackCount = 0;
        world.Hooks<TableA>()
            .OnAdd((DeferredWorld _, Entity _, in TableA _) => callbackCount++)
            .OnRemove((DeferredWorld _, Entity _, in TableA _) => callbackCount++);

        ComponentJobAccess<TableA>.ScheduleWrite(
            world,
            new ReplaceJob<TableA>(world, entity, new TableA { Value = 22 })).Complete();

        Assert.Equal(22, world.Read<TableA>(entity).Value);
        Assert.Equal(0, callbackCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ValueReplaceHook_RequiresExplicitTopologyWrite(bool insertHook)
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        int callbackCount = 0;
        ComponentHooks<TableA> hooks = world.Hooks<TableA>();
        if (insertHook)
        {
            hooks.OnInsert(
                (DeferredWorld _, Entity _, in TableA _) => callbackCount++);
        }
        else
        {
            hooks.OnReplace(
                (DeferredWorld _, Entity _, in TableA _) => callbackCount++);
        }

        JobHandle underDeclared = ComponentJobAccess<TableA>.ScheduleWrite(
            world,
            new ReplaceJob<TableA>(world, entity, new TableA { Value = 23 }));

        Assert.Throws<JobResourceSafetyException>(() => underDeclared.Complete());
        Assert.Equal(1, world.Read<TableA>(entity).Value);
        Assert.Equal(0, callbackCount);

        Span<JobResourceAccess> exactAccesses = stackalloc JobResourceAccess[2];
        exactAccesses[0] = ComponentJobAccess<TableA>.Write(world);
        exactAccesses[1] = RelationshipJobAccess.TopologyWrite(world);
        JobSystem.Schedule(
            new ReplaceJob<TableA>(world, entity, new TableA { Value = 24 }),
            exactAccesses).Complete();

        Assert.Equal(24, world.Read<TableA>(entity).Value);
        Assert.Equal(1, callbackCount);
    }

    [Fact]
    public void OrdinaryTableQuery_UsesMatchingTypedReadAndWriteCapabilities()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        QueryHandle readQuery = world.Query(
            new QueryDefinitionBuilder().Read<TableA>().Build());
        QueryHandle writeQuery = world.Query(
            new QueryDefinitionBuilder().Write<TableA>().Build());
        var readCapture = new IntCapture();

        ComponentJobAccess<TableA>.ScheduleRead(
            world,
            new ReadQueryJob<TableA>(world, readQuery, readCapture)).Complete();
        Assert.Equal(1, readCapture.Value);

        Span<JobResourceAccess> readOnly = stackalloc JobResourceAccess[2];
        readOnly[0] = ComponentJobAccess<TableA>.Read(world);
        readOnly[1] = RelationshipJobAccess.TopologyRead(world);
        JobHandle underDeclared = JobSystem.Schedule(
            new WriteQueryJob<TableA>(
                world,
                writeQuery,
                new TableA { Value = 8 }),
            readOnly);
        Assert.Throws<JobResourceSafetyException>(() => underDeclared.Complete());

        ComponentJobAccess<TableA>.ScheduleWrite(
            world,
            new WriteQueryJob<TableA>(
                world,
                writeQuery,
                new TableA { Value = 9 })).Complete();
        Assert.Equal(9, world.Read<TableA>(entity).Value);
    }

    [Fact]
    public void BufferQuery_StillUsesOneBufferLogicalCapability()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        world.AddBuffer<Element>(entity);
        QueryHandle query = world.Query(
            new QueryDefinitionBuilder().ReadBuffer<Element>().Build());

        BufferJobAccess<Element>.ScheduleRead(
            world,
            new EmptyQueryJob(world, query)).Complete();
    }

    [Fact]
    public void RelationshipSchedulers_UseTheSameTableComponentKeys()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Entity target = world.CreateEntity();
        Hierarchy<Domain>.SetParent(world, child, parent);
        RelationEdge<Payload> edge = world.CreateRelation(
            child,
            target,
            new Payload { Value = 10 });
        var parentCapture = new EntityCapture();
        var endpointCapture = new EndpointsCapture();

        ComponentJobAccess<Parent<Domain>>.ScheduleRead(
            world,
            new ParentViaRelationshipAccessJob(world, child, parentCapture)).Complete();
        ComponentJobAccess<DirectedRelationEndpoints<Payload>>.ScheduleRead(
            world,
            new EndpointsViaRelationshipAccessJob(world, edge, endpointCapture)).Complete();

        Assert.Equal(parent, parentCapture.Value);
        Assert.Equal(child, endpointCapture.Source);
        Assert.Equal(target, endpointCapture.Target);
    }

    [Fact]
    public void WarmedDirectComponentAdmission_DoesNotAllocate()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateWorld(out Entity entity);
        _ = ComponentJobAccess<TableA>.Write(world);
        _ = ComponentJobAccess<EnabledA>.Write(world);
        _ = ComponentJobAccess<IndexedA>.Read(world);
        {
            for (int i = 0; i < 128; i++)
                ExerciseDirectEntries(world, entity, i);

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 1_000; i++)
                ExerciseDirectEntries(world, entity, i);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(0, allocated);
        }
    }

    [Fact]
    public void Clock_AcquireIsUniqueAndMonotonicAcrossThreads()
    {
        var world = new World();
        const int threadCount = 8;
        const int ticksPerThread = 4_096;
        var ticks = new uint[threadCount * ticksPerThread];
        var threads = new Thread[threadCount];
        using var start = new ManualResetEventSlim();

        for (int threadIndex = 0; threadIndex < threadCount; threadIndex++)
        {
            int offset = threadIndex * ticksPerThread;
            threads[threadIndex] = new Thread(() =>
            {
                start.Wait();
                for (int i = 0; i < ticksPerThread; i++)
                    ticks[offset + i] = world.AcquireSystemTick();
            });
            threads[threadIndex].Start();
        }

        start.Set();
        for (int i = 0; i < threads.Length; i++)
            Assert.True(threads[i].Join(StartTimeout));

        Array.Sort(ticks);
        for (int i = 0; i < ticks.Length; i++)
            Assert.Equal(unchecked((uint)(i + 1)), ticks[i]);
        Assert.Equal(unchecked((uint)(ticks.Length + 1)), world.CurrentTick);
    }

    private static World CreateWorld(out Entity entity)
    {
        var world = new World();
        entity = world.CreateEntity(new TableA { Value = 1 });
        world.Add(entity, new TableB { Value = 2 });
        world.Add(entity, new EnabledA { Value = 3 });
        world.Add(entity, new IndexedA { Key = 4 });
        _ = world.GetByIndex<IndexedA, int>(4);
        return world;
    }

    private static void ExerciseDirectEntries(World world, Entity entity, int iteration)
    {
        _ = world.Read<TableA>(entity);
        world.Replace(entity, new TableA { Value = iteration });
        world.Disable<EnabledA>(entity);
        world.Enable<EnabledA>(entity);
        _ = world.IsEnabled<EnabledA>(entity);
        _ = world.GetByIndex<IndexedA, int>(4).Length;
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
                    _ = _world.Read<TableA>(_entity);
                    break;
                case ComponentEntry.Replace:
                    _world.Replace(_entity, new TableA { Value = 11 });
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
                    _ = _world.GetByIndex<IndexedA, int>(4).Length;
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

        public void Execute() => _ = _world.Read<T>(_entity);
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

        public void Execute() => _world.Replace(_entity, in _value);
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
            _ = _world.Read<T>(_entity);
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
            _ = _world.Read<T>(_entity);
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
            _world.Replace(_entity, in _value);
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
            _world.Replace(_entity, in _value);
            _started.Set();
        }
    }

    private readonly struct ReadQueryJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly IntCapture _capture;

        internal ReadQueryJob(World world, QueryHandle query, IntCapture capture)
        {
            _world = world;
            _query = query;
            _capture = capture;
        }

        public void Execute()
        {
            int count = 0;
            _world.ExecuteQuery(
                _query,
                ref count,
                static (QueryCursor cursor, ref int result) =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                        result += chunk.Read<T>().Length;
                });
            _capture.Value = count;
        }
    }

    private readonly struct WriteQueryJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly T _value;

        internal WriteQueryJob(World world, QueryHandle query, in T value)
        {
            _world = world;
            _query = query;
            _value = value;
        }

        public void Execute()
        {
            T value = _value;
            _world.ExecuteQuery(
                _query,
                ref value,
                static (QueryCursor cursor, ref T replacement) =>
                {
                    foreach (QueryChunkView chunk in cursor.Chunks)
                        chunk.Write<T>().Fill(replacement);
                });
        }
    }

    private readonly struct EmptyQueryJob : IJob
    {
        private readonly World _world;
        private readonly QueryHandle _query;

        internal EmptyQueryJob(World world, QueryHandle query)
        {
            _world = world;
            _query = query;
        }

        public void Execute() => _world.ExecuteQuery(_query, static _ => { });
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
            _capture.Value = HierarchyJobAccess<Domain>.GetParent(_world, _child);
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
            DirectedRelationEndpoints<Payload> endpoints =
                RelationJobAccess<Payload>.GetDirectedEndpoints(_world, _edge);
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
