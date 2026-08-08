using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class HierarchyMaintenanceSystemTests
{
    [Fact]
    public void RelaxedReaderSeesLastApplied_AndFreshReaderDependsOnMaintenanceHandle()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity newParent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<DomainA>.SetParent(world, child, oldParent);

            using var parentWritten = new ManualResetEventSlim();
            using var releaseWriter = new ManualResetEventSlim();
            var writer = new DeferredParentWriter<DomainA>(
                world,
                child,
                newParent,
                parentWritten,
                releaseWriter);
            JobHandle writerHandle = HierarchyJobAccess<DomainA>.ScheduleParentWrite(
                world,
                writer);

            Assert.True(parentWritten.Wait(TimeSpan.FromSeconds(2)));

            var relaxedCapture = new ChildrenCapture();
            JobHandle relaxed = JobSystem.Schedule(
                new CaptureChildrenJob<DomainA>(world, oldParent, relaxedCapture));
            relaxed.Complete();

            Assert.Equal([child], relaxedCapture.Children);
            Assert.Equal([child], Hierarchy<DomainA>.GetChildren(world, oldParent).ToArray());
            Assert.Empty(Hierarchy<DomainA>.GetChildren(world, newParent));

            JobHandle maintenance = HierarchyMaintenanceSystem<DomainA>.Schedule(world);
            var freshCapture = new ChildrenCapture();
            JobHandle fresh = JobSystem.Schedule(
                new CaptureChildrenJob<DomainA>(world, newParent, freshCapture),
                maintenance);

            Assert.False(maintenance.IsCompleted);
            Assert.False(fresh.IsCompleted);

            releaseWriter.Set();
            fresh.Complete();
            writerHandle.Complete();
            maintenance.Complete();

            Assert.Equal([child], freshCapture.Children);
            Assert.Empty(Hierarchy<DomainA>.GetChildren(world, oldParent));
            Assert.Equal([child], Hierarchy<DomainA>.GetChildren(world, newParent).ToArray());
        });
    }

    [Fact]
    public void TopologyWrites_SerializeAcrossDomainsButNotWorlds()
    {
        WithJobRuntime(() =>
        {
            var worldA = new World();
            var worldB = new World();
            using var blockerStarted = new ManualResetEventSlim();
            using var releaseBlocker = new ManualResetEventSlim();

            JobHandle blocker = HierarchyJobAccess<DomainA>.ScheduleParentWrite(
                worldA,
                new BlockingJob(blockerStarted, releaseBlocker));
            Assert.True(blockerStarted.Wait(TimeSpan.FromSeconds(2)));

            JobHandle otherDomain = HierarchyMaintenanceSystem<DomainB>.Schedule(worldA);
            JobHandle otherWorld = HierarchyMaintenanceSystem<DomainA>.Schedule(worldB);

            otherWorld.Complete();
            Assert.False(otherDomain.IsCompleted);
            Assert.False(blocker.IsCompleted);

            releaseBlocker.Set();
            otherDomain.Complete();
            blocker.Complete();
        });
    }

    [Fact]
    public void OwnerBoundParentMutation_RejectsMissingOrReadOnlyCapability()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            Entity child = world.CreateEntity();
            var mutation = new SetParentJob<DomainA>(world, child, parent);

            JobHandle undeclared = JobSystem.Schedule(mutation);
            Assert.Throws<JobResourceSafetyException>(() => undeclared.Complete());
            Assert.Equal(Entity.Null, Hierarchy<DomainA>.GetParent(world, child));

            JobHandle missingWorldGate = JobSystem.Schedule(
                mutation,
                HierarchyJobAccess<DomainA>.ParentWrite(world));
            Assert.Throws<JobResourceSafetyException>(() => missingWorldGate.Complete());
            Assert.Equal(Entity.Null, Hierarchy<DomainA>.GetParent(world, child));

            JobResourceAccess[] readOnly =
            [
                HierarchyJobAccess<DomainA>.ParentRead(world),
                RelationshipJobAccess.TopologyWrite(world),
            ];
            JobHandle insufficient = JobSystem.Schedule(mutation, readOnly);
            Assert.Throws<JobResourceSafetyException>(() => insufficient.Complete());
            Assert.Equal(Entity.Null, Hierarchy<DomainA>.GetParent(world, child));

            HierarchyJobAccess<DomainA>.ScheduleParentWrite(world, mutation).Complete();
            Assert.Equal(parent, Hierarchy<DomainA>.GetParent(world, child));
        });
    }

    [Fact]
    public void OwnerBoundParentMutation_RejectsMultiBatchParallelOwner()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            Entity child = world.CreateEntity();
            JobResourceAccess[] accesses =
            [
                HierarchyJobAccess<DomainA>.ParentWrite(world),
                RelationshipJobAccess.TopologyWrite(world),
            ];

            JobHandle parallel = JobSystem.ScheduleParallel(
                new ParallelSetParentJob<DomainA>(world, child, parent),
                length: 2,
                batchSize: 1,
                accesses);

            Assert.Throws<JobResourceSafetyException>(() => parallel.Complete());
            Assert.Equal(Entity.Null, Hierarchy<DomainA>.GetParent(world, child));
        });
    }

    [Fact]
    public void ImmutableChildrenSnapshot_IsSafeWithoutALifetimeResourceOwner()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            var capture = new ChildrenCapture();

            JobHandle reader = JobSystem.Schedule(
                new CaptureChildrenJob<DomainA>(world, parent, capture));

            reader.Complete();
            Assert.Empty(capture.Children);
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
            HierarchyMaintenanceSystem<DomainA>.Schedule(world).Complete();

            InitializeJobRuntime(previousSafety);
            HierarchyMaintenanceSystem<DomainA>.Schedule(world).Complete();
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
                () => HierarchyMaintenanceSystem<DomainA>.Schedule(world));

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

    private sealed class ChildrenCapture
    {
        public Entity[] Children { get; set; } = [];
    }

    private readonly struct DeferredParentWriter<TDomain> : IJob
        where TDomain : IHierarchyDomain
    {
        private readonly World _world;
        private readonly Entity _child;
        private readonly Entity _parent;
        private readonly ManualResetEventSlim _written;
        private readonly ManualResetEventSlim _release;

        public DeferredParentWriter(
            World world,
            Entity child,
            Entity parent,
            ManualResetEventSlim written,
            ManualResetEventSlim release)
        {
            _world = world;
            _child = child;
            _parent = parent;
            _written = written;
            _release = release;
        }

        public void Execute()
        {
            HierarchyJobAccess<TDomain>.SetParentDeferred(_world, _child, _parent);
            _written.Set();
            _release.Wait();
        }
    }

    private readonly struct CaptureChildrenJob<TDomain> : IJob
        where TDomain : IHierarchyDomain
    {
        private readonly World _world;
        private readonly Entity _parent;
        private readonly ChildrenCapture _capture;

        public CaptureChildrenJob(World world, Entity parent, ChildrenCapture capture)
        {
            _world = world;
            _parent = parent;
            _capture = capture;
        }

        public void Execute()
        {
            _capture.Children = HierarchyJobAccess<TDomain>.GetChildren(_world, _parent).ToArray();
        }
    }

    private readonly struct SetParentJob<TDomain> : IJob
        where TDomain : IHierarchyDomain
    {
        private readonly World _world;
        private readonly Entity _child;
        private readonly Entity _parent;

        internal SetParentJob(World world, Entity child, Entity parent)
        {
            _world = world;
            _child = child;
            _parent = parent;
        }

        public void Execute()
        {
            HierarchyJobAccess<TDomain>.SetParentDeferred(_world, _child, _parent);
        }
    }

    private readonly struct ParallelSetParentJob<TDomain> : IJobParallelFor
        where TDomain : IHierarchyDomain
    {
        private readonly World _world;
        private readonly Entity _child;
        private readonly Entity _parent;

        internal ParallelSetParentJob(World world, Entity child, Entity parent)
        {
            _world = world;
            _child = child;
            _parent = parent;
        }

        public void Execute(int index)
        {
            _ = index;
            HierarchyJobAccess<TDomain>.SetParentDeferred(_world, _child, _parent);
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

    private readonly struct DomainA : IHierarchyDomain;

    private readonly struct DomainB : IHierarchyDomain;
}
