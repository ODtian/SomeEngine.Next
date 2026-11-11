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

        world.SetSparse(entity, new SparseValue { Value = 7 });

        Assert.Equal(1u, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.Equal(7, world.ReadSparse<SparseValue>(entity).Value);
    }

    [Fact]
    public void ReplacingSparseComponentAdvancesOnlyItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 2 });
        world.SetSparse(entity, new SparseValue { Value = 7 });
        ulong sparseVersion = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);
        ulong positionVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);

        world.SetSparse(entity, new SparseValue { Value = 13 });

        Assert.Equal(sparseVersion + 1, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.Equal(positionVersion, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.Equal(13, world.ReadSparse<SparseValue>(entity).Value);
    }

    [Fact]
    public void RemovingSparseComponentAdvancesItsTypeWriteVersion()
    {
        using var world = new World();
        var entity = world.Create();
        world.SetSparse(entity, new SparseValue { Value = 7 });
        ulong before = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);

        world.RemoveSparse<SparseValue>(entity);

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.False(world.HasSparse<SparseValue>(entity));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WritingSparseValuesAdvancesItsTypeWriteVersion(bool withState)
    {
        using var world = new World();
        var entity = world.Create();
        world.SetSparse(entity, new SparseValue { Value = 7 });
        ulong before = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);

        if (withState)
        {
            int value = 17;
            {
                var sparseScope = world.OpenSparseWrite<SparseValue>();
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
                var sparseScope = world.OpenSparseWrite<SparseValue>();
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
        Assert.Equal(17, world.ReadSparse<SparseValue>(entity).Value);
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
    public void BatchCreationAdvancesTypeWriteVersionForEachComponent()
    {
        using var world = new World();
        world.Create(new Position());
        ulong before = world.ComponentWriteVersion(ComponentId<Position>.Value);
        int[] components = [ComponentId<Position>.Value];

        world.ExecuteBundleSpawnBatch(components, 3, 0, new NumberedPositions());

        Assert.True(world.ComponentWriteVersion(ComponentId<Position>.Value) > before);
        Assert.Equal(4, world.EntityCount);
    }

    [Fact]
    public void CloningEntityAdvancesCopiedComponentTypeWriteVersions()
    {
        using var world = new World();
        var source = world.Create(new Position { X = 7 });
        world.SetSparse(source, new SparseValue { Value = 13 });
        ulong positionVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);
        ulong sparseVersion = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);

        var clone = world.Clone(source);

        Assert.True(world.ComponentWriteVersion(ComponentId<Position>.Value) > positionVersion);
        Assert.True(world.ComponentWriteVersion(ComponentId<SparseValue>.Value) > sparseVersion);
        Assert.Equal(7f, world.Read<Position>(clone).X);
        Assert.Equal(13, world.ReadSparse<SparseValue>(clone).Value);
    }

    [Fact]
    public void DestroyingEntityAdvancesRemovedComponentTypeWriteVersions()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 7 });
        world.SetSparse(entity, new SparseValue { Value = 13 });
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
        world.SetSparse(target, new SparseValue { Value = 13 });
        ulong before = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);

        world.Copy(source, target);

        Assert.Equal(before + 1, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.False(world.HasSparse<SparseValue>(target));
        Assert.Equal(7f, world.Read<Position>(target).X);
    }

    [Fact]
    public void FailedBundleReplacementPreservesValuesAndTypeWriteVersions()
    {
        using var world = new World();
        var entity = world.Create(new Position { X = 1 });
        world.SetSparse(entity, new SparseValue { Value = 13 });
        ulong positionVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);
        ulong sparseVersion = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);
        int[] components = [ComponentId<Position>.Value];
        int[] sparseComponents = [ComponentId<SparseValue>.Value];

        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        Assert.Throws<InvalidOperationException>(() =>
            candidate.ExecuteBundleReplace(entity, components, sparseComponents, 0, new FaultingReplacement()));

        Assert.Equal(positionVersion, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.Equal(sparseVersion, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
        Assert.Equal(1f, world.Read<Position>(entity).X);
        Assert.Equal(13, world.ReadSparse<SparseValue>(entity).Value);
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
            world.SetSparse(entity, new SparseValue { Value = 7 });
            ulong rowVersion = world.ComponentDataWriteVersion<SparseValue>(entity);
            ulong typeVersion = world.ComponentWriteVersion(ComponentId<SparseValue>.Value);
            int componentId = ComponentId<SparseValue>.Value;

            ref byte value = ref (withRange
                ? ref world.WriteBytes(entity, componentId, 0, sizeof(int))
                : ref world.WriteBytes(entity, componentId));
            Unsafe.WriteUnaligned(ref value, 13);

            Assert.NotEqual(rowVersion, world.ComponentDataWriteVersion<SparseValue>(entity));
            Assert.Equal(typeVersion + 1, world.ComponentWriteVersion(ComponentId<SparseValue>.Value));
            Assert.Equal(13, world.ReadSparse<SparseValue>(entity).Value);
        }
        else
        {
            world.Add(entity, new Position { X = 7 });
            ulong rowVersion = world.ComponentDataWriteVersion<Position>(entity);
            ulong typeVersion = world.ComponentWriteVersion(ComponentId<Position>.Value);
            int componentId = ComponentId<Position>.Value;

            ref byte value = ref (withRange
                ? ref world.WriteBytes(entity, componentId, 0, sizeof(float))
                : ref world.WriteBytes(entity, componentId));
            Unsafe.WriteUnaligned(ref value, 13f);

            Assert.NotEqual(rowVersion, world.ComponentDataWriteVersion<Position>(entity));
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
        ulong rowVersion = world.ComponentDataWriteVersion<Position>(first);
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

        Assert.NotEqual(rowVersion, world.ComponentDataWriteVersion<Position>(first));
        Assert.Equal(world.ComponentDataWriteVersion<Position>(first), world.ComponentDataWriteVersion<Position>(second));
        Assert.Equal(typeVersion + 1, world.ComponentWriteVersion(ComponentId<Position>.Value));
        Assert.Equal(19f, world.Read<Position>(first).X);
        Assert.Equal(19f, world.Read<Position>(second).X);
    }

    private struct SparseValue : ISparseComponent, IAddedComponent
    {
        public int Value;
    }

    private readonly struct NumberedPositions : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int state) => view.Write(new Position { X = view.Index + 1 });
    }

    private readonly struct FaultingReplacement : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value)
        {
            view.Write(new Position { X = 7 });
            view.WriteSparse(new SparseValue { Value = 17 });
            throw new InvalidOperationException();
        }
    }

}
