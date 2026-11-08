using SomeEngine.ECS.Components;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
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
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = CreateMovingWorld(32, out Entity[] entities);
        foreach (Entity entity in entities)
            world.AddSparse(entity, new GeneratedSparse { Value = 3 });
        uint sparseVersion = world.ComponentWriteVersion<GeneratedSparse>();
        uint positionVersion = world.ComponentWriteVersion<GeneratedPosition>();

        new GeneratedSparseJob().ScheduleParallel(
            world, new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket)).Complete();

        Assert.Equal(sparseVersion + 32, world.ComponentWriteVersion<GeneratedSparse>());
        Assert.Equal(positionVersion, world.ComponentWriteVersion<GeneratedPosition>());
        Assert.All(entities, entity => Assert.Equal(13, world.ReadSparse<GeneratedSparse>(entity).Value));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    public void ParallelTableJobAdvancesComponentWriteVersionForEveryRow(int rowsPerPacket)
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        using World world = CreateMovingWorld(128, out Entity[] entities);
        uint positionVersion = world.ComponentWriteVersion<GeneratedPosition>();
        uint velocityVersion = world.ComponentWriteVersion<GeneratedVelocity>();

        new IntegrateGeneratedJob().ScheduleParallel(
            world, new JobEntityScheduleOptions(rowsPerPacket: rowsPerPacket)).Complete();

        Assert.Equal(positionVersion + 128, world.ComponentWriteVersion<GeneratedPosition>());
        Assert.Equal(velocityVersion, world.ComponentWriteVersion<GeneratedVelocity>());
        Assert.All(entities, entity => Assert.Equal(1, world.Read<GeneratedPosition>(entity).Value));
    }


    [Fact]
    public void GeneratedCommandRecordingRequiresAJobCommandWriter()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(8, out _);
        var commands = world.Commands();
        using var data = new CallbackData<GeneratedCommandData>(new GeneratedCommandData(commands));
        Assert.Throws<InvalidOperationException>(() =>
            new GeneratedCommandJob(data).Schedule(world).Complete());
        Assert.Equal(0, commands.CommandCount);
        Assert.Throws<InvalidOperationException>(() =>
            new GeneratedCommandJob(data).ScheduleParallel(world,
                new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete());
        Assert.Equal(0, commands.CommandCount);
    }

    [Fact]
    public async Task GeneratedCallbacksFinishBeforeStructuralMutation()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(16, out _);
        using var attempted = new ManualResetEventSlim();
        using var data = new CallbackData<GeneratedTopologyData>(new GeneratedTopologyData(16));
        JobHandle handle = new GeneratedTopologyJob(data).ScheduleParallel(world,
            new JobEntityScheduleOptions(rowsPerPacket: 1));
        Task<Entity>? writer = null;
        try
        {
            Assert.True(data.Value.Started.Wait(TimeSpan.FromSeconds(5)));
            writer = Task.Run(() =>
            {
                attempted.Set();
                var entity = world.CreateEntity();
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
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.CreateEntity(new GeneratedPosition { Value = 4 });
        world.AddSparse(entity, new GeneratedSparse { Value = 9 });
        var descriptor = new GeneratedQueryAccessDescriptor(
            new QueryDefinitionBuilder().Read<GeneratedPosition>().Build(),
            GeneratedQueryAccess.Table<GeneratedPosition>(GeneratedQueryMode.Read));
        var job = new GeneratedBoundaryProbeJob();

        JobHandle handle = JobEntityRuntime.ScheduleParallel(
            world,
            in job,
            new UndeclaredSparseAdapter(),
            descriptor);

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => handle.Complete());

        Assert.Equal(9, world.ReadSparse<GeneratedSparse>(entity).Value);
    }

    [Fact]
    public void Descriptor_NormalizesFilterReadsAndRejectsForgedValueAccessSets()
    {
        QueryDefinition filtered = new QueryDefinitionBuilder()
            .Read<GeneratedPosition>()
            .Changed<GeneratedVelocity>()
            .Build();
        var descriptor = new GeneratedQueryAccessDescriptor(
            filtered,
            GeneratedQueryAccess.Table<GeneratedPosition>(GeneratedQueryMode.Read));

        Assert.Equal(2, descriptor.AccessCount);
        GeneratedQueryAccess filterAccess = Assert.Single(
            Enumerable.Range(0, descriptor.AccessCount)
                .Select(descriptor.GetAccess),
            static access => !access.HasDirectAccess);
        Assert.Equal(typeof(GeneratedVelocity), filterAccess.ValueType);
        Assert.Equal(QueryTermFilter.Changed, filterAccess.Filters);

        Assert.Throws<InvalidOperationException>(() =>
            new GeneratedQueryAccessDescriptor(filtered));
        Assert.Throws<InvalidOperationException>(() =>
            new GeneratedQueryAccessDescriptor(
                new QueryDefinitionBuilder().Read<GeneratedPosition>().Build(),
                GeneratedQueryAccess.Table<GeneratedPosition>(GeneratedQueryMode.Read),
                GeneratedQueryAccess.Table<GeneratedVelocity>(GeneratedQueryMode.Read)));
        Assert.Throws<InvalidOperationException>(() =>
            new GeneratedQueryAccessDescriptor(
                new QueryDefinitionBuilder().Read<GeneratedPosition>().Build(),
                GeneratedQueryAccess.Table<GeneratedPosition>(GeneratedQueryMode.ReadWrite)));
    }

    [Fact]
    public void Descriptor_RejectsReferenceBearingDirectStorageEvenForSerialScheduling()
    {
        QueryDefinition query = new QueryDefinitionBuilder()
            .Read<GeneratedManagedComponent>()
            .Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new GeneratedQueryAccessDescriptor(
                query,
                GeneratedQueryAccess.Table<GeneratedManagedComponent>(GeneratedQueryMode.Read)));

    }

    [Fact]
    public void ExplicitFilter_ComposesSelectionWithoutAddingValueAccess()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(8, out Entity[] entities);
        for (int i = 0; i < entities.Length; i += 2)
            world.AddTag<GeneratedExcludedTag>(entities[i]);
        QueryDefinition filter = world.QueryDefinition().None<GeneratedExcludedTag>().Build();

        new IntegrateGeneratedJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 2, filter: filter)).Complete();

        for (int i = 0; i < entities.Length; i++)
        {
            Assert.Equal((i & 1) == 0 ? 0 : 1, world.Read<GeneratedPosition>(entities[i]).Value);
        }

        QueryDefinition invalidAccess = world.QueryDefinition().Read<GeneratedVelocity>().Build();
        Assert.Throws<InvalidOperationException>(() =>
            new IntegrateGeneratedJob().ScheduleParallel(
                world,
                new JobEntityScheduleOptions(filter: invalidAccess)));
    }

    [Fact]
    public void ExactChangedFilter_RunsAfterRangeHazardsAndBeforeCurrentRowWrite()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(10, out Entity[] entities);
        uint baseline = world.AcquireSystemTick();
        for (int i = 0; i < entities.Length; i += 2)
            world.Replace(entities[i], new GeneratedVelocity { Value = 2 });
        QueryDefinition filter = world.QueryDefinition().Changed<GeneratedVelocity>().Build();

        new IntegrateGeneratedJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(
                rowsPerPacket: 2,
                lastSystemVersion: baseline,
                filter: filter)).Complete();

        for (int i = 0; i < entities.Length; i++)
            Assert.Equal((i & 1) == 0 ? 2 : 0, world.Read<GeneratedPosition>(entities[i]).Value);
    }

    [Fact]
    public void EmptySerialAndParallelJobs_DoNotAcquireAnExecutionVersion()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        uint tickBefore = world.CurrentTick;

        new IntegrateGeneratedJob().Schedule(world).Complete();
        Assert.Equal(tickBefore, world.CurrentTick);

        new IntegrateGeneratedJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();
        Assert.Equal(tickBefore, world.CurrentTick);
    }

    [Fact]
    public void FullyRowFilteredSerialAndParallelJobs_DoNotAcquireAnExecutionVersion()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(4, out Entity[] entities);
        foreach (Entity entity in entities)
        {
            world.Add(entity, new GeneratedEnableable());
            world.Disable<GeneratedEnableable>(entity);
        }
        QueryDefinition filter = world.QueryDefinition().Enabled<GeneratedEnableable>().Build();
        uint tickBefore = world.CurrentTick;

        new IntegrateGeneratedJob().Schedule(
            world,
            new JobEntityScheduleOptions(filter: filter)).Complete();
        Assert.Equal(tickBefore, world.CurrentTick);

        new IntegrateGeneratedJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 1, filter: filter)).Complete();
        Assert.Equal(tickBefore, world.CurrentTick);
    }

    [Fact]
    public void FullyChunkFilteredParallelPackets_DoNotAcquireAnExecutionVersion()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(4, out _);
        QueryDefinition filter = world.QueryDefinition().ChunkChanged<GeneratedVelocity>().Build();
        uint tickBefore = world.CurrentTick;

        new IntegrateGeneratedJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(
                rowsPerPacket: 1,
                lastSystemVersion: tickBefore,
                filter: filter)).Complete();

        Assert.Equal(tickBefore, world.CurrentTick);
    }

    [Fact]
    public void MatchingReadOnlySerialAndParallelJobs_DoNotAcquireAnExecutionVersion()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(6, out Entity[] entities);
        uint tickBefore = world.CurrentTick;
        using var data = new CallbackData<GeneratedReadCount>(new GeneratedReadCount());

        new GeneratedReadOnlyProbeJob(data).Schedule(world).Complete();
        Assert.Equal(entities.Length, data.Value.Count);
        Assert.Equal(tickBefore, world.CurrentTick);
        data.Value.Count = 0;

        new GeneratedReadOnlyProbeJob(data).ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();
        Assert.Equal(entities.Length, data.Value.Count);
        Assert.Equal(tickBefore, world.CurrentTick);
        data.Value.Count = 0;
    }

    [Fact]
    public void EnabledFilter_OwnsPacketBitRangesAndSkipsDisabledRows()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(10, out Entity[] entities);
        for (int i = 0; i < entities.Length; i++)
        {
            world.Add(entities[i], new GeneratedEnableable());
            if ((i & 1) == 0)
                world.Disable<GeneratedEnableable>(entities[i]);
        }
        QueryDefinition filter = world.QueryDefinition().Enabled<GeneratedEnableable>().Build();

        new IntegrateGeneratedJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 2, filter: filter)).Complete();

        for (int i = 0; i < entities.Length; i++)
            Assert.Equal((i & 1) == 0 ? 0 : 1, world.Read<GeneratedPosition>(entities[i]).Value);
    }

    [Fact]
    public void ChunkChangedFilter_WithSameFamilyDirectWriteIsExplicitlySerialOnly()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(4, out _);
        QueryDefinition filter = world.QueryDefinition().ChunkChanged<GeneratedPosition>().Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new GeneratedPositionIncrementJob().ScheduleParallel(
                world,
                new JobEntityScheduleOptions(filter: filter)));

        new GeneratedPositionIncrementJob().Schedule(
            world,
            new JobEntityScheduleOptions(filter: filter)).Complete();
    }

    [Fact]
    public void GeneratedParallelWritesEachMatchingEntityOnce()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(32, out Entity[] entities);
        using var data = new CallbackData<GeneratedConcurrencyData>(new GeneratedConcurrencyData());
        JobHandle handle = new GeneratedConcurrencyJob(data).ScheduleParallel(world,
            new JobEntityScheduleOptions(rowsPerPacket: 1));
        try
        {
            Assert.True(data.Value.Arrivals.Wait(TimeSpan.FromSeconds(5)));
            data.Value.Release.Set();
            handle.Complete();
            foreach (var entity in entities)
                Assert.Equal(1, world.Read<GeneratedPosition>(entity).Value);
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
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(4, out _);
        using var dependencyStarted = new ManualResetEventSlim();
        using var releaseDependency = new ManualResetEventSlim();
        JobHandle dependency = Jobs.Schedule(
            new BlockingDependencyJob(dependencyStarted, releaseDependency));
        Assert.True(dependencyStarted.Wait(TimeSpan.FromSeconds(5)));

        JobHandle generated = new IntegrateGeneratedJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 1),
            dependency);

        Task<Entity> mutation = Task.Run(() =>
        {
            Entity entity = world.CreateEntity(new GeneratedPosition());
            world.Add(entity, new GeneratedVelocity { Value = 2 });
            return entity;
        });
        Entity mutatedEntity = await mutation.WaitAsync(TimeSpan.FromSeconds(5));

        releaseDependency.Set();
        generated.Complete();
        dependency.Complete();

        Assert.Equal(2, world.Read<GeneratedPosition>(mutatedEntity).Value);
    }

    [Fact]
    public void ScheduleParallel_CapturesAfterDynamicTopologyWriterDescendant()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(3, out _);
        var capture = new EntityCapture();
        JobHandle dependency = Jobs.Schedule(
            new DynamicTopologyDependencyJob(world, capture));

        new IntegrateGeneratedJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 1),
            dependency).Complete();

        dependency.Complete();
        Assert.NotEqual(Entity.Null, capture.Entity);
        Assert.Equal(3, world.Read<GeneratedPosition>(capture.Entity).Value);
    }

    [Fact]
    public void ScheduleParallel_RegisteredTopologySuccessorCannotSilentlyInvalidateCapturedPackets()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 1, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(3, out Entity[] originalEntities);
        using var workerOccupied = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        JobHandle occupier = Jobs.Schedule(
            new BlockingDependencyJob(workerOccupied, releaseWorker));
        Assert.True(workerOccupied.Wait(TimeSpan.FromSeconds(5)));

        JobHandle generated = new IntegrateGeneratedJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 1));

        // The capture topology-read is active but cannot execute while the only worker is
        // occupied. Registering this writer now places it after capture and before the packets
        // that capture will synchronously attach from its body.
        var writerCapture = new EntityCapture();
        Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[3];
        accesses[0] = RelationshipJobAccess.TopologyWrite(world);
        accesses[1] = ComponentJobAccess<GeneratedPosition>.Write(world);
        accesses[2] = ComponentJobAccess<GeneratedVelocity>.Write(world);
        JobHandle writer = Jobs.Schedule(
            new CreateMovingEntityJob(world, writerCapture),
            accesses);

        releaseWorker.Set();
        Assert.True(
            SpinWait.SpinUntil(() => generated.IsCompleted, TimeSpan.FromSeconds(5)),
            "Capture, registered writer, and attached packet scopes formed a completion ring.");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => generated.Complete());

        occupier.Complete();
        writer.Complete();

        Assert.NotEqual(Entity.Null, writerCapture.Entity);
        Assert.All(
            originalEntities,
            entity => Assert.Equal(0, world.Read<GeneratedPosition>(entity).Value));
        Assert.Equal(0, world.Read<GeneratedPosition>(writerCapture.Entity).Value);
    }

    [Fact]
    public void Schedule_RegistersQueryResourcesAfterDynamicTopologyAndDataWriterDescendant()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(3, out _);
        var capture = new EntityCapture();
        JobHandle dependency = Jobs.Schedule(
            new DynamicTopologyDependencyJob(world, capture));

        new IntegrateGeneratedJob().Schedule(world, dependency).Complete();

        dependency.Complete();
        Assert.NotEqual(Entity.Null, capture.Entity);
        Assert.Equal(3, world.Read<GeneratedPosition>(capture.Entity).Value);
    }

    [Fact]
    public void GeneratedParallelWritesPreserveValuesAfterCommandPlayback()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(48, out Entity[] entities);
        world.Commands().CreateEntity();
        world.Flush();
        using var data = new CallbackData<GeneratedConcurrencyData>(new GeneratedConcurrencyData());
        JobHandle handle = new GeneratedConcurrencyJob(data).ScheduleParallel(world,
            new JobEntityScheduleOptions(rowsPerPacket: 1));
        try
        {
            Assert.True(data.Value.Arrivals.Wait(TimeSpan.FromSeconds(5)));
            data.Value.Release.Set();
            handle.Complete();
            foreach (var entity in entities)
                Assert.Equal(1, world.Read<GeneratedPosition>(entity).Value);
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
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(12, out Entity[] entities);
        for (int i = 0; i < entities.Length; i += 2)
            world.AddSparse(entities[i], new GeneratedSparse { Value = i });

        new GeneratedSparseJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 2)).Complete();

        for (int i = 0; i < entities.Length; i++)
        {
            if ((i & 1) == 0)
                Assert.Equal(i + 10, world.ReadSparse<GeneratedSparse>(entities[i]).Value);
            else
                Assert.False(world.HasSparse<GeneratedSparse>(entities[i]));
        }
    }

    [Fact]
    public void ScheduleParallel_BufferWrapperBorrowsOnlyCurrentPacketRows()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(10, out Entity[] entities);
        foreach (Entity entity in entities)
        {
            world.AddBuffer<GeneratedBufferElement>(entity);
            world.ExecuteBufferWrite<GeneratedBufferElement>(
                entity,
                static buffer => buffer.Add(new GeneratedBufferElement { Value = 3 }));
        }

        new GeneratedBufferJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 2)).Complete();

        foreach (Entity entity in entities)
        {
            int value = -1;
            world.ExecuteBufferRead<GeneratedBufferElement>(
                entity,
                buffer => value = buffer[0].Value);
            Assert.Equal(4, value);
        }
    }

    [Fact]
    public void GeneratedStorageWritesShareOneWorldWriteVersion()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 3, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        World world = CreateMovingWorld(12, out Entity[] entities);
        foreach (Entity entity in entities)
        {
            world.AddBuffer<GeneratedBufferElement>(entity);
            world.ExecuteBufferWrite<GeneratedBufferElement>(entity,
                buffer => buffer.Add(new GeneratedBufferElement { Value = 1 }));
            world.AddSparse(entity, new GeneratedSparse { Value = 1 });
        }
        uint baseline = world.CurrentTick;
        new GeneratedStorageJob().ScheduleParallel(world,
            new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();
        Assert.Equal(baseline + 1, world.CurrentTick);
        foreach (var entity in entities)
        {
            Assert.Equal(1, world.Read<GeneratedPosition>(entity).Value);
            Assert.Equal(2, world.ReadSparse<GeneratedSparse>(entity).Value);
            world.ExecuteBufferRead<GeneratedBufferElement>(entity,
                buffer => Assert.Equal(2, buffer[0].Value));
        }
        var changed = world.Query(world.QueryDefinition().Changed<GeneratedPosition>());
        int positionChanges = 0;
        world.ExecuteQuery(changed, baseline, cursor =>
        {
            foreach (var row in cursor.Rows)
                positionChanges++;
        });
        Assert.Equal(entities.Length, positionChanges);
        var buffers = world.Query(world.QueryDefinition().ChangedBuffer<GeneratedBufferElement>());
        int bufferChanges = 0;
        world.ExecuteQuery(buffers, baseline, cursor =>
        {
            foreach (var row in cursor.Rows)
                bufferChanges++;
        });
        Assert.Equal(entities.Length, bufferChanges);
    }

    [Fact]
    public void ColdBufferDescriptor_CompilesLogicalBufferAdmissionBeforeAnyBufferExists()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        GeneratedQueryAccessDescriptor descriptor =
            new GeneratedColdBufferJob().GetGeneratedQueryAccess();
        Assert.Equal(1, descriptor.AccessCount);
        Assert.Equal(GeneratedQueryStorage.Buffer, descriptor.GetAccess(0).Storage);

        var world = CreateJobWorld();
        Entity entity = world.CreateEntity();
        world.AddBuffer<GeneratedColdBufferElement>(entity);
        world.ExecuteBufferWrite<GeneratedColdBufferElement>(
            entity,
            static buffer => buffer.Add(new GeneratedColdBufferElement { Value = 5 }));

        new GeneratedColdBufferJob().Schedule(world).Complete();

        int value = -1;
        world.ExecuteBufferRead<GeneratedColdBufferElement>(
            entity,
            buffer => value = buffer[0].Value);
        Assert.Equal(6, value);
    }

    [Fact]
    public void SerialCanonicalParentWriter_UsesWorldFinalizerAndHasNoParallelSurface()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity firstParent = world.CreateEntity();
        Entity secondParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<GeneratedHierarchyDomain>.SetParent(world, child, firstParent);

        var job = new GeneratedReparentJob(secondParent);
        Assert.True(job.GetGeneratedQueryAccess().HasRelationshipWrite);
        Assert.False(job.GetGeneratedQueryAccess().SupportsParallel);

        job.Schedule(world).Complete();

        Assert.Equal(secondParent, Hierarchy<GeneratedHierarchyDomain>.GetParent(world, child));
        // Parent writes are deferred; the same hierarchy kernel publishes inverse state later.
        Assert.Equal(
            [child],
            Hierarchy<GeneratedHierarchyDomain>.GetChildren(world, firstParent).ToArray());
        Assert.Empty(Hierarchy<GeneratedHierarchyDomain>.GetChildren(world, secondParent));
        Hierarchy<GeneratedHierarchyDomain>.Maintain(world);
        Assert.Empty(Hierarchy<GeneratedHierarchyDomain>.GetChildren(world, firstParent));
        Assert.Equal([child], Hierarchy<GeneratedHierarchyDomain>.GetChildren(world, secondParent).ToArray());
    }

    private World CreateMovingWorld(int count, out Entity[] entities)
    {
        var world = CreateJobWorld();
        entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            Entity entity = world.CreateEntity(new GeneratedPosition());
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
            Span<JobResourceAccess> accesses = stackalloc JobResourceAccess[3];
            accesses[0] = RelationshipJobAccess.TopologyWrite(_world);
            accesses[1] = ComponentJobAccess<GeneratedPosition>.Write(_world);
            accesses[2] = ComponentJobAccess<GeneratedVelocity>.Write(_world);
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
            Entity entity = _world.CreateEntity(new GeneratedPosition());
            _world.Add(entity, new GeneratedVelocity { Value = 3 });
            _capture.Entity = entity;
        }
    }
}

public struct GeneratedPosition : SomeEngine.ECS.IComponent
{
    public int Value;
}

public struct GeneratedVelocity : SomeEngine.ECS.IComponent
{
    public int Value;
}

public struct GeneratedSparse : SomeEngine.ECS.Components.ISparseComponent
{
    public int Value;
}

public struct GeneratedBufferElement : SomeEngine.ECS.Components.IBufferElement
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

public readonly struct GeneratedExcludedTag : SomeEngine.ECS.Components.ITag;

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

public partial struct GeneratedBoundaryProbeJob : IJobEntity
{
    public void Execute(in GeneratedPosition position)
    {
        _ = position.Value;
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

public readonly struct UndeclaredSparseAdapter : IGeneratedJobEntityAdapter<GeneratedBoundaryProbeJob>
{
    public void Execute(ref GeneratedBoundaryProbeJob job, ref JobEntityRow row)
    {
        _ = row.ReadSparse<GeneratedSparse>();
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

public readonly partial struct GeneratedReparentJob : IJobEntity
{
    private readonly Entity _parent;

    public GeneratedReparentJob(Entity parent)
    {
        _parent = parent;
    }

    public void Execute(ref Parent<GeneratedHierarchyDomain> parent)
    {
        parent.Value = _parent;
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

internal sealed class GeneratedCommandData(CommandBuffer commands)
{
    internal readonly CommandBuffer Commands = commands;
}

public partial struct GeneratedCommandJob : IJobEntity
{
    private readonly CallbackData<GeneratedCommandData> _data;
    internal GeneratedCommandJob(CallbackData<GeneratedCommandData> data) => _data = data;
    public void Execute(Entity entity, in GeneratedPosition position) =>
        _data.Value.Commands.Replace(entity, new GeneratedPosition { Value = 99 });
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
