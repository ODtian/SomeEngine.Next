using SomeEngine.ECS.Components;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Hooks;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class WorldJobAdmissionTests
{
    private static World? s_escapedWorld;

    [Fact]
    public void FreshWorldRemainsUsableBySynchronousCallersWithoutTypedAccessBinding()
    {
        EnsureAmbientAdmissionInstalled();
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();

        Entity entity = world.CreateEntity(new AdmissionProbe { Value = 3 });
        world.Replace(entity, new AdmissionProbe { Value = 5 });
        Entity destination = world.CreateEntity();
        using var commands = new CommandBuffer(world);
        commands.Add(destination, new AdmissionProbe { Value = 7 });
        commands.Playback();

        Assert.Equal(5, world.Read<AdmissionProbe>(entity).Value);
        Assert.Equal(7, world.Read<AdmissionProbe>(destination).Value);
    }

    [Fact]
    public void FreshWorldManagedPayloadEscapeCannotReplaceWithoutDeclaredAccess()
    {
        EnsureAmbientAdmissionInstalled();
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = world.CreateEntity(new AdmissionProbe { Value = 11 });
        int entityCount = world.EntityCount;
        long topologyRevision = world.PublishedTopologyRevision;

        JobHandle writer = JobSystem.Schedule(
            new UndeclaredWorldReplaceJob(world, entity, value: 29));

        Assert.Throws<JobResourceSafetyException>(() => writer.Complete());
        Assert.Equal(entityCount, world.EntityCount);
        Assert.Equal(topologyRevision, world.PublishedTopologyRevision);
        Assert.Equal(11, world.Read<AdmissionProbe>(entity).Value);
    }

    [Fact]
    public void FreshWorldStaticEscapeCannotCreateWithoutDeclaredAccess()
    {
        EnsureAmbientAdmissionInstalled();
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        int entityCount = world.EntityCount;
        long topologyRevision = world.PublishedTopologyRevision;
        s_escapedWorld = world;
        try
        {
            JobHandle writer = JobSystem.Schedule(new UndeclaredStaticWorldCreateJob());

            Assert.Throws<JobResourceSafetyException>(() => writer.Complete());
            Assert.Equal(entityCount, world.EntityCount);
            Assert.Equal(topologyRevision, world.PublishedTopologyRevision);
        }
        finally
        {
            s_escapedWorld = null;
        }
    }

    [Fact]
    public void FreshWorldPrebuiltCommandBufferCannotRecordFromJob()
    {
        EnsureAmbientAdmissionInstalled();
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = world.CreateEntity();
        using var commands = new CommandBuffer(world);

        JobHandle recorder = JobSystem.Schedule(
            new RecordCommandJob(commands, entity));

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => recorder.Complete());
        Assert.Contains("CommandBuffer", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, commands.CommandCount);
        Assert.False(world.Has<AdmissionProbe>(entity));
    }

    [Fact]
    public async Task FirstJobBindingFailsFastForAnInFlightUnboundWorldOperation()
    {
        EnsureAmbientAdmissionInstalled();
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 2,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });

        using var queryEntered = new ManualResetEventSlim();
        using var releaseQuery = new ManualResetEventSlim();
        try
        {
            var world = new World();
            world.CreateEntity(new AdmissionProbe { Value = 1 });
            QueryHandle query = world.Query(
                world.QueryDefinition().Read<AdmissionProbe>());

            Task reader = Task.Run(() =>
                world.ExecuteQuery(query, _ =>
                {
                    queryEntered.Set();
                    if (!releaseQuery.Wait(TimeSpan.FromSeconds(5)))
                        throw new TimeoutException("Unbound query release timed out.");
                }));
            Assert.True(queryEntered.Wait(TimeSpan.FromSeconds(3)));

            Task firstBinding = Task.Run(() =>
                _ = ComponentJobAccess<AdmissionProbe>.Write(world));
            InvalidOperationException error =
                await Assert.ThrowsAsync<InvalidOperationException>(() => firstBinding);
            Assert.Contains("retry", error.Message, StringComparison.OrdinalIgnoreCase);

            releaseQuery.Set();
            await reader;
            _ = ComponentJobAccess<AdmissionProbe>.Write(world);
        }
        finally
        {
            releaseQuery.Set();
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    [Fact]
    public void UnboundCallbackCompletingAFirstUseRawJobFailsInsteadOfDeadlocking()
    {
        EnsureAmbientAdmissionInstalled();
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        world.CreateEntity(new AdmissionProbe { Value = 1 });
        QueryHandle query = world.Query(world.QueryDefinition().Read<AdmissionProbe>());
        InvalidOperationException? jobFault = null;

        world.ExecuteQuery(query, _ =>
        {
            JobHandle handle = JobSystem.Schedule(new ReadEntityCountJob(world));
            jobFault = Assert.Throws<InvalidOperationException>(() => handle.Complete());
        });

        Assert.NotNull(jobFault);
        Assert.Contains("retry", jobFault.Message, StringComparison.OrdinalIgnoreCase);
        _ = ComponentJobAccess<AdmissionProbe>.Read(world);
    }

    [Fact]
    public void FirstJobBindingInsideAnUnboundWorldCallbackFailsInsteadOfDeadlocking()
    {
        EnsureAmbientAdmissionInstalled();
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 2,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        try
        {
            var world = new World();
            world.CreateEntity(new AdmissionProbe { Value = 1 });
            QueryHandle query = world.Query(
                world.QueryDefinition().Read<AdmissionProbe>());

            InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
                world.ExecuteQuery(
                    query,
                    cursor =>
                    {
                        _ = cursor;
                        _ = ComponentJobAccess<AdmissionProbe>.Read(world);
                    }));

            Assert.Contains("before entering", error.Message, StringComparison.OrdinalIgnoreCase);
            _ = ComponentJobAccess<AdmissionProbe>.Read(world);
        }
        finally
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    [Fact]
    public void JobCommandBufferRejectionDoesNotConsumePendingCommandWave()
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 2,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        try
        {
            var world = new World();
            Entity entity = world.CreateEntity();
            var commands = world.Commands();
            commands.Add(entity, new AdmissionProbe { Value = 17 });

            JobResourceAccess topologyWrite = RelationshipJobAccess.TopologyWrite(world);
            JobHandle topologyWriter = JobSystem.Schedule(
                new FlushWorldJob(world),
                topologyWrite);

            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => topologyWriter.Complete());
            Assert.Contains("CommandBuffer", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, commands.CommandCount);
            Assert.False(world.Has<AdmissionProbe>(entity));

            world.Flush();
            Assert.Equal(17, world.Read<AdmissionProbe>(entity).Value);
        }
        finally
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    [Fact]
    public void JobCommandBufferRejectionDoesNotPoisonDirectPlayback()
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 2,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        try
        {
            var world = new World();
            Entity entity = world.CreateEntity();
            var commands = new CommandBuffer(world);
            commands.Add(entity, new AdmissionProbe { Value = 29 });

            JobResourceAccess topologyWrite = RelationshipJobAccess.TopologyWrite(world);
            JobHandle topologyWriter = JobSystem.Schedule(
                new PlaybackCommandJob(commands),
                topologyWrite);

            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => topologyWriter.Complete());
            Assert.Contains("CommandBuffer", error.Message, StringComparison.Ordinal);
            Assert.Equal(1, commands.CommandCount);
            Assert.False(world.Has<AdmissionProbe>(entity));

            commands.Playback();
            Assert.Equal(29, world.Read<AdmissionProbe>(entity).Value);
            commands.Dispose();
        }
        finally
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    [Fact]
    public void OrdinaryJobCannotRecordIntoPrebuiltCommandBuffer()
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 2,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        try
        {
            var world = new World();
            Entity entity = world.CreateEntity();
            var commands = world.Commands();

            _ = RelationshipJobAccess.TopologyWrite(world);
            JobHandle recorder = JobSystem.Schedule(
                new RecordCommandJob(commands, entity));

            InvalidOperationException error =
                Assert.Throws<InvalidOperationException>(() => recorder.Complete());
            Assert.Contains("CommandBuffer", error.Message, StringComparison.Ordinal);
            Assert.Equal(0, commands.CommandCount);
            Assert.False(world.Has<AdmissionProbe>(entity));
        }
        finally
        {
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    [Fact]
    public void MultiWorkItemOwnerCannotUseOrdinaryTopologyWriteApis()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        int entityCount = world.EntityCount;
        long topologyRevision = world.PublishedTopologyRevision;

        JobHandle writer = JobSystem.ScheduleParallel(
            new ParallelCreateEntityJob(world),
            length: 2,
            batchSize: 1,
            RelationshipJobAccess.TopologyWrite(world));

        Assert.Throws<JobResourceSafetyException>(() => writer.Complete());
        Assert.Equal(entityCount, world.EntityCount);
        Assert.Equal(topologyRevision, world.PublishedTopologyRevision);
    }

    [Fact]
    public void MultiWorkItemOwnerCannotUseOrdinaryComponentWriteApis()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = world.CreateEntity(new AdmissionProbe { Value = 3 });
        long topologyRevision = world.PublishedTopologyRevision;
        JobResourceAccess[] accesses =
        [
            RelationshipJobAccess.TopologyRead(world),
            ComponentJobAccess<AdmissionProbe>.Write(world),
        ];

        JobHandle writer = JobSystem.ScheduleParallel(
            new ParallelReplaceJob(world, entity, value: 41),
            length: 2,
            batchSize: 1,
            accesses);

        Assert.Throws<JobResourceSafetyException>(() => writer.Complete());
        Assert.Equal(3, world.Read<AdmissionProbe>(entity).Value);
        Assert.Equal(topologyRevision, world.PublishedTopologyRevision);
    }

    [Fact]
    public void MultiWorkItemReadOnlyWorldAccessRemainsAvailable()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = world.CreateEntity(new AdmissionProbe { Value = 13 });
        var values = new int[4];
        JobResourceAccess[] accesses =
        [
            RelationshipJobAccess.TopologyRead(world),
            ComponentJobAccess<AdmissionProbe>.Read(world),
        ];

        JobSystem.ScheduleParallel(
            new ParallelReadJob(world, entity, values),
            values.Length,
            batchSize: 1,
            accesses).Complete();

        Assert.Equal([13, 13, 13, 13], values);
    }

    [Fact]
    public void SingleWorkItemParallelOwnerCanUseOrdinaryTopologyWriteApis()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();

        JobSystem.ScheduleParallel(
            new ParallelCreateEntityJob(world),
            length: 1,
            batchSize: 8,
            RelationshipJobAccess.TopologyWrite(world)).Complete();

        Assert.Equal(1, world.EntityCount);
    }

    [Fact]
    public void SerialTopologyWriteJobCanRecordNextWaveFromImmediateHook()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity trigger = world.CreateEntity();
        Entity destination = world.CreateEntity();
        world.Hooks<AdmissionProbe>().OnAdd(
            (DeferredWorld hookWorld, Entity _, in AdmissionProbe value) =>
            {
                hookWorld.Commands().Add(
                    destination,
                    new HookResult { Value = value.Value + 1 });
            });

        JobSystem.Schedule(
            new AddProbeJob(world, trigger, value: 17),
            RelationshipJobAccess.TopologyWrite(world)).Complete();

        Assert.Equal(17, world.Read<AdmissionProbe>(trigger).Value);
        Assert.False(world.Has<HookResult>(destination));

        world.Flush();
        Assert.Equal(18, world.Read<HookResult>(destination).Value);
    }

    [Fact]
    public void DeferredCommandWriterExposesOnlyHookScopedRecordSurfaces()
    {
        Type writer = typeof(DeferredCommandWriter);
        Assert.True(writer.IsByRefLike);
        Assert.Equal(
            writer,
            typeof(DeferredWorld).GetMethod(nameof(DeferredWorld.Commands))!.ReturnType);

        string[] forbidden = ["Playback", "Clear", "Dispose", "get_CommandCount"];
        Assert.DoesNotContain(
            writer.GetMethods(System.Reflection.BindingFlags.Instance |
                              System.Reflection.BindingFlags.Public),
            method => forbidden.Contains(method.Name, StringComparer.Ordinal));
        Assert.True(typeof(RelationCommandWriter<AdmissionLink>).IsByRefLike);
        Assert.True(typeof(HierarchyCommandWriter<Domain>).IsByRefLike);
    }

    [Fact]
    public void ImmediateHookCannotEscapeThroughCapturedWorldCommands()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity trigger = world.CreateEntity();
        Entity destination = world.CreateEntity();
        CommandBuffer exactWorldBuffer = world.Commands();
        world.Hooks<AdmissionProbe>().OnAdd(
            (DeferredWorld _, Entity _, in AdmissionProbe _) =>
                exactWorldBuffer.Add(destination, new HookResult { Value = 1 }));

        JobHandle writer = JobSystem.Schedule(
            new AddProbeJob(world, trigger, value: 17),
            RelationshipJobAccess.TopologyWrite(world));

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => writer.Complete());
        Assert.Contains("CommandBuffer", error.Message, StringComparison.Ordinal);
        // Direct World mutation commits before its immediate notification hook runs.
        Assert.True(world.Has<AdmissionProbe>(trigger));
        Assert.False(world.Has<HookResult>(destination));
        Assert.Equal(0, exactWorldBuffer.CommandCount);
    }

    [Fact]
    public void ImmediateHookCannotRecordIntoAnotherPrebuiltCommandBuffer()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity trigger = world.CreateEntity();
        Entity destination = world.CreateEntity();
        using var other = new CommandBuffer(world);
        world.Hooks<AdmissionProbe>().OnAdd(
            (DeferredWorld _, Entity _, in AdmissionProbe _) =>
                other.Add(destination, new HookResult { Value = 1 }));

        JobHandle writer = JobSystem.Schedule(
            new AddProbeJob(world, trigger, value: 17),
            RelationshipJobAccess.TopologyWrite(world));

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => writer.Complete());
        Assert.Contains("CommandBuffer", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, other.CommandCount);
        Assert.True(world.Has<AdmissionProbe>(trigger));
    }

    [Fact]
    public void LeakedDeferredWorldCannotMintACommandWriterAfterItsHookReturns()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity trigger = world.CreateEntity();
        Entity destination = world.CreateEntity();
        var capture = new DeferredWorldCapture();
        world.Hooks<AdmissionProbe>().OnAdd(
            (DeferredWorld hookWorld, Entity _, in AdmissionProbe _) =>
            {
                capture.World = hookWorld;
                capture.HasWorld = true;
                hookWorld.Commands().Add(destination, new HookResult { Value = 1 });
            });

        JobHandle writer = JobSystem.Schedule(
            new AddThenReuseDeferredWorldJob(
                world,
                trigger,
                destination,
                capture),
            RelationshipJobAccess.TopologyWrite(world));

        InvalidOperationException error =
            Assert.Throws<InvalidOperationException>(() => writer.Complete());
        Assert.Contains("deferred command writer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(world.Has<AdmissionProbe>(trigger));
        Assert.False(world.Has<HookResult>(destination));

        world.Flush();
        Assert.Equal(1, world.Read<HookResult>(destination).Value);
    }

    [Fact]
    public void RawDeferredOverloadsRejectJobAccessBeforeReadingInvalidHandles()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        CommandBuffer commands = world.Commands();
        JobResourceAccess topologyWrite = RelationshipJobAccess.TopologyWrite(world);

        InvalidOperationException entityError = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new InvalidDeferredEntityRecordJob(commands), topologyWrite).Complete());
        InvalidOperationException relationError = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new InvalidDeferredRelationRecordJob(commands), topologyWrite).Complete());
        InvalidOperationException hierarchyError = Assert.Throws<InvalidOperationException>(() =>
            JobSystem.Schedule(new InvalidDeferredHierarchyRecordJob(commands), topologyWrite).Complete());

        Assert.Contains("CommandBuffer", entityError.Message, StringComparison.Ordinal);
        Assert.Contains("CommandBuffer", relationError.Message, StringComparison.Ordinal);
        Assert.Contains("CommandBuffer", hierarchyError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("default/uninitialized", entityError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("default/uninitialized", relationError.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("default/uninitialized", hierarchyError.Message, StringComparison.Ordinal);
        Assert.Equal(0, commands.CommandCount);
    }

    [Fact]
    public void HookFaultPreservesOriginalExceptionAndDiscardsOwnedOverlayInsideJob()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity trigger = world.CreateEntity();
        Entity destination = world.CreateEntity();
        world.Hooks<AdmissionProbe>().OnAdd(
            (DeferredWorld hookWorld, Entity _, in AdmissionProbe _) =>
            {
                hookWorld.Commands().Add(destination, new HookResult { Value = 1 });
                throw new AdmissionHookFaultException();
            });

        JobHandle writer = JobSystem.Schedule(
            new CandidateAddProbeJob(world, trigger, value: 17),
            RelationshipJobAccess.TopologyWrite(world));

        Assert.Throws<AdmissionHookFaultException>(() => writer.Complete());
        Assert.False(world.Has<AdmissionProbe>(trigger));
        Assert.False(world.Has<HookResult>(destination));

        world.Flush();
        Assert.False(world.Has<HookResult>(destination));
    }

    [Fact]
    public void ConcurrentFlushReservesTheExactEarlierWaveWithoutBlockingHookRecording()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        using var hookEntered = new ManualResetEventSlim();
        using var allowHookRecord = new ManualResetEventSlim();
        using var hookRecorded = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        var world = new World();
        Entity earlierDestination = world.CreateEntity();
        Entity laterDestination = world.CreateEntity();
        Entity trigger = world.CreateEntity();
        CommandBuffer earlierWave = world.Commands();
        earlierWave.Add(earlierDestination, new HookResult { Value = 1 });

        world.Hooks<AdmissionProbe>().OnAdd(
            (DeferredWorld hookWorld, Entity _, in AdmissionProbe _) =>
            {
                DeferredCommandWriter hookCommands = hookWorld.Commands();
                hookEntered.Set();
                if (!allowHookRecord.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Hook record release timed out.");

                hookCommands.Add(laterDestination, new HookResult { Value = 2 });
                hookRecorded.Set();
                if (!releaseHook.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Hook completion release timed out.");
            });

        JobHandle writer = JobSystem.Schedule(
            new AddProbeJob(world, trigger, value: 17),
            RelationshipJobAccess.TopologyWrite(world));
        Assert.True(hookEntered.Wait(TimeSpan.FromSeconds(5)));

        Exception? flushFault = null;
        var flushThread = new Thread(() =>
        {
            try
            {
                world.Flush();
            }
            catch (Exception exception)
            {
                flushFault = exception;
            }
        });

        try
        {
            flushThread.Start();
            Assert.True(SpinWait.SpinUntil(
                () =>
                {
                    try
                    {
                        _ = earlierWave.CommandCount;
                        return false;
                    }
                    catch (InvalidOperationException exception)
                        when (exception.Message.Contains("reserved", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                },
                TimeSpan.FromSeconds(5)));

            // Flush has reserved the direct wave and is waiting for topology admission. The
            // command gate must be free so the current hook can publish its distinct later wave.
            allowHookRecord.Set();
            Assert.True(hookRecorded.Wait(TimeSpan.FromSeconds(5)));
            releaseHook.Set();
            writer.Complete();
            Assert.True(flushThread.Join(TimeSpan.FromSeconds(5)));

            Assert.Null(flushFault);
            Assert.Equal(1, world.Read<HookResult>(earlierDestination).Value);
            Assert.False(world.Has<HookResult>(laterDestination));

            world.Flush();
            Assert.Equal(2, world.Read<HookResult>(laterDestination).Value);
        }
        finally
        {
            allowHookRecord.Set();
            releaseHook.Set();
            flushThread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void EmptyFlushCannotDisposeAWriterMintedByAnExecutingHook()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        using var writerMinted = new ManualResetEventSlim();
        using var allowRecord = new ManualResetEventSlim();
        var world = new World();
        Entity trigger = world.CreateEntity();
        Entity destination = world.CreateEntity();
        world.Hooks<AdmissionProbe>().OnAdd(
            (DeferredWorld hookWorld, Entity _, in AdmissionProbe _) =>
            {
                DeferredCommandWriter commands = hookWorld.Commands();
                writerMinted.Set();
                if (!allowRecord.Wait(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("Minted writer release timed out.");
                commands.Add(destination, new HookResult { Value = 5 });
            });

        JobHandle writer = JobSystem.Schedule(
            new AddProbeJob(world, trigger, value: 17),
            RelationshipJobAccess.TopologyWrite(world));
        Assert.True(writerMinted.Wait(TimeSpan.FromSeconds(5)));

        Exception? flushFault = null;
        var flushThread = new Thread(() =>
        {
            try
            {
                world.Flush();
            }
            catch (Exception exception)
            {
                flushFault = exception;
            }
        });

        try
        {
            flushThread.Start();
            // The hook-owned empty recording is not a published playback wave. Flush must neither
            // wait for topology admission nor dispose the minted writer's target.
            Assert.True(flushThread.Join(TimeSpan.FromSeconds(5)));
            Assert.Null(flushFault);

            allowRecord.Set();
            writer.Complete();
            Assert.False(world.Has<HookResult>(destination));

            world.Flush();
            Assert.Equal(5, world.Read<HookResult>(destination).Value);
        }
        finally
        {
            allowRecord.Set();
            flushThread.Join(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public void DirectHierarchyMutationWaitsForAnExistingTopologyOwner()
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 2,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });

        using var blockerStarted = new ManualResetEventSlim();
        using var releaseBlocker = new ManualResetEventSlim();
        using var mutationCompleted = new ManualResetEventSlim();
        JobHandle blocker = default;
        Thread? mutationThread = null;
        Exception? mutationFault = null;
        try
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            Entity child = world.CreateEntity();

            blocker = HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new BlockingJob(blockerStarted, releaseBlocker));
            Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(5)));

            mutationThread = new Thread(() =>
            {
                try
                {
                    Hierarchy<Domain>.SetParent(world, child, parent);
                }
                catch (Exception exception)
                {
                    mutationFault = exception;
                }
                finally
                {
                    mutationCompleted.Set();
                }
            });
            mutationThread.Start();

            Assert.False(mutationCompleted.Wait(TimeSpan.FromMilliseconds(100)));
            releaseBlocker.Set();
            Assert.True(mutationThread.Join(TimeSpan.FromSeconds(5)));
            blocker.Complete();

            Assert.Null(mutationFault);
            Assert.Equal(parent, Hierarchy<Domain>.GetParent(world, child));
        }
        finally
        {
            releaseBlocker.Set();
            blocker.Complete();
            mutationThread?.Join(TimeSpan.FromSeconds(5));
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    [Fact]
    public void DirectStructuralCallHoldsTopologyAdmissionThroughImmediateHooks()
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 2,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });

        using var hookStarted = new ManualResetEventSlim();
        using var releaseHook = new ManualResetEventSlim();
        using var jobStarted = new ManualResetEventSlim();
        Thread? mutationThread = null;
        JobHandle laterWriter = default;
        Exception? mutationFault = null;
        try
        {
            var world = new World();
            Entity entity = world.CreateEntity();

            // Declaring the resource installs the optional ECS -> Job coordinator on this World.
            _ = RelationshipJobAccess.TopologyWrite(world);
            world.Hooks<AdmissionProbe>().OnAdd(
                (DeferredWorld _, Entity _, in AdmissionProbe _) =>
                {
                    hookStarted.Set();
                    releaseHook.Wait();
                });

            mutationThread = new Thread(() =>
            {
                try
                {
                    world.Add(entity, new AdmissionProbe { Value = 1 });
                }
                catch (Exception exception)
                {
                    mutationFault = exception;
                }
            });
            mutationThread.Start();
            Assert.True(hookStarted.Wait(TimeSpan.FromSeconds(5)));

            laterWriter = HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new SignalJob(jobStarted));
            Assert.False(jobStarted.Wait(TimeSpan.FromMilliseconds(100)));

            releaseHook.Set();
            Assert.True(mutationThread.Join(TimeSpan.FromSeconds(5)));
            laterWriter.Complete();

            Assert.Null(mutationFault);
            Assert.True(jobStarted.IsSet);
            Assert.Equal(1, world.Read<AdmissionProbe>(entity).Value);
        }
        finally
        {
            releaseHook.Set();
            mutationThread?.Join(TimeSpan.FromSeconds(5));
            laterWriter.Complete();
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    private readonly struct BlockingJob : IJob
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingJob(ManualResetEventSlim started, ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            _started.Set();
            _release.Wait();
        }
    }

    private readonly struct ReadEntityCountJob : IJob
    {
        private readonly World _world;

        internal ReadEntityCountJob(World world)
        {
            _world = world;
        }

        public void Execute()
        {
            _ = _world.EntityCount;
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

    private readonly struct FlushWorldJob : IJob
    {
        private readonly World _world;

        internal FlushWorldJob(World world)
        {
            _world = world;
        }

        public void Execute()
        {
            _world.Flush();
        }
    }

    private readonly struct UndeclaredWorldReplaceJob : IJob
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly int _value;

        internal UndeclaredWorldReplaceJob(World world, Entity entity, int value)
        {
            _world = world;
            _entity = entity;
            _value = value;
        }

        public void Execute()
        {
            _world.Replace(_entity, new AdmissionProbe { Value = _value });
        }
    }

    private readonly struct UndeclaredStaticWorldCreateJob : IJob
    {
        public void Execute()
        {
            s_escapedWorld!.CreateEntity();
        }
    }

    private readonly struct RecordCommandJob : IJob
    {
        private readonly CommandBuffer _commands;
        private readonly Entity _entity;

        internal RecordCommandJob(CommandBuffer commands, Entity entity)
        {
            _commands = commands;
            _entity = entity;
        }

        public void Execute()
        {
            _commands.Add(_entity, new AdmissionProbe { Value = 23 });
        }
    }

    private readonly struct PlaybackCommandJob : IJob
    {
        private readonly CommandBuffer _commands;

        internal PlaybackCommandJob(CommandBuffer commands)
        {
            _commands = commands;
        }

        public void Execute()
        {
            _commands.Playback();
        }
    }

    private readonly struct ParallelCreateEntityJob : IJobParallelFor
    {
        private readonly World _world;

        internal ParallelCreateEntityJob(World world)
        {
            _world = world;
        }

        public void Execute(int index)
        {
            _ = index;
            _world.CreateEntity();
        }
    }

    private readonly struct ParallelReplaceJob : IJobParallelFor
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly int _value;

        internal ParallelReplaceJob(World world, Entity entity, int value)
        {
            _world = world;
            _entity = entity;
            _value = value;
        }

        public void Execute(int index)
        {
            _ = index;
            _world.Replace(_entity, new AdmissionProbe { Value = _value });
        }
    }

    private readonly struct ParallelReadJob : IJobParallelFor
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly int[] _values;

        internal ParallelReadJob(World world, Entity entity, int[] values)
        {
            _world = world;
            _entity = entity;
            _values = values;
        }

        public void Execute(int index)
        {
            _values[index] = _world.Read<AdmissionProbe>(_entity).Value;
        }
    }

    private readonly struct AddProbeJob : IJob
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly int _value;

        internal AddProbeJob(World world, Entity entity, int value)
        {
            _world = world;
            _entity = entity;
            _value = value;
        }

        public void Execute()
        {
            _world.Add(_entity, new AdmissionProbe { Value = _value });
        }
    }

    private readonly struct CandidateAddProbeJob : IJob
    {
        private readonly World _world;
        private readonly Entity _entity;
        private readonly int _value;

        internal CandidateAddProbeJob(World world, Entity entity, int value)
        {
            _world = world;
            _entity = entity;
            _value = value;
        }

        public void Execute()
        {
            using StructuralMutationScope mutation = _world.BeginStructuralMutation();
            _world.Add(_entity, new AdmissionProbe { Value = _value });
            mutation.Commit();
        }
    }

    private readonly struct AddThenReuseDeferredWorldJob : IJob
    {
        private readonly World _world;
        private readonly Entity _trigger;
        private readonly Entity _destination;
        private readonly DeferredWorldCapture _capture;

        internal AddThenReuseDeferredWorldJob(
            World world,
            Entity trigger,
            Entity destination,
            DeferredWorldCapture capture)
        {
            _world = world;
            _trigger = trigger;
            _destination = destination;
            _capture = capture;
        }

        public void Execute()
        {
            _world.Add(_trigger, new AdmissionProbe { Value = 17 });
            if (!_capture.HasWorld)
                throw new InvalidOperationException("The immediate hook did not publish its DeferredWorld capture.");
            _capture.World.Commands().Add(_destination, new HookResult { Value = 2 });
        }
    }

    private readonly struct InvalidDeferredEntityRecordJob : IJob
    {
        private readonly CommandBuffer _commands;

        internal InvalidDeferredEntityRecordJob(CommandBuffer commands)
        {
            _commands = commands;
        }

        public void Execute()
        {
            _commands.Add(default(DeferredEntity), new HookResult { Value = 1 });
        }
    }

    private readonly struct InvalidDeferredRelationRecordJob : IJob
    {
        private readonly CommandBuffer _commands;

        internal InvalidDeferredRelationRecordJob(CommandBuffer commands)
        {
            _commands = commands;
        }

        public void Execute()
        {
            _commands.Relations<AdmissionLink>().Destroy(default(DeferredRelationEdge<AdmissionLink>));
        }
    }

    private readonly struct InvalidDeferredHierarchyRecordJob : IJob
    {
        private readonly CommandBuffer _commands;

        internal InvalidDeferredHierarchyRecordJob(CommandBuffer commands)
        {
            _commands = commands;
        }

        public void Execute()
        {
            _commands.Hierarchy<Domain>().Detach(default(DeferredEntity));
        }
    }

    private sealed class DeferredWorldCapture
    {
        internal DeferredWorld World;
        internal bool HasWorld;
    }

    private sealed class AdmissionHookFaultException : Exception;

    private static void EnsureAmbientAdmissionInstalled()
    {
        // The escape jobs deliberately call no Systems access helper. Force only the optional
        // integration module to load; this installs the ambient bridge without binding a World.
        System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(
            typeof(RelationshipJobAccess).Module.ModuleHandle);
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

    private struct AdmissionProbe : IComponent
    {
        internal int Value;
    }

    private struct HookResult : IComponent
    {
        internal int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct AdmissionLink : IComponent;
}
