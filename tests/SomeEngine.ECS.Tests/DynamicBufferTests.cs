using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

// 测试用 buffer 元素类型
public struct IntElement : SomeEngine.ECS.Components.IBufferElement
{
    public int Value;
}

public struct FloatElement : SomeEngine.ECS.Components.IBufferElement
{
    public float X;
    public float Y;
}

[BufferCapacity(2)]
public struct SmallInlineElement : SomeEngine.ECS.Components.IBufferElement
{
    public int Value;
}

public struct BufferMoveMarker : SomeEngine.ECS.IComponent
{
    public int Value;
}

public class DynamicBufferTests
{
    // ——————————————————————————————————————————————————
    // ComponentMetadata 检测
    // ——————————————————————————————————————————————————

    [Fact]
    public void BufferElement_IsNotStandaloneComponent()
    {
        var ex = Assert.Throws<TypeInitializationException>(() => ComponentMetadata<IntElement>.Storage);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }


    // ——————————————————————————————————————————————————
    // World API
    // ——————————————————————————————————————————————————

    [Fact]
    public void WorldAddBufferMakesBufferAvailable()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<IntElement>(entity);

        Assert.True(world.HasBuffer<IntElement>(entity));
        Assert.Throws<InvalidOperationException>(() => world.AddBuffer<IntElement>(entity));
    }

    [Fact]
    public void World_ExecuteBufferWriteAndRead_BorrowsWithinCallbacks()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<IntElement>(entity);

        world.ExecuteBufferWrite<IntElement>(entity, static buffer =>
        {
            buffer.Add(new IntElement { Value = 100 });
            buffer.Add(new IntElement { Value = 200 });
        });

        Assert.Equal([100, 200], SnapshotValues<IntElement>(world, entity).Select(x => x.Value));
    }

    [Fact]
    public void World_BufferAsSpan_ReturnsFullOverflowSpan()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<SmallInlineElement>(entity);
        world.ExecuteBufferWrite<SmallInlineElement>(entity, static buffer =>
        {
            buffer.Add(new SmallInlineElement { Value = 1 });
            buffer.Add(new SmallInlineElement { Value = 2 });
            buffer.Add(new SmallInlineElement { Value = 3 });

            Span<SmallInlineElement> span = buffer.AsSpan();
            Assert.Equal(3, span.Length);
            Assert.Equal([1, 2, 3], span.ToArray().Select(x => x.Value).ToArray());
            span[0] = new SmallInlineElement { Value = 10 };
        });

        Assert.Equal(10, SnapshotValues<SmallInlineElement>(world, entity)[0].Value);
    }

    [Fact]
    public void World_BufferReplaceWith_ReplacesInlineAndOverflowContents()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<SmallInlineElement>(entity);
        world.ExecuteBufferWrite<SmallInlineElement>(entity, static buffer =>
        {
            buffer.ReplaceWith(
            [
                new SmallInlineElement { Value = 1 },
                new SmallInlineElement { Value = 2 },
                new SmallInlineElement { Value = 3 },
            ]);

            Assert.Equal([1, 2, 3], buffer.AsSpan().ToArray().Select(x => x.Value).ToArray());

            buffer.ReplaceWith([new SmallInlineElement { Value = 9 }]);
            Assert.Equal(1, buffer.Count);
            Assert.Equal(9, buffer.Read(0).Value);

            buffer.ReplaceWith(ReadOnlySpan<SmallInlineElement>.Empty);
            Assert.Equal(0, buffer.Count);
            Assert.Empty(buffer.AsSpan().ToArray());
        });
    }

    [Fact]
    public void World_BufferReadOnlyAccess_DoesNotBumpChangeVersion()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<SmallInlineElement>(entity);

        world.ExecuteBufferWrite<SmallInlineElement>(
            entity,
            static buffer => buffer.Add(new SmallInlineElement { Value = 1 }));

        uint lastTick = world.AcquireSystemTick();

        world.ExecuteBufferRead<SmallInlineElement>(entity, static buffer =>
        {
            Assert.Equal(1, buffer.Read(0).Value);
            Assert.Equal(1, buffer.AsSpan()[0].Value);
        });

        var query = world.Query(world.QueryDefinition().ChangedBuffer<SmallInlineElement>());
        int rows = 0;
        world.ExecuteQuery(query, lastTick, world.CurrentTick, cursor =>
        {
            foreach (var _ in cursor.Rows)
                rows++;
        });

        Assert.Equal(0, rows);
    }

    [Fact]
    public void World_BufferWriteAccess_BumpsChangeVersion()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<SmallInlineElement>(entity);

        uint lastTick = world.AcquireSystemTick();

        world.ExecuteBufferWrite<SmallInlineElement>(
            entity,
            static buffer => buffer.Add(new SmallInlineElement { Value = 5 }));

        var query = world.Query(world.QueryDefinition().ChangedBuffer<SmallInlineElement>());
        int rows = 0;
        world.ExecuteQuery(query, lastTick, world.CurrentTick, cursor =>
        {
            foreach (var _ in cursor.Rows)
                rows++;
        });

        Assert.Equal(1, rows);
    }

    [Fact]
    public void ChangedBuffer_ReturnsOnlyWrittenInlineRowFromSharedChunk()
    {
        var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        world.AddBuffer<SmallInlineElement>(first);
        world.AddBuffer<SmallInlineElement>(second);


        uint lastTick = world.AcquireSystemTick();
        world.ExecuteBufferWrite<SmallInlineElement>(
            second,
            static buffer => buffer.Add(new SmallInlineElement { Value = 5 }));

        var query = world.Query(
            world.QueryDefinition().ChangedBuffer<SmallInlineElement>());
        var changed = new List<Entity>();
        world.ExecuteQuery(query, lastTick, world.CurrentTick, cursor =>
        {
            foreach (var row in cursor.Rows)
                changed.Add(row.Entity);
        });

        Assert.Equal([second], changed);
    }

    [Fact]
    public void ChangedBuffer_ReturnsOnlyWrittenOverflowRowFromSharedChunk()
    {
        var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        world.AddBuffer<SmallInlineElement>(first);
        world.AddBuffer<SmallInlineElement>(second);

        world.ExecuteBufferWrite<SmallInlineElement>(first, FillOverflowBuffer);
        world.ExecuteBufferWrite<SmallInlineElement>(second, FillOverflowBuffer);


        uint lastTick = world.AcquireSystemTick();
        world.ExecuteBufferWrite<SmallInlineElement>(second, static buffer =>
        {
            buffer[2] = new SmallInlineElement { Value = 30 };
        });

        var query = world.Query(
            world.QueryDefinition().ChangedBuffer<SmallInlineElement>());
        var changed = new List<Entity>();
        world.ExecuteQuery(query, lastTick, world.CurrentTick, cursor =>
        {
            foreach (var row in cursor.Rows)
                changed.Add(row.Entity);
        });

        Assert.Equal([second], changed);
    }

    [Fact]
    public void OptionalBufferSnapshot_ReportsTheExactChangedRow()
    {
        var world = new World();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        world.AddBuffer<SmallInlineElement>(first);
        world.AddBuffer<SmallInlineElement>(second);
        uint lastTick = world.AcquireSystemTick();
        world.ExecuteBufferWrite<SmallInlineElement>(
            second,
            static buffer => buffer.Add(new SmallInlineElement { Value = 17 }));
        QueryHandle query = world.Query(
            world.QueryDefinition().OptionalBuffer<SmallInlineElement>(QueryAccess.Read));
        var changed = new List<Entity>();

        world.ExecuteReadSnapshot(query, lastTick, ref changed, static (cursor, ref state) =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                if (!chunk.HasBuffer<SmallInlineElement>() ||
                    !chunk.HasBufferChangedSinceLastSystemVersion<SmallInlineElement>())
                {
                    continue;
                }

                ReadOnlySpan<Entity> entities = chunk.Entities;
                for (int row = 0; row < entities.Length; row++)
                {
                    if (chunk.RowBufferChangedSinceLastSystemVersion<SmallInlineElement>(row))
                        state.Add(entities[row]);
                }
            }
        });

        Assert.Equal([second], changed);
        world.ReleaseQuery(query);
    }

    [Fact]
    public void ChangedBuffer_RowVersionSurvivesStructuralRowMove()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<SmallInlineElement>(entity);
        uint lastTick = world.AcquireSystemTick();

        world.ExecuteBufferWrite<SmallInlineElement>(
            entity,
            static buffer => buffer.Add(new SmallInlineElement { Value = 5 }));
        world.Add(entity, new BufferMoveMarker { Value = 1 });

        var query = world.Query(
            world.QueryDefinition().ChangedBuffer<SmallInlineElement>());
        int rows = 0;
        world.ExecuteQuery(query, lastTick, world.CurrentTick, cursor =>
        {
            foreach (var _ in cursor.Rows)
                rows++;
        });

        Assert.Equal(1, rows);
    }

    private static void FillOverflowBuffer(DynamicBuffer<SmallInlineElement> buffer)
    {
        buffer.Add(new SmallInlineElement { Value = 1 });
        buffer.Add(new SmallInlineElement { Value = 2 });
        buffer.Add(new SmallInlineElement { Value = 3 });
    }





    [Fact]
    public void QueryWithBufferFindsMatchingEntities()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<IntElement>(entity);

        var query = world.Query(world.QueryDefinition().Buffer<IntElement>());
        int rows = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var _ in cursor.Rows)
                rows++;
        });

        Assert.Equal(1, rows);
    }

    [Fact]
    public void Query_DirectBufferElement_Throws()
    {
        var world = new World();

        Assert.Throws<InvalidOperationException>(() => world.QueryDefinition().All<IntElement>());
        Assert.Throws<InvalidOperationException>(() => world.QueryDefinition().Read<IntElement>());
    }

    [Fact]
    public void World_BufferSurvivesMigration()
    {
        var world = new World();
        var entity = world.CreateEntity<Position>(new Position { X = 1, Y = 2 });

        world.AddBuffer<IntElement>(entity);
        world.ExecuteBufferWrite<IntElement>(
            entity,
            static buffer => buffer.Add(new IntElement { Value = 42 }));

        // 添加另一个组件触发迁移
        world.Add(entity, new Health { Value = 100 });

        // buffer 数据应该在迁移后保持
        Assert.Equal([42], SnapshotValues<IntElement>(world, entity).Select(x => x.Value));

        // 原组件也应保持
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(100, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void World_BufferOverflowSurvivesMigration()
    {
        var world = new World();
        var entity = world.CreateEntity<Position>(new Position { X = 1, Y = 2 });

        world.AddBuffer<SmallInlineElement>(entity);
        world.ExecuteBufferWrite<SmallInlineElement>(entity, static buffer =>
        {
            buffer.Add(new SmallInlineElement { Value = 1 });
            buffer.Add(new SmallInlineElement { Value = 2 });
            buffer.Add(new SmallInlineElement { Value = 3 });
            buffer.Add(new SmallInlineElement { Value = 4 });
        });

        world.Add(entity, new Health { Value = 100 });

        Assert.Equal(
            [1, 2, 3, 4],
            SnapshotValues<SmallInlineElement>(world, entity).Select(x => x.Value));
        Assert.Equal(100, world.Read<Health>(entity).Value);
    }

    [Fact]
    public void World_MultipleEntitiesWithBuffer()
    {
        var world = new World();
        var e1 = world.CreateEntity();
        var e2 = world.CreateEntity();

        world.AddBuffer<IntElement>(e1);
        world.AddBuffer<IntElement>(e2);

        world.ExecuteBufferWrite<IntElement>(
            e1,
            static buffer => buffer.Add(new IntElement { Value = 1 }));
        world.ExecuteBufferWrite<IntElement>(e2, static buffer =>
        {
            buffer.Add(new IntElement { Value = 2 });
            buffer.Add(new IntElement { Value = 3 });
        });

        Assert.Equal([1], SnapshotValues<IntElement>(world, e1).Select(x => x.Value));
        Assert.Equal([2, 3], SnapshotValues<IntElement>(world, e2).Select(x => x.Value));
    }

    [Fact]
    public void World_DestroyEntity_CleansOverflowBeforeEntityIndexReuse()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<SmallInlineElement>(entity);
        world.ExecuteBufferWrite<SmallInlineElement>(entity, static buffer =>
        {
            buffer.Add(new SmallInlineElement { Value = 1 });
            buffer.Add(new SmallInlineElement { Value = 2 });
            buffer.Add(new SmallInlineElement { Value = 3 });
        });

        int oldIndex = entity.Index;
        world.DestroyEntity(entity);

        var reused = world.CreateEntity();
        Assert.Equal(oldIndex, reused.Index);

        world.AddBuffer<SmallInlineElement>(reused);
        world.ExecuteBufferWrite<SmallInlineElement>(reused, static buffer =>
        {
            buffer.Add(new SmallInlineElement { Value = 10 });
            buffer.Add(new SmallInlineElement { Value = 20 });
            buffer.Add(new SmallInlineElement { Value = 30 });
        });

        Assert.Equal(
            [10, 20, 30],
            SnapshotValues<SmallInlineElement>(world, reused).Select(x => x.Value));
    }

    [Fact]
    public void World_ExecuteBufferRead_ThrowsIfNotPresent()
    {
        var world = new World();
        var entity = world.CreateEntity();

        Assert.Throws<InvalidOperationException>(() =>
            world.ExecuteBufferRead<IntElement>(entity, static _ => { }));
    }

    private static T[] SnapshotValues<T>(World world, SomeEngine.ECS.Entities.Entity entity)
        where T : struct, IBufferElement
    {
        T[] values = null!;
        world.ExecuteBufferRead<T, T[]>(
            entity,
            ref values,
            static (BufferView<T> buffer, ref T[] destination) =>
                destination = buffer.AsSpan().ToArray());
        return values;
    }
}
