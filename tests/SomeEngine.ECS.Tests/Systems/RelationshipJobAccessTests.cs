using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class RelationshipJobAccessTests : JobTest
{
    [Fact]
    public void TopologyWrite_IsSharedAcrossHierarchyAndRelationsPerWorld()
    {
        ManagedPayloadPolicy previousPolicy = Jobs.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = Jobs.SafetyMode;
        ConfigureJobs(new JobConfiguration
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
            var world = CreateJobWorld();
            var otherWorld = CreateJobWorld();
            blocker = world.Execution!.Scheduler!.Schedule(new BlockingJob(started, release), world.Execution!.StructureWrite);
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

            JobHandle sameWorldRelation = Jobs.Schedule(new MaintainRelations(world), world.Execution!.StructureWrite);
            JobHandle otherWorldRelation = Jobs.Schedule(new MaintainRelations(otherWorld), otherWorld.Execution!.StructureWrite);

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
            ConfigureJobs(new JobConfiguration
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    [Fact]
    public void CanonicalParentRead_RequiresTopologyReadAndWaitsForAnyWorldTopologyWriter()
    {
        ManagedPayloadPolicy previousPolicy = Jobs.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = Jobs.SafetyMode;
        ConfigureJobs(new JobConfiguration
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
            var world = CreateJobWorld();
            Entity child = world.Create();
            var capture = new ParentCapture();

            World other = CreateJobWorld();
            JobHandle missingTopology = Jobs.Schedule(new CaptureParentJob(world, child, capture), other.Execution!.StructureRead);
            Assert.Throws<JobResourceSafetyException>(() => missingTopology.Complete());

            blocker = world.Execution!.Scheduler!.Schedule(new BlockingJob(started, release), world.Execution!.StructureWrite);
            Assert.True(started.Wait(TimeSpan.FromSeconds(2)));

            JobHandle reader = world.Execution!.Scheduler!.Schedule(new CaptureParentJob(world, child, capture), world.Execution!.StructureRead);
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
            ConfigureJobs(new JobConfiguration
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    private readonly struct MaintainRelations(World world) : IJob
    {
        public void Execute() => Relations<Payload>.Maintain(world);
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
            _capture.Parent = Hierarchy<Domain>.GetParent(_world, _child);
        }
    }

    private readonly struct Domain : IHierarchyDomain;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct Payload : IComponent;
}
