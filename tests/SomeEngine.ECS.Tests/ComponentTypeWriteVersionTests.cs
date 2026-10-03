using System.Runtime.CompilerServices;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Queries;
using SomeEngine.ECS.Registry;

namespace SomeEngine.ECS.Tests;

public sealed class ComponentTypeWriteVersionTests
{
    [Fact]
    public void AddingTableComponentAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.CreateEntity();

        Assert.Equal(0u, world.ComponentWriteVersion<Position>());

        world.Add(entity, new Position { X = 7 });

        Assert.Equal(1u, world.ComponentWriteVersion<Position>());
        Assert.Equal(7f, world.Read<Position>(entity).X);
    }

    [Fact]
    public void ReplacingTableComponentAdvancesOnlyItsTypeWriteVersion()
    {
        using var world = new World();
        var first = world.CreateEntity(new Position { X = 1 });
        var second = world.CreateEntity(new Position { X = 2 });
        world.Add(first, new Velocity { X = 3 });
        uint positionVersion = world.ComponentWriteVersion<Position>();
        uint velocityVersion = world.ComponentWriteVersion<Velocity>();

        world.Replace(second, new Position { X = 9 });

        Assert.Equal(positionVersion + 1, world.ComponentWriteVersion<Position>());
        Assert.Equal(velocityVersion, world.ComponentWriteVersion<Velocity>());
        Assert.Equal(9f, world.Read<Position>(second).X);
    }

    [Fact]
    public void WritingTableReferenceAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        var query = world.Query(world.QueryDefinition().ReadWrite<Position>());
        uint before = world.ComponentWriteVersion<Position>();

        world.ExecuteQuery(query, static queryResult =>
        {
            foreach (QueryRow row in queryResult.Rows)
                row.ReadWrite<Position>().X = 11;
        });

        Assert.Equal(before + 1, world.ComponentWriteVersion<Position>());
        Assert.Equal(11f, world.Read<Position>(entity).X);
    }

    [Fact]
    public void RemovingTableComponentAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        uint before = world.ComponentWriteVersion<Position>();

        world.Remove<Position>(entity);

        Assert.Equal(before + 1, world.ComponentWriteVersion<Position>());
        Assert.False(world.Has<Position>(entity));
    }

    [Fact]
    public void AddingSparseComponentAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.CreateEntity();

        Assert.Equal(0u, world.ComponentWriteVersion<SparseValue>());

        world.AddSparse(entity, new SparseValue { Value = 7 });

        Assert.Equal(1u, world.ComponentWriteVersion<SparseValue>());
        Assert.Equal(7, world.ReadSparse<SparseValue>(entity).Value);
    }

    [Fact]
    public void ReplacingSparseComponentAdvancesOnlyItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.CreateEntity(new Position { X = 2 });
        world.AddSparse(entity, new SparseValue { Value = 7 });
        uint sparseVersion = world.ComponentWriteVersion<SparseValue>();
        uint positionVersion = world.ComponentWriteVersion<Position>();

        world.ReplaceSparse(entity, new SparseValue { Value = 13 });

        Assert.Equal(sparseVersion + 1, world.ComponentWriteVersion<SparseValue>());
        Assert.Equal(positionVersion, world.ComponentWriteVersion<Position>());
        Assert.Equal(13, world.ReadSparse<SparseValue>(entity).Value);
    }

    [Fact]
    public void RemovingSparseComponentAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        world.AddSparse(entity, new SparseValue { Value = 7 });
        uint before = world.ComponentWriteVersion<SparseValue>();

        world.RemoveSparse<SparseValue>(entity);

        Assert.Equal(before + 1, world.ComponentWriteVersion<SparseValue>());
        Assert.False(world.HasSparse<SparseValue>(entity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritingSparseValuesAdvancesItsTypeWriteVersion(bool withState)
    {
        using var world = new World();
        var entity = world.CreateEntity();
        world.AddSparse(entity, new SparseValue { Value = 7 });
        uint before = world.ComponentWriteVersion<SparseValue>();

        if (withState)
        {
            int value = 17;
            world.ExecuteSparseWrite<SparseValue, int>(ref value,
                static (ReadOnlySpan<Entity> entities, Span<SparseValue> values, ref int state) =>
                    values[0].Value = state);
        }
        else
        {
            world.ExecuteSparseWrite<SparseValue>(
                static (entities, values) => values[0].Value = 17);
        }

        Assert.Equal(before + 1, world.ComponentWriteVersion<SparseValue>());
        Assert.Equal(17, world.ReadSparse<SparseValue>(entity).Value);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritingTableValuesAdvancesItsTypeWriteVersion(bool readWrite)
    {
        using var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        var query = world.Query(world.QueryDefinition().ReadWrite<Position>());
        uint before = world.ComponentWriteVersion<Position>();

        world.ExecuteQuery(query, queryResult =>
        {
            foreach (QueryChunkView chunk in queryResult.Chunks)
            {
                Span<Position> values = readWrite ? chunk.ReadWrite<Position>() : chunk.Write<Position>();
                values[0].X = 19;
            }
        });

        Assert.Equal(before + 1, world.ComponentWriteVersion<Position>());
        Assert.Equal(19f, world.Read<Position>(entity).X);
    }

    [Fact]
    public void BatchCreationAdvancesTypeWriteVersionForEachComponent()
    {
        using var world = new World();
        world.CreateEntity(new Position());
        int[] components = [ComponentMetadata<Position>.Id];

        world.ExecuteBundleSpawnBatch(components, 3, static view =>
        {
            var value = new Position { X = view.Index + 1 };
            view.Write(in value);
        });

        Assert.Equal(4u, world.ComponentWriteVersion<Position>());
        Assert.Equal(4, world.EntityCount);
    }

    [Fact]
    public void CloningEntityAdvancesCopiedComponentTypeWriteVersions()
    {
        using var world = new World();
        var source = world.CreateEntity(new Position { X = 7 });
        world.AddSparse(source, new SparseValue { Value = 13 });
        uint positionVersion = world.ComponentWriteVersion<Position>();
        uint sparseVersion = world.ComponentWriteVersion<SparseValue>();

        var clone = world.CloneEntity(source);

        Assert.Equal(positionVersion + 1, world.ComponentWriteVersion<Position>());
        Assert.Equal(sparseVersion + 1, world.ComponentWriteVersion<SparseValue>());
        Assert.Equal(7f, world.Read<Position>(clone).X);
        Assert.Equal(13, world.ReadSparse<SparseValue>(clone).Value);
    }

    [Fact]
    public void DestroyingEntityAdvancesRemovedComponentTypeWriteVersions()
    {
        using var world = new World();
        var entity = world.CreateEntity(new Position { X = 7 });
        world.AddSparse(entity, new SparseValue { Value = 13 });
        uint positionVersion = world.ComponentWriteVersion<Position>();
        uint sparseVersion = world.ComponentWriteVersion<SparseValue>();

        world.DestroyEntity(entity);

        Assert.Equal(positionVersion + 1, world.ComponentWriteVersion<Position>());
        Assert.Equal(sparseVersion + 1, world.ComponentWriteVersion<SparseValue>());
        Assert.False(world.IsAlive(entity));
    }

    [Fact]
    public void CopyingEntityAdvancesVersionOfSparseComponentsRemovedFromTarget()
    {
        using var world = new World();
        var source = world.CreateEntity(new Position { X = 7 });
        var target = world.CreateEntity(new Position { X = 1 });
        world.AddSparse(target, new SparseValue { Value = 13 });
        uint before = world.ComponentWriteVersion<SparseValue>();

        world.CopyEntity(source, target);

        Assert.Equal(before + 1, world.ComponentWriteVersion<SparseValue>());
        Assert.False(world.HasSparse<SparseValue>(target));
        Assert.Equal(7f, world.Read<Position>(target).X);
    }

    [Fact]
    public void FailedBundleReplacementPreservesValuesAndTypeWriteVersions()
    {
        using var world = new World();
        var entity = world.CreateEntity(new Position { X = 1 });
        world.AddSparse(entity, new SparseValue { Value = 13 });
        uint positionVersion = world.ComponentWriteVersion<Position>();
        uint sparseVersion = world.ComponentWriteVersion<SparseValue>();
        int[] components = [ComponentMetadata<Position>.Id];
        int[] sparseComponents = [ComponentMetadata<SparseValue>.Id];

        Assert.Throws<InvalidOperationException>(() =>
            world.ExecuteBundleReplace(entity, components, sparseComponents, static view =>
            {
                var position = new Position { X = 7 };
                var sparse = new SparseValue { Value = 17 };
                view.Write(in position);
                view.WriteSparse(in sparse);
                throw new InvalidOperationException("Bundle replacement failed.");
            }));

        Assert.Equal(positionVersion, world.ComponentWriteVersion<Position>());
        Assert.Equal(sparseVersion, world.ComponentWriteVersion<SparseValue>());
        Assert.Equal(1f, world.Read<Position>(entity).X);
        Assert.Equal(13, world.ReadSparse<SparseValue>(entity).Value);
    }

    [Fact]
    public void CommandPlaybackAdvancesTypeWriteVersionForEachComponentChange()
    {
        using var world = new World();
        var entity = world.CreateEntity();
        var commands = world.Commands();
        commands.Add(entity, new Position { X = 1 });
        commands.Replace(entity, new Position { X = 7 });
        commands.Remove<Position>(entity);

        world.Flush();

        Assert.Equal(3u, world.ComponentWriteVersion<Position>());
        Assert.False(world.Has<Position>(entity));
    }

    [Fact]
    public void ComponentTypeWriteVersionsBelongToTheirWorld()
    {
        using var firstWorld = new World();
        using var secondWorld = new World();
        firstWorld.CreateEntity(new Position { X = 1 });
        var entity = secondWorld.CreateEntity(new Position { X = 2 });

        secondWorld.Replace(entity, new Position { X = 7 });

        Assert.Equal(1u, firstWorld.ComponentWriteVersion<Position>());
        Assert.Equal(2u, secondWorld.ComponentWriteVersion<Position>());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void WritingComponentBytesAdvancesRowAndTypeVersions(bool sparse, bool withRange)
    {
        using var world = new World();
        var entity = world.CreateEntity();
        if (sparse)
        {
            world.AddSparse(entity, new SparseValue { Value = 7 });
            uint rowVersion = world.SparseWriteVersion<SparseValue>(entity);
            uint typeVersion = world.ComponentWriteVersion<SparseValue>();
            ushort componentId = ComponentId<SparseValue>.Value;

            ref byte value = ref (withRange
                ? ref world.WriteBytes(entity, componentId, 0, sizeof(int))
                : ref world.WriteBytes(entity, componentId));
            Unsafe.WriteUnaligned(ref value, 13);

            Assert.NotEqual(rowVersion, world.SparseWriteVersion<SparseValue>(entity));
            Assert.Equal(typeVersion + 1, world.ComponentWriteVersion<SparseValue>());
            Assert.Equal(13, world.ReadSparse<SparseValue>(entity).Value);
        }
        else
        {
            world.Add(entity, new Position { X = 7 });
            uint rowVersion = world.WriteVersion<Position>(entity);
            uint typeVersion = world.ComponentWriteVersion<Position>();
            ushort componentId = ComponentId<Position>.Value;

            ref byte value = ref (withRange
                ? ref world.WriteBytes(entity, componentId, 0, sizeof(float))
                : ref world.WriteBytes(entity, componentId));
            Unsafe.WriteUnaligned(ref value, 13f);

            Assert.NotEqual(rowVersion, world.WriteVersion<Position>(entity));
            Assert.Equal(typeVersion + 1, world.ComponentWriteVersion<Position>());
            Assert.Equal(13f, world.Read<Position>(entity).X);
        }
    }

    [Fact]
    public void WritingColumnBytesAdvancesRowAndTypeVersions()
    {
        using var world = new World();
        var first = world.CreateEntity(new Position { X = 1 });
        var second = world.CreateEntity(new Position { X = 2 });
        uint rowVersion = world.WriteVersion<Position>(first);
        uint typeVersion = world.ComponentWriteVersion<Position>();
        var query = world.Query(world.QueryDefinition().ReadWrite<Position>());

        world.ExecuteQuery(query, static queryResult =>
        {
            foreach (QueryChunkView chunk in queryResult.Chunks)
            {
                Span<byte> values = chunk.WriteColumn(ComponentId<Position>.Value, out int stride);
                for (int row = 0; row < chunk.Count; row++)
                    Unsafe.WriteUnaligned(ref values[row * stride], 19f);
            }
        });

        Assert.NotEqual(rowVersion, world.WriteVersion<Position>(first));
        Assert.Equal(world.WriteVersion<Position>(first), world.WriteVersion<Position>(second));
        Assert.Equal(typeVersion + 1, world.ComponentWriteVersion<Position>());
        Assert.Equal(19f, world.Read<Position>(first).X);
        Assert.Equal(19f, world.Read<Position>(second).X);
    }

    private struct SparseValue : ISparseComponent
    {
        public int Value;
    }
}
