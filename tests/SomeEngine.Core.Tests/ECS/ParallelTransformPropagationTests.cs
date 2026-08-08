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
            var world = new World();
            Entity orderedRoot = CreateTransform(world, new Vector3(10, 0, 0));
            Entity orderedChild = CreateTransform(world, new Vector3(1, 0, 0));
            Entity organization = world.CreateEntity();
            Entity organizationLeaf = CreateTransform(world, new Vector3(0, 2, 0));
            Entity unorderedRoot = CreateTransform(world, new Vector3(0, 20, 0));
            Entity unorderedChild = CreateTransform(world, new Vector3(0, 3, 0));

            Hierarchy.SetChildOrderPolicy(
                world,
                orderedRoot,
                ChildOrderPolicy.Ordered);
            Hierarchy.SetParent(world, orderedChild, orderedRoot, insertIndex: 0);
            Hierarchy.SetParent(world, organization, orderedRoot, insertIndex: 1);
            Hierarchy.SetParent(world, organizationLeaf, organization);
            Hierarchy.SetParent(world, unorderedChild, unorderedRoot);

            HierarchyMaintenanceDependency<DefaultHierarchyDomain> maintenance =
                HierarchyMaintenanceSystem<DefaultHierarchyDomain>.ScheduleDependency(world);
            HierarchyPropagation propagation = ParallelTransformPropagation.Schedule(
                world,
                [organizationLeaf, unorderedChild, unorderedRoot, orderedChild, orderedRoot],
                maintenance,
                rootsPerPacket: 1);

            propagation.Handle.Complete();

            Assert.Equal(2, propagation.Partition.RootCount);
            Assert.Equal(2, propagation.Partition.PacketCount);
            Assert.Equal(1, propagation.Partition.RootsPerPacket);
            Assert.True(propagation.Partition.ProvesNonOverlap(0, 1));
            Assert.Equal(
                new[] { orderedRoot, unorderedRoot },
                propagation.Partition.NormalizedRoots.ToArray());
            Assert.Equal(
                new Vector3(10, 0, 0),
                world.Read<WorldTransform>(orderedRoot).Qvvs.Position);
            Assert.Equal(
                new Vector3(11, 0, 0),
                world.Read<WorldTransform>(orderedChild).Qvvs.Position);
            Assert.Equal(
                new Vector3(10, 2, 0),
                world.Read<WorldTransform>(organizationLeaf).Qvvs.Position);
            Assert.Equal(
                new Vector3(0, 20, 0),
                world.Read<WorldTransform>(unorderedRoot).Qvvs.Position);
            Assert.Equal(
                new Vector3(0, 23, 0),
                world.Read<WorldTransform>(unorderedChild).Qvvs.Position);
            Assert.False(world.Has<LocalTransform>(organization));
            Assert.False(world.Has<WorldTransform>(organization));
        });
    }

    [Fact]
    public void WorldTransformOnlyOrganizationNode_IsIdentityPassThrough()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = CreateTransform(world, new Vector3(10, 0, 0));
            Entity organization = world.CreateEntity();
            world.Add(organization, new WorldTransform
            {
                Qvvs = new TransformQvvs(new Vector3(100, 0, 0), Quaternion.Identity),
            });
            Entity leaf = CreateTransform(world, new Vector3(1, 0, 0));
            Hierarchy.SetParent(world, organization, root);
            Hierarchy.SetParent(world, leaf, organization);

            HierarchyMaintenanceDependency<DefaultHierarchyDomain> maintenance =
                HierarchyMaintenanceSystem<DefaultHierarchyDomain>.ScheduleDependency(world);
            HierarchyPropagation propagation = ParallelTransformPropagation.Schedule(
                world,
                [root],
                maintenance);

            propagation.Handle.Complete();

            Assert.False(world.Has<LocalTransform>(organization));
            Assert.Equal(
                new Vector3(100, 0, 0),
                world.Read<WorldTransform>(organization).Qvvs.Position);
            Assert.Equal(
                new Vector3(11, 0, 0),
                world.Read<WorldTransform>(leaf).Qvvs.Position);
        });
    }

    [Fact]
    public void ExplicitMaintenanceToken_MakesDeferredReparentFreshForPropagation()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldRoot = CreateTransform(world, new Vector3(100, 0, 0));
            Entity newRoot = CreateTransform(world, new Vector3(10, 0, 0));
            Entity organization = world.CreateEntity();
            Entity child = CreateTransform(world, new Vector3(1, 0, 0));
            Hierarchy.SetParent(world, organization, newRoot);
            Hierarchy.SetParent(world, child, oldRoot);

            JobHandle writer = HierarchyJobAccess<DefaultHierarchyDomain>.ScheduleParentWrite(
                world,
                new DeferredReparentJob(world, child, organization));
            HierarchyMaintenanceDependency<DefaultHierarchyDomain> maintenance =
                HierarchyMaintenanceSystem<DefaultHierarchyDomain>.ScheduleDependency(world, writer);

            HierarchyPropagation propagation = ParallelTransformPropagation.Schedule(
                world,
                [newRoot],
                maintenance,
                rootsPerPacket: 1);
            propagation.Handle.Complete();

            Assert.Equal(organization, Hierarchy.GetParent(world, child));
            Assert.Empty(Hierarchy.GetChildren(world, oldRoot));
            Assert.Equal([child], Hierarchy.GetChildren(world, organization).ToArray());
            Assert.Equal(
                new Vector3(11, 0, 0),
                world.Read<WorldTransform>(child).Qvvs.Position);
        });
    }

    [Fact]
    public void DirtySubtreeRoot_MayReadItsStableExternalAncestorFromWritableWorldFamily()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = CreateTransform(world, new Vector3(10, 0, 0));
            Entity child = CreateTransform(world, new Vector3(1, 0, 0));
            Hierarchy.SetParent(world, child, parent);
            world.Replace(parent, new WorldTransform
            {
                Qvvs = new TransformQvvs(new Vector3(10, 0, 0), Quaternion.Identity),
            });
            HierarchyMaintenanceDependency<DefaultHierarchyDomain> maintenance =
                HierarchyMaintenanceSystem<DefaultHierarchyDomain>.ScheduleDependency(world);

            HierarchyPropagation propagation = ParallelTransformPropagation.Schedule(
                world,
                [child],
                maintenance);
            propagation.Handle.Complete();

            Assert.Equal([child], propagation.Partition.NormalizedRoots.ToArray());
            Assert.Equal(
                new Vector3(11, 0, 0),
                world.Read<WorldTransform>(child).Qvvs.Position);
        });
    }

    private static Entity CreateTransform(World world, Vector3 localPosition)
    {
        Entity entity = world.CreateEntity();
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
        JobSystem.Initialize(new JobRuntimeConfig
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
            JobSystem.Initialize(new JobRuntimeConfig
            {
                SafetyMode = previousSafety,
                ManagedPayloadPolicy = previousPolicy,
            });
        }
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
            HierarchyJobAccess<DefaultHierarchyDomain>.SetParentDeferred(
                _world,
                _child,
                _parent);
        }
    }
}
