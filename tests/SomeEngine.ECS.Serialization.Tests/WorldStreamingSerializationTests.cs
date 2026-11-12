using System.Collections.Concurrent;
using SomeEngine.ECS;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Relations;
using SomeEngine.ECS.Serialization;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;
using DefaultHierarchy = SomeEngine.ECS.Hierarchy.Hierarchy;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class WorldStreamingSerializationTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ComponentThenWorldReadFromOneStreamPreservesBothValues(bool seekable)
    {
        var registry = new SerializationRegistry()
            .RegisterCodec<SerName, SerNameCodec>(default)
            .RegisterCodec<SerPosition, SerPositionFullCodec>(default);
        var name = new SerName { Value = "入口 🚲", Id = 7 };
        using var world = new World();
        Entity entity = world.Create(new SerPosition { X = 17, Y = 23 });
        using var output = new MemoryStream();
        WorldSerializer.WriteComponent(output, name, registry);
        new WorldSerializer(registry).Save(world, output);
        using Stream input = seekable
            ? new MemoryStream(output.ToArray(), writable: false)
            : new SequentialReadStream(output.ToArray());
        SerName restoredName = WorldSerializer.ReadComponent<SerName>(input, registry);
        using World restoredWorld = WorldSerializer.ReadWorld(input, registry,
            new WorldLoadOptions(RequireEndOfInput: true));
        Assert.Equal(name.Value, restoredName.Value);
        Assert.Equal(name.Id, restoredName.Id);
        Assert.Equal(1, restoredWorld.EntityCount);
        Assert.Equal(17, restoredWorld.Read<SerPosition>(entity).X);
        Assert.Equal(23, restoredWorld.Read<SerPosition>(entity).Y);
    }

    private sealed class SequentialReadStream(byte[] bytes) : MemoryStream(bytes, writable: false)
    {
        public override bool CanSeek => false;
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    }

    [Fact]
    public void DurableWorldUsesManagedStorageWithoutChangingItsEncodingOrReadBehavior()
    {
        var registry = new SerializationRegistry().Register<SerPosition, PagePositionCodec>(new(
            Guid.Parse("57C630DB-1452-42CB-915A-A0CE63728294"), "tests.page-position", 1));
        using var world = new World();
        var entity = world.Create(new SerPosition { X = 17, Y = 23 });
        using var stream = new MemoryStream();
        new WorldSerializer(registry).Save(world, stream, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        using var expected = new MemoryStream();
        new WorldSerializer(registry).Save(world, expected, new SerializeOptions(Purpose: SavePurpose.DurableSave));
        byte[] encoded = new byte[stream.Length];
        stream.Position = 0;
        stream.ReadExactly(encoded);
        Assert.Equal(expected.ToArray(), encoded);

        byte[] retained = stream.ToArray();
        using var input = new MemoryStream(retained, writable: false);
        using World decoded = WorldSerializer.ReadWorld(input, registry,
            new() { RequiredPurpose = SavePurpose.DurableSave });
        Assert.True(decoded.IsAlive(entity));
        Assert.Equal(17, decoded.Read<SerPosition>(entity).X);
        Assert.Equal(23, decoded.Read<SerPosition>(entity).Y);
    }

    private struct PagePositionCodec : IComponentCodec<SerPosition>
    {
        public void Write(ref DataWriter writer, in SerPosition value)
        {
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
        }
        public void Read(ref DataReader reader, out SerPosition value)
            => value = new() { X = reader.ReadSingle(), Y = reader.ReadSingle() };
    }

    [Fact]
    public void FailedOutputCanBeRetriedWithoutChangingSerializedData()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        world.Create(new SerPosition { X = 1, Y = 2 });

        using var successful = new MemoryStream();
        new WorldSerializer(registry).Save(world, successful);

        using var faulting = new ThrowingWriteStream();
        Assert.Throws<IOException>(() => new WorldSerializer(registry).Save(world, faulting));

        // The owner-thread root context must be gone after the output exception. A subsequent
        // capture on this same test thread must pin the current successor, not collide with or
        // read through the failed capture's retained root.
        using var retry = new MemoryStream();
        new WorldSerializer(registry).Save(world, retry);
        Assert.Equal(successful.ToArray(), retry.ToArray());
        Assert.Equal(1, world.EntityCount);
    }

    [Theory]
    [InlineData(WriteFailureKind.Io)]
    [InlineData(WriteFailureKind.Canceled)]
    [InlineData(WriteFailureKind.Disposed)]
    public async Task WorldWrite_StreamFailureReleasesAdmission(WriteFailureKind kind)
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        world.Create(new SerPosition { X = 1, Y = 2 });
        Exception expected = kind switch
        {
            WriteFailureKind.Io => new IOException("Injected output fault."),
            WriteFailureKind.Canceled => new OperationCanceledException("Injected cancellation."),
            WriteFailureKind.Disposed => new ObjectDisposedException("destination"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using var output = new FaultingWriteStream(expected);

        Exception? actual = Record.Exception(() =>
            new WorldSerializer(registry).Save(world, output));
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        await AssertMutationCompletesAsync(world);
    }

    [Fact]
    public async Task WorldWrite_ComponentCodecFailureReleasesAdmission()
    {
        var registry = new SerializationRegistry()
            .RegisterCodec<ThrowingWorldComponent, ThrowingWorldComponentCodec>(default);
        var world = new World();
        world.Create(new ThrowingWorldComponent { Value = 1 });
        using var output = new MemoryStream();

        Assert.Throws<InvalidOperationException>(() =>
            new WorldSerializer(registry).Save(world, output));

        await AssertMutationCompletesAsync(world);
    }

    [Fact]
    public void WorldWrite_ComponentCodecReentryMutatesSuccessorWithoutChangingRetainedOutput()
    {
        using var world = new World();
        var registry = new SerializationRegistry()
            .RegisterCodec<ReentrantWorldComponent, ReentrantWorldComponentCodec>(new ReentrantWorldComponentCodec(world));
        world.Create(new ReentrantWorldComponent { Value = 1 });
        using var output = new MemoryStream();
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world);
        new WorldSerializer(registry).Save(checkpoint, output);

        Assert.Equal(2, world.EntityCount);
        output.Position = 0;
        using World restored = WorldSerializer.ReadWorld(output, registry);
        Assert.Equal(1, restored.EntityCount);
    }

    [Fact]
    public void WorldWrite_OutputCallbackMutatesPublishedSuccessor()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var world = new World();
        world.Create(new SerPosition { X = 1, Y = 2 });
        Entity created = default;
        using var output = new CallbackWriteStream(() => created = world.Create());

        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world);
        new WorldSerializer(registry).Save(checkpoint, output);

        Assert.True(world.IsAlive(created));
        Assert.Equal(2, world.EntityCount);
    }

    [Fact]
    public void WorldWrite_OutputCallbackCanDisposeAnUnrelatedWorld()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var serializedWorld = new World();
        serializedWorld.Create(new SerPosition { X = 1, Y = 2 });
        using var otherWorld = new World();
        using var output = new CallbackWriteStream(otherWorld.Dispose);

        new WorldSerializer(registry).Save(serializedWorld, output);

        Assert.Equal(1, serializedWorld.EntityCount);
        Assert.Throws<ObjectDisposedException>(() => otherWorld.Create());
    }

    [Fact]
    public void WriteEntities_InvalidMemberFailsBeforeOutputWithoutPublishingSuccessor()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity valid = world.Create(new SerPosition { X = 1, Y = 2 });
        Entity invalid = world.Create();
        world.Destroy(invalid);
        using var output = new MemoryStream();

        Assert.Throws<ArgumentException>(() =>
            new WorldSerializer(registry).Save(world, [valid, invalid], output));

        Assert.Equal(0, output.Length);
        Assert.True(world.IsAlive(valid));
    }

    [Fact]
    public void WorldSerializationPreservesQueryResults()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity entity = world.Create(new SerPosition { X = 1, Y = 2 });
        var query = world.Query(new QueryDescription().With<SerPosition>());

        using var snapshot = new MemoryStream();
        new WorldSerializer(registry).Save(world, snapshot);

        int matched = 0;
        using (QueryScope scope = query.OpenRead())
            foreach (QueryChunk chunk in scope)
                foreach (Entity match in chunk.Entities)
                {
                    Assert.Equal(entity, match);
                    matched++;
                }
        Assert.Equal(1, matched);
    }

    [Fact]
    public void CanonicalWorldStateHash_IsStableAndChangesWithState()
    {
        var registry = new SerializationRegistry()
            .Register<SerPosition>()
            .RegisterSparse<SerSparse>(SaveTypes.Key<SerSparse>());
        var first = new World();
        Entity firstEntity = first.Create(new SerPosition { X = 1, Y = 2 });
        first.SetSparse(firstEntity, new SerSparse { Value = 3 });
        var second = new World();
        Entity secondEntity = second.Create(new SerPosition { X = 1, Y = 2 });
        second.SetSparse(secondEntity, new SerSparse { Value = 3 });

        Digest256 firstHash = WorldSerializer.ComputeWorldStateHash(first, registry);
        Digest256 repeatedHash = WorldSerializer.ComputeWorldStateHash(first, registry);
        Digest256 equivalentHash = WorldSerializer.ComputeWorldStateHash(second, registry);

        Assert.Equal(firstHash, repeatedHash);
        Assert.Equal(firstHash, equivalentHash);

        second.Set(secondEntity, new SerPosition { X = 9, Y = 2 });
        Digest256 mutatedHash = WorldSerializer.ComputeWorldStateHash(second, registry);
        Assert.NotEqual(firstHash, mutatedHash);
    }

    [Fact]
    public async Task BlockedOutput_AllowsSuccessorMutationAndKeepsSerializedBytesStable()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity captured = world.Create(new SerPosition { X = 1, Y = 2 });
        using var output = new GateWriteStream();

        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world);
        Task write = Task.Run(() => new WorldSerializer(registry).Save(checkpoint, output));
        Assert.True(output.WaitUntilWrite(TimeSpan.FromSeconds(10)));

        Task<Entity> mutate = Task.Run(() =>
        {
            world.Set(captured, new SerPosition { X = 10, Y = 20 });
            return world.Create(new SerPosition { X = 30, Y = 40 });
        });
        Entity createdAfterWrite;
        try
        {
            createdAfterWrite = await mutate.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(10, world.Read<SerPosition>(captured).X);
            Assert.True(world.IsAlive(createdAfterWrite));
        }
        finally
        {
            output.Release();
        }
        await write.WaitAsync(TimeSpan.FromSeconds(10));

        using var input = new MemoryStream(output.ToArray(), writable: false);
        using World restored = WorldSerializer.ReadWorld(input, registry);
        Assert.Equal(1, restored.EntityCount);
        Assert.True(restored.IsAlive(captured));
        Assert.Equal(1, restored.Read<SerPosition>(captured).X);
    }

    [Theory]
    [InlineData(NarrowCaptureOperation.Entity)]
    [InlineData(NarrowCaptureOperation.EntitySet)]
    [InlineData(NarrowCaptureOperation.Query)]
    public async Task NarrowWrite_ExplicitCaptureAllowsSourceMutationDuringOutput(
        NarrowCaptureOperation operation)
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity first = world.Create(new SerPosition { X = 1, Y = 2 });
        Entity second = world.Create(new SerPosition { X = 3, Y = 4 });
        Entity[] selected = [first, second];
        Query query = world.Query(new QueryDescription().With<SerPosition>());

        using var baseline = new MemoryStream();
        WriteNarrow(operation, baseline, world, first, selected, query, registry);

        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        using var captured = new World();
        checkpoint.Restore(captured);
        Query capturedQuery = captured.Query(new QueryDescription().With<SerPosition>());

        using var output = new GateWriteStream();
        Task write = Task.Run(() =>
            WriteNarrow(operation, output, captured, first, selected, capturedQuery, registry));
        Assert.True(output.WaitUntilWrite(TimeSpan.FromSeconds(10)));

        try
        {
            await Task.Run(() =>
            {
                world.Set(first, new SerPosition { X = 10, Y = 20 });
                world.Destroy(second);
                world.Create(new SerPosition { X = 30, Y = 40 });
            }).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(10, world.Read<SerPosition>(first).X);
            Assert.False(world.IsAlive(second));
            Assert.False(write.IsCompleted);
        }
        finally
        {
            output.Release();
        }

        await write.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(baseline.ToArray(), output.ToArray());
    }

    [Fact]
    public async Task WorldWrite_CapturedDataAllowsSourceDisposalDuringEncoding()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity entity = world.Create(new SerPosition { X = 1, Y = 2 });
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world);
        using var output = new GateWriteStream();
        Task write = Task.Run(() => new WorldSerializer(registry).Save(checkpoint, output));
        Assert.True(output.WaitUntilWrite(TimeSpan.FromSeconds(10)));
        try
        {
            await Task.Run(world.Dispose).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Throws<ObjectDisposedException>(() => world.Create());
            Assert.False(write.IsCompleted);
        }
        finally { output.Release(); }
        await write.WaitAsync(TimeSpan.FromSeconds(10));
        using var input = new MemoryStream(output.ToArray(), writable: false);
        using World restored = WorldSerializer.ReadWorld(input, registry);
        Assert.Equal(1, restored.EntityCount);
        Assert.Equal(1, restored.Read<SerPosition>(entity).X);
    }

    [Fact]
    public void WorldWrite_InvokesComponentAndBufferCodecsExactlyOncePerValue()
    {
        var componentCalls = new CodecCalls();
        var bufferCalls = new CodecCalls();
        var registry = new SerializationRegistry()
            .RegisterCodec<CountingWorldComponent, CountingWorldComponentCodec>(new CountingWorldComponentCodec(componentCalls))
            .RegisterBufferCodec<CountingWorldBuffer, CountingWorldBufferCodec>(new CountingWorldBufferCodec(bufferCalls));
        var world = new World();
        world.Create(new CountingWorldComponent { Value = 1 });
        world.Create(new CountingWorldComponent { Value = 2 });
        Entity buffered = world.Create();
        world.AddBuffer<CountingWorldBuffer>(buffered);
        int[] values = [3, 4, 5];
        {
            using DynamicBuffer<CountingWorldBuffer> buffer = world.GetBuffer<CountingWorldBuffer>(buffered);
            for (int i = 0; i < values.Length; i++)
                buffer.Add(new CountingWorldBuffer { Value = values[i] });
        }

        using var output = new MemoryStream();
        new WorldSerializer(registry).Save(world, output);

        Assert.Equal(2, componentCalls.Writes);
        Assert.Equal(3, bufferCalls.Writes);
    }

    [Fact]
    public void LargeArchetypeAndSparseWorldRoundTripsThroughNonSeekableOutput()
    {
        const int entityCount = 20_000;
        var registry = new SerializationRegistry()
            .Register<SerPosition>()
            .RegisterSparse<SerSparse>(SaveTypes.Key<SerSparse>());
        using var world = new World(entityCount);
        var entities = new Entity[entityCount];
        for (int i = 0; i < entityCount; i++)
        {
            Entity entity = world.Create(new SerPosition { X = i, Y = -i });
            entities[i] = entity;
            if (i % 3 == 0)
                world.SetSparse(entity, new SerSparse { Value = i });
        }

        using var output = new NonSeekableWriteStream();
        new WorldSerializer(registry).Save(world, output);

        using var input = new MemoryStream(output.ToArray(), writable: false);
        using World loaded = WorldSerializer.ReadWorld(input, registry);
        Assert.Equal(entityCount, loaded.EntityCount);
        for (int i = 0; i < entities.Length; i++)
        {
            Assert.True(loaded.IsAlive(entities[i]));
            Assert.Equal(i, loaded.Read<SerPosition>(entities[i]).X);
            Assert.Equal(-i, loaded.Read<SerPosition>(entities[i]).Y);
            if (i % 3 == 0)
                Assert.Equal(i, loaded.ReadSparse<SerSparse>(entities[i]).Value);
            else
                Assert.False(loaded.HasSparse<SerSparse>(entities[i]));
        }
    }

    [Fact]
    public void SparseMembershipWorkingSet_RespectsConfiguredLimit()
    {
        var registry = new SerializationRegistry().RegisterSparse<SerSparse>(SaveTypes.Key<SerSparse>());
        var world = new World();
        for (int i = 0; i < 3; i++)
        {
            Entity entity = world.Create();
            world.SetSparse(entity, new SerSparse { Value = i });
        }

        using var rejected = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() =>
            new WorldSerializer(registry).Save(world, rejected, new SerializeOptions(MaximumSparseMemberships: 2)));

        using var accepted = new MemoryStream();
        new WorldSerializer(registry).Save(world, accepted, new SerializeOptions(MaximumSparseMemberships: 3));
        Assert.True(accepted.Length > 0);
    }

    [Fact]
    public void CheckpointCopiesManagedRowsAndSharesOrdinaryReferenceFields()
    {
        var registry = new SerializationRegistry()
            .RegisterCodec<SnapshotReferenceComponent, SnapshotReferenceCodec>(default);
        int[] values = [1, 2, 3];
        using var world = new World();
        Entity entity = world.Create(new SnapshotReferenceComponent { Values = values });
        using var checkpoint = new WorldCheckpoint();
        checkpoint.Capture(world, CheckpointProgress.Preserve);
        values[0] = 99;
        world.Set(entity, new SnapshotReferenceComponent { Values = [7, 8] });
        using var output = new MemoryStream();
        new WorldSerializer(registry).Save(checkpoint, output);
        output.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(output, registry);
        Assert.Equal(new[] { 99, 2, 3 }, loaded.Read<SnapshotReferenceComponent>(entity).Values);
        Assert.Equal(new[] { 7, 8 }, world.Read<SnapshotReferenceComponent>(entity).Values);
    }

    [Fact]
    public void WholeWorldSnapshot_AllowsRegisteredManagedReferenceTypeWhenAbsent()
    {
        var registry = new SerializationRegistry()
            .RegisterCodec<SnapshotReferenceComponent, SnapshotReferenceCodec>(default);
        var world = new World();
        Entity entity = world.Create();
        using var output = new MemoryStream();

        new WorldSerializer(registry).Save(world, output);

        Assert.True(output.Length > 0);
        Assert.True(world.IsAlive(entity));
        Assert.False(world.Has<SnapshotReferenceComponent>(entity));
    }

    [Fact]
    public void TopologyWorkingSet_RespectsRecordAndPayloadLimits_OnNonSeekableOutput()
    {
        var registry = new SerializationRegistry()
            .RegisterHierarchy<DefaultHierarchyDomain>(SaveTypes.Key<DefaultHierarchyDomain>());
        var world = new World();
        Entity parent = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();
        DefaultHierarchy.SetParent(world, first, parent);
        DefaultHierarchy.SetParent(world, second, parent);

        using var recordRejected = new NonSeekableWriteStream();
        Assert.Throws<InvalidOperationException>(() =>
            new WorldSerializer(registry).Save(world, recordRejected, new SerializeOptions(MaximumTopologyRecords: 1)));

        using var payloadRejected = new NonSeekableWriteStream();
        Assert.Throws<InvalidOperationException>(() =>
            new WorldSerializer(registry).Save(world, payloadRejected, new SerializeOptions(
                    MaximumTopologyRecords: 16,
                    MaximumTopologyPayloadBytes: 1)));

        using var accepted = new NonSeekableWriteStream();
        new WorldSerializer(registry).Save(world, accepted, new SerializeOptions(
                MaximumTopologyRecords: 16,
                MaximumTopologyPayloadBytes: 1024));
        using var input = new MemoryStream(accepted.ToArray(), writable: false);
        using World loaded = WorldSerializer.ReadWorld(input, registry);
        Assert.Equal(parent, DefaultHierarchy.GetParent(loaded, first));
        Assert.Equal(parent, DefaultHierarchy.GetParent(loaded, second));
    }

    [Fact]
    public void HierarchyRecordBudget_RejectsTheDirectWriteWithoutSnapshotDto()
    {
        var registry = new SerializationRegistry()
            .RegisterHierarchy<DefaultHierarchyDomain>(SaveTypes.Key<DefaultHierarchyDomain>());
        var world = new World();
        Entity parent = world.Create();
        Entity first = world.Create();
        Entity second = world.Create();
        Hierarchy<DefaultHierarchyDomain>.SetOrdered(
            world,
            parent, true);
        Hierarchy<DefaultHierarchyDomain>.SetParent(world, first, parent);
        Hierarchy<DefaultHierarchyDomain>.SetParent(world, second, parent);
        using var rejected = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() =>
            new WorldSerializer(registry).Save(world, rejected, new SerializeOptions(MaximumTopologyRecords: 4)));

        using var accepted = new MemoryStream();
        new WorldSerializer(registry).Save(world, accepted, new SerializeOptions(MaximumTopologyRecords: 5));
        Assert.True(accepted.Length > 0);
    }

    [Fact]
    public void RelationTopologyWrite_VisitsOnlyEdgesAndActualOrderedShards()
    {
        var registry = new SerializationRegistry()
            .Register<StreamingRelation>()
            .RegisterRelation<StreamingRelation>();
        var world = new World();
        Entity source = world.Create();
        Entity target = world.Create();
        world.SetRelationAdjacencyOrder<StreamingRelation>(
            source,
            RelationAdjacencyRole.Outgoing,
            RelationAdjacencyOrderPolicy.Ordered);
        world.SetRelationAdjacencyOrder<StreamingRelation>(
            target,
            RelationAdjacencyRole.Incoming,
            RelationAdjacencyOrderPolicy.Ordered);
        world.CreateRelation(source, target, new StreamingRelation { Value = 1 });
        for (int i = 0; i < 10_000; i++)
            world.Create();

        using var rejected = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() =>
            new WorldSerializer(registry).Save(world, rejected, new SerializeOptions(MaximumTopologyRecords: 4)));

        using var accepted = new MemoryStream();
        new WorldSerializer(registry).Save(world, accepted, new SerializeOptions(MaximumTopologyRecords: 5));
        accepted.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(accepted, registry);
        Assert.Single(TopologyValues.Outgoing<StreamingRelation>(loaded, source));
    }

    [Fact]
    public void RepeatedWorldSerializationKeepsLatestClockAndBytes()
    {
        var registry = new SerializationRegistry()
            .Register<SavedTrackedPosition>()
            .RegisterSparse<SerSparse>(SaveTypes.Key<SerSparse>());
        var world = new World();
        Entity first = world.Create(new SavedTrackedPosition { X = 1, Y = 2 });
        Entity second = world.Create(new SavedTrackedPosition { X = 3, Y = 4 });
        world.SetSparse(second, new SerSparse { Value = 5 });
        ulong versionBefore = world.Version;
        ulong tickBefore = world.Version;

        using var beforeMutation = new MemoryStream();
        new WorldSerializer(registry).Save(world, beforeMutation, progress: CheckpointProgress.Preserve);

        world.Set(first, new SavedTrackedPosition { X = 100, Y = 200 });
        world.Set(first, new SavedTrackedPosition { X = 1, Y = 2 });
        ulong versionAfter = world.Version;
        Assert.NotEqual(versionBefore, versionAfter);

        using var afterMutation = new MemoryStream();
        new WorldSerializer(registry).Save(world, afterMutation, progress: CheckpointProgress.Preserve);
        using var repeated = new MemoryStream();
        new WorldSerializer(registry).Save(world, repeated, progress: CheckpointProgress.Preserve);
        Assert.Equal(afterMutation.ToArray(), repeated.ToArray());

        beforeMutation.Position = 0;
        afterMutation.Position = 0;
        using var earlier = WorldSerializer.ReadWorld(beforeMutation, registry);
        using var later = WorldSerializer.ReadWorld(afterMutation, registry);
        Assert.Equal(tickBefore, earlier.Version);
        Assert.Equal(world.Version, later.Version);
        Assert.Equal(1, later.Read<SavedTrackedPosition>(first).X);
        Assert.Equal(2, later.Read<SavedTrackedPosition>(first).Y);
    }

    private struct SavedTrackedPosition : ITrackedComponent
    {
        public float X;
        public float Y;
    }

    private static async Task AssertMutationCompletesAsync(World world)
    {
        Entity created = await Task.Run(() => world.Create())
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(world.IsAlive(created));
    }

    private static void WriteNarrow(
        NarrowCaptureOperation operation,
        Stream output,
        World world,
        Entity entity,
        Entity[] entities,
        Query query,
        SerializationRegistry registry)
    {
        switch (operation)
        {
            case NarrowCaptureOperation.Entity:
                new WorldSerializer(registry).Save(world, entity, output);
                break;
            case NarrowCaptureOperation.EntitySet:
                new WorldSerializer(registry).Save(world, entities, output);
                break;
            case NarrowCaptureOperation.Query:
                new WorldSerializer(registry).Save(world, query, output);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(operation));
        }
    }

    public enum WriteFailureKind
    {
        Io,
        Canceled,
        Disposed,
    }

    public enum ActiveCaptureOperation
    {
        Entity,
        Query,
        World,
        Checkpoint,
    }

    public enum NarrowCaptureOperation
    {
        Entity,
        EntitySet,
        Query,
    }

    private struct ThrowingWorldComponent : IComponent
    {
        public int Value;
    }

    private struct ThrowingWorldComponentCodec : IComponentCodec<ThrowingWorldComponent>
    {
        public void Write(ref DataWriter writer, in ThrowingWorldComponent value) =>
            throw new InvalidOperationException("Injected codec fault.");

        public void Read(ref DataReader reader, out ThrowingWorldComponent value) =>
            value = new ThrowingWorldComponent { Value = reader.ReadInt32() };
    }

    private struct ReentrantWorldComponent : IComponent
    {
        public int Value;
    }

    private struct ReentrantWorldComponentCodec(World world) : IComponentCodec<ReentrantWorldComponent>
    {

        public void Write(ref DataWriter writer, in ReentrantWorldComponent value)
        {
            world.Create();
            writer.WriteInt32(value.Value);
        }

        public void Read(ref DataReader reader, out ReentrantWorldComponent value) =>
            value = new ReentrantWorldComponent { Value = reader.ReadInt32() };
    }

    private struct CountingWorldComponent : IComponent
    {
        public int Value;
    }

    private struct CountingWorldComponentCodec(CodecCalls calls) : IComponentCodec<CountingWorldComponent>
    {
        public void Write(ref DataWriter writer, in CountingWorldComponent value)
        {
            Interlocked.Increment(ref calls.Writes);
            writer.WriteInt32(value.Value);
        }

        public void Read(ref DataReader reader, out CountingWorldComponent value) =>
            value = new CountingWorldComponent { Value = reader.ReadInt32() };
    }

    private struct CountingWorldBuffer : IBufferElement
    {
        public int Value;
    }

    private struct CountingWorldBufferCodec(CodecCalls calls) : IComponentCodec<CountingWorldBuffer>
    {
        public void Write(ref DataWriter writer, in CountingWorldBuffer value)
        {
            Interlocked.Increment(ref calls.Writes);
            writer.WriteInt32(value.Value);
        }

        public void Read(ref DataReader reader, out CountingWorldBuffer value) =>
            value = new CountingWorldBuffer { Value = reader.ReadInt32() };
    }

    private struct SnapshotReferenceComponent : IComponent
    {
        public int[]? Values;
    }

    [RelationSchema(RelationDirection.Directed, RelationCardinality.Parallel)]
    private struct StreamingRelation : IComponent
    {
        public int Value;
    }

    private struct SnapshotReferenceCodec : IComponentCodec<SnapshotReferenceComponent>
    {
        public void Write(ref DataWriter writer, in SnapshotReferenceComponent value)
        {
            int[] values = value.Values ?? Array.Empty<int>();
            writer.WriteInt32(values.Length);
            for (int i = 0; i < values.Length; i++)
                writer.WriteInt32(values[i]);
        }

        public void Read(ref DataReader reader, out SnapshotReferenceComponent value)
        {
            int count = reader.ReadInt32();
            var values = new int[count];
            for (int i = 0; i < values.Length; i++)
                values[i] = reader.ReadInt32();
            value = new SnapshotReferenceComponent { Values = values };
        }
    }

    private sealed class ThrowingWriteStream : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override void Write(byte[] buffer, int offset, int count) =>
            throw new IOException("Injected output fault.");
        public override void Write(ReadOnlySpan<byte> buffer) =>
            throw new IOException("Injected output fault.");
        public override void WriteByte(byte value) =>
            throw new IOException("Injected output fault.");
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class FaultingWriteStream : Stream
    {
        private readonly Exception _failure;

        internal FaultingWriteStream(Exception failure) => _failure = failure;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override void Write(byte[] buffer, int offset, int count) => throw _failure;
        public override void Write(ReadOnlySpan<byte> buffer) => throw _failure;
        public override void WriteByte(byte value) => throw _failure;
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private sealed class CallbackWriteStream : Stream
    {
        private readonly Action _callback;
        private int _invoked;

        internal CallbackWriteStream(Action callback) => _callback = callback;

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }
        public override void Flush()
        {
        }
        public override void Write(byte[] buffer, int offset, int count) => Invoke();
        public override void Write(ReadOnlySpan<byte> buffer) => Invoke();
        public override void WriteByte(byte value) => Invoke();
        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        private void Invoke()
        {
            if (Interlocked.Exchange(ref _invoked, 1) == 0)
                _callback();
        }
    }

    private sealed class GateWriteStream : Stream
    {
        internal Action? WriteObserved { get; init; }

        private readonly MemoryStream _inner = new();
        private readonly ManualResetEventSlim _entered = new(initialState: false);
        private readonly ManualResetEventSlim _release = new(initialState: false);
        private readonly Exception? _failure;
        private int _blocked;

        internal GateWriteStream(Exception? failure = null) => _failure = failure;

        internal bool WaitUntilWrite(TimeSpan timeout) => _entered.Wait(timeout);

        internal void Release() => _release.Set();

        internal byte[] ToArray() => _inner.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            BlockFirstWrite();
            if (_failure is not null)
                throw _failure;
            _inner.Write(buffer, offset, count);
            WriteObserved?.Invoke();
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BlockFirstWrite();
            if (_failure is not null)
                throw _failure;
            _inner.Write(buffer);
            WriteObserved?.Invoke();
        }

        public override void WriteByte(byte value)
        {
            BlockFirstWrite();
            if (_failure is not null)
                throw _failure;
            _inner.WriteByte(value);
            WriteObserved?.Invoke();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
                _inner.Dispose();
                _entered.Dispose();
                _release.Dispose();
            }
            base.Dispose(disposing);
        }

        private void BlockFirstWrite()
        {
            if (Interlocked.Exchange(ref _blocked, 1) != 0)
                return;

            _entered.Set();
            _release.Wait();
        }
    }

    private sealed class NonSeekableWriteStream : Stream
    {
        private readonly MemoryStream _bytes = new();
        internal byte[] ToArray() => _bytes.ToArray();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override void Write(byte[] buffer, int offset, int count) => _bytes.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _bytes.Write(buffer);
        public override void WriteByte(byte value) => _bytes.WriteByte(value);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _bytes.Dispose();
            base.Dispose(disposing);
        }
    }
}
