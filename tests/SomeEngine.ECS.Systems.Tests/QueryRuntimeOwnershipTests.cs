using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed class QueryRuntimeOwnershipTests
{
    [Fact]
    public void QueryRegistryIsOwnedDirectlyWithoutForwardingOwner()
    {
        Type? forwardingOwner = typeof(World).Assembly.GetType(
            "SomeEngine.ECS.Owners.Queries",
            throwOnError: false);

        Assert.Null(forwardingOwner);
    }

    [Fact]
    public void GeneratedDescriptor_UsesCurrentRegistryPinWithoutRetainingObsoleteRecord()
    {
        var world = new World();
        var descriptor = new GeneratedQueryAccessDescriptor(QueryDefinition.Empty);

        QueryHandle handle = descriptor.Resolve(world);
        QueryRecord original = world.ActiveStructureRoot.Queries.Get(handle);
        Assert.Equal(1, original.AcquisitionCount);

        QueryRecord candidate;
        using (StructuralMutationScope mutation = world.BeginStructuralMutation())
        {
            candidate = world.ActiveStructureRoot.Queries.Get(handle);
            Assert.NotSame(original, candidate);
            mutation.Commit();
        }

        QueryRecord published = world.ActiveStructureRoot.Queries.Get(handle);
        Assert.Same(candidate, published);
        Assert.Equal(handle, descriptor.Resolve(world));
        Assert.Same(published, world.ActiveStructureRoot.Queries.Get(handle));
        Assert.Equal(1, published.AcquisitionCount);
    }

    [Fact]
    public void DistinctReadQueriesRemainExactUnderConcurrentRepeatedLookup()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        Entity first = world.CreateEntity(new ComponentA { Value = 1 });
        Entity second = world.CreateEntity(new ComponentB { Value = 2 });
        QueryHandle firstQuery = world.Query(world.QueryDefinition().Read<ComponentA>());
        QueryHandle secondQuery = world.Query(world.QueryDefinition().Read<ComponentB>());

        JobHandle firstReader = ComponentJobAccess<ComponentA>.ScheduleRead(
            world,
            new RepeatedExactQueryJob(world, firstQuery, first));
        JobHandle secondReader = ComponentJobAccess<ComponentB>.ScheduleRead(
            world,
            new RepeatedExactQueryJob(world, secondQuery, second));

        firstReader.Complete();
        secondReader.Complete();
    }

    [Fact]
    public void DifferentTableComponents_EnterQueryCallbacksConcurrently()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        _ = world.CreateEntity(new ComponentA { Value = 1 });
        _ = world.CreateEntity(new ComponentB { Value = 2 });
        QueryHandle firstQuery = world.Query(
            world.QueryDefinition().ReadWrite<ComponentA>());
        QueryHandle secondQuery = world.Query(
            world.QueryDefinition().ReadWrite<ComponentB>());
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        JobHandle second = default;

        try
        {
            first = ComponentJobAccess<ComponentA>.ScheduleWrite(
                world,
                new BlockingQueryJob(world, firstQuery, firstStarted, releaseFirst));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            second = ComponentJobAccess<ComponentB>.ScheduleWrite(
                world,
                new SignalQueryJob(world, secondQuery, secondStarted));

            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            second.Complete();
        }
        finally
        {
            releaseFirst.Set();
            first.Complete();
            second.Complete();
        }
    }

    [Fact]
    public void SameTableComponent_WriteQueryBlocksReadQueryAtResourceFrontier()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        _ = world.CreateEntity(new ComponentA { Value = 1 });
        QueryHandle writeQuery = world.Query(
            world.QueryDefinition().ReadWrite<ComponentA>());
        QueryHandle readQuery = world.Query(
            world.QueryDefinition().Read<ComponentA>());
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        using var readerStarted = new ManualResetEventSlim();
        JobHandle writer = default;
        JobHandle reader = default;

        try
        {
            writer = ComponentJobAccess<ComponentA>.ScheduleWrite(
                world,
                new BlockingQueryJob(world, writeQuery, writerStarted, releaseWriter));
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

            reader = ComponentJobAccess<ComponentA>.ScheduleRead(
                world,
                new SignalQueryJob(world, readQuery, readerStarted));

            Assert.False(readerStarted.Wait(TimeSpan.FromMilliseconds(100)));
            releaseWriter.Set();
            reader.Complete();
            writer.Complete();
            Assert.True(readerStarted.IsSet);
        }
        finally
        {
            releaseWriter.Set();
            writer.Complete();
            reader.Complete();
        }
    }

    [Fact]
    public void SameTableComponent_InDifferentWorldsDoesNotShareQueryOwner()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var firstWorld = new World();
        var secondWorld = new World();
        _ = firstWorld.CreateEntity(new ComponentA { Value = 1 });
        _ = secondWorld.CreateEntity(new ComponentA { Value = 2 });
        QueryHandle firstQuery = firstWorld.Query(
            firstWorld.QueryDefinition().ReadWrite<ComponentA>());
        QueryHandle secondQuery = secondWorld.Query(
            secondWorld.QueryDefinition().ReadWrite<ComponentA>());
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        JobHandle second = default;

        try
        {
            first = ComponentJobAccess<ComponentA>.ScheduleWrite(
                firstWorld,
                new BlockingQueryJob(firstWorld, firstQuery, firstStarted, releaseFirst));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            second = ComponentJobAccess<ComponentA>.ScheduleWrite(
                secondWorld,
                new SignalQueryJob(secondWorld, secondQuery, secondStarted));

            Assert.True(secondStarted.Wait(TimeSpan.FromSeconds(5)));
            second.Complete();
        }
        finally
        {
            releaseFirst.Set();
            first.Complete();
            second.Complete();
        }
    }

    [Fact]
    public void ReadonlyRelationshipQuery_CanOverlapUnrelatedTableQuery()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();
        _ = world.CreateEntity(new ComponentB { Value = 2 });
        Hierarchy<Domain>.SetParent(world, child, parent);
        QueryHandle parentQuery = world.Query(
            world.QueryDefinition().Read<Parent<Domain>>());
        QueryHandle componentQuery = world.Query(
            world.QueryDefinition().ReadWrite<ComponentB>());
        using var parentStarted = new ManualResetEventSlim();
        using var releaseParent = new ManualResetEventSlim();
        using var componentStarted = new ManualResetEventSlim();
        JobHandle parentReader = default;
        JobHandle componentWriter = default;

        try
        {
            parentReader = HierarchyJobAccess<Domain>.ScheduleParentReadChunks(
                world,
                parentQuery,
                new BlockingParentReadJob(parentStarted, releaseParent));
            Assert.True(parentStarted.Wait(TimeSpan.FromSeconds(5)));

            componentWriter = ComponentJobAccess<ComponentB>.ScheduleWrite(
                world,
                new SignalQueryJob(world, componentQuery, componentStarted));

            Assert.True(componentStarted.Wait(TimeSpan.FromSeconds(5)));
            componentWriter.Complete();
        }
        finally
        {
            releaseParent.Set();
            parentReader.Complete();
            componentWriter.Complete();
        }
    }

    [Fact]
    public void WritableRelationshipQuery_BlocksOrdinaryQueryAtTopologyFrontier()
    {
        using var runtime = new JobRuntimeScope(workerCount: 4);
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();
        _ = world.CreateEntity(new ComponentB { Value = 2 });
        Hierarchy<Domain>.SetParent(world, child, parent);
        QueryHandle parentQuery = world.Query(
            world.QueryDefinition().ReadWrite<Parent<Domain>>());
        QueryHandle componentQuery = world.Query(
            world.QueryDefinition().ReadWrite<ComponentB>());
        using var parentStarted = new ManualResetEventSlim();
        using var releaseParent = new ManualResetEventSlim();
        using var componentStarted = new ManualResetEventSlim();
        JobHandle parentWriter = default;
        JobHandle componentWriter = default;

        try
        {
            parentWriter = HierarchyJobAccess<Domain>.ScheduleParentWriteChunks(
                world,
                parentQuery,
                new BlockingParentWriteJob(parentStarted, releaseParent));
            Assert.True(parentStarted.Wait(TimeSpan.FromSeconds(5)));

            componentWriter = ComponentJobAccess<ComponentB>.ScheduleWrite(
                world,
                new SignalQueryJob(world, componentQuery, componentStarted));

            Assert.False(componentStarted.Wait(TimeSpan.FromMilliseconds(100)));
            releaseParent.Set();
            componentWriter.Complete();
            parentWriter.Complete();
            Assert.True(componentStarted.IsSet);
        }
        finally
        {
            releaseParent.Set();
            parentWriter.Complete();
            componentWriter.Complete();
        }
    }

    [Fact]
    public void WritableRelationshipOwner_DefensivelyRejectsNestedOrdinaryQuery()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity child = world.CreateEntity();
        _ = world.CreateEntity(new ComponentB { Value = 2 });
        Hierarchy<Domain>.SetParent(world, child, parent);
        QueryHandle relationshipQuery = world.Query(
            world.QueryDefinition().ReadWrite<Parent<Domain>>());
        QueryHandle ordinaryQuery = world.Query(
            world.QueryDefinition().Read<ComponentB>());

        world.ExecuteQuery(relationshipQuery, _ =>
        {
            Assert.Throws<InvalidOperationException>(
                () => world.ExecuteQuery(ordinaryQuery, static _ => { }));
        });

        Assert.Equal(parent, Hierarchy<Domain>.GetParent(world, child));
    }

    [Fact]
    public void FaultedOrdinaryQuery_ReleasesBorrowAndSynchronousResourceOwner()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity entity = world.CreateEntity(new ComponentA { Value = 1 });
        QueryHandle query = world.Query(
            world.QueryDefinition().ReadWrite<ComponentA>());
        _ = ComponentJobAccess<ComponentA>.Write(world);

        Assert.Throws<ProbeException>(
            () => world.ExecuteQuery(query, static _ => throw new ProbeException()));

        // A leaked ordinary borrow rejects this structural mutation. The scheduled callback then
        // proves the synchronous component-resource owner was released as well.
        world.Add(entity, new ComponentB { Value = 2 });
        using var started = new ManualResetEventSlim();
        ComponentJobAccess<ComponentA>
            .ScheduleWrite(world, new SignalQueryJob(world, query, started))
            .Complete();
        Assert.True(started.IsSet);
    }

    [Fact]
    public void WritableRelationshipQuery_BodyFaultStillRollsBackCanonicalValue()
    {
        using var runtime = new JobRuntimeScope(workerCount: 2);
        var world = new World();
        Entity oldParent = world.CreateEntity();
        Entity newParent = world.CreateEntity();
        Entity child = world.CreateEntity();
        Hierarchy<Domain>.SetParent(world, child, oldParent);
        QueryHandle query = world.Query(
            world.QueryDefinition().ReadWrite<Parent<Domain>>());

        Assert.Throws<ProbeException>(() =>
            world.ExecuteQuery(query, cursor =>
            {
                foreach (QueryRow row in cursor.Rows)
                {
                    row.ReadWrite<Parent<Domain>>().Value = newParent;
                    throw new ProbeException();
                }
            }));

        Assert.Equal(oldParent, Hierarchy<Domain>.GetParent(world, child));
        Assert.Equal(
            new[] { child },
            Hierarchy<Domain>.GetChildren(world, oldParent).ToArray());
        Assert.Empty(Hierarchy<Domain>.GetChildren(world, newParent));
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

    private readonly struct BlockingQueryJob : IJob
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingQueryJob(
            World world,
            QueryHandle query,
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _world = world;
            _query = query;
            _started = started;
            _release = release;
        }

        public void Execute()
        {
            var state = new BlockingState(_started, _release);
            _world.ExecuteQuery(
                _query,
                ref state,
                static (QueryCursor _, ref BlockingState blocking) => blocking.Block());
        }
    }

    private readonly struct SignalQueryJob : IJob
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly ManualResetEventSlim _started;

        internal SignalQueryJob(
            World world,
            QueryHandle query,
            ManualResetEventSlim started)
        {
            _world = world;
            _query = query;
            _started = started;
        }

        public void Execute()
        {
            ManualResetEventSlim started = _started;
            _world.ExecuteQuery(
                _query,
                ref started,
                static (QueryCursor _, ref ManualResetEventSlim signal) => signal.Set());
        }
    }

    private readonly struct RepeatedExactQueryJob : IJob
    {
        private readonly World _world;
        private readonly QueryHandle _query;
        private readonly Entity _expected;

        internal RepeatedExactQueryJob(World world, QueryHandle query, Entity expected)
        {
            _world = world;
            _query = query;
            _expected = expected;
        }

        public void Execute()
        {
            for (int iteration = 0; iteration < 2_000; iteration++)
            {
                var state = new ExactQueryState(_expected);
                _world.ExecuteQuery(
                    _query,
                    lastSystemVersion: 0,
                    currentSystemVersion: uint.MaxValue,
                    ref state,
                    static (QueryCursor cursor, ref ExactQueryState exact) =>
                    {
                        foreach (QueryRow row in cursor.Rows)
                        {
                            if (row.Entity != exact.Expected)
                            {
                                throw new InvalidOperationException(
                                    "A concurrent query lookup returned another query's state.");
                            }

                            exact.Count++;
                        }
                    });

                if (state.Count != 1)
                {
                    throw new InvalidOperationException(
                        $"Expected one exact query row, observed {state.Count}.");
                }
            }
        }
    }

    private struct ExactQueryState
    {
        internal ExactQueryState(Entity expected)
        {
            Expected = expected;
            Count = 0;
        }

        internal Entity Expected;
        internal int Count;
    }

    private readonly struct BlockingParentReadJob : IParentReadChunkJob<Domain>
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingParentReadJob(
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        public void Execute(
            ReadOnlySpan<Entity> entities,
            ReadOnlySpan<Parent<Domain>> parents)
        {
            Assert.Equal(entities.Length, parents.Length);
            _started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release the parent query callback.");
        }
    }

    private readonly struct BlockingParentWriteJob : IParentWriteChunkJob<Domain>
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingParentWriteJob(
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        public void Execute(
            ReadOnlySpan<Entity> entities,
            Span<Parent<Domain>> parents)
        {
            Assert.Equal(entities.Length, parents.Length);
            _started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release the parent query callback.");
        }
    }

    private sealed class BlockingState
    {
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingState(
            ManualResetEventSlim started,
            ManualResetEventSlim release)
        {
            _started = started;
            _release = release;
        }

        internal void Block()
        {
            _started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release the query callback.");
        }
    }

    private sealed class ProbeException : Exception;

    private struct ComponentA : IComponent
    {
        internal int Value;
    }

    private struct ComponentB : IComponent
    {
        internal int Value;
    }

    private sealed class Domain : IHierarchyDomain;
}
