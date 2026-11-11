using System.Numerics;
using SomeEngine.Core.ECS;
using SomeEngine.Core.ECS.Components;
using SomeEngine.Core.Math;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.Core.Tests.ECS;

public sealed class ParallelTransformPropagationTests
{
    [Fact]
    public void DisjointMixedOrderRoots_PropagateParentBeforeChildThroughOrganizationNodes()
    {
        WithJobRuntime(() =>
        {
            var world = new World(execution: new WorldJobs(JobSystem.Default));
            Entity orderedRoot = CreateTransform(world, new Vector3(10, 0, 0));
            Entity orderedChild = CreateTransform(world, new Vector3(1, 0, 0));
            Entity organization = world.Create();
            Entity organizationLeaf = CreateTransform(world, new Vector3(0, 2, 0));
            Entity unorderedRoot = CreateTransform(world, new Vector3(0, 20, 0));
            Entity unorderedChild = CreateTransform(world, new Vector3(0, 3, 0));

            Hierarchy<DefaultHierarchyDomain>.SetOrdered(world, orderedRoot, true);
            Hierarchy.SetParent(world, orderedChild, orderedRoot, insertAt: 0);
            Hierarchy.SetParent(world, organization, orderedRoot, insertAt: 1);
            Hierarchy.SetParent(world, organizationLeaf, organization);
            Hierarchy.SetParent(world, unorderedChild, unorderedRoot);

            Hierarchy<DefaultHierarchyDomain>.Maintain(world);
            JobHandle maintenance = default;
            JobHandle propagation = ParallelTransformPropagation.Schedule(
                world,
                [organizationLeaf, unorderedChild, unorderedRoot, orderedChild, orderedRoot],
                maintenance,
                rootsPerPacket: 1);

            propagation.Complete();

            Assert.Equal(
                new Vector3(10, 0, 0),
                world.ReadComponentData<WorldTransform>(orderedRoot).Qvvs.Position);
            Assert.Equal(
                new Vector3(11, 0, 0),
                world.ReadComponentData<WorldTransform>(orderedChild).Qvvs.Position);
            Assert.Equal(
                new Vector3(10, 2, 0),
                world.ReadComponentData<WorldTransform>(organizationLeaf).Qvvs.Position);
            Assert.Equal(
                new Vector3(0, 20, 0),
                world.ReadComponentData<WorldTransform>(unorderedRoot).Qvvs.Position);
            Assert.Equal(
                new Vector3(0, 23, 0),
                world.ReadComponentData<WorldTransform>(unorderedChild).Qvvs.Position);
            Assert.False(world.Has<LocalTransform>(organization));
            Assert.False(world.Has<WorldTransform>(organization));
        });
    }

    [Fact]
    public void WorldTransformOnlyOrganizationNode_IsIdentityPassThrough()
    {
        WithJobRuntime(() =>
        {
            var world = new World(execution: new WorldJobs(JobSystem.Default));
            Entity root = CreateTransform(world, new Vector3(10, 0, 0));
            Entity organization = world.Create();
            world.Add(organization, new WorldTransform
            {
                Qvvs = new TransformQvvs(new Vector3(100, 0, 0), Quaternion.Identity),
            });
            Entity leaf = CreateTransform(world, new Vector3(1, 0, 0));
            Hierarchy.SetParent(world, organization, root);
            Hierarchy.SetParent(world, leaf, organization);

            Hierarchy<DefaultHierarchyDomain>.Maintain(world);
            JobHandle maintenance = default;
            JobHandle propagation = ParallelTransformPropagation.Schedule(
                world,
                [root],
                maintenance);

            propagation.Complete();

            Assert.False(world.Has<LocalTransform>(organization));
            Assert.Equal(
                new Vector3(100, 0, 0),
                world.ReadComponentData<WorldTransform>(organization).Qvvs.Position);
            Assert.Equal(
                new Vector3(11, 0, 0),
                world.ReadComponentData<WorldTransform>(leaf).Qvvs.Position);
        });
    }

    [Fact]
    public void ExplicitMaintenanceToken_MakesDeferredReparentFreshForPropagation()
    {
        WithJobRuntime(() =>
        {
            var world = new World(execution: new WorldJobs(JobSystem.Default));
            Entity oldRoot = CreateTransform(world, new Vector3(100, 0, 0));
            Entity newRoot = CreateTransform(world, new Vector3(10, 0, 0));
            Entity organization = world.Create();
            Entity child = CreateTransform(world, new Vector3(1, 0, 0));
            Hierarchy.SetParent(world, organization, newRoot);
            Hierarchy.SetParent(world, child, oldRoot);

            var jobs = (WorldJobs)world.Execution!;
            JobHandle writer = jobs.Scheduler.Schedule(
                new DeferredReparentJob(world, child, organization), jobs.StructureWrite);
            JobHandle maintenance = jobs.Scheduler.Schedule(new MaintainHierarchy(world),
                jobs.StructureWrite, dependency: writer);

            JobHandle propagation = ParallelTransformPropagation.Schedule(
                world,
                [newRoot],
                maintenance,
                rootsPerPacket: 1);
            propagation.Complete();

            Assert.Equal(organization, Hierarchy.GetParent(world, child));
            Assert.Empty(Children(world, oldRoot));
            Assert.Equal([child], Children(world, organization));
            Assert.Equal(
                new Vector3(11, 0, 0),
                world.ReadComponentData<WorldTransform>(child).Qvvs.Position);
        });
    }

    [Fact]
    public void DirtySubtreeRoot_MayReadItsStableExternalAncestorFromWritableWorldFamily()
    {
        WithJobRuntime(() =>
        {
            var world = new World(execution: new WorldJobs(JobSystem.Default));
            Entity parent = CreateTransform(world, new Vector3(10, 0, 0));
            Entity child = CreateTransform(world, new Vector3(1, 0, 0));
            Hierarchy.SetParent(world, child, parent);
            world.Set(parent, new WorldTransform
            {
                Qvvs = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity),
            });
            Hierarchy<DefaultHierarchyDomain>.Maintain(world);
            JobHandle maintenance = default;

            JobHandle propagation = ParallelTransformPropagation.Schedule(
                world,
                [child],
                maintenance);
            propagation.Complete();

            Assert.Equal(
                new Vector3(11, 0, 0),
                world.ReadComponentData<WorldTransform>(child).Qvvs.Position);
        });
    }

    private static Entity CreateTransform(World world, Vector3 localPosition)
    {
        Entity entity = world.Create();
        world.Add(entity, new LocalTransform
        {
            Value = new TransformQvvs(localPosition, Quaternion.Identity),
        });
        world.Add(entity, new WorldTransform
        {
            Qvvs = TransformQvvs.Identity,
        });
        return entity;
    }

    private static void WithJobRuntime(Action action)
    {
        ManagedPayloadPolicy previousPolicy = JobSystem.ManagedPayloadPolicy;
        JobSafetyMode previousSafety = JobSystem.SafetyMode;
        JobSystem.Initialize(new JobConfiguration
        {
            WorkerCount = 4,
            SafetyMode = previousSafety,
            ManagedPayloadPolicy = ManagedPayloadPolicy.Allow,
        });
        try
        {
            action();
        }
        finally
        {
            JobSystem.Initialize(new JobConfiguration
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
    }

    private readonly struct MaintainHierarchy(World world) : IJob
    { public void Execute() => Hierarchy<DefaultHierarchyDomain>.Maintain(world); }

    private static Entity[] Children(World world, Entity parent)
    {
        using ChildrenRead children = Hierarchy.GetChildren(world, parent);
        return children.Entities.ToArray();
    }

    private readonly struct DeferredReparentJob : IJob
    {
        private readonly World _world;
        private readonly Entity _child;
        private readonly Entity _parent;

        internal DeferredReparentJob(World world, Entity child, Entity parent)
        {
            _world = world;
            _child = child;
            _parent = parent;
        }

        public void Execute()
        {
            Hierarchy<DefaultHierarchyDomain>.SetParentDeferred(
                _world,
                _child,
                _parent);
        }
    }
}
