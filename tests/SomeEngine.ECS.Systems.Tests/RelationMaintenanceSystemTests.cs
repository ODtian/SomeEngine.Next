using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class RelationMaintenanceSystemTests
{
    [Fact]
    public void RelaxedReaderSeesLastApplied_AndFreshReaderDependsOnMaintenanceHandle()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity source = world.CreateEntity();
            Entity oldTarget = world.CreateEntity();
            Entity newTarget = world.CreateEntity();
            RelationEdge<DirectedA> edge = world.CreateRelation(source, oldTarget, new DirectedA());

            using var endpointsWritten = new ManualResetEventSlim();
            using var releaseWriter = new ManualResetEventSlim();
            JobHandle writerHandle = default;
            try
            {
                var writer = new DeferredEndpointWriter<DirectedA>(
                    world,
                    edge,
                    source,
                    newTarget,
                    endpointsWritten,
                    releaseWriter);
                writerHandle = RelationJobAccess<DirectedA>.ScheduleEndpointsWrite(
                    world,
                    writer);

                Assert.True(endpointsWritten.Wait(TimeSpan.FromSeconds(2)));

                var relaxedCapture = new EdgeCapture<DirectedA>();
                JobHandle relaxed = JobSystem.Schedule(
                    new CaptureIncomingJob<DirectedA>(world, oldTarget, relaxedCapture));
                relaxed.Complete();

                Assert.Equal([edge], relaxedCapture.Edges);
                // Canonical endpoint reads participate in TopologyRead and therefore cannot race
                // the still-running endpoint writer. The relaxed immutable inverse remains safe
                // and intentionally observable while that writer owns canonical topology.
                Assert.False(writerHandle.IsCompleted);
                Assert.Equal(
                    [edge],
                    world.GetIncomingRelations<DirectedA>(oldTarget)
                        .Entries.ToArray().Select(static entry => entry.Edge));
                Assert.Empty(world.GetIncomingRelations<DirectedA>(newTarget).Entries.ToArray());

                JobHandle maintenance = RelationMaintenanceSystem<DirectedA>.Schedule(world);
                var freshCapture = new EdgeCapture<DirectedA>();
                JobHandle fresh = JobSystem.Schedule(
                    new CaptureIncomingJob<DirectedA>(world, newTarget, freshCapture),
                    maintenance);

                Assert.False(maintenance.IsCompleted);
                Assert.False(fresh.IsCompleted);

                releaseWriter.Set();
                fresh.Complete();
                writerHandle.Complete();
                maintenance.Complete();

                Assert.Equal(newTarget, world.GetDirectedRelationEndpoints(edge).Target);
                Assert.Equal([edge], freshCapture.Edges);
                Assert.Empty(world.GetIncomingRelations<DirectedA>(oldTarget).Entries.ToArray());
                Assert.Equal(
                    [edge],
                    world.GetIncomingRelations<DirectedA>(newTarget)
                        .Entries.ToArray().Select(static entry => entry.Edge));
            }
            finally
            {
                releaseWriter.Set();
                writerHandle.Complete();
            }
        });
    }

    [Fact]
    public void PinnedAdjacencyReader_DoesNotBlockNextMaintenancePublication()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            using var readerStarted = new ManualResetEventSlim();
            using var releaseReader = new ManualResetEventSlim();
            JobHandle reader = default;
            try
            {
                reader = JobSystem.Schedule(
                    new BlockingJob(readerStarted, releaseReader));
                Assert.True(readerStarted.Wait(TimeSpan.FromSeconds(2)));

                JobHandle maintenance = RelationMaintenanceSystem<DirectedA>.Schedule(world);
                maintenance.Complete();
                Assert.True(maintenance.IsCompleted);
                releaseReader.Set();
                maintenance.Complete();
                reader.Complete();
            }
            finally
            {
                releaseReader.Set();
                reader.Complete();
            }
        });
    }

    [Fact]
    public void TopologyWrites_SerializeAcrossPayloadsButNotWorlds()
    {
        WithJobRuntime(() =>
        {
            var worldA = new World();
            var worldB = new World();
            using var blockerStarted = new ManualResetEventSlim();
            using var releaseBlocker = new ManualResetEventSlim();
            JobHandle blocker = default;
            try
            {
                blocker = RelationJobAccess<DirectedA>.ScheduleEndpointsWrite(
                    worldA,
                    new BlockingJob(blockerStarted, releaseBlocker));
                Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(2)));

                JobHandle otherPayload = RelationMaintenanceSystem<DirectedB>.Schedule(worldA);
                JobHandle otherWorld = RelationMaintenanceSystem<DirectedA>.Schedule(worldB);

                otherWorld.Complete();
                Assert.False(otherPayload.IsCompleted);
                Assert.False(blocker.IsCompleted);

                releaseBlocker.Set();
                otherPayload.Complete();
                blocker.Complete();
            }
            finally
            {
                releaseBlocker.Set();
                blocker.Complete();
            }
        });
    }

    [Fact]
    public void OwnerBoundEndpointMutation_RejectsMissingOrReadOnlyCapability()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity source = world.CreateEntity();
            Entity oldTarget = world.CreateEntity();
            Entity newTarget = world.CreateEntity();
            RelationEdge<DirectedA> edge = world.CreateRelation(source, oldTarget, new DirectedA());
            var mutation = new RetargetJob<DirectedA>(world, edge, source, newTarget);

            JobHandle undeclared = JobSystem.Schedule(mutation);
            Assert.Throws<JobResourceSafetyException>(() => undeclared.Complete());
            Assert.Equal(oldTarget, world.GetDirectedRelationEndpoints(edge).Target);

            JobHandle missingWorldGate = JobSystem.Schedule(
                mutation,
                RelationJobAccess<DirectedA>.EndpointsWrite(world));
            Assert.Throws<JobResourceSafetyException>(() => missingWorldGate.Complete());
            Assert.Equal(oldTarget, world.GetDirectedRelationEndpoints(edge).Target);

            JobResourceAccess[] readOnly =
            [
                RelationJobAccess<DirectedA>.EndpointsRead(world),
                RelationshipJobAccess.TopologyWrite(world),
            ];
            JobHandle insufficient = JobSystem.Schedule(mutation, readOnly);
            Assert.Throws<JobResourceSafetyException>(() => insufficient.Complete());
            Assert.Equal(oldTarget, world.GetDirectedRelationEndpoints(edge).Target);

            RelationJobAccess<DirectedA>.ScheduleEndpointsWrite(world, mutation).Complete();
            Assert.Equal(newTarget, world.GetDirectedRelationEndpoints(edge).Target);
        });
    }

    [Fact]
    public void OwnerBoundEndpointMutation_RejectsMultiBatchParallelOwner()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity source = world.CreateEntity();
            Entity oldTarget = world.CreateEntity();
            Entity newTarget = world.CreateEntity();
            RelationEdge<DirectedA> edge = world.CreateRelation(source, oldTarget, new DirectedA());
            JobResourceAccess[] accesses =
            [
                RelationJobAccess<DirectedA>.EndpointsWrite(world),
                RelationshipJobAccess.TopologyWrite(world),
            ];

            JobHandle parallel = JobSystem.ScheduleParallel(
                new ParallelRetargetJob<DirectedA>(world, edge, source, newTarget),
                length: 2,
                batchSize: 1,
                accesses);

            Assert.Throws<JobResourceSafetyException>(() => parallel.Complete());
            Assert.Equal(oldTarget, world.GetDirectedRelationEndpoints(edge).Target);
        });
    }

    [Fact]
    public void ImmutableAdjacencySnapshot_IsSafeWithoutALifetimeResourceOwner()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity endpoint = world.CreateEntity();
            var capture = new EdgeCapture<DirectedA>();

            JobHandle reader = JobSystem.Schedule(
                new CaptureIncomingJob<DirectedA>(world, endpoint, capture));

            reader.Complete();
            Assert.Empty(capture.Edges);
        });
    }

    [Fact]
    public void ContainerKeys_RebindAfterJobRuntimeGenerationChanges()
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        try
        {
            var world = new World();
            InitializeJobRuntime(previousSafety);
            RelationMaintenanceSystem<DirectedA>.Schedule(world).Complete();

            InitializeJobRuntime(previousSafety);
            RelationMaintenanceSystem<DirectedA>.Schedule(world).Complete();
        }
        finally
        {
            RestoreJobRuntime(previousPolicy, previousSafety);
        }
    }

    [Fact]
    public void RejectManagedPayloadPolicy_IsHonoredAndNotMutatedBySchedule()
    {
        ManagedPayloadPolicy previous = JobSystem.ManagedPayloadPolicy;
        try
        {
            JobSystem.ManagedPayloadPolicy = ManagedPayloadPolicy.Reject;
            var world = new World();

            var error = Assert.Throws<InvalidOperationException>(
                () => RelationMaintenanceSystem<DirectedA>.Schedule(world));

            Assert.Contains("managed payload policy", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(ManagedPayloadPolicy.Reject, JobSystem.ManagedPayloadPolicy);
        }
        finally
        {
            JobSystem.ManagedPayloadPolicy = previous;
        }
    }

    private static void WithJobRuntime(Action action)
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        InitializeJobRuntime(previousSafety);
        try
        {
            action();
        }
        finally
        {
            RestoreJobRuntime(previousPolicy, previousSafety);
        }
    }

    private static void InitializeJobRuntime(JobSafetyMode safetyMode)
    {
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 4,
            SafetyMode = safetyMode,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
    }

    private static void RestoreJobRuntime(
        ManagedPayloadPolicy payloadPolicy,
        JobSafetyMode safetyMode)
    {
        JobSystem.Initialize(new JobRuntimeConfig
        {
            SafetyMode = safetyMode,
            ManagedPayloadPolicy = payloadPolicy,
        });
    }

    private sealed class EdgeCapture<T>
        where T : struct, IComponent
    {
        public RelationEdge<T>[] Edges { get; set; } = [];
    }

    private readonly struct DeferredEndpointWriter<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly RelationEdge<T> _edge;
        private readonly Entity _source;
        private readonly Entity _target;
        private readonly ManualResetEventSlim _written;
        private readonly ManualResetEventSlim _release;

        public DeferredEndpointWriter(
            World world,
            RelationEdge<T> edge,
            Entity source,
            Entity target,
            ManualResetEventSlim written,
            ManualResetEventSlim release)
        {
            _world = world;
            _edge = edge;
            _source = source;
            _target = target;
            _written = written;
            _release = release;
        }

        public void Execute()
        {
            RelationJobAccess<T>.RetargetDeferred(_world, _edge, _source, _target);
            _written.Set();
            _release.Wait();
        }
    }

    private readonly struct CaptureIncomingJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly Entity _target;
        private readonly EdgeCapture<T> _capture;

        public CaptureIncomingJob(World world, Entity target, EdgeCapture<T> capture)
        {
            _world = world;
            _target = target;
            _capture = capture;
        }

        public void Execute()
        {
            _capture.Edges = RelationJobAccess<T>.GetIncoming(_world, _target)
                .Entries.ToArray().Select(static entry => entry.Edge).ToArray();
        }
    }

    private readonly struct RetargetJob<T> : IJob
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly RelationEdge<T> _edge;
        private readonly Entity _source;
        private readonly Entity _target;

        internal RetargetJob(World world, RelationEdge<T> edge, Entity source, Entity target)
        {
            _world = world;
            _edge = edge;
            _source = source;
            _target = target;
        }

        public void Execute()
        {
            RelationJobAccess<T>.RetargetDeferred(_world, _edge, _source, _target);
        }
    }

    private readonly struct ParallelRetargetJob<T> : IJobParallelFor
        where T : struct, IComponent
    {
        private readonly World _world;
        private readonly RelationEdge<T> _edge;
        private readonly Entity _source;
        private readonly Entity _target;

        internal ParallelRetargetJob(
            World world,
            RelationEdge<T> edge,
            Entity source,
            Entity target)
        {
            _world = world;
            _edge = edge;
            _source = source;
            _target = target;
        }

        public void Execute(int index)
        {
            _ = index;
            RelationJobAccess<T>.RetargetDeferred(_world, _edge, _source, _target);
        }
    }

    private readonly struct BlockingJob : IJob
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        public BlockingJob(ManualResetEventSlim started, ManualResetEventSlim release)
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

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct DirectedA : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct DirectedB : IComponent;
}
