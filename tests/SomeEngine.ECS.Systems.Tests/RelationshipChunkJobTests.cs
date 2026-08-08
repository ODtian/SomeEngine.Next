using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Relations;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class RelationshipChunkJobTests
{
    [Fact]
    public void ParentChunks_BulkWriteCanonicalAndLeaveChildrenLastAppliedUntilMaintenance()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity oldParent = world.CreateEntity();
            Entity newParent = world.CreateEntity();
            Entity childA = world.CreateEntity();
            Entity childB = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, childA, oldParent);
            Hierarchy<Domain>.SetParent(world, childB, oldParent);
            QueryHandle query = world.Query(
                world.QueryDefinition().Write<Parent<Domain>>());

            var ordering = new OrderingProbe();
            JobHandle dependency = JobSystem.Schedule(new MarkDependencyJob(ordering));
            JobHandle writer = HierarchyJobAccess<Domain>.ScheduleParentWriteChunks(
                world,
                query,
                new SetAllParentsJob<Domain>(newParent, ordering),
                dependency);

            writer.Complete();

            Assert.Equal(1, Volatile.Read(ref ordering.Completed));
            Assert.Equal(newParent, Hierarchy<Domain>.GetParent(world, childA));
            Assert.Equal(newParent, Hierarchy<Domain>.GetParent(world, childB));
            Assert.Equal(
                new[] { childA, childB },
                Hierarchy<Domain>.GetChildren(world, oldParent).ToArray());
            Assert.Empty(Hierarchy<Domain>.GetChildren(world, newParent));

            HierarchyMaintenanceSystem<Domain>.Schedule(world, writer).Complete();

            Assert.Empty(Hierarchy<Domain>.GetChildren(world, oldParent));
            Assert.Equal(
                new[] { childA, childB },
                Hierarchy<Domain>.GetChildren(world, newParent).ToArray());
        });
    }

    [Fact]
    public void ParentChunks_InvalidFinalForestRollsBackEveryChunkWrite()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity root = world.CreateEntity();
            Entity first = world.CreateEntity();
            Entity second = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, first, root);
            Hierarchy<Domain>.SetParent(world, second, first);
            QueryHandle query = world.Query(
                world.QueryDefinition().Write<Parent<Domain>>());

            JobHandle writer = HierarchyJobAccess<Domain>.ScheduleParentWriteChunks(
                world,
                query,
                new CreateParentCycleJob<Domain>(first, second));

            Assert.Throws<InvalidOperationException>(() => writer.Complete());

            Assert.Equal(root, Hierarchy<Domain>.GetParent(world, first));
            Assert.Equal(first, Hierarchy<Domain>.GetParent(world, second));
            Assert.Equal(
                new[] { first },
                Hierarchy<Domain>.GetChildren(world, root).ToArray());
            Assert.Equal(
                new[] { second },
                Hierarchy<Domain>.GetChildren(world, first).ToArray());

            Hierarchy<Domain>.Maintain(world);
            Assert.Equal(root, Hierarchy<Domain>.GetParent(world, first));
            Assert.Equal(first, Hierarchy<Domain>.GetParent(world, second));
        });
    }

    [Fact]
    public void ParentChunks_RejectMissingReadonlyAndRowFilteredQueryShapes()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Entity markerOnly = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, parent);
            world.Add(child, new EnabledProbe());
            world.Add(child, new ChunkProbe());
            world.Add(markerOnly, new ChunkProbe());
            var job = new SetAllParentsJob<Domain>(parent, ordering: null);

            QueryHandle missing = world.Query(
                world.QueryDefinition().All<Parent<Domain>>());
            QueryHandle readOnly = world.Query(
                world.QueryDefinition().Read<Parent<Domain>>());
            QueryHandle rowFiltered = world.Query(
                world.QueryDefinition()
                    .Write<Parent<Domain>>()
                    .Enabled<EnabledProbe>());
            QueryHandle optionalNonmatching = world.Query(
                world.QueryDefinition()
                    .All<ChunkProbe>()
                    .Optional<Parent<Domain>>(QueryAccess.Write));

            Assert.Throws<InvalidOperationException>(
                () => HierarchyJobAccess<Domain>
                    .ScheduleParentWriteChunks(world, missing, job)
                    .Complete());
            Assert.Throws<InvalidOperationException>(
                () => HierarchyJobAccess<Domain>
                    .ScheduleParentWriteChunks(world, readOnly, job)
                    .Complete());
            InvalidOperationException filteredError = Assert.Throws<InvalidOperationException>(
                () => HierarchyJobAccess<Domain>
                    .ScheduleParentWriteChunks(world, rowFiltered, job)
                    .Complete());

            Assert.Contains("row filters", filteredError.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Throws<InvalidOperationException>(
                () => HierarchyJobAccess<Domain>
                    .ScheduleParentWriteChunks(world, optionalNonmatching, job)
                    .Complete());
            Assert.Equal(parent, Hierarchy<Domain>.GetParent(world, child));
        });
    }

    [Fact]
    public void DirectedEndpointChunks_UniqueTargetSwapValidatesOneFinalImage()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity sourceA = world.CreateEntity();
            Entity sourceB = world.CreateEntity();
            Entity targetA = world.CreateEntity();
            Entity targetB = world.CreateEntity();
            RelationEdge<UniqueTargetDirected> edgeA =
                world.CreateRelation(sourceA, targetA, new UniqueTargetDirected());
            RelationEdge<UniqueTargetDirected> edgeB =
                world.CreateRelation(sourceB, targetB, new UniqueTargetDirected());
            QueryHandle query = world.Query(
                world.QueryDefinition()
                    .Write<DirectedRelationEndpoints<UniqueTargetDirected>>());

            RelationJobAccess<UniqueTargetDirected>.ScheduleDirectedEndpointWriteChunks(
                world,
                query,
                new SwapUniqueTargetsJob(sourceA, targetA, targetB))
                .Complete();

            Assert.Equal(
                targetB,
                world.GetDirectedRelationEndpoints(edgeA).Target);
            Assert.Equal(
                targetA,
                world.GetDirectedRelationEndpoints(edgeB).Target);
            Assert.Equal(
                edgeA,
                Assert.Single(
                    world.GetIncomingRelations<UniqueTargetDirected>(targetA)
                        .Entries.ToArray()).Edge);
            Assert.Equal(
                edgeB,
                Assert.Single(
                    world.GetIncomingRelations<UniqueTargetDirected>(targetB)
                        .Entries.ToArray()).Edge);

            RelationMaintenanceSystem<UniqueTargetDirected>.Schedule(world).Complete();

            Assert.Equal(
                edgeB,
                Assert.Single(
                    world.GetIncomingRelations<UniqueTargetDirected>(targetA)
                        .Entries.ToArray()).Edge);
            Assert.Equal(
                edgeA,
                Assert.Single(
                    world.GetIncomingRelations<UniqueTargetDirected>(targetB)
                        .Entries.ToArray()).Edge);
        });
    }

    [Fact]
    public void UndirectedEndpointChunks_BulkWriteCanonicalAndLeaveIncidentLastApplied()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity endpointA = world.CreateEntity();
            Entity oldEndpointB = world.CreateEntity();
            Entity newEndpointB = world.CreateEntity();
            RelationEdge<UndirectedPayload> edge =
                world.CreateRelation(endpointA, oldEndpointB, new UndirectedPayload());
            QueryHandle query = world.Query(
                world.QueryDefinition()
                    .Write<UndirectedRelationEndpoints<UndirectedPayload>>());

            RelationJobAccess<UndirectedPayload>.ScheduleUndirectedEndpointWriteChunks(
                world,
                query,
                new SetUndirectedSecondEndpointJob(newEndpointB))
                .Complete();

            Assert.Equal(
                newEndpointB,
                world.GetUndirectedRelationEndpoints(edge).EndpointB);
            Assert.Equal(
                edge,
                Assert.Single(
                    world.GetIncidentRelations<UndirectedPayload>(oldEndpointB)
                        .Entries.ToArray()).Edge);
            Assert.Empty(
                world.GetIncidentRelations<UndirectedPayload>(newEndpointB)
                    .Entries.ToArray());

            RelationMaintenanceSystem<UndirectedPayload>.Schedule(world).Complete();

            Assert.Empty(
                world.GetIncidentRelations<UndirectedPayload>(oldEndpointB)
                    .Entries.ToArray());
            Assert.Equal(
                edge,
                Assert.Single(
                    world.GetIncidentRelations<UndirectedPayload>(newEndpointB)
                        .Entries.ToArray()).Edge);
        });
    }

    [Fact]
    public void ReadChunkVariants_ExposeCanonicalProtectedSources()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, parent);

            Entity source = world.CreateEntity();
            Entity target = world.CreateEntity();
            RelationEdge<UniqueTargetDirected> directed =
                world.CreateRelation(source, target, new UniqueTargetDirected());

            Entity endpointA = world.CreateEntity();
            Entity endpointB = world.CreateEntity();
            RelationEdge<UndirectedPayload> undirected =
                world.CreateRelation(endpointA, endpointB, new UndirectedPayload());

            QueryHandle parentQuery = world.Query(
                world.QueryDefinition().Read<Parent<Domain>>());
            QueryHandle directedQuery = world.Query(
                world.QueryDefinition()
                    .Read<DirectedRelationEndpoints<UniqueTargetDirected>>());
            QueryHandle undirectedQuery = world.Query(
                world.QueryDefinition()
                    .Read<UndirectedRelationEndpoints<UndirectedPayload>>());

            var parentCapture = new ParentReadCapture();
            var directedCapture = new DirectedReadCapture();
            var undirectedCapture = new UndirectedReadCapture();

            HierarchyJobAccess<Domain>.ScheduleParentReadChunks(
                world,
                parentQuery,
                new CaptureParentChunksJob<Domain>(parentCapture))
                .Complete();
            RelationJobAccess<UniqueTargetDirected>.ScheduleDirectedEndpointReadChunks(
                world,
                directedQuery,
                new CaptureDirectedChunksJob(directedCapture))
                .Complete();
            RelationJobAccess<UndirectedPayload>.ScheduleUndirectedEndpointReadChunks(
                world,
                undirectedQuery,
                new CaptureUndirectedChunksJob(undirectedCapture))
                .Complete();

            Assert.Equal((child, parent), Assert.Single(parentCapture.Items));
            Assert.Equal(
                (directed.Entity, source, target),
                Assert.Single(directedCapture.Items));
            Assert.Equal(
                (undirected.Entity, endpointA, endpointB),
                Assert.Single(undirectedCapture.Items));
        });
    }

    [Fact]
    public void ParentReadChunks_WaitForSameWorldTopologyWriter()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, parent);
            QueryHandle query = world.Query(
                world.QueryDefinition().Read<Parent<Domain>>());
            var capture = new ParentReadCapture();
            using var writerStarted = new ManualResetEventSlim();
            using var releaseWriter = new ManualResetEventSlim();
            JobHandle writer = default;
            JobHandle reader = default;
            try
            {
                writer = HierarchyJobAccess<Domain>.ScheduleParentWrite(
                    world,
                    new BlockingJob(writerStarted, releaseWriter));
                Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(2)));

                reader = HierarchyJobAccess<Domain>.ScheduleParentReadChunks(
                    world,
                    query,
                    new CaptureParentChunksJob<Domain>(capture));

                Assert.False(reader.IsCompleted);
                releaseWriter.Set();
                reader.Complete();
                writer.Complete();
            }
            finally
            {
                releaseWriter.Set();
                writer.Complete();
                reader.Complete();
            }

            Assert.Equal((child, parent), Assert.Single(capture.Items));
        });
    }

    [Fact]
    public void ParentReadChunks_RejectMissingAndWriteOnlyQueryAccess()
    {
        WithJobRuntime(() =>
        {
            var world = new World();
            Entity parent = world.CreateEntity();
            Entity child = world.CreateEntity();
            Hierarchy<Domain>.SetParent(world, child, parent);
            QueryHandle missing = world.Query(
                world.QueryDefinition().All<Parent<Domain>>());
            QueryHandle writeOnly = world.Query(
                world.QueryDefinition().Write<Parent<Domain>>());
            var capture = new ParentReadCapture();
            var job = new CaptureParentChunksJob<Domain>(capture);

            Assert.Throws<InvalidOperationException>(
                () => HierarchyJobAccess<Domain>
                    .ScheduleParentReadChunks(world, missing, job)
                    .Complete());
            Assert.Throws<InvalidOperationException>(
                () => HierarchyJobAccess<Domain>
                    .ScheduleParentReadChunks(world, writeOnly, job)
                    .Complete());
            Assert.Empty(capture.Items);
        });
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

    private sealed class OrderingProbe
    {
        internal int Completed;
    }

    private readonly struct MarkDependencyJob : IJob
    {
        private readonly OrderingProbe _probe;

        internal MarkDependencyJob(OrderingProbe probe)
        {
            _probe = probe;
        }

        public void Execute()
        {
            Volatile.Write(ref _probe.Completed, 1);
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

    private readonly struct SetAllParentsJob<TDomain> : IParentWriteChunkJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly Entity _parent;
        private readonly OrderingProbe? _ordering;

        internal SetAllParentsJob(Entity parent, OrderingProbe? ordering)
        {
            _parent = parent;
            _ordering = ordering;
        }

        public void Execute(
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            Assert.Equal(entities.Length, parents.Length);
            if (_ordering is not null)
                Assert.Equal(1, Volatile.Read(ref _ordering.Completed));

            for (int i = 0; i < parents.Length; i++)
                parents[i].Value = _parent;
        }
    }

    private readonly struct CreateParentCycleJob<TDomain> : IParentWriteChunkJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly Entity _first;
        private readonly Entity _second;

        internal CreateParentCycleJob(Entity first, Entity second)
        {
            _first = first;
            _second = second;
        }

        public void Execute(
            ReadOnlySpan<Entity> entities,
            Span<Parent<TDomain>> parents)
        {
            for (int i = 0; i < entities.Length; i++)
            {
                if (entities[i] == _first)
                    parents[i].Value = _second;
            }
        }
    }

    private readonly struct SwapUniqueTargetsJob :
        IDirectedRelationEndpointsWriteChunkJob<UniqueTargetDirected>
    {
        private readonly Entity _sourceA;
        private readonly Entity _targetA;
        private readonly Entity _targetB;

        internal SwapUniqueTargetsJob(Entity sourceA, Entity targetA, Entity targetB)
        {
            _sourceA = sourceA;
            _targetA = targetA;
            _targetB = targetB;
        }

        public void Execute(
            ReadOnlySpan<Entity> entities,
            Span<DirectedRelationEndpoints<UniqueTargetDirected>> endpoints)
        {
            Assert.Equal(entities.Length, endpoints.Length);
            for (int i = 0; i < endpoints.Length; i++)
            {
                endpoints[i].Target = endpoints[i].Source == _sourceA
                    ? _targetB
                    : _targetA;
            }
        }
    }

    private readonly struct SetUndirectedSecondEndpointJob :
        IUndirectedRelationEndpointsWriteChunkJob<UndirectedPayload>
    {
        private readonly Entity _endpoint;

        internal SetUndirectedSecondEndpointJob(Entity endpoint)
        {
            _endpoint = endpoint;
        }

        public void Execute(
            ReadOnlySpan<Entity> entities,
            Span<UndirectedRelationEndpoints<UndirectedPayload>> endpoints)
        {
            Assert.Equal(entities.Length, endpoints.Length);
            for (int i = 0; i < endpoints.Length; i++)
                endpoints[i].EndpointB = _endpoint;
        }
    }

    private sealed class ParentReadCapture
    {
        internal List<(Entity Child, Entity Parent)> Items { get; } = [];
    }

    private readonly struct CaptureParentChunksJob<TDomain> : IParentReadChunkJob<TDomain>
        where TDomain : IHierarchyDomain
    {
        private readonly ParentReadCapture _capture;

        internal CaptureParentChunksJob(ParentReadCapture capture)
        {
            _capture = capture;
        }

        public void Execute(
            ReadOnlySpan<Entity> entities,
            ReadOnlySpan<Parent<TDomain>> parents)
        {
            Assert.Equal(entities.Length, parents.Length);
            for (int i = 0; i < parents.Length; i++)
                _capture.Items.Add((entities[i], parents[i].Value));
        }
    }

    private sealed class DirectedReadCapture
    {
        internal List<(Entity Edge, Entity Source, Entity Target)> Items { get; } = [];
    }

    private readonly struct CaptureDirectedChunksJob :
        IDirectedRelationEndpointsReadChunkJob<UniqueTargetDirected>
    {
        private readonly DirectedReadCapture _capture;

        internal CaptureDirectedChunksJob(DirectedReadCapture capture)
        {
            _capture = capture;
        }

        public void Execute(
            ReadOnlySpan<Entity> entities,
            ReadOnlySpan<DirectedRelationEndpoints<UniqueTargetDirected>> endpoints)
        {
            Assert.Equal(entities.Length, endpoints.Length);
            for (int i = 0; i < endpoints.Length; i++)
            {
                _capture.Items.Add(
                    (entities[i], endpoints[i].Source, endpoints[i].Target));
            }
        }
    }

    private sealed class UndirectedReadCapture
    {
        internal List<(Entity Edge, Entity EndpointA, Entity EndpointB)> Items { get; } = [];
    }

    private readonly struct CaptureUndirectedChunksJob :
        IUndirectedRelationEndpointsReadChunkJob<UndirectedPayload>
    {
        private readonly UndirectedReadCapture _capture;

        internal CaptureUndirectedChunksJob(UndirectedReadCapture capture)
        {
            _capture = capture;
        }

        public void Execute(
            ReadOnlySpan<Entity> entities,
            ReadOnlySpan<UndirectedRelationEndpoints<UndirectedPayload>> endpoints)
        {
            Assert.Equal(entities.Length, endpoints.Length);
            for (int i = 0; i < endpoints.Length; i++)
            {
                _capture.Items.Add(
                    (entities[i], endpoints[i].EndpointA, endpoints[i].EndpointB));
            }
        }
    }

    private readonly struct Domain : IHierarchyDomain;

    private struct EnabledProbe : IEnableableComponent;

    private struct ChunkProbe : IComponent;

    [RelationSchema(RelationDirection.Directed, RelationCardinality.UniqueTarget)]
    private struct UniqueTargetDirected : IComponent;

    [RelationSchema(RelationDirection.Undirected, RelationCardinality.Parallel)]
    private struct UndirectedPayload : IComponent;
}
