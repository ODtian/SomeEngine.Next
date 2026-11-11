using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Registry;
using Xunit;

namespace SomeEngine.ECS.Tests;

// 测试用 buffer 元素类型
public struct IntElement : SomeEngine.ECS.Components.IBufferElement, IAddedComponent
{
    public int Value;
}

public struct FloatElement : SomeEngine.ECS.Components.IBufferElement, IAddedComponent
{
    public float X;
    public float Y;
}

public struct SmallInlineElement : SomeEngine.ECS.Components.IBufferElement, IAddedComponent
{
    public int Value;
}

public struct BufferMoveMarker : SomeEngine.ECS.IAddedComponent
{
    public int Value;
}

public class DynamicBufferTests
{
    // ——————————————————————————————————————————————————
    // ComponentMetadata 检测
    // ——————————————————————————————————————————————————

    [Fact]
    public void BufferElementCanAlsoBeStoredAsAnOrdinaryValue()
    {
        using var world = new World();
        Entity entity = world.Create(new IntElement { Value = 7 });
        Assert.Equal(7, world.Read<IntElement>(entity).Value);
        Assert.Equal(1, world.Query<IntElement>().Count);
    }

    [Fact]
    public void WorldAddBufferMakesBufferAvailable()
    {
        var world = new World();
        var entity = world.Create();

        world.AddBuffer<IntElement>(entity);

        Assert.True(world.Has<Buffer<IntElement>>(entity));
        Assert.Throws<InvalidOperationException>(() => world.AddBuffer<IntElement>(entity));
        Assert.True(world.Has<Buffer<IntElement>>(entity));
    }

    [Fact]
    public void World_ExecuteBufferWriteAndRead_BorrowsWithinCallbacks()
    {
        var world = new World();
        var entity = world.Create();

        world.AddBuffer<IntElement>(entity);

        {
            var buffer = world.GetBuffer<IntElement>(entity);
            try
            {

            buffer.Add(new IntElement { Value = 100 });
            buffer.Add(new IntElement { Value = 200 });

            }
            finally { buffer.Dispose(); }
        };

        Assert.Equal([100, 200], SnapshotValues<IntElement>(world, entity).Select(x => x.Value));
    }

    [Fact]
    public void World_BufferAsSpan_ReturnsFullOverflowSpan()
    {
        var world = new World();
        var entity = world.Create();

        world.AddBuffer<SmallInlineElement>(entity);
        {
            var buffer = world.GetBuffer<SmallInlineElement>(entity);
            try
            {

            buffer.Add(new SmallInlineElement { Value = 1 });
            buffer.Add(new SmallInlineElement { Value = 2 });
            buffer.Add(new SmallInlineElement { Value = 3 });

            Span<SmallInlineElement> span = buffer.AsSpan();
            Assert.Equal(3, span.Length);
            Assert.Equal([1, 2, 3], span.ToArray().Select(x => x.Value).ToArray());
            span[0] = new SmallInlineElement { Value = 10 };

            }
            finally { buffer.Dispose(); }
        };

        Assert.Equal(10, SnapshotValues<SmallInlineElement>(world, entity)[0].Value);
    }

    [Fact]
    public void BufferClearAndAddReplaceInlineAndOverflowContents()
    {
        var world = new World();
        var entity = world.Create();

        world.AddBuffer<SmallInlineElement>(entity);
        {
            var buffer = world.GetBuffer<SmallInlineElement>(entity);
            try
            {

            buffer.Clear();
            foreach (var value in new SmallInlineElement[]
            {
                new SmallInlineElement { Value = 1 },
                new SmallInlineElement { Value = 2 },
                new SmallInlineElement { Value = 3 },
            })
                buffer.Add(value);

            Assert.Equal([1, 2, 3], buffer.AsSpan().ToArray().Select(x => x.Value).ToArray());

            buffer.Clear();
            buffer.Add(new SmallInlineElement { Value = 9 });
            Assert.Equal(1, buffer.Count);
            Assert.Equal(9, buffer[0].Value);

            buffer.Clear();
            Assert.Equal(0, buffer.Count);
            Assert.Empty(buffer.AsSpan().ToArray());

            }
            finally { buffer.Dispose(); }
        };
    }

    [Fact]
    public void World_BufferReadOnlyAccess_DoesNotBumpChangeVersion()
    {
        var world = new World();
        var entity = world.Create();
        world.AddBuffer<SmallInlineElement>(entity);

        {
            var buffer = world.GetBuffer<SmallInlineElement>(entity);
            try
            {
buffer.Add(new SmallInlineElement { Value = 1 });
            }
            finally { buffer.Dispose(); }
        };

        ulong lastTick = world.AcquireSystemVersion();

        {
            using ReadOnlyBuffer<SmallInlineElement> buffer = world.ReadBuffer<SmallInlineElement>(entity);

            Assert.Equal(1, buffer[0].Value);
            Assert.Equal(1, buffer.AsSpan()[0].Value);

        };

        var query = world.Query(new QueryDescription().WithChangedBuffer<SmallInlineElement>(0));
        int rows = 0;
        {
            using QueryScope cursor = (query).Since(lastTick).OpenRead();
            foreach (QueryChunk _Chunk in cursor)
                for (int _ = 0; _ < _Chunk.Count; _++)
                rows++;

        };

        Assert.Equal(0, rows);
    }

    [Fact]
    public void World_BufferWriteAccess_BumpsChangeVersion()
    {
        var world = new World();
        var entity = world.Create();
        world.AddBuffer<SmallInlineElement>(entity);

        ulong lastTick = world.AcquireSystemVersion();

        {
            var buffer = world.GetBuffer<SmallInlineElement>(entity);
            try
            {
buffer.Add(new SmallInlineElement { Value = 5 });
            }
            finally { buffer.Dispose(); }
        };

        var query = world.Query(new QueryDescription().WithChangedBuffer<SmallInlineElement>(0));
        int rows = 0;
        {
            using QueryScope cursor = (query).Since(lastTick).OpenRead();
            foreach (QueryChunk _Chunk in cursor)
                for (int _ = 0; _ < _Chunk.Count; _++)
                rows++;

        };

        Assert.Equal(1, rows);
    }

    [Fact]
    public void ChangedBuffer_ReturnsOnlyWrittenInlineRowFromSharedChunk()
    {
        var world = new World();
        Entity first = world.Create();
        Entity second = world.Create();
        world.AddBuffer<SmallInlineElement>(first);
        world.AddBuffer<SmallInlineElement>(second);

        ulong lastTick = world.AcquireSystemVersion();
        {
            var buffer = world.GetBuffer<SmallInlineElement>(second);
            try
            {
buffer.Add(new SmallInlineElement { Value = 5 });
            }
            finally { buffer.Dispose(); }
        };

        var query = world.Query(
            new QueryDescription().WithChangedBuffer<SmallInlineElement>(0));
        var changed = new List<Entity>();
        {
            using QueryScope cursor = (query).Since(lastTick).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                changed.Add(rowChunk.Entities[row]);

        };

        Assert.Equal([second], changed);
    }

    [Fact]
    public void ChangedBuffer_ReturnsOnlyWrittenOverflowRowFromSharedChunk()
    {
        var world = new World();
        Entity first = world.Create();
        Entity second = world.Create();
        world.AddBuffer<SmallInlineElement>(first);
        world.AddBuffer<SmallInlineElement>(second);

        var firstValues = world.GetBuffer<SmallInlineElement>(first);
        try { FillOverflowBuffer(firstValues); }
        finally { firstValues.Dispose(); }
        var secondValues = world.GetBuffer<SmallInlineElement>(second);
        try { FillOverflowBuffer(secondValues); }
        finally { secondValues.Dispose(); }

        ulong lastTick = world.AcquireSystemVersion();
        {
            var buffer = world.GetBuffer<SmallInlineElement>(second);
            try
            {

            buffer[2] = new SmallInlineElement { Value = 30 };

            }
            finally { buffer.Dispose(); }
        };

        var query = world.Query(
            new QueryDescription().WithChangedBuffer<SmallInlineElement>(0));
        var changed = new List<Entity>();
        {
            using QueryScope cursor = (query).Since(lastTick).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
                changed.Add(rowChunk.Entities[row]);

        };

        Assert.Equal([second], changed);
    }

    [Fact]
    public void OptionalBufferColumnReportsItsWrittenMember()
    {
        using var world = new World();
        Entity first = world.Create();
        Entity second = world.Create();
        world.AddBuffer<SmallInlineElement>(first);
        world.AddBuffer<SmallInlineElement>(second);
        ulong since = world.Version;
        var values = world.GetBuffer<SmallInlineElement>(second);
        try { values.Add(new SmallInlineElement { Value = 17 }); }
        finally { values.Dispose(); }
        Query query = world.Query(new QueryDescription().Optional<Buffer<SmallInlineElement>>());
        var changed = new List<Entity>();
        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope)
                for (int row = 0; row < chunk.Count; row++)
                    if (chunk.WasWritten<Buffer<SmallInlineElement>>(row, since)) changed.Add(chunk.Entities[row]);
        Assert.Equal([second], changed);
    }

    [Fact]
    public void ChangedBuffer_RowVersionSurvivesStructuralRowMove()
    {
        var world = new World();
        var entity = world.Create();
        world.AddBuffer<SmallInlineElement>(entity);
        ulong lastTick = world.AcquireSystemVersion();

        {
            var buffer = world.GetBuffer<SmallInlineElement>(entity);
            try
            {
buffer.Add(new SmallInlineElement { Value = 5 });
            }
            finally { buffer.Dispose(); }
        };
        world.Add(entity, new BufferMoveMarker { Value = 1 });

        var query = world.Query(
            new QueryDescription().WithChangedBuffer<SmallInlineElement>(0));
        int rows = 0;
        {
            using QueryScope cursor = (query).Since(lastTick).OpenRead();
            foreach (QueryChunk _Chunk in cursor)
                for (int _ = 0; _ < _Chunk.Count; _++)
                rows++;

        };

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
        var entity = world.Create();
        world.AddBuffer<IntElement>(entity);

        var query = world.Query(new QueryDescription().WithStored<Buffer<IntElement>>());
        int rows = 0;
        {
            using QueryScope cursor = (query).OpenRead();
            foreach (QueryChunk _Chunk in cursor)
                for (int _ = 0; _ < _Chunk.Count; _++)
                rows++;

        };

        Assert.Equal(1, rows);
    }

    [Fact]
    public void ElementQueriesDistinguishTheElementFromItsBufferColumn()
    {
        using var world = new World();
        Entity value = world.Create(new IntElement { Value = 3 });
        Entity buffer = world.Create();
        world.AddBuffer<IntElement>(buffer);
        using QueryScope elements = world.Query<IntElement>().OpenRead();
        Assert.True(elements.Contains(value));
        Assert.False(elements.Contains(buffer));
        Assert.Equal(1, world.Query<Buffer<IntElement>>().Count);
    }

    [Fact]
    public void World_BufferSurvivesMigration()
    {
        var world = new World();
        var entity = world.Create<Position>(new Position { X = 1, Y = 2 });

        world.AddBuffer<IntElement>(entity);
        {
            var buffer = world.GetBuffer<IntElement>(entity);
            try
            {
buffer.Add(new IntElement { Value = 42 });
            }
            finally { buffer.Dispose(); }
        };

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
        var entity = world.Create<Position>(new Position { X = 1, Y = 2 });

        world.AddBuffer<SmallInlineElement>(entity);
        {
            var buffer = world.GetBuffer<SmallInlineElement>(entity);
            try
            {

            buffer.Add(new SmallInlineElement { Value = 1 });
            buffer.Add(new SmallInlineElement { Value = 2 });
            buffer.Add(new SmallInlineElement { Value = 3 });
            buffer.Add(new SmallInlineElement { Value = 4 });

            }
            finally { buffer.Dispose(); }
        };

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
        var e1 = world.Create();
        var e2 = world.Create();

        world.AddBuffer<IntElement>(e1);
        world.AddBuffer<IntElement>(e2);

        {
            var buffer = world.GetBuffer<IntElement>(e1);
            try
            {
buffer.Add(new IntElement { Value = 1 });
            }
            finally { buffer.Dispose(); }
        };
        {
            var buffer = world.GetBuffer<IntElement>(e2);
            try
            {

            buffer.Add(new IntElement { Value = 2 });
            buffer.Add(new IntElement { Value = 3 });

            }
            finally { buffer.Dispose(); }
        };

        Assert.Equal([1], SnapshotValues<IntElement>(world, e1).Select(x => x.Value));
        Assert.Equal([2, 3], SnapshotValues<IntElement>(world, e2).Select(x => x.Value));
    }

    [Fact]
    public void World_DestroyEntity_CleansOverflowBeforeEntityIndexReuse()
    {
        var world = new World();
        var entity = world.Create();

        world.AddBuffer<SmallInlineElement>(entity);
        {
            var buffer = world.GetBuffer<SmallInlineElement>(entity);
            try
            {

            buffer.Add(new SmallInlineElement { Value = 1 });
            buffer.Add(new SmallInlineElement { Value = 2 });
            buffer.Add(new SmallInlineElement { Value = 3 });

            }
            finally { buffer.Dispose(); }
        };

        int oldIndex = entity.Id;
        world.Destroy(entity);

        var reused = world.Create();
        Assert.Equal(oldIndex, reused.Id);

        world.AddBuffer<SmallInlineElement>(reused);
        {
            var buffer = world.GetBuffer<SmallInlineElement>(reused);
            try
            {

            buffer.Add(new SmallInlineElement { Value = 10 });
            buffer.Add(new SmallInlineElement { Value = 20 });
            buffer.Add(new SmallInlineElement { Value = 30 });

            }
            finally { buffer.Dispose(); }
        };

        Assert.Equal(
            [10, 20, 30],
            SnapshotValues<SmallInlineElement>(world, reused).Select(x => x.Value));
    }

    [Fact]
    public void World_ExecuteBufferRead_ThrowsIfNotPresent()
    {
        var world = new World();
        var entity = world.Create();

        Assert.Throws<ArgumentException>(() =>
            {
            using ReadOnlyBuffer<IntElement> _ = world.ReadBuffer<IntElement>(entity);

        });
    }

    private static T[] SnapshotValues<T>(World world, SomeEngine.ECS.Entities.Entity entity)
        where T : struct, IBufferElement
    {
        T[] values = null!;
        {
            using ReadOnlyBuffer<T> buffer = world.ReadBuffer<T>(entity);
values = buffer.AsSpan().ToArray();
        };
        return values;
    }
}
