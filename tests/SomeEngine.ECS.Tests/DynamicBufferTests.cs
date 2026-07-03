using SomeEngine.ECS.Components;
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

    [Fact]
    public void BufferBackingComponents_AreRegularTableComponents()
    {
        Assert.Equal(StoragePath.Table, ComponentMetadata<DynamicBufferHeader<IntElement>>.Storage);
        Assert.Equal(StoragePath.Table, ComponentMetadata<DynamicBufferInline<IntElement>>.Storage);
    }

    // ——————————————————————————————————————————————————
    // World API
    // ——————————————————————————————————————————————————

    [Fact]
    public void World_AddBuffer_AddsBackingComponents()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<IntElement>(entity);

        Assert.True(world.HasBuffer<IntElement>(entity));
        Assert.True(world.Has<DynamicBufferHeader<IntElement>>(entity));
        Assert.True(world.Has<DynamicBufferInline<IntElement>>(entity));
        Assert.Throws<InvalidOperationException>(() => world.AddBuffer<IntElement>(entity));
    }

    [Fact]
    public void World_GetBuffer_AddAndRead()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<IntElement>(entity);

        var buffer = world.GetBuffer<IntElement>(entity);
        buffer.Add(new IntElement { Value = 100 });
        buffer.Add(new IntElement { Value = 200 });

        var buffer2 = world.GetBuffer<IntElement>(entity);
        Assert.Equal(2, buffer2.Count);
        Assert.Equal(100, buffer2[0].Value);
        Assert.Equal(200, buffer2[1].Value);
    }

    [Fact]
    public void World_BufferAsSpan_ReturnsFullOverflowSpan()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<SmallInlineElement>(entity);
        var buffer = world.GetBuffer<SmallInlineElement>(entity);
        buffer.Add(new SmallInlineElement { Value = 1 });
        buffer.Add(new SmallInlineElement { Value = 2 });
        buffer.Add(new SmallInlineElement { Value = 3 });

        var span = buffer.AsSpan();
        Assert.Equal(3, span.Length);
        Assert.Equal([1, 2, 3], span.ToArray().Select(x => x.Value).ToArray());

        span[0] = new SmallInlineElement { Value = 10 };
        Assert.Equal(10, world.GetBuffer<SmallInlineElement>(entity)[0].Value);
    }

    [Fact]
    public void World_BufferReplaceWith_ReplacesInlineAndOverflowContents()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<SmallInlineElement>(entity);
        var buffer = world.GetBuffer<SmallInlineElement>(entity);
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
    }

    [Fact]
    public void World_BufferReadOnlyAccess_DoesNotBumpChangeVersion()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<SmallInlineElement>(entity);

        var buffer = world.GetBuffer<SmallInlineElement>(entity);
        buffer.Add(new SmallInlineElement { Value = 1 });

        uint lastTick = world.AcquireSystemTick();

        var readBuffer = world.GetBuffer<SmallInlineElement>(entity);
        Assert.Equal(1, readBuffer.Read(0).Value);
        Assert.Equal(1, readBuffer.ReadSpan()[0].Value);

        var query = world.CreateQuery().ChangedBuffer<SmallInlineElement>().Build();
        var archetype = Assert.Single(query.Archetypes);
        Assert.False(query.MatchesChunkChanged(archetype, archetype.Chunks[0], lastTick));
    }

    [Fact]
    public void World_BufferWriteAccess_BumpsChangeVersion()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<SmallInlineElement>(entity);

        uint lastTick = world.AcquireSystemTick();

        var buffer = world.GetBuffer<SmallInlineElement>(entity);
        buffer.Add(new SmallInlineElement { Value = 5 });

        var query = world.CreateQuery().ChangedBuffer<SmallInlineElement>().Build();
        var archetype = Assert.Single(query.Archetypes);
        Assert.True(query.MatchesChunkChanged(archetype, archetype.Chunks[0], lastTick));
    }

    [Fact]
    public void Query_WithBuffer_UsesBackingComponents()
    {
        var world = new World();
        var entity = world.CreateEntity();
        world.AddBuffer<IntElement>(entity);

        var query = world.CreateQuery().WithBuffer<IntElement>().Build();

        Assert.Contains(
            world.AllArchetypes,
            arch => query.Matches(arch) && arch.HasComponent(ComponentMetadata<DynamicBufferHeader<IntElement>>.Id));
    }

    [Fact]
    public void Query_DirectBufferElement_Throws()
    {
        var world = new World();

        Assert.Throws<InvalidOperationException>(() => world.CreateQuery().With<IntElement>());
        Assert.Throws<InvalidOperationException>(() => world.CreateQuery().Read<IntElement>());
    }

    [Fact]
    public void World_BufferSurvivesMigration()
    {
        var world = new World();
        var entity = world.CreateEntity<Position>(new Position { X = 1, Y = 2 });

        world.AddBuffer<IntElement>(entity);
        var buffer = world.GetBuffer<IntElement>(entity);
        buffer.Add(new IntElement { Value = 42 });

        // 添加另一个组件触发迁移
        world.Add(entity, new Health { Value = 100 });

        // buffer 数据应该在迁移后保持
        var bufferAfter = world.GetBuffer<IntElement>(entity);
        Assert.Equal(1, bufferAfter.Count);
        Assert.Equal(42, bufferAfter[0].Value);

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
        var buffer = world.GetBuffer<SmallInlineElement>(entity);
        buffer.Add(new SmallInlineElement { Value = 1 });
        buffer.Add(new SmallInlineElement { Value = 2 });
        buffer.Add(new SmallInlineElement { Value = 3 });
        buffer.Add(new SmallInlineElement { Value = 4 });

        world.Add(entity, new Health { Value = 100 });

        var bufferAfter = world.GetBuffer<SmallInlineElement>(entity);
        Assert.Equal(4, bufferAfter.Count);
        Assert.Equal([1, 2, 3, 4], bufferAfter.AsSpan().ToArray().Select(x => x.Value).ToArray());
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

        var buf1 = world.GetBuffer<IntElement>(e1);
        buf1.Add(new IntElement { Value = 1 });

        var buf2 = world.GetBuffer<IntElement>(e2);
        buf2.Add(new IntElement { Value = 2 });
        buf2.Add(new IntElement { Value = 3 });

        Assert.Equal(1, world.GetBuffer<IntElement>(e1).Count);
        Assert.Equal(2, world.GetBuffer<IntElement>(e2).Count);
        Assert.Equal(1, world.GetBuffer<IntElement>(e1)[0].Value);
        Assert.Equal(2, world.GetBuffer<IntElement>(e2)[0].Value);
    }

    [Fact]
    public void World_DestroyEntity_CleansOverflowBeforeEntityIndexReuse()
    {
        var world = new World();
        var entity = world.CreateEntity();

        world.AddBuffer<SmallInlineElement>(entity);
        var buffer = world.GetBuffer<SmallInlineElement>(entity);
        buffer.Add(new SmallInlineElement { Value = 1 });
        buffer.Add(new SmallInlineElement { Value = 2 });
        buffer.Add(new SmallInlineElement { Value = 3 });

        int oldIndex = entity.Index;
        world.DestroyEntity(entity);

        var reused = world.CreateEntity();
        Assert.Equal(oldIndex, reused.Index);

        world.AddBuffer<SmallInlineElement>(reused);
        var reusedBuffer = world.GetBuffer<SmallInlineElement>(reused);
        reusedBuffer.Add(new SmallInlineElement { Value = 10 });
        reusedBuffer.Add(new SmallInlineElement { Value = 20 });
        reusedBuffer.Add(new SmallInlineElement { Value = 30 });

        Assert.Equal([10, 20, 30], reusedBuffer.AsSpan().ToArray().Select(x => x.Value).ToArray());
    }

    [Fact]
    public void World_GetBuffer_ThrowsIfNotPresent()
    {
        var world = new World();
        var entity = world.CreateEntity();

        Assert.Throws<InvalidOperationException>(() =>
            world.GetBuffer<IntElement>(entity));
    }
}
