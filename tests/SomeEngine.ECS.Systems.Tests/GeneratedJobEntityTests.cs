using SomeEngine.ECS.Components;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;
using SomeEngine.Job;
using System.Runtime.CompilerServices;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class GeneratedJobEntityTests : JobTest
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void ParallelSparseJobAdvancesComponentWriteVersionForEveryRow(int rowsPerPacket)
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = CreateMovingWorld(32, out Entity[] entities);
        foreach (Entity entity in entities)
            world.SetSparse(entity, new GeneratedSparse { Value = 3 });
        ulong sparseVersion = world.ComponentWriteVersion(ComponentId<GeneratedSparse>.Value);
        ulong positionVersion = world.ComponentWriteVersion(ComponentId<GeneratedPosition>.Value);
        ulong before = world.Version;

        new SystemRunner(world, rangeRows: rowsPerPacket).Schedule(new GeneratedSparseJob(), dependency: default, parallel: true).Complete();

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<GeneratedSparse>.Value));
        Assert.True(world.ComponentWriteVersion(ComponentId<GeneratedSparse>.Value) > sparseVersion);
        Assert.Equal(positionVersion, world.ComponentWriteVersion(ComponentId<GeneratedPosition>.Value));
        Assert.All(entities, entity => Assert.Equal(13, world.ReadComponentData<GeneratedSparse>(entity).Value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void ParallelTableJobAdvancesComponentWriteVersionForEveryRow(int rowsPerPacket)
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = CreateMovingWorld(128, out Entity[] entities);
        ulong positionVersion = world.ComponentWriteVersion(ComponentId<GeneratedPosition>.Value);
        ulong velocityVersion = world.ComponentWriteVersion(ComponentId<GeneratedVelocity>.Value);
        ulong before = world.Version;

        new SystemRunner(world, rangeRows: rowsPerPacket).Schedule(new IntegrateGeneratedJob(), dependency: default, parallel: true).Complete();

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<GeneratedPosition>.Value));
        Assert.True(world.ComponentWriteVersion(ComponentId<GeneratedPosition>.Value) > positionVersion);
        Assert.Equal(velocityVersion, world.ComponentWriteVersion(ComponentId<GeneratedVelocity>.Value));
        Assert.All(entities, entity => Assert.Equal(1, world.ReadComponentData<GeneratedPosition>(entity).Value));
    }


    [Fact]
    public void GeneratedCommandsUseSeparateBuffersForEachProducer()
    {
        World world = CreateMovingWorld(8, out Entity[] entities);
        using var commands = new CommandBatch(world, entities.Max(entity => entity.Id) + 1);
        using var data = new CallbackData<GeneratedCommandData>(new(commands));
        new GeneratedCommandJob(data).Schedule(world).Complete();
        Assert.All(entities, entity => Assert.Equal(0, world.ReadComponentData<GeneratedPosition>(entity).Value));
        Assert.Equal(entities.Length, entities.Sum(entity => commands[entity.Id].Count));
        commands.Playback();
        Assert.All(entities, entity => Assert.Equal(99, world.ReadComponentData<GeneratedPosition>(entity).Value));
        commands.Clear();
        new SystemRunner(world, rangeRows: 1).Schedule(new GeneratedCommandJob(data), parallel: true).Complete();
        Assert.Equal(entities.Length, entities.Sum(entity => commands[entity.Id].Count));
        commands.Playback();
        Assert.All(entities, entity => Assert.Equal(99, world.ReadComponentData<GeneratedPosition>(entity).Value));
    }

    [Fact]
    public async Task GeneratedCallbacksFinishBeforeStructuralMutation()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(16, out _);
        using var attempted = new ManualResetEventSlim();
        using var data = new CallbackData<GeneratedTopologyData>(new GeneratedTopologyData(16));
        JobHandle handle = new SystemRunner(world, rangeRows: 1).Schedule(new GeneratedTopologyJob(data), dependency: default, parallel: true);
        Task<Entity>? writer = null;
        try
        {
            Assert.True(data.Value.Started.Wait(TimeSpan.FromSeconds(5)));
            writer = Task.Run(() =>
            {
                attempted.Set();
                var entity = world.Create();
                data.Value.Events.Enqueue("entity created");
                return entity;
            });
            Assert.True(attempted.Wait(TimeSpan.FromSeconds(5)));
            data.Value.Release.Set();
            handle.Complete();
            var created = await writer.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(world.IsAlive(created));
            Assert.Equal(new[] { "callbacks finished", "entity created" }, data.Value.Events.ToArray());
        }
        finally
        {
            data.Value.Release.Set();
            handle.Complete();
            if (writer is not null)
                await writer.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void RuntimeBoundaryHoist_StillRejectsAnUndeclaredSparseBorrow()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.Create(new GeneratedPosition { Value = 4 });
        world.SetSparse(entity, new GeneratedSparse { Value = 9 });
        var job = new GeneratedBoundaryProbeJob(world, entity);
        JobHandle handle = job.ScheduleParallel(world);
        Assert.Throws<JobResourceSafetyException>(handle.Complete);

        Assert.Equal(9, world.ReadComponentData<GeneratedSparse>(entity).Value);
    }

    [Fact]
    public void FilterReadsSelectRowsAndKeepTheirValuesReadonlyToTheAction()
    {
        World world = CreateMovingWorld(1, out Entity[] entities);
        Query query = world.Query(new QueryDescription().WithChanged<GeneratedVelocity>(0));
        using var reads = new CallbackData<GeneratedReadCount>(new());
        new GeneratedReadOnlyProbeJob(reads).Schedule(world, query: query).Complete();
        Assert.Equal(1, reads.Value.Count);
        Assert.Throws<JobResourceSafetyException>(() =>
            new GeneratedFilterWriteProbe(world, entities[0]).Schedule(world, query: query).Complete());
        Assert.Equal(1, world.ReadComponentData<GeneratedVelocity>(entities[0]).Value);
    }

    [Fact]
    public void DirectManagedStorageRequiresConcreteResourceDeclarations()
    {
        World world = CreateJobWorld();
        world.Create(new GeneratedManagedComponent { Value = "shared data" });
        Assert.Throws<InvalidOperationException>(() =>
            ((WorldJobs)world.Execution!).CollectReadAccess(world.Query<GeneratedManagedComponent>()));
    }

    [Fact]
    public void ExplicitFilter_ComposesSelectionWithoutAddingValueAccess()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(8, out Entity[] entities);
        for (int i = 0; i < entities.Length; i += 2)
            world.Add(entities[i], default(GeneratedExcludedTag));
        QueryDescription filter = new QueryDescription().Without<GeneratedExcludedTag>();

        new SystemRunner(world, rangeRows: 2).Schedule(new IntegrateGeneratedJob(), dependency: default, parallel: true, query: world.Query(filter)).Complete();

        for (int i = 0; i < entities.Length; i++)
        {
            Assert.Equal((i & 1) == 0 ? 0 : 1, world.ReadComponentData<GeneratedPosition>(entities[i]).Value);
        }

        QueryDescription invalidAccess = new QueryDescription().WithStored<GeneratedVelocity>();
        Assert.Throws<JobResourceSafetyException>(() =>
            new GeneratedFilterWriteProbe(world, entities[1]).Schedule(world, query: world.Query(invalidAccess)).Complete());
    }

    [Fact]
    public void ExactChangedFilter_RunsAfterRangeHazardsAndBeforeCurrentRowWrite()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(10, out Entity[] entities);
        ulong baseline = world.AcquireSystemVersion();
        for (int i = 0; i < entities.Length; i += 2)
            world.Set(entities[i], new GeneratedVelocity { Value = 2 });
        QueryDescription filter = new QueryDescription().WithChanged<GeneratedVelocity>(0);

        new SystemRunner(world, rangeRows: 2).Schedule(new IntegrateGeneratedJob(), dependency: default, parallel: true, query: world.Query(filter).Since(baseline)).Complete();

        for (int i = 0; i < entities.Length; i++)
            Assert.Equal((i & 1) == 0 ? 2 : 0, world.ReadComponentData<GeneratedPosition>(entities[i]).Value);
    }

    [Fact]
    public void EmptySerialAndParallelJobs_DoNotAcquireAnExecutionVersion()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        ulong tickBefore = world.Version;

        new IntegrateGeneratedJob().Schedule(world).Complete();
        Assert.Equal(tickBefore, world.Version);

        new SystemRunner(world, rangeRows: 1).Schedule(new IntegrateGeneratedJob(), dependency: default, parallel: true).Complete();
        Assert.Equal(tickBefore, world.Version);
    }

    [Fact]
    public void FullyRowFilteredSerialAndParallelJobs_DoNotAcquireAnExecutionVersion()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(4, out Entity[] entities);
        foreach (Entity entity in entities)
        {
            world.Add(entity, new GeneratedEnableable());
            world.Disable<GeneratedEnableable>(entity);
        }
        QueryDescription filter = new QueryDescription().With<GeneratedEnableable>();
        ulong tickBefore = world.Version;

        new SystemRunner(world, rangeRows: 1024).Schedule(new IntegrateGeneratedJob(), dependency: default, parallel: false, query: world.Query(filter)).Complete();
        Assert.Equal(tickBefore, world.Version);

        new SystemRunner(world, rangeRows: 1).Schedule(new IntegrateGeneratedJob(), dependency: default, parallel: true, query: world.Query(filter)).Complete();
        Assert.Equal(tickBefore, world.Version);
    }

    [Fact]
    public void FullyChunkFilteredParallelPackets_DoNotAcquireAnExecutionVersion()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(4, out _);
        QueryDescription filter = new QueryDescription().WithChanged<GeneratedVelocity>(0);
        ulong tickBefore = world.Version;

        new SystemRunner(world, rangeRows: 1).Schedule(new IntegrateGeneratedJob(), dependency: default, parallel: true, query: world.Query(filter).Since(tickBefore)).Complete();

        Assert.Equal(tickBefore, world.Version);
    }

    [Fact]
    public void MatchingReadOnlySerialAndParallelJobs_DoNotAcquireAnExecutionVersion()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(6, out Entity[] entities);
        ulong tickBefore = world.Version;
        using var data = new CallbackData<GeneratedReadCount>(new GeneratedReadCount());

        new GeneratedReadOnlyProbeJob(data).Schedule(world).Complete();
        Assert.Equal(entities.Length, data.Value.Count);
        Assert.Equal(tickBefore, world.Version);
        data.Value.Count = 0;

        new SystemRunner(world, rangeRows: 1).Schedule(new GeneratedReadOnlyProbeJob(data), dependency: default, parallel: true).Complete();
        Assert.Equal(entities.Length, data.Value.Count);
        Assert.Equal(tickBefore, world.Version);
        data.Value.Count = 0;
    }

    [Fact]
    public void EnabledFilter_OwnsPacketBitRangesAndSkipsDisabledRows()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(10, out Entity[] entities);
        for (int i = 0; i < entities.Length; i++)
        {
            world.Add(entities[i], new GeneratedEnableable());
            if ((i & 1) == 0)
                world.Disable<GeneratedEnableable>(entities[i]);
        }
        QueryDescription filter = new QueryDescription().With<GeneratedEnableable>();

        new SystemRunner(world, rangeRows: 2).Schedule(new IntegrateGeneratedJob(), dependency: default, parallel: true, query: world.Query(filter)).Complete();

        for (int i = 0; i < entities.Length; i++)
            Assert.Equal((i & 1) == 0 ? 0 : 1, world.ReadComponentData<GeneratedPosition>(entities[i]).Value);
    }

    [Fact]
    public void ChangedFiltersReadTheirVersionBeforeParallelAndSerialWrites()
    {
        World world = CreateMovingWorld(4, out Entity[] entities);
        ulong baseline = world.Version;
        world.Set(entities[1], new GeneratedPosition { Value = 4 });
        Query query = world.Query(new QueryDescription().WithChanged<GeneratedPosition>(baseline));
        new SystemRunner(world, rangeRows: 1).Schedule(new GeneratedPositionIncrementJob(), query: query).Complete();
        Assert.Equal([0, 5, 0, 0], entities.Select(entity => world.ReadComponentData<GeneratedPosition>(entity).Value));
        baseline = world.Version;
        world.Set(entities[2], new GeneratedPosition { Value = 7 });
        new GeneratedPositionIncrementJob().Schedule(world, query: query.Since(baseline)).Complete();
        Assert.Equal([0, 5, 8, 0], entities.Select(entity => world.ReadComponentData<GeneratedPosition>(entity).Value));
    }

    [Fact]
    public void GeneratedParallelWritesEachMatchingEntityOnce()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(128, out Entity[] entities);
        using var data = new CallbackData<GeneratedConcurrencyData>(new GeneratedConcurrencyData());
        JobHandle handle = new SystemRunner(world, rangeRows: 1).Schedule(new GeneratedConcurrencyJob(data), dependency: default, parallel: true);
        try
        {
            Assert.True(data.Value.Arrivals.Wait(TimeSpan.FromSeconds(5)));
            data.Value.Release.Set();
            handle.Complete();
            foreach (var entity in entities)
                Assert.Equal(1, world.ReadComponentData<GeneratedPosition>(entity).Value);
        }
        finally
        {
            data.Value.Release.Set();
            handle.Complete();
        }
    }

    [Fact]
    public async Task ScheduleParallel_ResolvesPacketsAfterSemanticDependencyWithoutBlockingIntermediateTopology()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(4, out _);
        using var dependencyStarted = new ManualResetEventSlim();
        using var releaseDependency = new ManualResetEventSlim();
        JobHandle dependency = Jobs.Schedule(
            new BlockingDependencyJob(dependencyStarted, releaseDependency));
        Assert.True(dependencyStarted.Wait(TimeSpan.FromSeconds(5)));

        JobHandle generated = new SystemRunner(world, rangeRows: 1).Schedule(new IntegrateGeneratedJob(), dependency: dependency, parallel: true);

        Task<Entity> mutation = Task.Run(() =>
        {
            Entity entity = world.Create(new GeneratedPosition());
            world.Add(entity, new GeneratedVelocity { Value = 2 });
            return entity;
        });
        Entity mutatedEntity = await mutation.WaitAsync(TimeSpan.FromSeconds(5));

        releaseDependency.Set();
        generated.Complete();
        dependency.Complete();

        Assert.Equal(2, world.ReadComponentData<GeneratedPosition>(mutatedEntity).Value);
    }

    [Fact]
    public void ScheduleParallel_CapturesAfterDynamicTopologyWriterDescendant()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(3, out _);
        var capture = new EntityCapture();
        JobHandle dependency = Jobs.Schedule(
            new DynamicTopologyDependencyJob(world, capture));

        new SystemRunner(world, rangeRows: 1).Schedule(new IntegrateGeneratedJob(), dependency: dependency, parallel: true).Complete();

        dependency.Complete();
        Assert.NotEqual(Entity.Null, capture.Entity);
        Assert.Equal(3, world.ReadComponentData<GeneratedPosition>(capture.Entity).Value);
    }

    [Fact]
    public void GeneratedRowsIncludeTopologyWrittenBeforeRangeBinding()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 1, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(3, out Entity[] originalEntities);
        using var workerOccupied = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        JobHandle occupier = Jobs.Schedule(
            new BlockingDependencyJob(workerOccupied, releaseWorker));
        Assert.True(workerOccupied.Wait(TimeSpan.FromSeconds(5)));

        JobHandle generated = new SystemRunner(world, rangeRows: 1).Schedule(new IntegrateGeneratedJob(), dependency: default, parallel: true);

        // The writer registers before the generated job binds its component ranges.
        var writerCapture = new EntityCapture();
        JobResourceAccess[] accesses = world.Execution!.StructureWrite;
        JobHandle writer = Jobs.Schedule(
            new CreateMovingEntityJob(world, writerCapture),
            accesses);

        releaseWorker.Set();
        generated.Complete();

        occupier.Complete();
        writer.Complete();

        Assert.NotEqual(Entity.Null, writerCapture.Entity);
        Assert.All(
            originalEntities,
            entity => Assert.Equal(1, world.ReadComponentData<GeneratedPosition>(entity).Value));
        Assert.Equal(3, world.ReadComponentData<GeneratedPosition>(writerCapture.Entity).Value);
    }

    [Fact]
    public void Schedule_RegistersQueryResourcesAfterDynamicTopologyAndDataWriterDescendant()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(3, out _);
        var capture = new EntityCapture();
        JobHandle dependency = Jobs.Schedule(
            new DynamicTopologyDependencyJob(world, capture));

        new IntegrateGeneratedJob().Schedule(world, dependency).Complete();

        dependency.Complete();
        Assert.NotEqual(Entity.Null, capture.Entity);
        Assert.Equal(3, world.ReadComponentData<GeneratedPosition>(capture.Entity).Value);
    }

    [Fact]
    public void GeneratedParallelWritesPreserveValuesAfterCommandPlayback()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(128, out Entity[] entities);
        using (var commands = new CommandBuffer(world)) { commands.Create(); commands.Playback(); }
        using var data = new CallbackData<GeneratedConcurrencyData>(new GeneratedConcurrencyData());
        JobHandle handle = new SystemRunner(world, rangeRows: 1).Schedule(new GeneratedConcurrencyJob(data), dependency: default, parallel: true);
        try
        {
            Assert.True(data.Value.Arrivals.Wait(TimeSpan.FromSeconds(5)));
            data.Value.Release.Set();
            handle.Complete();
            foreach (var entity in entities)
                Assert.Equal(1, world.ReadComponentData<GeneratedPosition>(entity).Value);
        }
        finally
        {
            data.Value.Release.Set();
            handle.Complete();
        }
    }

    [Fact]
    public void ScheduleParallel_SparseWrapperFiltersAndWritesRequiredRows()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(12, out Entity[] entities);
        for (int i = 0; i < entities.Length; i += 2)
            world.SetSparse(entities[i], new GeneratedSparse { Value = i });

        new SystemRunner(world, rangeRows: 2).Schedule(new GeneratedSparseJob(), dependency: default, parallel: true).Complete();

        for (int i = 0; i < entities.Length; i++)
        {
            if ((i & 1) == 0)
                Assert.Equal(i + 10, world.ReadComponentData<GeneratedSparse>(entities[i]).Value);
            else
                Assert.False(world.HasSparse<GeneratedSparse>(entities[i]));
        }
    }

    [Fact]
    public void ScheduleParallel_BufferWrapperBorrowsOnlyCurrentPacketRows()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(10, out Entity[] entities);
        foreach (Entity entity in entities)
        {
            world.AddBuffer<GeneratedBufferElement>(entity);
            { using (var buffer = world.GetBuffer<GeneratedBufferElement>(entity)) { buffer.Add(new GeneratedBufferElement { Value = 3 }); } };
        }

        new SystemRunner(world, rangeRows: 2).Schedule(new GeneratedBufferJob(), dependency: default, parallel: true).Complete();

        foreach (Entity entity in entities)
        {
            int value = -1;
            { using (var buffer = world.ReadBuffer<GeneratedBufferElement>(entity)) { value = buffer[0].Value; } };
            Assert.Equal(4, value);
        }
    }

    [Fact]
    public void GeneratedStorageWritesShareOneWorldWriteVersion()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(12, out Entity[] entities);
        foreach (Entity entity in entities)
        {
            world.AddBuffer<GeneratedBufferElement>(entity);
            { using (var buffer = world.GetBuffer<GeneratedBufferElement>(entity)) { buffer.Add(new GeneratedBufferElement { Value = 1 }); } };
            world.SetSparse(entity, new GeneratedSparse { Value = 1 });
        }
        ulong baseline = world.Version;
        new SystemRunner(world, rangeRows: 1).Schedule(new GeneratedStorageJob(), dependency: default, parallel: true).Complete();
        Assert.Equal(baseline + 1, world.Version);
        foreach (var entity in entities)
        {
            Assert.Equal(1, world.ReadComponentData<GeneratedPosition>(entity).Value);
            Assert.Equal(2, world.ReadComponentData<GeneratedSparse>(entity).Value);
            { using (var buffer = world.ReadBuffer<GeneratedBufferElement>(entity)) { Assert.Equal(2, buffer[0].Value); } };
        }
        var changed = world.Query(new QueryDescription().WithChanged<GeneratedPosition>(0));
        int positionChanges = changed.Since(baseline).Count;
        Assert.Equal(entities.Length, positionChanges);
        Query buffers = world.Query(new QueryDescription().WithChangedBuffer<GeneratedBufferElement>(baseline));
        int bufferChanges = buffers.Count;
        Assert.Equal(entities.Length, bufferChanges);
    }

    [Fact]
    public void ColdBufferDescriptor_CompilesLogicalBufferAdmissionBeforeAnyBufferExists()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.Create();
        world.AddBuffer<GeneratedColdBufferElement>(entity);
        { using (var buffer = world.GetBuffer<GeneratedColdBufferElement>(entity)) { buffer.Add(new GeneratedColdBufferElement { Value = 5 }); } };

        new GeneratedColdBufferJob().Schedule(world).Complete();

        int value = -1;
        { using (var buffer = world.ReadBuffer<GeneratedColdBufferElement>(entity)) { value = buffer[0].Value; } };
        Assert.Equal(6, value);
    }

    [Fact]
    public void SerialCanonicalParentWriter_UsesWorldFinalizerAndHasNoParallelSurface()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity firstParent = world.Create();
        Entity secondParent = world.Create();
        Entity child = world.Create();
        Hierarchy<GeneratedHierarchyDomain>.SetParent(world, child, firstParent);

        var job = new GeneratedReparentJob(world, secondParent);
        Jobs.Schedule(job, world.Execution!.StructureWrite).Complete();

        Assert.Equal(secondParent, Hierarchy<GeneratedHierarchyDomain>.GetParent(world, child));
        // Parent writes are deferred; the same hierarchy kernel publishes inverse state later.
        Assert.Equal(
            [child],
            ReadGeneratedChildren(world, firstParent));
        Assert.Empty(ReadGeneratedChildren(world, secondParent));
        Hierarchy<GeneratedHierarchyDomain>.Maintain(world);
        Assert.Empty(ReadGeneratedChildren(world, firstParent));
        Assert.Equal([child], ReadGeneratedChildren(world, secondParent));
    }

    private static Entity[] ReadGeneratedChildren(World world, Entity parent)
    {
        using var children = Hierarchy<GeneratedHierarchyDomain>.GetChildren(world, parent);
        return children.Entities.ToArray();
    }

    private World CreateMovingWorld(int count, out Entity[] entities)
    {
        var world = CreateJobWorld();
        entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            Entity entity = world.Create(new GeneratedPosition());
            world.Add(entity, new GeneratedVelocity { Value = 1 });
            entities[i] = entity;
        }
        return world;
    }

    private readonly struct BlockingDependencyJob : IJob
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingDependencyJob(
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            _started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Dependency release was not signaled.");
        }
    }

    private sealed class EntityCapture
    {
        internal Entity Entity;
    }

    private readonly struct DynamicTopologyDependencyJob : IJob
    {
        private readonly World _world;
        private readonly EntityCapture _capture;

        internal DynamicTopologyDependencyJob(World world, EntityCapture capture)
        {
            _world = world;
            _capture = capture;
        }

        public void Execute()
        {
            JobResourceAccess[] accesses = _world.Execution!.StructureWrite;
            JobSystem.Schedule(
                new CreateMovingEntityJob(_world, _capture),
                accesses);
        }
    }

    private readonly struct CreateMovingEntityJob : IJob
    {
        private readonly World _world;
        private readonly EntityCapture _capture;

        internal CreateMovingEntityJob(World world, EntityCapture capture)
        {
            _world = world;
            _capture = capture;
        }

        public void Execute()
        {
            Entity entity = _world.Create(new GeneratedPosition());
            _world.Add(entity, new GeneratedVelocity { Value = 3 });
            _capture.Entity = entity;
        }
    }
}

public struct GeneratedPosition : SomeEngine.ECS.IComponent, ITrackedComponent
{
    public int Value;
}

public struct GeneratedVelocity : SomeEngine.ECS.IComponent, ITrackedComponent
{
    public int Value;
}

public struct GeneratedSparse : SomeEngine.ECS.Components.ISparseComponent, ITrackedComponent
{
    public int Value;
}

public struct GeneratedBufferElement : SomeEngine.ECS.Components.IBufferElement, ITrackedComponent
{
    public int Value;
}

public struct GeneratedColdBufferElement : SomeEngine.ECS.Components.IBufferElement
{
    public int Value;
}

public struct GeneratedEnableable : SomeEngine.ECS.IEnableableComponent
{
    public int Value;
}

public struct GeneratedManagedComponent : SomeEngine.ECS.IComponent
{
    public string? Value;
}

public readonly struct GeneratedExcludedTag : SomeEngine.ECS.IComponent;

public readonly struct GeneratedHierarchyDomain : IHierarchyDomain;

public partial struct IntegrateGeneratedJob : IJobEntity
{
    public void Execute(in GeneratedVelocity velocity, ref GeneratedPosition position)
    {
        position.Value += velocity.Value;
    }
}

public partial struct GeneratedSparseJob : IJobEntity
{
    public void Execute(ref GeneratedSparse sparse)
    {
        sparse.Value += 10;
    }
}

[JobRead(typeof(GeneratedPosition))]
public partial struct GeneratedBoundaryProbeJob(World world, Entity entity) : IJobEntity
{
    public void Execute(in GeneratedPosition position)
    {
        _ = position.Value;
        _ = world.ReadComponentData<GeneratedSparse>(entity);
    }
}

public partial struct GeneratedReadOnlyProbeJob : IJobEntity
{
    private readonly CallbackData<GeneratedReadCount> _data;

    internal GeneratedReadOnlyProbeJob(CallbackData<GeneratedReadCount> data) => _data = data;
    public void Execute(in GeneratedPosition position)
    {
        _ = position.Value;
        Assert.Equal(0, position.Value);
        Interlocked.Increment(ref _data.Value.Count);
    }
}

public partial struct GeneratedBufferJob : IJobEntity
{
    public void Execute(DynamicBuffer<GeneratedBufferElement> buffer)
    {
        buffer[0].Value++;
    }
}

public partial struct GeneratedColdBufferJob : IJobEntity
{
    public void Execute(DynamicBuffer<GeneratedColdBufferElement> buffer)
    {
        buffer[0].Value++;
    }
}

public partial struct GeneratedPositionIncrementJob : IJobEntity
{
    public void Execute(ref GeneratedPosition position)
    {
        position.Value++;
    }
}

public readonly struct GeneratedReparentJob(World world, Entity parent) : IJob
{
    public void Execute()
    {
        var edits = new List<ParentEdit>();
        using (QueryScope rows = world.Query<Parent<GeneratedHierarchyDomain>>().OpenRead())
            foreach (QueryChunk chunk in rows)
                foreach (Entity entity in chunk.Entities) edits.Add(new(entity, parent));
        Hierarchy<GeneratedHierarchyDomain>.SetParents(world, System.Runtime.InteropServices.CollectionsMarshal.AsSpan(edits), deferred: true);
    }
}

public partial struct GeneratedStorageJob : IJobEntity
{
    public void Execute(ref GeneratedPosition position,
        DynamicBuffer<GeneratedBufferElement> buffer,
        ref GeneratedSparse sparse)
    {
        position.Value++;
        buffer[0].Value++;
        sparse.Value++;
    }
}

internal sealed class GeneratedReadCount
{
    internal int Count;
}

internal sealed class GeneratedConcurrencyData : IDisposable
{
    internal readonly CountdownEvent Arrivals = new(2);
    internal readonly ManualResetEventSlim Release = new();
    internal int Entered;
    public void Dispose() { Arrivals.Dispose(); Release.Dispose(); }
}

public partial struct GeneratedConcurrencyJob : IJobEntity
{
    private readonly CallbackData<GeneratedConcurrencyData> _data;
    internal GeneratedConcurrencyJob(CallbackData<GeneratedConcurrencyData> data) => _data = data;
    public void Execute(ref GeneratedPosition position)
    {
        if (Interlocked.Increment(ref _data.Value.Entered) <= 2)
        {
            _data.Value.Arrivals.Signal();
            if (!_data.Value.Release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The concurrent callbacks were not released.");
        }
        position.Value++;
    }
}

internal sealed class GeneratedCommandData(CommandBatch commands)
{
    internal readonly CommandBatch Commands = commands;
}

public partial struct GeneratedCommandJob : IJobEntity
{
    private readonly CallbackData<GeneratedCommandData> _data;
    internal GeneratedCommandJob(CallbackData<GeneratedCommandData> data) => _data = data;
    public void Execute(Entity entity, in GeneratedPosition position) =>
        _data.Value.Commands[entity.Id].Set(entity, new GeneratedPosition { Value = 99 });
}

internal sealed class GeneratedTopologyData(int count) : IDisposable
{
    internal readonly ManualResetEventSlim Started = new();
    internal readonly ManualResetEventSlim Release = new();
    internal readonly System.Collections.Concurrent.ConcurrentQueue<string> Events = new();
    internal readonly int Count = count;
    internal int First;
    internal int Finished;
    public void Dispose() { Started.Dispose(); Release.Dispose(); }
}

public partial struct GeneratedTopologyJob : IJobEntity
{
    private readonly CallbackData<GeneratedTopologyData> _data;
    internal GeneratedTopologyJob(CallbackData<GeneratedTopologyData> data) => _data = data;
    public void Execute(ref GeneratedPosition position)
    {
        if (Interlocked.CompareExchange(ref _data.Value.First, 1, 0) == 0)
        {
            _data.Value.Started.Set();
            if (!_data.Value.Release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The generated callback was not released.");
        }
        position.Value++;
        if (Interlocked.Increment(ref _data.Value.Finished) == _data.Value.Count)
            _data.Value.Events.Enqueue("callbacks finished");
    }
}

[JobRead(typeof(GeneratedPosition))]
public partial struct GeneratedFilterWriteProbe(World world, Entity entity) : IJobEntity
{
    public void Execute(in GeneratedPosition position) => world.Set(entity, new GeneratedVelocity { Value = 99 });
}
