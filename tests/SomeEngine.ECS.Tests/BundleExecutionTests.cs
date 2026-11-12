using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Tests;

public sealed class BundleExecutionTests
{
    [Fact]
    public void FailedCandidateBundleCreationPreservesTheSourceAndAllowsLaterCreation()
    {
        using var world = new World();
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        int[] ids = [ComponentId<Position>.Value, ComponentId<Velocity>.Value];

        Assert.Throws<BundleWriteException>(() => candidate.ExecuteBundleSpawn(ids, 0, new PositionFaultWriter()));

        Assert.Equal(0, world.EntityCount);
        Entity entity = world.Spawn(new PhysicsBundle { Position = new() { X = 1, Y = 2 }, Velocity = new() { X = 3, Y = 4 } });
        Assert.True(world.IsAlive(entity));
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(4, world.Read<Velocity>(entity).Y);
    }

    [Fact]
    public void LaterCandidateBatchFaultPreservesTheSourceRows()
    {
        using var world = new World();
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        int[] ids = [ComponentId<Position>.Value];
        Assert.Throws<BundleWriteException>(() => candidate.ExecuteBundleSpawnBatch(ids, 4, 0, new BatchFaultWriter()));
        Assert.Equal(0, world.EntityCount);
        world.ExecuteBundleSpawnBatch(ids, 2, 10, new PositionWriter());
        Assert.Equal(2, world.EntityCount);
    }

    [Fact]
    public void CandidateBundleAddAndReplaceFaultsPreserveTheSourceEntity()
    {
        using var world = new World();
        Entity entity = world.Create(new Health { Value = 9 });
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        int[] ids = [ComponentId<Position>.Value, ComponentId<Velocity>.Value];
        Assert.Throws<BundleWriteException>(() => candidate.ExecuteBundleAdd(entity, ids, [], 0, new PositionFaultWriter()));
        Assert.False(world.Has<Velocity>(entity));
        Assert.False(world.Has<Position>(entity));
        Assert.Equal(9, world.Read<Health>(entity).Value);
        world.AddBundle(entity, new PhysicsBundle { Position = new() { X = 1, Y = 2 }, Velocity = new() { X = 5, Y = 6 } });
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        checkpoint.Restore(candidate);
        Assert.Throws<BundleWriteException>(() => candidate.ExecuteBundleReplace(entity, ids, [], 0, new PositionFaultWriter()));
        Assert.Equal(1, world.Read<Position>(entity).X);
        Assert.Equal(6, world.Read<Velocity>(entity).Y);
    }

    [Fact]
    public void CompositeStorageCandidateFaultPreservesEverySourceBinding()
    {
        using var world = new World();
        _ = world.Index<NameIndex, string>();
        using var checkpoint = new WorldCheckpoint().Capture(world, CheckpointProgress.Preserve);
        using var candidate = new World();
        checkpoint.Restore(candidate);
        int[] ids = [ComponentId<NameIndex>.Value, ComponentId<SceneId>.Value, ComponentId<Buffer<IntElement>>.Value];
        int[] sparse = [ComponentId<Damage>.Value];
        Assert.Throws<BundleWriteException>(() => candidate.ExecuteBundleSpawn(ids, sparse, 0, new MixedFaultWriter()));
        Assert.Equal(0, world.EntityCount);
        Assert.Empty(ReadIndexedEntities<NameIndex, string>(world, "published"));
        Entity entity = world.ExecuteBundleSpawn(ids, sparse, 0, new MixedWriter());
        Assert.Equal(3, world.ReadShared<SceneId>(entity).Value);
        Assert.Equal(5, world.ReadSparse<Damage>(entity).Amount);
        Assert.Equal([entity], ReadIndexedEntities<NameIndex, string>(world, "published"));
        using ReadOnlyBuffer<IntElement> buffer = world.ReadBuffer<IntElement>(entity);
        Assert.Equal(1, buffer.Count);
        Assert.Equal(13, buffer[0].Value);
    }

    [Fact]
    public void BundleReplaceRejectsMissingColumnsBeforeChangingAnyField()
    {
        using var world = new World();
        Entity entity = world.Create(new Position { X = 4, Y = 5 });
        int[] ids = [ComponentId<Position>.Value, ComponentId<Velocity>.Value];
        Assert.Throws<InvalidOperationException>(() => world.ExecuteBundleReplace(entity, ids, [], 0, new PhysicsWriter()));
        Assert.Equal(4, world.Read<Position>(entity).X);
        Assert.False(world.Has<Velocity>(entity));
    }

    [Fact]
    public void RepeatedSharedWritesCreateOneFinalSharedMembership()
    {
        using var world = new World();
        Entity entity = world.ExecuteBundleSpawn([ComponentId<SceneId>.Value], 0, new SharedWriter());
        SharedId<SceneId> id = world.InternShared(new SceneId { Value = 6 });
        using SharedMembers members = world.FindShared(id);
        Assert.Equal(1, world.EntityCount);
        Assert.Equal(1, members.Count);
        Assert.Equal(6, world.ReadShared<SceneId>(entity).Value);
    }

    [Fact]
    public void CountedStructWriterReturnsEachCreatedIdentity()
    {
        using var world = new World();
        var output = new Entity[4];
        int[] ids = [ComponentId<Position>.Value];
        world.ExecuteBundleSpawnBatch(ids, output.Length, new EntityOutput(output), new OutputWriter());
        Assert.Equal(output.Length, world.EntityCount);
        for (int index = 0; index < output.Length; index++)
        {
            Assert.True(world.IsAlive(output[index]));
            Assert.Equal(index, world.Read<Position>(output[index]).X);
        }
    }

    [Fact]
    public void BatchConstructionMaintainsAnIndexActivatedAfterTheFirstFieldWrite()
    {
        using var world = new World();
        int[] ids = [ComponentId<IndexedName>.Value];
        world.ExecuteBundleSpawnBatch(ids, 3, new IndexInput(world), new LateIndexWriter());
        for (int index = 0; index < 3; index++)
            Assert.Single(ReadIndexedEntities<IndexedName, string>(world, $"name-{index}"));
    }

    [Fact]
    public void FinalBundleColumnsSupportIndexActivationBeforeTheFieldAssignment()
    {
        using var world = new World();
        Entity entity = world.ExecuteBundleSpawn([ComponentId<IndexedName>.Value], new IndexInput(world), new EarlyIndexWriter());
        Assert.Equal([entity], ReadIndexedEntities<IndexedName, string>(world, "ready"));
    }

    private readonly struct PositionWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int offset)
            => view.Write(new Position { X = view.Index + offset, Y = view.Index + offset + 1 });
    }
    private readonly struct PositionFaultWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value)
        {
            view.Write(new Position { X = 100, Y = 200 });
            throw new BundleWriteException();
        }
    }
    private readonly struct BatchFaultWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value)
        {
            view.Write(new Position { X = view.Index, Y = view.Index + 1 });
            if (view.Index == 2) throw new BundleWriteException();
        }
    }
    private readonly struct PhysicsWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value)
        {
            view.Write(new Position { X = 1, Y = 2 });
            view.Write(new Velocity { X = 3, Y = 4 });
        }
    }
    private readonly struct MixedWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value)
        {
            view.WriteShared(new SceneId { Value = 3 });
            view.Write(new NameIndex { Value = "published" });
            view.WriteSparse(new Damage { Amount = 5 });
            ReadOnlyMemory<IntElement> buffer = new IntElement[] { new() { Value = 13 } };
            view.WriteBuffer(in buffer);
        }
    }
    private readonly struct MixedFaultWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value)
        {
            new MixedWriter().Write(view, in value);
            throw new BundleWriteException();
        }
    }
    private readonly struct SharedWriter : IBundleWriter<int>
    {
        public void Write(BundleWriteView view, in int value)
        {
            view.WriteShared(new SceneId { Value = 6 });
            view.WriteShared(new SceneId { Value = 6 });
        }
    }
    private readonly record struct EntityOutput(Entity[] Entities);
    private readonly struct OutputWriter : IBundleWriter<EntityOutput>
    {
        public void Write(BundleWriteView view, in EntityOutput output)
        {
            view.Write(new Position { X = view.Index });
            output.Entities[view.Index] = view.Entity;
        }
    }
    private readonly record struct IndexInput(World World);
    private readonly struct LateIndexWriter : IBundleWriter<IndexInput>
    {
        public void Write(BundleWriteView view, in IndexInput input)
        {
            string key = $"name-{view.Index}";
            view.Write(new IndexedName { Value = key });
            if (view.Index == 0) Assert.Single(ReadIndexedEntities<IndexedName, string>(input.World, key));
        }
    }
    private readonly struct EarlyIndexWriter : IBundleWriter<IndexInput>
    {
        public void Write(BundleWriteView view, in IndexInput input)
        {
            _ = input.World.Index<IndexedName, string>();
            view.Write(new IndexedName { Value = "ready" });
        }
    }
    private sealed class BundleWriteException : Exception;
}
