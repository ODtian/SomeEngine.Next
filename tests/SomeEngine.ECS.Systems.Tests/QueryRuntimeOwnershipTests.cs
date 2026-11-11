using SomeEngine.ECS.Serialization;
using ManualResetEventSlim = SomeEngine.Testing.TestSignal;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.Job;

namespace SomeEngine.ECS.Systems.Tests;

public sealed partial class QueryRuntimeOwnershipTests : JobTest
{

    [Fact]
    public void ConcurrentRepeatedReadQueriesDoesNotThrow()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity first = world.Create(new ComponentA { Value = 1 });
        Entity second = world.Create(new ComponentB { Value = 2 });
        Query firstQuery = world.Query(new QueryDescription().WithStored<ComponentA>());
        Query secondQuery = world.Query(new QueryDescription().WithStored<ComponentB>());

        JobHandle firstReader = world.Execution!.Scheduler!.Schedule(new RepeatedQueryJob(world, firstQuery, first), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<ComponentA>()]);
        JobHandle secondReader = world.Execution!.Scheduler!.Schedule(new RepeatedQueryJob(world, secondQuery, second), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<ComponentB>()]);

        firstReader.Complete();
        secondReader.Complete();
    }

    [Fact]
    public void DifferentTableComponents_EnterQueryCallbacksConcurrently()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        _ = world.Create(new ComponentA { Value = 1 });
        _ = world.Create(new ComponentB { Value = 2 });
        Query firstQuery = world.Query(
            new QueryDescription().WithStored<ComponentA>());
        Query secondQuery = world.Query(
            new QueryDescription().WithStored<ComponentB>());
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        JobHandle second = default;

        try
        {
            first = world.Execution!.Scheduler!.Schedule(new BlockingQueryJob(world, firstQuery, firstStarted, releaseFirst), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<ComponentA>()));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            second = world.Execution!.Scheduler!.Schedule(new SignalQueryJob(world, secondQuery, secondStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<ComponentB>()));

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
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        _ = world.Create(new ComponentA { Value = 1 });
        Query writeQuery = world.Query(
            new QueryDescription().WithStored<ComponentA>());
        Query readQuery = world.Query(
            new QueryDescription().WithStored<ComponentA>());
        using var writerStarted = new ManualResetEventSlim();
        using var releaseWriter = new ManualResetEventSlim();
        releaseWriter.WaitCallback = () => events.Enqueue("released");
        using var readerStarted = new ManualResetEventSlim();
        readerStarted.SetCallback = () => events.Enqueue("readerStarted");
        JobHandle writer = default;
        JobHandle reader = default;

        try
        {
            writer = world.Execution!.Scheduler!.Schedule(new BlockingQueryJob(world, writeQuery, writerStarted, releaseWriter), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<ComponentA>()));
            Assert.True(writerStarted.Wait(TimeSpan.FromSeconds(5)));

            reader = world.Execution!.Scheduler!.Schedule(new SignalQueryJob(world, readQuery, readerStarted), [.. world.Execution!.StructureRead, ((WorldJobs)world.Execution!).Read<ComponentA>()]);

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

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("readerStarted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "readerStarted"));
    }

    [Fact]
    public void SameTableComponent_InDifferentWorldsDoesNotShareQueryOwner()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var firstWorld = CreateJobWorld();
        var secondWorld = CreateJobWorld();
        _ = firstWorld.Create(new ComponentA { Value = 1 });
        _ = secondWorld.Create(new ComponentA { Value = 2 });
        Query firstQuery = firstWorld.Query(
            new QueryDescription().WithStored<ComponentA>());
        Query secondQuery = secondWorld.Query(
            new QueryDescription().WithStored<ComponentA>());
        using var firstStarted = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        JobHandle first = default;
        JobHandle second = default;

        try
        {
            first = firstWorld.Execution!.Scheduler!.Schedule(new BlockingQueryJob(firstWorld, firstQuery, firstStarted, releaseFirst), ((WorldJobs)firstWorld.Execution!).CollectAccess(new SystemAccesses().WriteRandom<ComponentA>()));
            Assert.True(firstStarted.Wait(TimeSpan.FromSeconds(5)));

            second = secondWorld.Execution!.Scheduler!.Schedule(new SignalQueryJob(secondWorld, secondQuery, secondStarted), ((WorldJobs)secondWorld.Execution!).CollectAccess(new SystemAccesses().WriteRandom<ComponentA>()));

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
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity parent = world.Create();
        Entity child = world.Create();
        _ = world.Create(new ComponentB { Value = 2 });
        Hierarchy<Domain>.SetParent(world, child, parent);
        Query parentQuery = world.Query(
            new QueryDescription().WithStored<Parent<Domain>>());
        Query componentQuery = world.Query(
            new QueryDescription().WithStored<ComponentB>());
        using var parentStarted = new ManualResetEventSlim();
        using var releaseParent = new ManualResetEventSlim();
        using var componentStarted = new ManualResetEventSlim();
        JobHandle parentReader = default;
        JobHandle componentWriter = default;

        try
        {
            parentReader = new BlockingParentReadJob(parentStarted, releaseParent).Schedule(world, query: parentQuery);
            Assert.True(parentStarted.Wait(TimeSpan.FromSeconds(5)));

            componentWriter = world.Execution!.Scheduler!.Schedule(new SignalQueryJob(world, componentQuery, componentStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<ComponentB>()));

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
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        ConfigureJobs(new JobConfiguration { WorkerCount = 4, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity parent = world.Create();
        Entity child = world.Create();
        _ = world.Create(new ComponentB { Value = 2 });
        Hierarchy<Domain>.SetParent(world, child, parent);
        Query parentQuery = world.Query(
            new QueryDescription().WithStored<Parent<Domain>>());
        Query componentQuery = world.Query(
            new QueryDescription().WithStored<ComponentB>());
        using var parentStarted = new ManualResetEventSlim();
        using var releaseParent = new ManualResetEventSlim();
        releaseParent.WaitCallback = () => events.Enqueue("released");
        using var componentStarted = new ManualResetEventSlim();
        componentStarted.SetCallback = () => events.Enqueue("componentStarted");
        JobHandle parentWriter = default;
        JobHandle componentWriter = default;

        try
        {
            parentWriter = Jobs.Schedule(new BlockingParentWriteJob(parentStarted, releaseParent), world.Execution!.StructureWrite);
            Assert.True(parentStarted.Wait(TimeSpan.FromSeconds(5)));

            componentWriter = world.Execution!.Scheduler!.Schedule(new SignalQueryJob(world, componentQuery, componentStarted), ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<ComponentB>()));

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

        string[] sequence = events.ToArray();
        Assert.Contains("released", sequence);
        Assert.Contains("componentStarted", sequence);
        Assert.True(Array.LastIndexOf(sequence, "released") < Array.IndexOf(sequence, "componentStarted"));
    }

    [Fact]
    public void ProtectedParentColumnRequiresTheTypedHierarchyWriteEntry()
    {
        World world = CreateJobWorld();
        Entity parent = world.Create(), child = world.Create(new ComponentB { Value = 2 });
        Hierarchy<Domain>.SetParent(world, child, parent);
        Query relationshipQuery = world.Query<Parent<Domain>>();
        Assert.Throws<InvalidOperationException>(() =>
        {
            using WorldStructureWriteScope structure = world.OpenStructureWrite();
            using QueryScope rows = relationshipQuery.Open();
            foreach (QueryChunk chunk in rows) _ = chunk.Write<Parent<Domain>>();
        });
        Assert.Equal(1, world.Query<ComponentB>().Count);
        Assert.Equal(parent, Hierarchy<Domain>.GetParent(world, child));
    }

    [Fact]
    public void FaultedOrdinaryQuery_ReleasesBorrowAndSynchronousResourceOwner()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity entity = world.Create(new ComponentA { Value = 1 });
        Query query = world.Query(
            new QueryDescription().WithStored<ComponentA>());

        Assert.Throws<ProbeException>((Action)(() =>
        {
            using QueryScope rows = query.OpenRead();
            throw new ProbeException();
        }));

        // A leaked ordinary borrow rejects this structural mutation. The scheduled callback then
        // proves the synchronous component-resource owner was released as well.
        world.Add(entity, new ComponentB { Value = 2 });
        using var started = new ManualResetEventSlim();
        Jobs.Schedule(new SignalQueryJob(world, query, started),
            ((WorldJobs)world.Execution!).CollectAccess(new SystemAccesses().WriteRandom<ComponentA>())).Complete();
        Assert.True(started.IsSet);
    }

    [Fact]
    public void WritableRelationshipQuery_BodyFaultStillRollsBackCanonicalValue()
    {
        ConfigureJobs(new JobConfiguration { WorkerCount = 2, SafetyMode = JobSafetyMode.Checked, ManagedPayloadPolicy = ManagedPayloadPolicy.Allow });
        var world = CreateJobWorld();
        Entity oldParent = world.Create();
        Entity newParent = world.Create();
        Entity child = world.Create();
        Hierarchy<Domain>.SetParent(world, child, oldParent);
        Query query = world.Query(
            new QueryDescription().WithStored<Parent<Domain>>());

        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        Assert.Throws<ProbeException>((Action)(() =>
        {
            Hierarchy<Domain>.SetParentDeferred(candidate, child, newParent);
            throw new ProbeException();
        }));

        Assert.Equal(oldParent, Hierarchy<Domain>.GetParent(world, child));
        Assert.Equal(
            new[] { child },
            ReadChildren(world, oldParent));
        Assert.Empty(ReadChildren(world, newParent));
    }

    private readonly struct BlockingQueryJob : IJob
    {
        private readonly World _world;
        private readonly Query _query;
        private readonly ManualResetEventSlim _started;
        private readonly ManualResetEventSlim _release;

        internal BlockingQueryJob(
            World world,
            Query query,
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
            using QueryScope rows = _query.Open();
            _started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        }
    }

    private readonly struct SignalQueryJob : IJob
    {
        private readonly World _world;
        private readonly Query _query;
        private readonly ManualResetEventSlim _started;

        internal SignalQueryJob(
            World world,
            Query query,
            ManualResetEventSlim started)
        {
            _world = world;
            _query = query;
            _started = started;
        }

        public void Execute()
        {
            using QueryScope rows = _query.OpenRead();
            _started.Set();
        }
    }

    private readonly struct RepeatedQueryJob : IJob
    {
        private readonly World _world;
        private readonly Query _query;
        private readonly Entity _expected;

        internal RepeatedQueryJob(World world, Query query, Entity expected)
        {
            _world = world;
            _query = query;
            _expected = expected;
        }

        public void Execute()
        {
            for (int iteration = 0; iteration < 2_000; iteration++)
            {
                int count = 0;
                using QueryScope rows = _query.OpenRead();
                foreach (QueryChunk chunk in rows)
                    foreach (Entity entity in chunk.Entities)
                    {
                        Assert.Equal(_expected, entity);
                        count++;
                    }
                Assert.Equal(1, count);
            }
        }
    }

    private readonly partial struct BlockingParentReadJob : IJobChunk
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

        public void CollectAdditionalResources(ref JobResourceAccessBuilder resources)
        {
            resources.Write(_started);
            resources.Read(_release);
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

    private readonly struct BlockingParentWriteJob : IJob
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

        public void Execute()
        {
            _started.Set();
            if (!_release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Timed out waiting to release the parent query callback.");
        }
    }

    private static Entity[] ReadChildren(World world, Entity parent)
    {
        using var children = Hierarchy<Domain>.GetChildren(world, parent);
        return children.Entities.ToArray();
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

    private readonly struct Domain : IHierarchyDomain;
}
