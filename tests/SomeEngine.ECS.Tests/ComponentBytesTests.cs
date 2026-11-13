using System.Numerics;
using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;

namespace SomeEngine.ECS.Tests;

public sealed class ComponentBytesTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ByteReadsPreserveTheComponentValueAndWriteVersions(bool sparse)
    {
        using var world = new World();
        Entity entity = world.Create();
        int component;
        if (sparse)
        {
            world.SetSparse(entity, new SparseByteComponent { Number = 13, Opacity = 0.25f });
            component = ComponentId<SparseByteComponent>.Value;
        }
        else
        {
            world.Add(entity, new ByteComponent { Number = 13, Opacity = 0.25f });
            component = ComponentId<ByteComponent>.Value;
        }
        ulong version = sparse ? world.ComponentDataWriteVersion<SparseByteComponent>(entity) : world.ComponentDataWriteVersion<ByteComponent>(entity);
        ulong typeVersion = sparse ? world.ComponentWriteVersion(ComponentId<SparseByteComponent>.Value) : world.ComponentWriteVersion(ComponentId<ByteComponent>.Value);

        int number = Unsafe.ReadUnaligned<int>(in world.ReadBytes(entity, component));
        float opacity = Unsafe.ReadUnaligned<float>(in world.ReadBytes(entity, component, sizeof(int), sizeof(float)));

        Assert.Equal((13, 0.25f), (number, opacity));
        Assert.Equal(version, sparse ? world.ComponentDataWriteVersion<SparseByteComponent>(entity) : world.ComponentDataWriteVersion<ByteComponent>(entity));
        Assert.Equal(typeVersion, sparse ? world.ComponentWriteVersion(ComponentId<SparseByteComponent>.Value) : world.ComponentWriteVersion(ComponentId<ByteComponent>.Value));
        if (sparse)
            Assert.Equal((13, 0.25f), (world.ReadSparse<SparseByteComponent>(entity).Number, world.ReadSparse<SparseByteComponent>(entity).Opacity));
        else
            Assert.Equal((13, 0.25f), (world.Read<ByteComponent>(entity).Number, world.Read<ByteComponent>(entity).Opacity));
    }

    [Fact]
    public void ByteWriteUpdatesFieldAndEntityVersion()
    {
        using var world = new World();
        Entity entity = world.Create(new ByteComponent { Number = 7, Opacity = 0.25f });
        Entity other = world.Create(new ByteComponent { Number = 11, Opacity = 0.5f });
        ulong before = world.ComponentDataWriteVersion<ByteComponent>(entity);
        ulong otherBefore = world.ComponentDataWriteVersion<ByteComponent>(other);

        ref byte bytes = ref world.WriteBytes(entity, ComponentId<ByteComponent>.Value);
        Unsafe.As<byte, float>(ref Unsafe.Add(ref bytes, sizeof(int))) = 0.75f;

        ByteComponent value = world.Read<ByteComponent>(entity);
        Assert.Equal((7, 0.75f), (value.Number, value.Opacity));
        Assert.NotEqual(before, world.ComponentDataWriteVersion<ByteComponent>(entity));
        Assert.Equal(otherBefore, world.ComponentDataWriteVersion<ByteComponent>(other));
    }

    [Fact]
    public void ByteWriteUpdatesSparseComponentField()
    {
        using var world = new World();
        Entity entity = world.Create();
        world.SetSparse(entity, new SparseByteComponent { Number = 13, Opacity = 0.25f });
        ulong before = world.ComponentDataWriteVersion<SparseByteComponent>(entity);

        ref byte bytes = ref world.WriteBytes(entity, ComponentId<SparseByteComponent>.Value);
        Unsafe.As<byte, float>(ref Unsafe.Add(ref bytes, sizeof(int))) = 0.875f;

        SparseByteComponent value = world.ReadSparse<SparseByteComponent>(entity);
        Assert.Equal((13, 0.875f), (value.Number, value.Opacity));
        Assert.NotEqual(before, world.ComponentDataWriteVersion<SparseByteComponent>(entity));
    }

    [Fact]
    public void ColumnWriteUpdatesEveryRowAndMakesChangedQueryVisible()
    {
        using var world = new World();
        world.Create(new ByteComponent { Number = 7, Opacity = 0.25f });
        world.Create(new ByteComponent { Number = 11, Opacity = 0.5f });
        ulong before = world.Version;
        Query writeQuery = world.Query(new QueryDescription().WithStored<ByteComponent>());

        {
            using QueryScope cursor = (writeQuery).Open();
            foreach (QueryChunk chunk in cursor)
            {
                Span<byte> bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Write<ByteComponent>());
                int stride = Unsafe.SizeOf<ByteComponent>();
                Assert.Equal(8, stride);
                Assert.Equal(chunk.Count * stride, bytes.Length);
                for (int row = 0; row < chunk.Count; row++)
                    Unsafe.WriteUnaligned(ref bytes[row * stride + sizeof(int)], 0.75f);
            }

        };

        Query changed = world.Query(new QueryDescription().WithStored<ByteComponent>().WithChanged<ByteComponent>(0));
        var values = new List<(int Number, float Opacity)>();
        {
            using QueryScope cursor = (changed).Since(before).OpenRead();
            foreach (QueryChunk rowChunk in cursor)
                for (int row = 0; row < rowChunk.Count; row++)
            {
                ByteComponent value = rowChunk.Read<ByteComponent>()[row];
                values.Add((value.Number, value.Opacity));
            }

        };
        Assert.Equal([(7, 0.75f), (11, 0.75f)], values.OrderBy(value => value.Number).ToArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ComponentRangeWritesField(bool sparse)
    {
        using var world = new World();
        Entity entity = world.Create();
        int component;
        if (sparse)
        {
            world.SetSparse(entity, new SparseByteComponent { Number = 13, Opacity = 0.25f });
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
        first.Create(new ByteComponent { Number = 7 });
        int component = ComponentId<ByteComponent>.Value;
        Entity entity = second.Create(new ByteComponent { Number = 11 });

        Unsafe.WriteUnaligned(ref second.WriteBytes(entity, component), 23);

        Assert.Equal(23, second.Read<ByteComponent>(entity).Number);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EachByteReferenceAdvancesComponentWriteVersion(bool sparse)
    {
        using var world = new World();
        Entity entity = world.Create();
        int component;
        if (sparse)
        {
            world.SetSparse(entity, new SparseByteComponent { Number = 13 });
            component = ComponentId<SparseByteComponent>.Value;
        }
        else
        {
            world.Add(entity, new ByteComponent { Number = 13 });
            component = ComponentId<ByteComponent>.Value;
        }

        ulong ReadVersion() => sparse
            ? world.ComponentDataWriteVersion<SparseByteComponent>(entity)
            : world.ComponentDataWriteVersion<ByteComponent>(entity);
        ulong before = ReadVersion();
        world.WriteBytes(entity, component);
        ulong first = ReadVersion();
        world.WriteBytes(entity, component);
        ulong second = ReadVersion();

        Assert.NotEqual(before, first);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void GenericTrackBuffersSupportFloatAndVectorValues()
    {
        using var world = new World();
        Entity entity = world.Create();
        world.AddBuffer<Track<float>>(entity);
        world.AddBuffer<Track<Vector2>>(entity);
        {
            var buffer = world.GetBuffer<Track<float>>(entity);
            try
            {

            buffer.Add(new Track<float> { Value = 1 });
            buffer.Add(new Track<float> { Value = 2 });
            buffer.Add(new Track<float> { Value = 3 });
            buffer[1].Value = 42;

            }
            finally { buffer.Dispose(); }
        };
        {
            var buffer = world.GetBuffer<Track<Vector2>>(entity);
            try
            {

            buffer.Add(new Track<Vector2> { Value = new Vector2(1, 2) });
            buffer.Add(new Track<Vector2> { Value = new Vector2(3, 4) });
            buffer.Add(new Track<Vector2> { Value = new Vector2(5, 6) });
            buffer[2].Value = new Vector2(7, 8);

            }
            finally { buffer.Dispose(); }
        };

        {
            using ReadOnlyBuffer<Track<float>> buffer = world.ReadBuffer<Track<float>>(entity);
Assert.Equal([1f, 42f, 3f], buffer.AsSpan().ToArray().Select(track => track.Value));
        };
        world.RemoveBuffer<Track<float>>(entity);
        {
            using ReadOnlyBuffer<Track<Vector2>> buffer = world.ReadBuffer<Track<Vector2>>(entity);
Assert.Equal([new Vector2(1, 2), new Vector2(3, 4), new Vector2(7, 8)],
                buffer.AsSpan().ToArray().Select(track => track.Value));
        };
        world.RemoveBuffer<Track<Vector2>>(entity);

        Assert.False(world.Has<Buffer<Track<float>>>(entity));
        Assert.False(world.Has<Buffer<Track<Vector2>>>(entity));
    }

#if DEBUG
    [Fact]
    public void ByteWriteReportsAbsentSparseComponent()
    {
        using var world = new World();
        Entity entity = world.Create();

        Assert.Throws<InvalidOperationException>(() =>
            world.WriteBytes(entity, ComponentId<SparseByteComponent>.Value));
    }

    [Fact]
    public void ByteWriteReportsDynamicBufferStorage()
    {
        using var world = new World();
        Entity entity = world.Create();
        world.AddBuffer<Track<float>>(entity);

        Assert.Throws<InvalidOperationException>(() => world.WriteBytes(entity, ComponentId<Buffer<Track<float>>>.Value));
        using ReadOnlyBuffer<Track<float>> buffer = world.ReadBuffer<Track<float>>(entity);
        Assert.Equal(0, buffer.Count);
    }

    [Fact]
    public void ByteWriteReportsComponentWithManagedFields()
    {
        using var world = new World();
        Entity entity = world.Create(new NamedComponent { Name = "menu" });

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
        Entity entity = world.Create(new ByteComponent { Number = 7, Opacity = 0.25f });

        Assert.Throws<InvalidOperationException>(() =>
            world.WriteBytes(entity, ComponentId<ByteComponent>.Value, offset, length));
        Assert.Equal(0.25f, world.Read<ByteComponent>(entity).Opacity);
    }
#endif

    public struct ByteComponent : IAddedComponent
    {
        public int Number;
        public float Opacity;
    }

    public struct SparseByteComponent : ISparseComponent, IAddedComponent
{
        public int Number;
        public float Opacity;
    }

    public struct NamedComponent : IAddedComponent
    {
        public string? Name;
    }

    public struct Track<T> : IBufferElement, IAddedComponent where T : unmanaged
    {
        public T Value;
    }
}
