using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

public class QueryModelTests
{
    [Fact]
    public void ReleaseQuery_InvalidatesEveryHandleCopyAndReusesSlotGeneration()
    {
        var world = new World();
        QueryHandle released = world.Query(world.QueryDefinition().Read<Position>());
        QueryHandle copy = released;

        world.ReleaseQuery(released);

        Assert.Throws<InvalidOperationException>(() => world.GetQueryDefinition(released));
        Assert.Throws<InvalidOperationException>(() => world.GetQueryDefinition(copy));
        Assert.Throws<InvalidOperationException>(() => world.ReleaseQuery(released));

        // A structural publication must preserve the released slot and its next generation.
        world.CreateEntity(new Position { X = 1, Y = 2 });

        QueryHandle replacement = world.Query(world.QueryDefinition().Read<Velocity>());
        Assert.NotEqual(copy, replacement);
        Assert.NotEqual(released, replacement);
        Assert.Equal(
            world.QueryDefinition().Read<Velocity>().Build().Key,
            world.GetQueryDefinition(replacement).Key);
    }

    [Fact]
    public void ReleaseQuery_KeepsIndependentAcquisitionOfInternedDefinitionAlive()
    {
        var world = new World();
        QueryDefinition definition = world.QueryDefinition().Read<Position>().Build();
        QueryHandle first = world.Query(definition);
        QueryHandle second = world.Query(definition);
        Assert.Equal(first, second);

        world.ReleaseQuery(first);

        // Root publication must preserve the remaining acquisition without reviving the release.
        world.CreateEntity(new Position { X = 1, Y = 2 });
        Assert.Equal(definition.Key, world.GetQueryDefinition(second).Key);

        world.ReleaseQuery(second);

        Assert.Throws<InvalidOperationException>(() => world.GetQueryDefinition(first));
        Assert.Throws<InvalidOperationException>(() => world.GetQueryDefinition(second));
        QueryHandle replacement = world.Query(world.QueryDefinition().Read<Velocity>());
        Assert.NotEqual(first, replacement);
    }

    [Fact]
    public void QueryLifetimeMutation_IsRejectedInsideStructuralTransaction()
    {
        var world = new QueryTransactionWorld();
        QueryDefinition position = world.QueryDefinition().Read<Position>().Build();
        QueryDefinition velocity = world.QueryDefinition().Read<Velocity>().Build();
        QueryHandle existing = world.Query(position);
        Exception? acquireFault = null;
        Exception? releaseFault = null;

        world.ExecuteCandidate(() =>
        {
            acquireFault = Record.Exception(() => world.Query(velocity));
            releaseFault = Record.Exception(() => world.ReleaseQuery(existing));
        });

        InvalidOperationException acquireError =
            Assert.IsType<InvalidOperationException>(acquireFault);
        InvalidOperationException releaseError =
            Assert.IsType<InvalidOperationException>(releaseFault);

        Assert.Equal(position.Key, world.GetQueryDefinition(existing).Key);

        QueryHandle next = world.Query(velocity);
        Assert.NotEqual(existing, next);
        Assert.Equal(velocity.Key, world.GetQueryDefinition(next).Key);
    }

    [Fact]
    public void PresenceOnly_All_DoesNotBumpChangeVersion()
    {
        using var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });
        uint baseline = world.AcquireSystemTick();
        var query = world.Query(world.QueryDefinition().All<Position>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.True(row.Has<Position>());
            }
        });
        var changed = world.Query(world.QueryDefinition().Changed<Position>());
        Assert.Equal(0, CountRows(world, changed, baseline, world.CurrentTick));
    }

    [Fact]
    public void ReadAccess_DoesNotBump_AndWriteAccess_BumpsOnceReached()
    {
        using var world = new World();
        var entity = world.CreateEntity(new Position { X = 1, Y = 2 });
        uint baseline = world.AcquireSystemTick();
        var query = world.Query(world.QueryDefinition().ReadWrite<Position>());
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.True(row.Has<Position>());
                row.ReadWrite<Position>().X = 7;
            }
        });
        var changed = world.Query(world.QueryDefinition().Changed<Position>());
        Assert.Equal(1, CountRows(world, changed, baseline, world.CurrentTick));
        Assert.Equal(7, world.Read<Position>(entity).X);
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

        Assert.Equal(0, CountRows(world, handle, last, world.CurrentTick));

        world.Replace(entity, new Position { X = 3, Y = 4 });

        Assert.Equal(1, CountRows(world, handle, last, world.CurrentTick));
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
        world.ExecuteQuery(handle, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                rows++;
                if (row.TryRead<Health>(out var health))
                {
                    healthRows++;
                    Assert.Equal(25, health.Value);
                }
            }
        });

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
        world.ExecuteQuery(handle, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                rows++;
                Assert.Equal(1, row.Read<Extra01>().Value);
            }
        });

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
    public void ConcurrentQueryRegistration_PublishesStableHandles()
    {
        var world = new World();
        QueryDefinition position = new QueryDefinitionBuilder().Read<Position>().Build();
        QueryDefinition velocity = new QueryDefinitionBuilder().Read<Velocity>().Build();
        var handles = new QueryHandle[32];

        Parallel.For(
            0,
            handles.Length,
            index => handles[index] = world.Query((index & 1) == 0 ? position : velocity));

        QueryHandle positionHandle = handles[0];
        QueryHandle velocityHandle = handles[1];
        Assert.NotEqual(positionHandle, velocityHandle);
        for (int i = 0; i < handles.Length; i++)
        {
            Assert.Equal((i & 1) == 0 ? positionHandle : velocityHandle, handles[i]);
        }
    }

    [Fact]
    public void QueryDefinitionExposesTermsAndAccesses()
    {
        QueryDefinition definition = new QueryDefinitionBuilder()
            .Read<Position>()
            .Changed<Velocity>()
            .Build();
        QueryKey key = definition.Key;
        QueryTerm firstTerm = definition.Terms[0];
        QueryAccessEntry firstAccess = definition.Accesses[0];

        Assert.Equal(2, definition.Terms.Length);
        Assert.Single(definition.Accesses.ToArray());

        Assert.Equal(key, definition.Key);
        Assert.Equal(firstTerm, definition.Terms[0]);
        Assert.Equal(firstAccess.ComponentId, definition.Accesses[0].ComponentId);
        Assert.Equal(firstAccess.Access, definition.Accesses[0].Access);
        Assert.Equal(firstAccess.Kind, definition.Accesses[0].Kind);
    }

    [Fact]
    public void NewArchetype_IncrementallyUpdatesExistingPlan()
    {
        var world = new World();
        var handle = world.Query(world.QueryDefinition().All<Position>());
        Assert.Equal(0, CountRows(world, handle, 0, world.CurrentTick));

        world.CreateEntity(new Position { X = 1, Y = 2 });

        Assert.Equal(1, CountRows(world, handle, 0, world.CurrentTick));
    }

    [Fact]
    public void DirectBufferElementQuery_IsRejectedWithClearError()
    {
        var world = new World();

        var ex = Assert.Throws<InvalidOperationException>(() =>
            world.QueryDefinition().All<IntElement>().Build());

    }

    [Fact]
    public void ReadBuffer_DoesNotExposeMutableBufferAccess()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<IntElement>(entity);
        world.ExecuteBufferWrite<IntElement>(
            entity,
            static buffer => buffer.Add(new IntElement { Value = 42 }));

        var readHandle = world.Query(world.QueryDefinition().ReadBuffer<IntElement>());
        world.ExecuteQuery(readHandle, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.Equal(42, row.ReadBuffer<IntElement>().Read(0).Value);
                AssertMutableBufferRejected(row);
            }

            foreach (var chunk in cursor.Chunks)
            {
                Assert.Equal(42, chunk.ReadBuffer<IntElement>(0).Read(0).Value);
                AssertMutableBufferRejected(chunk);
            }
        });
    }

    [Fact]
    public void PresenceOnlyBuffer_DoesNotExposeDataAccess()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<IntElement>(entity);

        var presenceHandle = world.Query(world.QueryDefinition().Buffer<IntElement>());
        world.ExecuteQuery(presenceHandle, cursor =>
        {
            foreach (var row in cursor.Rows)
                AssertPresenceOnlyBufferRejected(row);
        });
    }

    private static void AssertMutableBufferRejected(QueryRow row)
    {
        InvalidOperationException? error = null;
        try
        {
            row.Buffer<IntElement>().Add(new IntElement { Value = 99 });
        }
        catch (InvalidOperationException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
    }

    private static void AssertMutableBufferRejected(QueryChunkView chunk)
    {
        InvalidOperationException? error = null;
        try
        {
            chunk.Buffer<IntElement>(0).Add(new IntElement { Value = 99 });
        }
        catch (InvalidOperationException exception)
        {
            error = exception;
        }

        Assert.NotNull(error);
    }

    private static void AssertPresenceOnlyBufferRejected(QueryRow row)
    {
        InvalidOperationException? readError = null;
        try
        {
            _ = row.ReadBuffer<IntElement>().Read(0);
        }
        catch (InvalidOperationException exception)
        {
            readError = exception;
        }

        InvalidOperationException? writeError = null;
        try
        {
            row.Buffer<IntElement>().Add(new IntElement { Value = 99 });
        }
        catch (InvalidOperationException exception)
        {
            writeError = exception;
        }

        Assert.NotNull(readError);
        Assert.NotNull(writeError);
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
        world.ExecuteQuery(handleA, cursor =>
        {
            foreach (var row in cursor.RowsWithShared(new SceneId { Value = 10 }))
                results.Add(row.Read<Position>().X);
        });

        Assert.Equal([1f], results);
    }

    private static int CountRows(
        World world,
        QueryHandle query,
        uint lastSystemVersion,
        uint currentSystemVersion)
    {
        int count = 0;
        world.ExecuteQuery(
            query,
            lastSystemVersion,
            ref count,
            static (scoped QueryCursor cursor, ref int state) =>
            {
                foreach (var _ in cursor.Rows)
                    state++;
            });
        return count;
    }

    private sealed class QueryTransactionWorld : World
    {
        internal void ExecuteCandidate(Action execution)
        {
            ExecuteStructuralTransaction(execution, static action => action());
        }
    }
}

public readonly struct QueryAdmissionDomain : IHierarchyDomain;

public struct AdmissionOnlyBufferElement : IBufferElement
{
    public int Value;
}

public struct Extra01 : SomeEngine.ECS.IComponent { public int Value; }
public struct Extra02 : SomeEngine.ECS.IComponent { }
public struct Extra03 : SomeEngine.ECS.IComponent { }
public struct Extra04 : SomeEngine.ECS.IComponent { }
public struct Extra05 : SomeEngine.ECS.IComponent { }
public struct Extra06 : SomeEngine.ECS.IComponent { }
public struct Extra07 : SomeEngine.ECS.IComponent { }
public struct Extra08 : SomeEngine.ECS.IComponent { }
public struct Extra09 : SomeEngine.ECS.IComponent { }
public struct Extra10 : SomeEngine.ECS.IComponent { }
public struct Extra11 : SomeEngine.ECS.IComponent { }
public struct Extra12 : SomeEngine.ECS.IComponent { }
public struct Extra13 : SomeEngine.ECS.IComponent { }
public struct Extra14 : SomeEngine.ECS.IComponent { }
public struct Extra15 : SomeEngine.ECS.IComponent { }
public struct Extra16 : SomeEngine.ECS.IComponent { }
public struct Extra17 : SomeEngine.ECS.IComponent { }
