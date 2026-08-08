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

public sealed class GeneratedJobEntityTests
{
    [Fact]
    public void GeneratedDescriptor_ContainsImmutableDirectAccessSet()
    {
        GeneratedQueryAccessDescriptor descriptor =
            new IntegrateGeneratedJob().GetGeneratedQueryAccess();

        Assert.Equal(2, descriptor.AccessCount);
        Assert.Equal(GeneratedQueryMode.Read, descriptor.GetAccess(0).Mode);
        Assert.Equal(typeof(GeneratedVelocity), descriptor.GetAccess(0).ValueType);
        Assert.Equal(GeneratedQueryMode.ReadWrite, descriptor.GetAccess(1).Mode);
        Assert.Equal(typeof(GeneratedPosition), descriptor.GetAccess(1).ValueType);
        Assert.True(descriptor.SupportsParallel);
    }

    [Fact]
    public void RuntimeBoundaryHoist_StillRejectsAnUndeclaredSparseBorrow()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
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
        Assert.Contains("Sparse", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(9, world.ReadSparse<GeneratedSparse>(entity).Value);
    }

    [Fact]
    public void GeneratedSerialAndParallelJobsRejectSharedCommandBufferEscapeBeforeRecording()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        World world = CreateMovingWorld(8, out _);
        CommandBuffer commands = world.Commands();
        GeneratedCommandBufferEscapeJob.Configure(commands);
        try
        {
            InvalidOperationException serial = Assert.Throws<InvalidOperationException>(() =>
                new GeneratedCommandBufferEscapeJob().Schedule(world).Complete());
            Assert.Contains("CommandBuffer", serial.Message, StringComparison.Ordinal);
            Assert.Equal(0, commands.CommandCount);

            InvalidOperationException parallel = Assert.Throws<InvalidOperationException>(() =>
                new GeneratedCommandBufferEscapeJob().ScheduleParallel(
                    world,
                    new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete());
            Assert.Contains("CommandBuffer", parallel.Message, StringComparison.Ordinal);
            Assert.Equal(0, commands.CommandCount);
        }
        finally
        {
            GeneratedCommandBufferEscapeJob.Clear();
        }
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

        Assert.Contains("alias-free", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void StablePartition_ExactlyCoversChunksWithoutPacketOverlap()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateMovingWorld(11, out _);
        GeneratedQueryAccessDescriptor descriptor =
            new IntegrateGeneratedJob().GetGeneratedQueryAccess();

        StableQueryPartitionProof proof = JobEntityRuntime.DescribePartition(
            world,
            descriptor,
            rowsPerPacket: 3);

        Assert.True(proof.PacketCount >= 4);
        for (int i = 0; i < proof.PacketCount; i++)
        {
            StableQueryPacketRange packet = proof.GetPacket(i);
            Assert.True(packet.PersistentChunkId > 0);
            Assert.InRange(packet.RowCount, 1, 3);
            for (int j = i + 1; j < proof.PacketCount; j++)
                Assert.True(proof.ProvesNonOverlap(i, j));
        }
        Assert.NotEqual(0UL, proof.Fingerprint);
        Assert.Equal(world.PublishedStructureEpoch, proof.StructureEpoch);
        Assert.Equal(world.PublishedTopologyRevision, proof.TopologyRevision);
        Assert.Equal(11, proof.TotalRowCount);
        Assert.True(proof.ChunkCount > 0);
        Assert.Equal(
            proof.TotalRowCount,
            Enumerable.Range(0, proof.PacketCount)
                .Select(proof.GetPacket)
                .GroupBy(static packet => packet.PersistentChunkId)
                .Sum(static chunk => chunk.First().ChunkRowCount));
    }

    [Fact]
    public void StablePartition_RejectsAContiguousPrefixThatDoesNotCoverTheCapturedChunkTail()
    {
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new StableQueryPartitionProof(
            [
                new StableQueryPacketRange(
                    persistentChunkId: 1,
                    rowStart: 0,
                    rowCount: 2,
                    chunkRowCount: 10),
            ]));

        Assert.Contains("cover", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExplicitFilter_ComposesSelectionWithoutAddingValueAccess()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
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
    public void PersistentFilter_ComposesOneDescriptorAndHasAllocationFreeCacheHits()
    {
        World world = CreateMovingWorld(1, out _);
        GeneratedQueryAccessDescriptor descriptor =
            new IntegrateGeneratedJob().GetGeneratedQueryAccess();
        QueryDefinition filter = world.QueryDefinition().None<GeneratedExcludedTag>().Build();

        GeneratedQueryAccessDescriptor first = descriptor.WithFilter(filter);
        Assert.Same(first, descriptor.WithFilter(filter));

        var concurrent = new GeneratedQueryAccessDescriptor[32];
        Parallel.For(
            0,
            concurrent.Length,
            index => concurrent[index] = descriptor.WithFilter(filter));
        Assert.All(concurrent, value => Assert.Same(first, value));

        _ = MeasurePersistentFilterCacheHits(descriptor, filter, out _);
        GeneratedQueryAccessDescriptor last =
            MeasurePersistentFilterCacheHits(descriptor, filter, out long allocated);

        Assert.Same(first, last);
        Assert.Equal(0, allocated);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static GeneratedQueryAccessDescriptor MeasurePersistentFilterCacheHits(
        GeneratedQueryAccessDescriptor descriptor,
        QueryDefinition filter,
        out long allocated)
    {
        GeneratedQueryAccessDescriptor last = descriptor.WithFilter(filter);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
            last = descriptor.WithFilter(filter);
        allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        GC.KeepAlive(descriptor);
        GC.KeepAlive(filter);
        return last;
    }

    [Fact]
    public void ExactChangedFilter_RunsAfterRangeHazardsAndBeforeCurrentRowWrite()
    {
        using var runtime = new JobRuntimeScope(workerCount: 3);
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
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
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
        using var runtime = new JobRuntimeScope(workerCount: 2);
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
        using var runtime = new JobRuntimeScope(workerCount: 2);
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
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateMovingWorld(6, out Entity[] entities);
        uint tickBefore = world.CurrentTick;

        GeneratedReadOnlyProbeJob.Reset();
        new GeneratedReadOnlyProbeJob().Schedule(world).Complete();
        Assert.Equal(entities.Length, GeneratedReadOnlyProbeJob.ExecutionCount);
        Assert.Equal(tickBefore, world.CurrentTick);

        GeneratedReadOnlyProbeJob.Reset();
        new GeneratedReadOnlyProbeJob().ScheduleParallel(
            world,
            new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();
        Assert.Equal(entities.Length, GeneratedReadOnlyProbeJob.ExecutionCount);
        Assert.Equal(tickBefore, world.CurrentTick);
    }

    [Fact]
    public void EnabledFilter_OwnsPacketBitRangesAndSkipsDisabledRows()
    {
        using var runtime = new JobRuntimeScope(workerCount: 3);
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
        using var runtime = new JobRuntimeScope(workerCount: 2);
        World world = CreateMovingWorld(4, out _);
        QueryDefinition filter = world.QueryDefinition().ChunkChanged<GeneratedPosition>().Build();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            new GeneratedPositionIncrementJob().ScheduleParallel(
                world,
                new JobEntityScheduleOptions(filter: filter)));

        Assert.Contains("parallel", error.Message, StringComparison.OrdinalIgnoreCase);
        new GeneratedPositionIncrementJob().Schedule(
            world,
            new JobEntityScheduleOptions(filter: filter)).Complete();
    }

    [Fact]
    public void ScheduleParallel_WritesDisjointPacketsConcurrently()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        World world = CreateMovingWorld(32, out Entity[] entities);
        GeneratedConcurrencyProbe.Reset();
        try
        {
            new GeneratedConcurrencyProbe().ScheduleParallel(
                world,
                new JobEntityScheduleOptions(rowsPerPacket: 4)).Complete();

            Assert.True(GeneratedConcurrencyProbe.ObservedOverlap);
            foreach (Entity entity in entities)
                Assert.Equal(1, world.Read<GeneratedPosition>(entity).Value);
        }
        finally
        {
            GeneratedConcurrencyProbe.Release();
        }
    }

    [Fact]
    public async Task ScheduleParallel_ResolvesPacketsAfterSemanticDependencyWithoutBlockingIntermediateTopology()
    {
        using var runtime = new JobRuntimeScope(workerCount: 3);
        World world = CreateMovingWorld(4, out _);
        using var dependencyStarted = new ManualResetEventSlim();
        using var releaseDependency = new ManualResetEventSlim();
        JobHandle dependency = JobSystem.Schedule(
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
        Task completed = await Task.WhenAny(mutation, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(
            mutation,
            completed);
        Entity mutatedEntity = await mutation;
        Assert.True(
            mutation.IsCompletedSuccessfully,
            "A pending semantic dependency must not publish a topology guard before packet capture.");

        releaseDependency.Set();
        generated.Complete();
        dependency.Complete();

        Assert.Equal(2, world.Read<GeneratedPosition>(mutatedEntity).Value);
    }

    [Fact]
    public void ScheduleParallel_CapturesAfterDynamicTopologyWriterDescendant()
    {
        using var runtime = new JobRuntimeScope(workerCount: 3);
        World world = CreateMovingWorld(3, out _);
        var capture = new EntityCapture();
        JobHandle dependency = JobSystem.Schedule(
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
        using var runtime = new JobRuntimeScope(workerCount: 1);
        World world = CreateMovingWorld(3, out Entity[] originalEntities);
        using var workerOccupied = new ManualResetEventSlim();
        using var releaseWorker = new ManualResetEventSlim();
        JobHandle occupier = JobSystem.Schedule(
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
        JobHandle writer = JobSystem.Schedule(
            new CreateMovingEntityJob(world, writerCapture),
            accesses);

        releaseWorker.Set();
        Assert.True(
            SpinWait.SpinUntil(() => generated.IsCompleted, TimeSpan.FromSeconds(5)),
            "Capture, registered writer, and attached packet scopes formed a completion ring.");

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(
            () => generated.Complete());
        Assert.Contains("stale", error.Message, StringComparison.OrdinalIgnoreCase);
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
        using var runtime = new JobRuntimeScope(workerCount: 3);
        World world = CreateMovingWorld(3, out _);
        var capture = new EntityCapture();
        JobHandle dependency = JobSystem.Schedule(
            new DynamicTopologyDependencyJob(world, capture));

        new IntegrateGeneratedJob().Schedule(world, dependency).Complete();

        dependency.Complete();
        Assert.NotEqual(Entity.Null, capture.Entity);
        Assert.Equal(3, world.Read<GeneratedPosition>(capture.Entity).Value);
    }

    [Fact]
    public async Task ScheduleParallel_HoldsTopologyLeaseAcrossAllPacketBodies()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        World world = CreateMovingWorld(16, out _);
        GeneratedTopologyLeaseProbe.Reset();
        JobHandle generated = default;
        try
        {
            generated = new GeneratedTopologyLeaseProbe().ScheduleParallel(
                world,
                new JobEntityScheduleOptions(rowsPerPacket: 1));
            Assert.True(GeneratedTopologyLeaseProbe.Started.Wait(TimeSpan.FromSeconds(5)));

            Task<Entity> writer = Task.Run(() => world.CreateEntity());
            Task first = await Task.WhenAny(writer, Task.Delay(TimeSpan.FromMilliseconds(150)));
            Assert.NotSame(writer, first);

            GeneratedTopologyLeaseProbe.Release();
            generated.Complete();
            Entity created = await writer;
            Assert.True(world.IsAlive(created));
        }
        finally
        {
            GeneratedTopologyLeaseProbe.Release();
            if (!generated.IsCompleted)
                generated.Complete();
        }
    }

    [Fact]
    public void ScheduleParallel_FirstCowWriteDetachesSharedChunkOnceForAllPackets()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        World world = CreateMovingWorld(48, out Entity[] entities);

        // Publishing an unrelated command candidate keeps untouched table chunks COW-shared with
        // the previous root. Multiple row packets then reach the first writable detach together.
        world.Commands().CreateEntity();
        world.Flush();
        GeneratedConcurrencyProbe.Reset();
        try
        {
            new GeneratedConcurrencyProbe().ScheduleParallel(
                world,
                new JobEntityScheduleOptions(rowsPerPacket: 1)).Complete();
        }
        finally
        {
            GeneratedConcurrencyProbe.Release();
        }

        Assert.True(GeneratedConcurrencyProbe.ObservedOverlap);
        foreach (Entity entity in entities)
            Assert.Equal(1, world.Read<GeneratedPosition>(entity).Value);
    }

    [Fact]
    public void ScheduleParallel_SparseWrapperFiltersAndWritesRequiredRows()
    {
        using var runtime = new JobRuntimeScope(workerCount: 3);
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
        using var runtime = new JobRuntimeScope(workerCount: 3);
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
    public void ScheduleParallel_UsesOneExecutionVersionAcrossAllPacketsAndTableBufferStorage()
    {
        using var runtime = new JobRuntimeScope(workerCount: 3);
        World world = CreateMovingWorld(12, out Entity[] entities);
        foreach (Entity entity in entities)
        {
            world.AddBuffer<GeneratedBufferElement>(entity);
            world.ExecuteBufferWrite<GeneratedBufferElement>(
                entity,
                static buffer => buffer.Add(new GeneratedBufferElement { Value = 1 }));
            world.AddSparse(entity, new GeneratedSparse { Value = 1 });
        }

        GeneratedVersionedStorageJob.Reset();
        JobHandle handle = default;
        try
        {
            handle = new GeneratedVersionedStorageJob().ScheduleParallel(
                world,
                new JobEntityScheduleOptions(rowsPerPacket: 1));
            Assert.True(GeneratedVersionedStorageJob.Started.Wait(TimeSpan.FromSeconds(5)));

            // Packet workers retain topology-read while paused. Advancing the global clock here
            // must not change the version already assigned to this logical execution.
            _ = world.AcquireSystemTick();
            _ = world.AcquireSystemTick();
            _ = world.AcquireSystemTick();
            GeneratedVersionedStorageJob.Release();
            handle.Complete();

            int positionId = ComponentMetadata<GeneratedPosition>.Id;
            uint[] rowVersions = world.AllArchetypes.ToArray()
                .Where(archetype => archetype.HasComponent(positionId))
                .SelectMany(archetype => archetype.Chunks.ToArray().SelectMany(chunk =>
                {
                    int column = archetype.Column(positionId);
                    return Enumerable.Range(0, chunk.Count)
                        .Select(row => chunk.WriteVersionRows(column)[row]);
                }))
                .ToArray();
            uint executionVersion = Assert.Single(rowVersions.Distinct());

            int bufferId = BufferComponents.Header<GeneratedBufferElement>();
            uint[] bufferVersions = world.AllArchetypes.ToArray()
                .Where(archetype => archetype.HasComponent(bufferId))
                .SelectMany(archetype => archetype.Chunks.ToArray().SelectMany(chunk =>
                {
                    int column = archetype.Column(bufferId);
                    return Enumerable.Range(0, chunk.Count)
                        .Select(row => chunk.WriteVersionRows(column)[row]);
                }))
                .ToArray();

            Assert.Equal(entities.Length, rowVersions.Length);
            Assert.Equal(entities.Length, bufferVersions.Length);
            Assert.All(rowVersions, version => Assert.Equal(executionVersion, version));
            Assert.All(bufferVersions, version => Assert.Equal(executionVersion, version));
        }
        finally
        {
            GeneratedVersionedStorageJob.Release();
            if (!handle.IsCompleted)
                handle.Complete();
        }
    }

    [Fact]
    public void ColdBufferDescriptor_CompilesLogicalBufferAdmissionBeforeAnyBufferExists()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        GeneratedQueryAccessDescriptor descriptor =
            new GeneratedColdBufferJob().GetGeneratedQueryAccess();
        Assert.Equal(1, descriptor.AccessCount);
        Assert.Equal(GeneratedQueryStorage.Buffer, descriptor.GetAccess(0).Storage);

        var world = new World();
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
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
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

    private static World CreateMovingWorld(int count, out Entity[] entities)
    {
        var world = new World();
        entities = new Entity[count];
        for (int i = 0; i < count; i++)
        {
            Entity entity = world.CreateEntity(new GeneratedPosition());
            world.Add(entity, new GeneratedVelocity { Value = 1 });
            entities[i] = entity;
        }
        return world;
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

public partial struct GeneratedConcurrencyProbe : IJobEntity
{
    private static ManualResetEventSlim s_release = new();
    private static int s_entered;
    private static int s_observedOverlap;

    public static bool ObservedOverlap => Volatile.Read(ref s_observedOverlap) != 0;

    public static void Reset()
    {
        s_release.Dispose();
        s_release = new ManualResetEventSlim();
        Volatile.Write(ref s_entered, 0);
        Volatile.Write(ref s_observedOverlap, 0);
    }

    public static void Release() => s_release.Set();

    public void Execute(ref GeneratedPosition position)
    {
        int entered = Interlocked.Increment(ref s_entered);
        if (entered == 1)
        {
            if (s_release.Wait(TimeSpan.FromSeconds(5)))
                Volatile.Write(ref s_observedOverlap, 1);
        }
        else if (entered == 2)
        {
            Volatile.Write(ref s_observedOverlap, 1);
            s_release.Set();
        }
        position.Value++;
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
    private static int s_executionCount;

    public static int ExecutionCount => Volatile.Read(ref s_executionCount);

    public static void Reset() => Volatile.Write(ref s_executionCount, 0);

    public void Execute(in GeneratedPosition position)
    {
        _ = position.Value;
        Interlocked.Increment(ref s_executionCount);
    }
}

public partial struct GeneratedCommandBufferEscapeJob : IJobEntity
{
    private static CommandBuffer? s_commands;

    public static void Configure(CommandBuffer commands)
    {
        s_commands = commands;
    }

    public static void Clear()
    {
        s_commands = null;
    }

    public void Execute(Entity entity, in GeneratedPosition position)
    {
        _ = position.Value;
        s_commands!.Replace(entity, new GeneratedPosition { Value = 99 });
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

public partial struct GeneratedVersionedStorageJob : IJobEntity
{
    private static ManualResetEventSlim s_started = new();
    private static ManualResetEventSlim s_release = new();
    private static int s_first;

    public static ManualResetEventSlim Started => s_started;

    public static void Reset()
    {
        s_started.Dispose();
        s_release.Dispose();
        s_started = new ManualResetEventSlim();
        s_release = new ManualResetEventSlim();
        Volatile.Write(ref s_first, 0);
    }

    public static void Release() => s_release.Set();

    public void Execute(
        ref GeneratedPosition position,
        DynamicBuffer<GeneratedBufferElement> buffer,
        ref GeneratedSparse sparse)
    {
        if (Interlocked.CompareExchange(ref s_first, 1, 0) == 0)
        {
            buffer[0].Value++;
            s_started.Set();
            if (!s_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Version probe release was not signaled.");
        }
        else
        {
            if (!s_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Version probe release was not signaled.");
            buffer[0].Value++;
        }

        position.Value++;
        sparse.Value++;
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

public partial struct GeneratedTopologyLeaseProbe : IJobEntity
{
    private static ManualResetEventSlim s_started = new();
    private static ManualResetEventSlim s_release = new();
    private static int s_first;

    public static ManualResetEventSlim Started => s_started;

    public static void Reset()
    {
        s_started.Dispose();
        s_release.Dispose();
        s_started = new ManualResetEventSlim();
        s_release = new ManualResetEventSlim();
        Volatile.Write(ref s_first, 0);
    }

    public static void Release() => s_release.Set();

    public void Execute(ref GeneratedPosition position)
    {
        if (Interlocked.CompareExchange(ref s_first, 1, 0) == 0)
        {
            s_started.Set();
            if (!s_release.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("Topology lease probe was not released.");
        }
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
