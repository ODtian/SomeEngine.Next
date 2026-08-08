using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class RelationshipJobAccessTests
{
    [Fact]
    public void TopologyWrite_IsSharedAcrossHierarchyAndRelationsPerWorld()
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 4,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });

        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle blocker = default;
        try
        {
            var world = new World();
            var otherWorld = new World();
            blocker = HierarchyJobAccess<Domain>.ScheduleParentWrite(
                world,
                new BlockingJob(started, release));
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

            JobHandle sameWorldRelation = RelationMaintenanceSystem<Payload>.Schedule(world);
            JobHandle otherWorldRelation = RelationMaintenanceSystem<Payload>.Schedule(otherWorld);

            otherWorldRelation.Complete();
            Assert.False(sameWorldRelation.IsCompleted);

            release.Set();
            sameWorldRelation.Complete();
            blocker.Complete();
        }
        finally
        {
            release.Set();
            blocker.Complete();
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    [Fact]
    public void CanonicalParentRead_RequiresTopologyReadAndWaitsForAnyWorldTopologyWriter()
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobRuntimeConfig
        {
            WorkerCount = 4,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });

        using var started = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        JobHandle blocker = default;
        try
        {
            var world = new World();
            Entity child = world.CreateEntity();
            var capture = new ParentCapture();

            JobHandle missingTopology = JobSystem.Schedule(
                new CaptureParentJob(world, child, capture),
                HierarchyJobAccess<Domain>.ParentRead(world));
            Assert.Throws<JobResourceSafetyException>(() => missingTopology.Complete());

            blocker = RelationJobAccess<Payload>.ScheduleEndpointsWrite(
                world,
                new BlockingJob(started, release));
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

            JobHandle reader = HierarchyJobAccess<Domain>.ScheduleParentRead(
                world,
                new CaptureParentJob(world, child, capture));
            Assert.False(reader.IsCompleted);

            release.Set();
            reader.Complete();
            blocker.Complete();
            Assert.Equal(Entity.Null, capture.Parent);
        }
        finally
        {
            release.Set();
            blocker.Complete();
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

    private sealed class ParentCapture
    {
        internal Entity Parent;
    }

    private readonly struct CaptureParentJob : IJob
    {
        private readonly World _world;
        private readonly Entity _child;
        private readonly ParentCapture _capture;

        internal CaptureParentJob(World world, Entity child, ParentCapture capture)
        {
            _world = world;
            _child = child;
            _capture = capture;
        }

        public void Execute()
        {
            _capture.Parent = HierarchyJobAccess<Domain>.GetParent(_world, _child);
        }
    }

    private readonly struct Domain : IHierarchyDomain;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct Payload : IComponent;
}
