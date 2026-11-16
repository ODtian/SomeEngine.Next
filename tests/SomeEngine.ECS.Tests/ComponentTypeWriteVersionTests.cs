using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;
using SomeEngine.ECS.Commands;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Tests;

public sealed class ComponentTypeWriteVersionTests
{
    [Fact]
    public void AddingTableComponentAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.Create();

        Assert.Equal(0u, world.ComponentWriteVersion(ComponentId<Position>.Value));

        world.Add(entity, new Position { X = 7 });

        Assert.Equal(1u, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.Equal(7f, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ReplacingTableComponentAdvancesOnlyItsTypeWriteVersion()
    {
        using var world = new World();
        var first = world.Create(new Position { X = 1 });
        var second = world.Create(new Position { X = 2 });
        world.Add(first, new Velocity { X = 3 });
        ulong positionVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);
        ulong velocityVersion = world.ComponentWriteVersion(ComponentId<Velocity>.Value);

        world.Set(second, new Position { X = 9 });

        Assert.True(world.ComponentWriteVersion(ComponentId<Position>.Value) > positionVersion);
        Assert.Equal(velocityVersion, world.ComponentWriteVersion(ComponentId<Velocity>.Value));
        Assert.Equal(9f, world.Read<Position>(second).X);
    }

    [Fact]
    public void WritingTableReferenceAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 });
        var query = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = world.ComponentWriteVersion(ComponentId<Position>.Value);

        {
            using QueryScope queryResult = query.Open();
            foreach (QueryChunk rowChunk in queryResult)
                for (int row = 0; row < rowChunk.Count; row++)
                    rowChunk.Write<Position>(row).X = 11;

        }

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.Equal(11f, world.Read<Position>(entity).X);
    }

    [Fact]
    public void RemovingTableComponentAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 });
        ulong before = world.ComponentWriteVersion(ComponentId<Position>.Value);

        world.Remove<Position>(entity);

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.False(world.Has<Position>(entity));
    }

    [Fact]
    public void AddingSparseComponentAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.Create();

        Assert.Equal(0u, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));

        world.Set(entity, new SparseValue { Value = 7 });

        Assert.Equal(1u, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.Equal(7, world.Read<SparseValue>(entity).Value);
    }

    [Fact]
    public void ReplacingSparseComponentAdvancesOnlyItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 2 });
        world.Set(entity, new SparseValue { Value = 7 });
        ulong sparseVersion = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);
        ulong positionVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);

        world.Set(entity, new SparseValue { Value = 13 });

        Assert.Equal(sparseVersion + 1, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.Equal(positionVersion, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.Equal(13, world.Read<SparseValue>(entity).Value);
    }

    [Fact]
    public void RemovingSparseComponentAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.Create();
        world.Set(entity, new SparseValue { Value = 7 });
        ulong before = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);

        world.Remove<SparseValue>(entity);

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.False(world.Has<SparseValue>(entity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritingSparseValuesAdvancesItsTypeWriteVersion(bool withState)
    {
        using var world = new World();
        var entity = world.Create();
        world.Set(entity, new SparseValue { Value = 7 });
        ulong before = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);

        if (withState)
        {
            int value = 17;
            {
                var sparseScope = world.OpenSparseValueWrites<SparseValue>();
                try
                {
                    ReadOnlySpan<Entity> entities = sparseScope.Entities;
                    Span<SparseValue> values = sparseScope.Values;
                    values[0].Value = value;
                }
                finally { sparseScope.Dispose(); }
            }
        }
        else
        {
            {
                var sparseScope = world.OpenSparseValueWrites<SparseValue>();
                try
                {
                    ReadOnlySpan<Entity> entities = sparseScope.Entities;
                    Span<SparseValue> values = sparseScope.Values;
                    values[0].Value = 17;
                }
                finally { sparseScope.Dispose(); }
            }
        }

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.Equal(17, world.Read<SparseValue>(entity).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritingTableValuesAdvancesItsTypeWriteVersion(bool readWrite)
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 });
        var query = world.Query(new QueryDescription().WithStored<Position>());
        ulong before = world.ComponentWriteVersion(ComponentId<Position>.Value);

        {
            using QueryScope queryResult = query.Open();
            foreach (QueryChunk chunk in queryResult)
            {
                if (readWrite) chunk.Write<Position>()[0].X = 19;
                else chunk.Write<Position>(0).X = 19;
            }

        }

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.Equal(19f, world.Read<Position>(entity).X);
    }

    [Fact]
    public void CloningEntityAdvancesCopiedComponentTypeWriteVersions()
    {
        using var world = new World();
        var source = world.Create(new Position { X = 7 });
        world.Set(source, new SparseValue { Value = 13 });
        ulong positionVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);
        ulong sparseVersion = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);

        var clone = world.Clone(source);

        Assert.True(world.ComponentWriteVersion(ComponentId<Position>.Value) > positionVersion);
        Assert.True(world.ComponentWriteVersion(ComponentId<SparseValue>.Value) > sparseVersion);
        Assert.Equal(7f, world.Read<Position>(clone).X);
        Assert.Equal(13, world.Read<SparseValue>(clone).Value);
    }

    [Fact]
    public void DestroyingEntityAdvancesRemovedComponentTypeWriteVersions()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 7 });
        world.Set(entity, new SparseValue { Value = 13 });
        ulong positionVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);
        ulong sparseVersion = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);

        world.Destroy(entity);

        Assert.True(world.ComponentWriteVersion(ComponentId<Position>.Value) > positionVersion);
        Assert.True(world.ComponentWriteVersion(ComponentId<SparseValue>.Value) > sparseVersion);
        Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void CopyingEntityAdvancesVersionOfSparseComponentsRemovedFromTarget()
    {
        using var world = new World();
        var source = world.Create(new Position { X = 7 });
        var target = world.Create(new Position { X = 1 });
        world.Set(target, new SparseValue { Value = 13 });
        ulong before = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);

        world.Copy(source, target);

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.False(world.Has<SparseValue>(target));
        Assert.Equal(7f, world.Read<Position>(target).X);
    }

    [Fact]
    public void CommandPlaybackAdvancesTypeWriteVersionForEachComponentChange()
    {
        using var world = new World();
        var entity = world.Create();
        using var commands = new CommandBuffer(world);
        commands.Add(entity, new Position { X = 1 });
        commands.Set(entity, new Position { X = 7 });
        commands.Remove<Position>(entity);

        commands.Playback();

        Assert.Equal(3u, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.False(world.Has<Position>(entity));
    }

    [Fact]
    public void ComponentTypeWriteVersionsBelongToTheirWorld()
    {
        using var firstWorld = new World();
        using var secondWorld = new World();
        firstWorld.Create(new Position { X = 1 });
        var entity = secondWorld.Create(new Position { X = 2 });

        secondWorld.Set(entity, new Position { X = 7 });

        Assert.Equal(1u, firstWorld.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.Equal(2u, secondWorld.ComponentWriteVersion(ComponentId<Position>.Value));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WritingComponentBytesAdvancesRowAndTypeVersions(bool sparse, bool withRange)
    {
        using var world = new World();
        var entity = world.Create();
        if (sparse)
        {
            world.Set(entity, new SparseValue { Value = 7 });
            ulong rowVersion = world.ComponentWriteVersion<SparseValue>(entity);
            ulong typeVersion = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);
            int componentId = ComponentId<SparseValue>.Value;

            ref byte value = ref (withRange
                ? ref world.WriteBytes(entity, componentId, 0, sizeof(int))
                : ref world.WriteBytes(entity, componentId));
            Unsafe.WriteUnaligned(ref value, 13);

            Assert.NotEqual(rowVersion, world.ComponentWriteVersion<SparseValue>(entity));
            Assert.Equal(typeVersion + 1, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
            Assert.Equal(13, world.Read<SparseValue>(entity).Value);
        }
        else
        {
            world.Add(entity, new Position { X = 7 });
            ulong rowVersion = world.ComponentWriteVersion<Position>(entity);
            ulong typeVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);
            int componentId = ComponentId<Position>.Value;

            ref byte value = ref (withRange
                ? ref world.WriteBytes(entity, componentId, 0, sizeof(float))
                : ref world.WriteBytes(entity, componentId));
            Unsafe.WriteUnaligned(ref value, 13f);

            Assert.NotEqual(rowVersion, world.ComponentWriteVersion<Position>(entity));
            Assert.Equal(typeVersion + 1, world.ComponentWriteVersion(ComponentId<Position>.Value));
            Assert.Equal(13f, world.Read<Position>(entity).X);
        }
    }

    [Fact]
    public void WritingColumnBytesAdvancesRowAndTypeVersions()
    {
        using var world = new World();
        var first = world.Create(new Position { X = 1 });
        var second = world.Create(new Position { X = 2 });
        ulong rowVersion = world.ComponentWriteVersion<Position>(first);
        ulong typeVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);
        var query = world.Query(new QueryDescription().WithStored<Position>());

        {
            using QueryScope queryResult = query.Open();
            foreach (QueryChunk chunk in queryResult)
            {
                Span<byte> values = System.Runtime.InteropServices.MemoryMarshal.AsBytes(chunk.Write<Position>());
                int stride = Unsafe.SizeOf<Position>();
                for (int row = 0; row < chunk.Count; row++)
                    Unsafe.WriteUnaligned(ref values[row * stride], 19f);
            }

        }

        Assert.NotEqual(rowVersion, world.ComponentWriteVersion<Position>(first));
        Assert.Equal(world.ComponentWriteVersion<Position>(first), world.ComponentWriteVersion<Position>(second));
        Assert.Equal(typeVersion + 1, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.Equal(19f, world.Read<Position>(first).X);
        Assert.Equal(19f, world.Read<Position>(second).X);
    }

    private struct SparseValue : ISparseComponent, IAddedComponent
    {
        public int Value;
    }

}
