using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Systems;
using SomeEngine.Job;

namespace SomeEngine.ECS.Tests;

public class QueryModelTests
{
    [Fact]
    public void QueryCopiesKeepTheirOwnObservationWindows()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 1 });
        Query allChanges = world.Query(new QueryDescription().WithChanged<Position>(0));
        Query laterChanges = allChanges.Since(world.Version);
        Assert.Equal(1, world.Query(new QueryDescription().WithChanged<Position>(0)).Count);
        Assert.Equal(0, laterChanges.Count);
        world.Set(entity, new Position { X = 2 });
        Assert.Equal(1, laterChanges.Count);
        laterChanges = laterChanges.Since(world.Version);
        Assert.Equal(0, laterChanges.Count);
        Assert.Equal(1, world.Query(new QueryDescription().WithChanged<Position>(0)).Count);
    }

    [Fact]
    public void RepeatedQueriesShareMembershipAndKeepIndependentWindows()
    {
        using var world = new World();
        Entity entity = world.Create(new Position());
        var description = new QueryDescription().WithChanged<Position>(0);
        Query first = world.Query(description);
        Query second = world.Query(description);
        ulong membership = first.MembershipVersion;
        first = first.Since(world.Version);
        Assert.Equal(0, first.Count);
        Assert.Equal(1, second.Count);
        world.Set(entity, new Position { X = 3 });
        Assert.Equal(membership, first.MembershipVersion);
        Assert.Equal(first.MembershipVersion, second.MembershipVersion);
        Assert.Equal(1, first.Count);
    }

    [Fact]
    public void CandidateQueriesReadTheirOwnWorldAndKeepSourceMembership()
    {
        using var world = new World();
        Entity entity = world.Create(new Position());
        Query source = world.Query<Position>();
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        candidate.Add(entity, new Velocity());
        Assert.Equal(1, source.Count);
        Assert.Equal(0, world.Query<Velocity>().Count);
        Assert.Equal(1, candidate.Query<Velocity>().Count);
    }

    [Fact]
    public void PresenceOnly_All_DoesNotBumpChangeVersion()
    {
        using var world = new World();
        world.Create(new Position { X = 1, Y = 2 });
        ulong baseline = world.Version;
        using (QueryScope scope = world.Query<Position>().OpenRead())
            foreach (QueryChunk chunk in scope) Assert.True(chunk.Has<Position>());
        Assert.Equal(0, world.Query(new QueryDescription().WithChanged<Position>(baseline)).Count);
    }

    [Fact]
    public void ReadAccess_DoesNotBump_AndWriteAccess_BumpsOnceReached()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 1, Y = 2 });
        ulong baseline = world.Version;
        using (QueryScope scope = world.Query<Position>().Open())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++) chunk.Write<Position>(row).X = 7;
        Assert.Equal(1, world.Query(new QueryDescription().WithChanged<Position>(baseline)).Count);
        Assert.Equal(7, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ChangedFilter_UsesSuppliedBaseline()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 1, Y = 2 });
        ulong last = world.Version;
        Query query = world.Query(new QueryDescription().WithChanged<Position>(last));
        Assert.Equal(0, query.Count);
        world.Set(entity, new Position { X = 3, Y = 4 });
        Assert.Equal(1, query.Count);
    }

    [Fact]
    public void OptionalTerm_WidensMatchAndExposesAccessWhenPresent()
    {
        using var world = new World();
        world.Create(new Position { X = 1 });
        Entity withHealth = world.Create(new Position { X = 3 });
        world.Add(withHealth, new Health { Value = 25 });
        Query query = world.Query(new QueryDescription().WithStored<Position>().Optional<Health>());
        int rows = 0, healthRows = 0;
        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++)
                {
                    rows++;
                    OptionalComponent<Health> health = chunk.Optional<Health>(row);
                    if (health.IsPresent) { healthRows++; Assert.Equal(25, health.Value.Value); }
                }
        Assert.Equal(2, rows);
        Assert.Equal(1, healthRows);
    }

    [Fact]
    public void QueryCanMatchMoreThanSixteenTerms()
    {
        using var world = new World();
        Entity entity = world.Create(new Extra01 { Value = 1 });
        world.Add(entity, new Extra02()); world.Add(entity, new Extra03()); world.Add(entity, new Extra04());
        world.Add(entity, new Extra05()); world.Add(entity, new Extra06()); world.Add(entity, new Extra07());
        world.Add(entity, new Extra08()); world.Add(entity, new Extra09()); world.Add(entity, new Extra10());
        world.Add(entity, new Extra11()); world.Add(entity, new Extra12()); world.Add(entity, new Extra13());
        world.Add(entity, new Extra14()); world.Add(entity, new Extra15()); world.Add(entity, new Extra16());
        world.Add(entity, new Extra17());
        Query query = world.Query(new QueryDescription().WithStored<Extra01>().WithStored<Extra02>().WithStored<Extra03>()
            .WithStored<Extra04>().WithStored<Extra05>().WithStored<Extra06>().WithStored<Extra07>().WithStored<Extra08>()
            .WithStored<Extra09>().WithStored<Extra10>().WithStored<Extra11>().WithStored<Extra12>().WithStored<Extra13>()
            .WithStored<Extra14>().WithStored<Extra15>().WithStored<Extra16>().WithStored<Extra17>());
        using QueryScope scope = query.OpenRead();
        Assert.True(scope.Contains(entity));
        Assert.Equal(1, query.Count);
        foreach (QueryChunk chunk in scope) Assert.Equal(1, chunk.Read<Extra01>()[0].Value);
    }

    [Fact]
    public void EquivalentSpecs_DedupeToSameHandle()
    {
        using var world = new World();
        Query first = world.Query(new QueryDescription().WithStored<Position>().WithStored<Position>().Without<Velocity>());
        Query second = world.Query(new QueryDescription().Without<Velocity>().WithStored<Position>());
        Assert.Equal(first, second);
    }

    [Fact]
    public void ConcurrentQueryRegistration_PublishesSharedQueryValues()
    {
        using var world = new World();
        var position = new QueryDescription().WithStored<Position>();
        var velocity = new QueryDescription().WithStored<Velocity>();
        var queries = new Query[32];
        Parallel.For(0, queries.Length, index => queries[index] = world.Query((index & 1) == 0 ? position : velocity));
        Assert.NotEqual(queries[0], queries[1]);
        for (int index = 0; index < queries.Length; index++) Assert.Equal(queries[index & 1], queries[index]);
    }

    [Fact]
    public void QueryDescriptionPreservesItsCriteriaAfterFurtherComposition()
    {
        using var world = new World();
        var description = new QueryDescription().WithStored<Position>();
        Query positions = world.Query(description);
        Query moving = world.Query(description.WithChanged<Velocity>(0));
        world.Create(new Position());
        world.Create(new Position(), new Velocity());
        Assert.Equal(2, positions.Count);
        Assert.Equal(1, moving.Count);
    }

    [Fact]
    public void NewLayoutsUpdateExistingQueries()
    {
        using var world = new World();
        Query query = world.Query<Position>();
        Assert.Equal(0, query.Count);
        world.Create(new Position { X = 1, Y = 2 });
        Assert.Equal(1, query.Count);
    }

    [Fact]
    public void OrdinaryValuesAndBufferColumnsRemainDistinct()
    {
        using var world = new World();
        Entity value = world.Create(new IntElement { Value = 3 });
        Entity buffer = world.Create();
        world.AddBuffer<IntElement>(buffer);
        using QueryScope values = world.Query<IntElement>().OpenRead();
        Assert.True(values.Contains(value));
        Assert.False(values.Contains(buffer));
        Assert.Equal(1, world.Query<Buffer<IntElement>>().Count);
    }

    [Fact]
    public void ReadBuffer_DoesNotExposeMutableBufferAccess()
    {
        using var scheduler = new Scheduler(new JobConfiguration { WorkerCount = 0 });
        using var world = new World(execution: new WorldJobs(scheduler));
        Entity entity = world.Create();
        world.AddBuffer<IntElement>(entity);
        var buffer = world.GetBuffer<IntElement>(entity);
        try { buffer.Add(new IntElement { Value = 42 }); }
        finally { buffer.Dispose(); }
        using QueryScope scope = world.Query<Buffer<IntElement>>().OpenRead();
        foreach (QueryChunk chunk in scope)
        {
            using ReadOnlyBuffer<IntElement> read = chunk.ReadBuffer<IntElement>(0);
            Assert.Equal(42, read[0].Value);
            Assert.ThrowsAny<InvalidOperationException>(() => chunk.GetBuffer<IntElement>(0).Add(new IntElement { Value = 99 }));
        }
    }

    [Fact]
    public void BufferPresenceQueriesRetainTheirSelectedIdentity()
    {
        using var world = new World();
        Entity entity = world.Create();
        world.AddBuffer<IntElement>(entity);
        using QueryScope scope = world.Query(new QueryDescription().WithStored<Buffer<IntElement>>()).OpenRead();
        Assert.True(scope.Contains(entity));
        foreach (QueryChunk chunk in scope) Assert.True(chunk.Has<Buffer<IntElement>>());
    }

    [Fact]
    public void SharedValuesSelectTheirOwnMatchingEntities()
    {
        using var world = new World();
        SharedId<SceneId> firstId = world.InternShared(new SceneId { Value = 10 });
        SharedId<SceneId> secondId = world.InternShared(new SceneId { Value = 20 });
        Entity first = world.Create(new Position { X = 1 }, new SceneId { Value = 10 });
        Entity second = world.Create(new Position { X = 2 }, new SceneId { Value = 20 });
        Query firstQuery = world.Query(new QueryDescription().WithStored<Position>().WithShared(firstId));
        Query secondQuery = world.Query(new QueryDescription().WithShared(secondId).WithStored<Position>());
        using QueryScope firstMembers = firstQuery.OpenRead();
        using QueryScope secondMembers = secondQuery.OpenRead();
        Assert.True(firstMembers.Contains(first));
        Assert.False(firstMembers.Contains(second));
        Assert.True(secondMembers.Contains(second));
        Assert.False(secondMembers.Contains(first));
    }
}

public readonly struct QueryAdmissionDomain : IHierarchyDomain;

public struct AdmissionOnlyBufferElement : IBufferElement, IAddedComponent
{
    public int Value;
}

public struct Extra01 : SomeEngine.ECS.IAddedComponent { public int Value; }
public struct Extra02 : SomeEngine.ECS.IAddedComponent { }
public struct Extra03 : SomeEngine.ECS.IAddedComponent { }
public struct Extra04 : SomeEngine.ECS.IAddedComponent { }
public struct Extra05 : SomeEngine.ECS.IAddedComponent { }
public struct Extra06 : SomeEngine.ECS.IAddedComponent { }
public struct Extra07 : SomeEngine.ECS.IAddedComponent { }
public struct Extra08 : SomeEngine.ECS.IAddedComponent { }
public struct Extra09 : SomeEngine.ECS.IAddedComponent { }
public struct Extra10 : SomeEngine.ECS.IAddedComponent { }
public struct Extra11 : SomeEngine.ECS.IAddedComponent { }
public struct Extra12 : SomeEngine.ECS.IAddedComponent { }
public struct Extra13 : SomeEngine.ECS.IAddedComponent { }
public struct Extra14 : SomeEngine.ECS.IAddedComponent { }
public struct Extra15 : SomeEngine.ECS.IAddedComponent { }
public struct Extra16 : SomeEngine.ECS.IAddedComponent { }
public struct Extra17 : SomeEngine.ECS.IAddedComponent { }
