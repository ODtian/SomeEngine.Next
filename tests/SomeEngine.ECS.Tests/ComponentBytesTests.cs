using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;

namespace SomeEngine.ECS.Tests;

public sealed class ComponentBytesTests
{
    [Fact]
    public void ByteWriteUpdatesFieldAndEntityVersion()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new ByteComponent { Number = 7, Opacity = 0.25f });
        Entity other = world.CreateEntity(new ByteComponent { Number = 11, Opacity = 0.5f });
        uint before = ReadVersion<ByteComponent>(world, entity);
        uint otherBefore = ReadVersion<ByteComponent>(world, other);

        ref byte bytes = ref world.WriteBytes(entity, ComponentId<ByteComponent>.Value);
        Unsafe.As<byte, float>(ref Unsafe.Add(ref bytes, sizeof(int))) = 0.75f;

        ByteComponent value = world.Read<ByteComponent>(entity);
        Assert.Equal((7, 0.75f), (value.Number, value.Opacity));
        Assert.NotEqual(before, ReadVersion<ByteComponent>(world, entity));
        Assert.Equal(otherBefore, ReadVersion<ByteComponent>(world, other));
    }

    [Fact]
    public void ByteWriteUpdatesSparseComponentField()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        world.AddSparse(entity, new SparseByteComponent { Number = 13, Opacity = 0.25f });

        ref byte bytes = ref world.WriteBytes(entity, ComponentId<SparseByteComponent>.Value);
        Unsafe.As<byte, float>(ref Unsafe.Add(ref bytes, sizeof(int))) = 0.875f;

        SparseByteComponent value = world.ReadSparse<SparseByteComponent>(entity);
        Assert.Equal((13, 0.875f), (value.Number, value.Opacity));
    }

    [Fact]
    public void ColumnWriteUpdatesEveryRowAndMakesChangedQueryVisible()
    {
        using var world = new World();
        world.CreateEntity(new ByteComponent { Number = 7, Opacity = 0.25f });
        world.CreateEntity(new ByteComponent { Number = 11, Opacity = 0.5f });
        uint before = world.CurrentTick;
        QueryHandle writeQuery = world.Query(world.QueryDefinition().Write<ByteComponent>());

        world.ExecuteQuery(writeQuery, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                Span<byte> bytes = chunk.WriteColumn(ComponentId<ByteComponent>.Value, out int stride);
                Assert.Equal(8, stride);
                Assert.Equal(chunk.Count * stride, bytes.Length);
                for (int row = 0; row < chunk.Count; row++)
                    Unsafe.WriteUnaligned(ref bytes[row * stride + sizeof(int)], 0.75f);
            }
        });

        QueryHandle changed = world.Query(world.QueryDefinition().Read<ByteComponent>().Changed<ByteComponent>());
        var values = new List<(int Number, float Opacity)>();
        world.ExecuteQuery(changed, before, cursor =>
        {
            foreach (QueryRow row in cursor.Rows)
            {
                ByteComponent value = row.Read<ByteComponent>();
                values.Add((value.Number, value.Opacity));
            }
        });
        Assert.Equal([(7, 0.75f), (11, 0.75f)], values.OrderBy(value => value.Number).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentRangeWritesField(bool sparse)
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        ushort component;
        if (sparse)
        {
            world.AddSparse(entity, new SparseByteComponent { Number = 13, Opacity = 0.25f });
            component = ComponentId<SparseByteComponent>.Value;
        }
        else
        {
            world.Add(entity, new ByteComponent { Number = 13, Opacity = 0.25f });
            component = ComponentId<ByteComponent>.Value;
        }

        ref byte field = ref world.WriteBytes(entity, component, sizeof(int), sizeof(float));
        Unsafe.WriteUnaligned(ref field, 0.875f);

        if (sparse)
        {
            SparseByteComponent value = world.ReadSparse<SparseByteComponent>(entity);
            Assert.Equal((13, 0.875f), (value.Number, value.Opacity));
        }
        else
        {
            ByteComponent value = world.Read<ByteComponent>(entity);
            Assert.Equal((13, 0.875f), (value.Number, value.Opacity));
        }
    }

    [Fact]
    public void ComponentNumberCanBeUsedAcrossWorlds()
    {
        using var first = new World();
        using var second = new World();
        first.CreateEntity(new ByteComponent { Number = 7 });
        ushort component = ComponentId<ByteComponent>.Value;
        Entity entity = second.CreateEntity(new ByteComponent { Number = 11 });

        Unsafe.WriteUnaligned(ref second.WriteBytes(entity, component), 23);

        Assert.Equal(23, second.Read<ByteComponent>(entity).Number);
    }

    [Fact]
    public void GenericTrackBuffersSupportFloatAndVectorValues()
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        world.AddBuffer<Track<float>>(entity);
        world.AddBuffer<Track<Vector2>>(entity);
        world.ExecuteBufferWrite<Track<float>>(entity, static buffer =>
        {
            buffer.Add(new Track<float> { Value = 1 });
            buffer.Add(new Track<float> { Value = 2 });
            buffer.Add(new Track<float> { Value = 3 });
            buffer[1].Value = 42;
        });
        world.ExecuteBufferWrite<Track<Vector2>>(entity, static buffer =>
        {
            buffer.Add(new Track<Vector2> { Value = new Vector2(1, 2) });
            buffer.Add(new Track<Vector2> { Value = new Vector2(3, 4) });
            buffer.Add(new Track<Vector2> { Value = new Vector2(5, 6) });
            buffer[2].Value = new Vector2(7, 8);
        });

        world.ExecuteBufferRead<Track<float>>(entity, static buffer =>
            Assert.Equal([1f, 42f, 3f], buffer.AsSpan().ToArray().Select(track => track.Value)));
        world.RemoveBuffer<Track<float>>(entity);
        world.ExecuteBufferRead<Track<Vector2>>(entity, static buffer =>
            Assert.Equal([new Vector2(1, 2), new Vector2(3, 4), new Vector2(7, 8)],
                buffer.AsSpan().ToArray().Select(track => track.Value)));
        world.RemoveBuffer<Track<Vector2>>(entity);

        Assert.False(world.HasBuffer<Track<float>>(entity));
        Assert.False(world.HasBuffer<Track<Vector2>>(entity));
    }

#if DEBUG
    [Fact]
    public void ColumnWriteReportsReadOnlyQuery()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new ByteComponent { Number = 7, Opacity = 0.25f });
        QueryHandle query = world.Query(world.QueryDefinition().Read<ByteComponent>());

        Assert.Throws<InvalidOperationException>(() => world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
                chunk.WriteColumn(ComponentId<ByteComponent>.Value, out _);
        }));
        Assert.Equal(0.25f, world.Read<ByteComponent>(entity).Opacity);
    }

    [Fact]
    public void ColumnWriteReportsComponentWithManagedFields()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new NamedComponent { Name = "menu" });
        QueryHandle query = world.Query(world.QueryDefinition().Write<NamedComponent>());

        Assert.Throws<InvalidOperationException>(() => world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
                chunk.WriteColumn(ComponentId<NamedComponent>.Value, out _);
        }));
        Assert.Equal("menu", world.Read<NamedComponent>(entity).Name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ByteWriteReportsDynamicBufferStorage(bool inline)
    {
        using var world = new World();
        Entity entity = world.CreateEntity();
        world.AddBuffer<Track<float>>(entity);
        ushort component = checked((ushort)(inline
            ? BufferComponents.Inline<Track<float>>()
            : BufferComponents.Header<Track<float>>()));

        Assert.Throws<InvalidOperationException>(() => world.WriteBytes(entity, component));
        world.ExecuteBufferRead<Track<float>>(entity, static buffer => Assert.Equal(0, buffer.Count));
    }

    [Fact]
    public void ByteWriteReportsComponentWithManagedFields()
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new NamedComponent { Name = "menu" });

        Assert.Throws<InvalidOperationException>(() =>
            world.WriteBytes(entity, ComponentId<NamedComponent>.Value));
        Assert.Equal("menu", world.Read<NamedComponent>(entity).Name);
    }

    [Theory]
    [InlineData(-1, 4)]
    [InlineData(0, -1)]
    [InlineData(4, 5)]
    [InlineData(8, 1)]
    [InlineData(int.MaxValue, int.MaxValue)]
    public void ByteWriteReportsRangeOutsideComponent(int offset, int length)
    {
        using var world = new World();
        Entity entity = world.CreateEntity(new ByteComponent { Number = 7, Opacity = 0.25f });

        Assert.Throws<InvalidOperationException>(() =>
            world.WriteBytes(entity, ComponentId<ByteComponent>.Value, offset, length));
        Assert.Equal(0.25f, world.Read<ByteComponent>(entity).Opacity);
    }
#endif

    private static uint ReadVersion<T>(World world, Entity entity) where T : struct, IComponent
    {
        QueryHandle query = world.Query(world.QueryDefinition().Read<T>());
        uint version = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (QueryChunkView chunk in cursor.Chunks)
            {
                for (int row = 0; row < chunk.Count; row++)
                {
                    if (chunk.GetEntity(row) == entity)
                        version = chunk.ReadWriteVersions<T>()[row];
                }
            }
        });
        return version;
    }

    public struct ByteComponent : IComponent
    {
        public int Number;
        public float Opacity;
    }

    public struct SparseByteComponent : ISparseComponent
    {
        public int Number;
        public float Opacity;
    }

    public struct NamedComponent : IComponent
    {
        public string? Name;
    }

    [BufferCapacity(2)]
    public struct Track<T> : IBufferElement where T : unmanaged
    {
        public T Value;
    }
}
