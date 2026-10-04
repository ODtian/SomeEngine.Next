using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;
using Commands = SomeEngine.ECS.Commands;
using Hierarchy = SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class JobCommandBufferTests : JobTest
{
    [Fact]
    public void ParallelProducersMergeByStableLogicalIndex()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        using var commands = new JobCommandBuffer(world, producerCount: 32);

        commands.ScheduleParallel(new CreateByIndexProducer(), batchSize: 1);
        commands.SchedulePlayback().Complete();

        var rows = new List<(int EntityIndex, int Value)>();
        var query = world.Query(world.QueryDefinition().Read<JobCommandValue>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
                rows.Add((row.Entity.Index, row.Read<JobCommandValue>().Value));
        });

        Assert.Equal(32, rows.Count);
        Assert.Equal(
            Enumerable.Range(0, 32),
            rows.OrderBy(static row => row.EntityIndex).Select(static row => row.Value));
    }

    [Fact]
    public void ManyRelationshipProducerSegmentsShareOneTypedGenerationAndLinearAdjacencyBatch()
    {
        const int ProducerCount = 1024;
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity source = world.CreateEntity();
        var targets = new Entity[ProducerCount];
        var pending = new Commands.DeferredRelationEdge<JobCommandLink>[ProducerCount];
        for (int i = 0; i < targets.Length; i++)
            targets[i] = world.CreateEntity();

        using var commands = new JobCommandBuffer(world, ProducerCount);

        commands.ScheduleParallel(
            new SameSourceRelationProducer(source, targets, pending),
            batchSize: 16);
        commands.SchedulePlayback().Complete();

        Assert.Equal(
            ProducerCount,
            world.GetOutgoingRelations<JobCommandLink>(source).Entries.Length);
        Assert.All(pending, static edge => Assert.True(edge.TryResolve(out _)));
    }

    [Fact]
    public void DeferredUniqueTargetSwapAcrossSegmentsValidatesTheMergedFinalImage()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity sourceA = world.CreateEntity();
        Entity sourceB = world.CreateEntity();
        Entity targetA = world.CreateEntity();
        Entity targetB = world.CreateEntity();
        RelationEdge<JobCommandUniqueTarget> edgeA = world.CreateRelation(
            sourceA,
            targetA,
            new JobCommandUniqueTarget());
        RelationEdge<JobCommandUniqueTarget> edgeB = world.CreateRelation(
            sourceB,
            targetB,
            new JobCommandUniqueTarget());
        using var commands = new JobCommandBuffer(world, producerCount: 2);

        commands.Schedule(
            0,
            new DeferredUniqueTargetRetargetProducer(edgeA, sourceA, targetB));
        commands.Schedule(
            1,
            new DeferredUniqueTargetRetargetProducer(edgeB, sourceB, targetA));
        commands.SchedulePlayback().Complete();

        Assert.Equal(targetB, world.GetDirectedRelationEndpoints(edgeA).Target);
        Assert.Equal(targetA, world.GetDirectedRelationEndpoints(edgeB).Target);
        Assert.Equal(
            edgeA,
            Assert.Single(
                world.GetIncomingRelations<JobCommandUniqueTarget>(targetA).Entries.ToArray()).Edge);
        Assert.Equal(
            edgeB,
            Assert.Single(
                world.GetIncomingRelations<JobCommandUniqueTarget>(targetB).Entries.ToArray()).Edge);

        world.MaintainRelations<JobCommandUniqueTarget>();

        Assert.Equal(
            edgeA,
            Assert.Single(
                world.GetIncomingRelations<JobCommandUniqueTarget>(targetB).Entries.ToArray()).Edge);
        Assert.Equal(
            edgeB,
            Assert.Single(
                world.GetIncomingRelations<JobCommandUniqueTarget>(targetA).Entries.ToArray()).Edge);
    }

    [Fact]
    public void RelationshipFaultInLaterSegmentRollsBackWholeMergedBatch()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity sourceA = world.CreateEntity();
        Entity sourceB = world.CreateEntity();
        Entity target = world.CreateEntity();
        var pending = new Commands.DeferredRelationEdge<JobCommandUniqueTarget>[2];
        int entityCountBefore = world.EntityCount;
        using var commands = new JobCommandBuffer(world, producerCount: 2);

        commands.Schedule(
            0,
            new CapturedUniqueTargetCreateProducer(sourceA, target, pending, captureIndex: 0));
        commands.Schedule(
            1,
            new CapturedUniqueTargetCreateProducer(sourceB, target, pending, captureIndex: 1));

        Assert.Throws<InvalidOperationException>(() => commands.SchedulePlayback().Complete());
        Assert.Equal(entityCountBefore, world.EntityCount);
        Assert.All(pending, static edge => Assert.False(edge.TryResolve(out _)));
        Assert.Empty(world.GetOutgoingRelations<JobCommandUniqueTarget>(sourceA).Entries.ToArray());
        Assert.Empty(world.GetOutgoingRelations<JobCommandUniqueTarget>(sourceB).Entries.ToArray());
        Assert.Empty(world.GetIncomingRelations<JobCommandUniqueTarget>(target).Entries.ToArray());
    }

    [Fact]
    public void ProducerFaultInvalidatesEverySegmentWithoutPublishing()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        var deferred = new Commands.DeferredEntity[8];
        using var commands = new JobCommandBuffer(world, deferred.Length);

        commands.ScheduleParallel(
            new FaultingCreateProducer(deferred, faultIndex: 3),
            batchSize: 1);
        JobHandle playback = commands.SchedulePlayback();

        AggregateException error = Assert.Throws<AggregateException>(() => playback.Complete());
        Assert.Contains(
            error.InnerExceptions,
            static exception => exception is ProducerProbeException);
        Assert.Equal(0, world.EntityCount);
        Assert.All(deferred, static entity => Assert.False(entity.TryResolve(out _)));
    }

    [Fact]
    public void PlaybackFaultRollsBackEarlierProducerSegments()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity target = world.CreateEntity();
        var deferred = new Commands.DeferredEntity[1];
        using var commands = new JobCommandBuffer(world, producerCount: 2);

        commands.Schedule(0, new CaptureCreateProducer(deferred));
        commands.Schedule(1, new InvalidRemoveProducer(target));
        JobHandle playback = commands.SchedulePlayback();

        Assert.Throws<InvalidOperationException>(() => playback.Complete());
        Assert.Equal(1, world.EntityCount);
        Assert.True(world.IsAlive(target));
        Assert.False(world.Has<JobCommandValue>(target));
        Assert.False(deferred[0].TryResolve(out _));
    }

    [Fact]
    public void EmptyBatchConsumesNoTopologyRevision()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        using var commands = new JobCommandBuffer(world, producerCount: 1);

        commands.Schedule(0, new EmptyProducer());
        commands.SchedulePlayback().Complete();

        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void ProducerScopeDoesNotAuthorizeWorldSharedCommandBuffer()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        using var commands = new JobCommandBuffer(world, producerCount: 1);

        commands.Schedule(0, new SharedBufferEscapeProducer(world));
        AggregateException error = Assert.Throws<AggregateException>(() =>
            commands.SchedulePlayback().Complete());

        Assert.Contains(
            error.InnerExceptions,
            static exception => exception is InvalidOperationException invalid);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void ProducerCannotSynchronouslyPlaybackItsOwningBatch()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        using var commands = new JobCommandBuffer(world, producerCount: 1);

        commands.Schedule(0, new OwningPlaybackEscapeProducer(commands));
        AggregateException error = Assert.Throws<AggregateException>(() =>
            commands.SchedulePlayback().Complete());

        Assert.Contains(
            error.InnerExceptions,
            static exception => exception is InvalidOperationException invalid);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void ProducerCannotSchedulePlaybackForItsOwningBatch()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        using var commands = new JobCommandBuffer(world, producerCount: 1);

        commands.Schedule(0, new OwningSchedulePlaybackEscapeProducer(commands));
        AggregateException error = Assert.Throws<AggregateException>(() =>
            commands.SchedulePlayback().Complete());

        Assert.Contains(
            error.InnerExceptions,
            static exception => exception is InvalidOperationException invalid);
        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void FaultedProducerDependencyStillRunsCleanupAndReleasesPlaybackOwnership()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        var commands = new JobCommandBuffer(world, producerCount: 1);
        JobHandle dependency = Jobs.Schedule(new ThrowingDependencyJob());

        commands.Schedule(0, new EmptyProducer(), dependency);
        JobHandle playback = commands.SchedulePlayback();

        var error = Assert.Throws<ProducerDependencyException>(() => playback.Complete());

        Assert.Equal(0, world.EntityCount);

        // The mandatory finalizer must leave the owner in a terminal state rather than pinning it
        // in PlaybackOwned after the scheduler cancels publication.
        commands.Dispose();
        commands.Dispose();
    }

    [Fact]
    public void FaultedTopologyPredecessorStillRunsCleanup()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        for (int iteration = 0; iteration < 32; iteration++)
        {
            using var release = new ManualResetEventSlim();
            using var started = new ManualResetEventSlim();
            using var world = CreateJobWorld();
            var commands = new JobCommandBuffer(world, producerCount: 1);
            JobHandle predecessor = Jobs.Schedule(
                new BlockingTopologyFaultJob(release, started),
                RelationshipJobAccess.TopologyWrite(world));

            JobHandle playback = default;
            JobHandle producer = default;
            bool startedInTime = false;
            Exception? schedulingFault = null;
            try
            {
                startedInTime = started.Wait(TimeSpan.FromSeconds(10));
                if (startedInTime)
                {
                    try
                    {
                        // The fault is a semantic prerequisite as well as a resource owner.
                        // Resource hazards alone only order work and never propagate failure.
                        producer = commands.Schedule(0, new EmptyProducer(), predecessor);
                        playback = commands.SchedulePlayback();
                    }
                    catch (Exception exception)
                    {
                        schedulingFault = exception;
                    }
                }
            }
            finally
            {
                release.Set();
            }

            Exception? playbackFault = startedInTime && schedulingFault is null
                ? Record.Exception(() => playback.Complete())
                : null;
            Exception? producerFault = startedInTime && schedulingFault is null
                ? Record.Exception(() => producer.Complete())
                : null;
            Exception? predecessorFault = Record.Exception(() => predecessor.Complete());
            commands.Dispose();

            Assert.True(startedInTime, "Topology predecessor did not start within ten seconds.");
            Assert.Null(schedulingFault);
            Assert.IsType<TopologyPredecessorException>(playbackFault);
            Assert.IsType<TopologyPredecessorException>(producerFault);
            Assert.True(predecessor.IsCompleted);
            Assert.IsType<TopologyPredecessorException>(predecessorFault);
            Assert.Equal(0, world.EntityCount);
        }
    }

    [Fact]
    public void FinalizerScheduleFailureRecoversAlreadyScheduledPublication()
    {
        ConfigureJobs(new JobRuntimeConfig
        {
            WorkerCount = 0,
            MaxCompletionStates = 1,
            SafetyMode = JobSafetyMode.Checked,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        var world = CreateJobWorld();
        var deferred = new Commands.DeferredEntity[1];
        var commands = new JobCommandBuffer(world, producerCount: 1);
        JobHandle producer = commands.Schedule(0, new CaptureCreateProducer(deferred));
        producer.Complete();

        var error = Assert.Throws<InvalidOperationException>(() => commands.SchedulePlayback());

        Assert.Equal(1, world.EntityCount);
        Assert.True(deferred[0].TryResolve(out Entity created));
        Assert.True(world.IsAlive(created));
        commands.Dispose();
    }

    [Fact]
    public async Task ScheduleAndPlaybackRaceIsLinearizable()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });

        for (int iteration = 0; iteration < 32; iteration++)
        {
            using var start = new Barrier(participantCount: 2);
            using var producerRelease = new ManualResetEventSlim();
            var world = CreateJobWorld();
            var commands = new JobCommandBuffer(world, producerCount: 1);
            JobHandle producer = default;
            JobHandle playback = default;
            Exception? producerScheduleFault = null;
            Exception? playbackScheduleFault = null;

            Task producerTask = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    producer = commands.Schedule(0, new BlockingProducer(producerRelease));
                }
                catch (Exception exception)
                {
                    producerScheduleFault = exception;
                }
            });
            Task playbackTask = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    playback = commands.SchedulePlayback();
                }
                catch (Exception exception)
                {
                    playbackScheduleFault = exception;
                }
            });

            try
            {
                await Task.WhenAll(producerTask, playbackTask);
            }
            finally
            {
                producerRelease.Set();
            }

            Assert.Null(producerScheduleFault);
            if (playbackScheduleFault is null)
            {
                playback.Complete();
            }
            else
            {
                Assert.IsType<InvalidOperationException>(playbackScheduleFault);
                producer.Complete();
                commands.SchedulePlayback().Complete();
            }

            commands.Dispose();
        }
    }

    [Fact]
    public async Task ScheduleAndDisposeRaceHasExactlyOneOwner()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });

        for (int iteration = 0; iteration < 32; iteration++)
        {
            using var start = new Barrier(participantCount: 2);
            using var producerRelease = new ManualResetEventSlim();
            var world = CreateJobWorld();
            var commands = new JobCommandBuffer(world, producerCount: 1);
            JobHandle producer = default;
            Exception? producerScheduleFault = null;
            Exception? disposeFault = null;

            Task producerTask = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    producer = commands.Schedule(0, new BlockingProducer(producerRelease));
                }
                catch (Exception exception)
                {
                    producerScheduleFault = exception;
                }
            });
            Task disposeTask = Task.Run(() =>
            {
                start.SignalAndWait();
                try
                {
                    commands.Dispose();
                }
                catch (Exception exception)
                {
                    disposeFault = exception;
                }
            });

            try
            {
                await Task.WhenAll(producerTask, disposeTask);
            }
            finally
            {
                producerRelease.Set();
            }

            Assert.NotEqual(producerScheduleFault is null, disposeFault is null);
            if (producerScheduleFault is null)
            {
                Assert.IsType<InvalidOperationException>(disposeFault);
                producer.Complete();
                commands.Dispose();
            }
            else
            {
                Assert.IsType<InvalidOperationException>(producerScheduleFault);
                Assert.Null(disposeFault);
            }
        }
    }

    [Fact]
    public void ZeroProducerBatchIsAValidTopologyFreeNoOp()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        using var commands = new JobCommandBuffer(world, producerCount: 0);

        commands.SchedulePlayback().Complete();

        Assert.Equal(0, world.EntityCount);
    }

    [Fact]
    public void ZeroProducerParallelSchedulePreservesItsExplicitDependency()
    {
        ConfigureJobs(new JobRuntimeConfig { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        var visits = new int[1];
        var commands = new JobCommandBuffer(world, producerCount: 0);
        JobHandle dependency = Jobs.Schedule(new ThrowingDependencyJob());

        commands.ScheduleParallel(
            new EmptyParallelProducer(visits),
            batchSize: 1,
            dependency);
        JobHandle playback = commands.SchedulePlayback();

        Assert.Throws<ProducerDependencyException>(() => playback.Complete());
        Assert.Equal(0, visits[0]);
        commands.Dispose();
    }

    private readonly struct CreateByIndexProducer : IJobParallelCommandProducer
    {
        public void Execute(int producerIndex, ref JobCommandWriter commands)
        {
            Commands.DeferredEntity entity = commands.CreateEntity();
            commands.Add(entity, new JobCommandValue { Value = producerIndex });
        }
    }

    private readonly struct SameSourceRelationProducer : IJobParallelCommandProducer
    {
        private readonly Entity _source;
        private readonly Entity[] _targets;
        private readonly Commands.DeferredRelationEdge<JobCommandLink>[] _capture;

        internal SameSourceRelationProducer(
            Entity source,
            Entity[] targets,
            Commands.DeferredRelationEdge<JobCommandLink>[] capture)
        {
            _source = source;
            _targets = targets;
            _capture = capture;
        }

        public void Execute(int producerIndex, ref JobCommandWriter commands)
        {
            var relations = commands.Relations<JobCommandLink>();
            _capture[producerIndex] = relations.Create(
                _source,
                _targets[producerIndex],
                new JobCommandLink { Value = producerIndex },
                RelationMaintenanceTiming.Immediate);
        }
    }

    private readonly struct DeferredUniqueTargetRetargetProducer : IJobCommandProducer
    {
        private readonly RelationEdge<JobCommandUniqueTarget> _edge;
        private readonly Entity _source;
        private readonly Entity _target;

        internal DeferredUniqueTargetRetargetProducer(
            RelationEdge<JobCommandUniqueTarget> edge,
            Entity source,
            Entity target)
        {
            _edge = edge;
            _source = source;
            _target = target;
        }

        public void Execute(ref JobCommandWriter commands) =>
            commands.Relations<JobCommandUniqueTarget>().Retarget(
                _edge,
                _source,
                _target,
                RelationMaintenanceTiming.Deferred);
    }

    private readonly struct CapturedUniqueTargetCreateProducer : IJobCommandProducer
    {
        private readonly Entity _source;
        private readonly Entity _target;
        private readonly Commands.DeferredRelationEdge<JobCommandUniqueTarget>[] _capture;
        private readonly int _captureIndex;

        internal CapturedUniqueTargetCreateProducer(
            Entity source,
            Entity target,
            Commands.DeferredRelationEdge<JobCommandUniqueTarget>[] capture,
            int captureIndex)
        {
            _source = source;
            _target = target;
            _capture = capture;
            _captureIndex = captureIndex;
        }

        public void Execute(ref JobCommandWriter commands)
        {
            _capture[_captureIndex] = commands.Relations<JobCommandUniqueTarget>().Create(
                _source,
                _target,
                new JobCommandUniqueTarget(),
                RelationMaintenanceTiming.Immediate);
        }
    }

    private readonly struct FaultingCreateProducer : IJobParallelCommandProducer
    {
        private readonly Commands.DeferredEntity[] _entities;
        private readonly int _faultIndex;

        internal FaultingCreateProducer(Commands.DeferredEntity[] entities, int faultIndex)
        {
            _entities = entities;
            _faultIndex = faultIndex;
        }

        public void Execute(int producerIndex, ref JobCommandWriter commands)
        {
            Commands.DeferredEntity entity = commands.CreateEntity();
            _entities[producerIndex] = entity;
            commands.Add(entity, new JobCommandValue { Value = producerIndex });
            if (producerIndex == _faultIndex)
                throw new ProducerProbeException();
        }
    }

    private readonly struct CaptureCreateProducer : IJobCommandProducer
    {
        private readonly Commands.DeferredEntity[] _capture;

        internal CaptureCreateProducer(Commands.DeferredEntity[] capture)
        {
            _capture = capture;
        }

        public void Execute(ref JobCommandWriter commands)
        {
            Commands.DeferredEntity entity = commands.CreateEntity();
            _capture[0] = entity;
            commands.Add(entity, new JobCommandValue { Value = 7 });
        }
    }

    private readonly struct InvalidRemoveProducer : IJobCommandProducer
    {
        private readonly Entity _target;

        internal InvalidRemoveProducer(Entity target)
        {
            _target = target;
        }

        public void Execute(ref JobCommandWriter commands) =>
            commands.Remove<JobCommandValue>(_target);
    }

    private readonly struct EmptyProducer : IJobCommandProducer
    {
        public void Execute(ref JobCommandWriter commands)
        {
            _ = commands;
        }
    }

    private readonly struct SharedBufferEscapeProducer : IJobCommandProducer
    {
        private readonly World _world;

        internal SharedBufferEscapeProducer(World world)
        {
            _world = world;
        }

        public void Execute(ref JobCommandWriter commands)
        {
            _ = commands;
            _world.Commands().CreateEntity();
        }
    }

    private readonly struct EmptyParallelProducer : IJobParallelCommandProducer
    {
        private readonly int[] _visits;

        internal EmptyParallelProducer(int[] visits)
        {
            _visits = visits;
        }

        public void Execute(int producerIndex, ref JobCommandWriter commands)
        {
            _ = producerIndex;
            _ = commands;
            Interlocked.Increment(ref _visits[0]);
        }
    }

    private readonly struct OwningPlaybackEscapeProducer : IJobCommandProducer
    {
        private readonly JobCommandBuffer _owner;

        internal OwningPlaybackEscapeProducer(JobCommandBuffer owner)
        {
            _owner = owner;
        }

        public void Execute(ref JobCommandWriter commands)
        {
            _ = commands;
            _owner.Playback();
        }
    }

    private readonly struct OwningSchedulePlaybackEscapeProducer : IJobCommandProducer
    {
        private readonly JobCommandBuffer _owner;

        internal OwningSchedulePlaybackEscapeProducer(JobCommandBuffer owner)
        {
            _owner = owner;
        }

        public void Execute(ref JobCommandWriter commands)
        {
            _ = commands;
            _owner.SchedulePlayback();
        }
    }

    private readonly struct ThrowingDependencyJob : IJob
    {
        public void Execute()
        {
            throw new ProducerDependencyException("producer dependency failed");
        }
    }

    private readonly struct BlockingTopologyFaultJob : IJob
    {
        private readonly ManualResetEventSlim _release;
        private readonly ManualResetEventSlim _started;

        internal BlockingTopologyFaultJob(
            ManualResetEventSlim release,
            ManualResetEventSlim started)
        {
            _release = release;
            _started = started;
        }

        public void Execute()
        {
            _started.Set();
            _release.Wait();
            throw new TopologyPredecessorException();
        }
    }

    private readonly struct BlockingProducer : IJobCommandProducer
    {
        private readonly ManualResetEventSlim _release;

        internal BlockingProducer(ManualResetEventSlim release)
        {
            _release = release;
        }

        public void Execute(ref JobCommandWriter commands)
        {
            _ = commands;
            _release.Wait();
        }
    }

    private struct JobCommandValue : IComponent
    {
        internal int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct JobCommandLink : IComponent
    {
        internal int Value;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private struct JobCommandUniqueTarget : IComponent;

    private sealed class ProducerProbeException : Exception;

    private sealed class ProducerDependencyException(string message) : Exception(message);

    private sealed class TopologyPredecessorException : Exception;
}
