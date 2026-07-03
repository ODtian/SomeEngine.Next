using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class QueryModelTests
{
    [Fact]
    public void PresenceOnly_All_DoesNotBumpChangeVersion()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var handle = world.Query(world.QueryDefinition().All<Position>());
        var plan = world.GetQueryState(handle);
        var archetype = Assert.Single(plan.Archetypes);
        var chunk = Assert.Single(archetype.Chunks);
        int column = archetype.Column(ComponentMetadata<Position>.Id);
        uint before = chunk.ChangeVersions[column];

        int count = 0;
        foreach (var row in world.RunQuery(handle).Rows)
        {
            Assert.True(row.Has<Position>());
            count++;
        }

        Assert.Equal(1, count);
        Assert.Equal(before, chunk.ChangeVersions[column]);
    }

    [Fact]
    public void ReadAccess_DoesNotBump_AndWriteAccess_BumpsOnceReached()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });

        var readHandle = world.Query(world.QueryDefinition().Read<Position>());
        var writeHandle = world.Query(world.QueryDefinition().ReadWrite<Position>());
        var archetype = Assert.Single(world.GetQueryState(readHandle).Archetypes);
        var chunk = Assert.Single(archetype.Chunks);
        int column = archetype.Column(ComponentMetadata<Position>.Id);

        uint beforeRead = chunk.ChangeVersions[column];
        foreach (var queryChunk in world.RunQuery(readHandle).Chunks)
            Assert.Equal(1, queryChunk.Read<Position>()[0].X);
        Assert.Equal(beforeRead, chunk.ChangeVersions[column]);

        uint beforeWrite = chunk.ChangeVersions[column];
        foreach (var queryChunk in world.RunQuery(writeHandle).Chunks)
            queryChunk.ReadWrite<Position>()[0].X = 7;
        Assert.True(chunk.ChangeVersions[column] > beforeWrite);
    }

    [Fact]
    public void ChangedFilter_UsesSuppliedBaseline()
    {
        var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });
        uint last = world.AcquireSystemTick();

        var handle = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Changed<Position>());

        Assert.Equal(0, CountRows(world.RunQuery(handle, last, world.CurrentTick)));

        world.Replace(entity, new Position { X = 3, Y = 4 });

        Assert.Equal(1, CountRows(world.RunQuery(handle, last, world.CurrentTick)));
    }

    [Fact]
    public void OptionalTerm_WidensMatchAndExposesAccessWhenPresent()
    {
        var world = new World();
        world.CreateEntity(new Position { X = 1, Y = 2 });
        var withHealth = world.CreateEntity(new Position { X = 3, Y = 4 });
        world.Add(withHealth, new Health { Value = 25 });

        var handle = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Optional<Health>(QueryAccess.Read));

        int rows = 0;
        int healthRows = 0;
        foreach (var row in world.RunQuery(handle).Rows)
        {
            rows++;
            if (row.TryRead<Health>(out var health))
            {
                healthRows++;
                Assert.Equal(25, health.Value);
            }
        }

        Assert.Equal(2, rows);
        Assert.Equal(1, healthRows);
    }

    [Fact]
    public void QueryCanMatchMoreThanSixteenTerms()
    {
        var world = new World();
        var entity = world.CreateEntity(new Extra01 { Value = 1 });
        world.Add(entity, new Extra02());
        world.Add(entity, new Extra03());
        world.Add(entity, new Extra04());
        world.Add(entity, new Extra05());
        world.Add(entity, new Extra06());
        world.Add(entity, new Extra07());
        world.Add(entity, new Extra08());
        world.Add(entity, new Extra09());
        world.Add(entity, new Extra10());
        world.Add(entity, new Extra11());
        world.Add(entity, new Extra12());
        world.Add(entity, new Extra13());
        world.Add(entity, new Extra14());
        world.Add(entity, new Extra15());
        world.Add(entity, new Extra16());
        world.Add(entity, new Extra17());

        var handle = world.Query(
            world.QueryDefinition()
                .Read<Extra01>()
                .All<Extra02>()
                .All<Extra03>()
                .All<Extra04>()
                .All<Extra05>()
                .All<Extra06>()
                .All<Extra07>()
                .All<Extra08>()
                .All<Extra09>()
                .All<Extra10>()
                .All<Extra11>()
                .All<Extra12>()
                .All<Extra13>()
                .All<Extra14>()
                .All<Extra15>()
                .All<Extra16>()
                .All<Extra17>());

        int rows = 0;
        foreach (var row in world.RunQuery(handle).Rows)
        {
            rows++;
            Assert.Equal(1, row.Read<Extra01>().Value);
        }

        Assert.Equal(1, rows);
    }

    [Fact]
    public void EquivalentSpecs_DedupeToSameHandle()
    {
        var world = new World();

        var first = world.Query(
            world.QueryDefinition()
                .All<Position>()
                .Read<Position>()
                .None<Velocity>());

        var second = world.Query(
            world.QueryDefinition()
                .None<Velocity>()
                .Read<Position>()
                .All<Position>());

        Assert.Equal(first, second);
    }

    [Fact]
    public void NewArchetype_IncrementallyUpdatesExistingPlan()
    {
        var world = new World();
        var handle = world.Query(world.QueryDefinition().All<Position>());
        Assert.Empty(world.GetQueryState(handle).Archetypes);

        world.CreateEntity(new Position { X = 1, Y = 2 });

        Assert.Single(world.GetQueryState(handle).Archetypes);
    }

    [Fact]
    public void DirectBufferElementQuery_IsRejectedWithClearError()
    {
        var world = new World();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            world.QueryDefinition().All<IntElement>().Build());

        Assert.Contains("cannot be queried directly", ex.Message);
    }

    [Fact]
    public void ReadBuffer_DoesNotExposeMutableBufferAccess()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<IntElement>(entity);
        world.GetBuffer<IntElement>(entity).Add(new IntElement { Value = 42 });

        var readHandle = world.Query(world.QueryDefinition().ReadBuffer<IntElement>());
        foreach (var row in world.RunQuery(readHandle).Rows)
        {
            Assert.Equal(42, row.ReadBuffer<IntElement>().Read(0).Value);
            Assert.Throws<InvalidOperationException>(
                () => row.Buffer<IntElement>().Add(new IntElement { Value = 99 }));
        }

        foreach (var chunk in world.RunQuery(readHandle).Chunks)
        {
            Assert.Equal(42, chunk.ReadBuffer<IntElement>(0).Read(0).Value);
            Assert.Throws<InvalidOperationException>(
                () => chunk.Buffer<IntElement>(0).Add(new IntElement { Value = 99 }));
        }
    }

    [Fact]
    public void PresenceOnlyBuffer_DoesNotExposeDataAccess()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<IntElement>(entity);

        var presenceHandle = world.Query(world.QueryDefinition().Buffer<IntElement>());
        foreach (var row in world.RunQuery(presenceHandle).Rows)
        {
            Assert.Throws<InvalidOperationException>(
                () => row.ReadBuffer<IntElement>().Read(0));
            Assert.Throws<InvalidOperationException>(
                () => row.Buffer<IntElement>().Add(new IntElement { Value = 99 }));
        }
    }

    [Fact]
    public void SharedRuntimeFilter_DoesNotCreateSeparateStaticPlanKeys()
    {
        var world = new World();
        var first = world.CreateEntity(new Position { X = 1, Y = 1 });
        var second = world.CreateEntity(new Position { X = 2, Y = 2 });
        world.AddShared(first, new SceneId { Value = 10 });
        world.AddShared(second, new SceneId { Value = 20 });

        var handleA = world.Query(
            world.QueryDefinition()
                .Read<Position>()
                .Shared<SceneId>());
        var handleB = world.Query(
            world.QueryDefinition()
                .Shared<SceneId>()
                .Read<Position>());

        Assert.Equal(handleA, handleB);

        var results = new List<float>();
        foreach (var row in world.RunQuery(handleA).RowsWithShared(new SceneId { Value = 10 }))
            results.Add(row.Read<Position>().X);

        Assert.Equal([1f], results);
    }

    private static int CountRows(QueryCursor run)
    {
        int count = 0;
        foreach (var _ in run.Rows)
            count++;
        return count;
    }
}

public struct Extra01 : SomeEngine.ECS.Components.IComponent { public int Value; }
public struct Extra02 : SomeEngine.ECS.Components.IComponent { }
public struct Extra03 : SomeEngine.ECS.Components.IComponent { }
public struct Extra04 : SomeEngine.ECS.Components.IComponent { }
public struct Extra05 : SomeEngine.ECS.Components.IComponent { }
public struct Extra06 : SomeEngine.ECS.Components.IComponent { }
public struct Extra07 : SomeEngine.ECS.Components.IComponent { }
public struct Extra08 : SomeEngine.ECS.Components.IComponent { }
public struct Extra09 : SomeEngine.ECS.Components.IComponent { }
public struct Extra10 : SomeEngine.ECS.Components.IComponent { }
public struct Extra11 : SomeEngine.ECS.Components.IComponent { }
public struct Extra12 : SomeEngine.ECS.Components.IComponent { }
public struct Extra13 : SomeEngine.ECS.Components.IComponent { }
public struct Extra14 : SomeEngine.ECS.Components.IComponent { }
public struct Extra15 : SomeEngine.ECS.Components.IComponent { }
public struct Extra16 : SomeEngine.ECS.Components.IComponent { }
public struct Extra17 : SomeEngine.ECS.Components.IComponent { }
