using System.Reflection;
using SomeEngine.ECS;
using SomeEngine.ECS.Archetypes;
using SomeEngine.ECS.Components;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;
using SomeEngine.ECS.Queries;
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
    [Fact]
    public void DurableWorldUsesManagedStorageWithoutChangingItsEncodingOrReadBehavior()
    {
        var registry = new SerializationRegistry().Register<SerPosition, PagePositionCodec>(new(
            Guid.Parse("57C630DB-1452-42CB-915A-A0CE63728294"), "tests.page-position", 1));
        using var world = new World();
        var entity = world.CreateEntity(new SerPosition { X = 17, Y = 23 });
        using var stream = new MemoryStream();
        WorldSerializer.WriteDurableWorld(stream, world, registry);
        using var expected = new MemoryStream();
        WorldSerializer.WriteDurableWorld(expected, world, registry);
        byte[] encoded = new byte[stream.Length];
        stream.Position = 0;
        stream.ReadExactly(encoded);
        Assert.Equal(expected.ToArray(), encoded);

        byte[] retained = stream.ToArray();
        using var input = new MemoryStream(retained, writable: false);
        using World decoded = WorldSerializer.ReadWorld(input, registry,
            new() { RequiredContract = SerializationContract.DurableSave });
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
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });

        using var successful = new MemoryStream();
        WorldSerializer.WriteWorld(successful, world, registry);

        using var faulting = new ThrowingWriteStream();
        Assert.Throws<IOException>(() => WorldSerializer.WriteWorld(faulting, world, registry));

        // The owner-thread root context must be gone after the output exception. A subsequent
        // capture on this same test thread must pin the current successor, not collide with or
        // read through the failed capture's retained root.
        using var retry = new MemoryStream();
        WorldSerializer.WriteWorld(retry, world, registry);
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
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Exception expected = kind switch
        {
            WriteFailureKind.Io => new IOException("Injected output fault."),
            WriteFailureKind.Canceled => new OperationCanceledException("Injected cancellation."),
            WriteFailureKind.Disposed => new ObjectDisposedException("destination"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using var output = new FaultingWriteStream(expected);

        Exception? actual = Record.Exception(() =>
            WorldSerializer.WriteWorld(output, world, registry));
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        await AssertMutationCompletesAsync(world);
    }

    [Fact]
    public async Task WorldWrite_ComponentCodecFailureReleasesAdmission()
    {
        var registry = new SerializationRegistry()
            .Register<ThrowingWorldComponent, ThrowingWorldComponentCodec>();
        var world = new World();
        world.CreateEntity(new ThrowingWorldComponent { Value = 1 });
        using var output = new MemoryStream();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(output, world, registry));
        Assert.Contains("Injected codec fault", error.Message);
        await AssertMutationCompletesAsync(world);
    }

    [Fact]
    public void WorldWrite_ComponentCodecReentryMutatesSuccessorWithoutChangingRetainedOutput()
    {
        var registry = new SerializationRegistry()
            .Register<ReentrantWorldComponent, ReentrantWorldComponentCodec>();
        using var world = new World();
        world.CreateEntity(new ReentrantWorldComponent { Value = 1 });
        using var output = new MemoryStream();
        ReentrantWorldComponentCodec.Target = world;
        try
        {
            WorldSerializer.WriteWorld(output, world, registry);
        }
        finally
        {
            ReentrantWorldComponentCodec.Target = null;
        }

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
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Entity created = default;
        using var output = new CallbackWriteStream(() => created = world.CreateEntity());

        WorldSerializer.WriteWorld(output, world, registry);

        Assert.True(world.IsAlive(created));
        Assert.Equal(2, world.EntityCount);
    }

    [Fact]
    public void WorldWrite_OutputCallbackCanDisposeAnUnrelatedWorld()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        using var serializedWorld = new World();
        serializedWorld.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var otherWorld = new World();
        using var output = new CallbackWriteStream(otherWorld.Dispose);

        WorldSerializer.WriteWorld(output, serializedWorld, registry);

        Assert.Equal(1, serializedWorld.EntityCount);
        Assert.Throws<ObjectDisposedException>(() => otherWorld.CreateEntity());
    }

    [Fact]
    public void WriteEntities_InvalidMemberFailsBeforeOutputWithoutPublishingSuccessor()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity valid = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var invalid = new Entity(valid.Index + 100, valid.Generation);
        using var output = new MemoryStream();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteEntities(output, world, [valid, invalid], registry));

        Assert.Contains("not alive", error.Message);
        Assert.Equal(0, output.Length);
        Assert.True(world.IsAlive(valid));
    }

    [Fact]
    public void WorldSerializationPreservesQueryResults()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity entity = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        var query = world.Query(world.QueryDefinition().Read<SerPosition>());

        using var snapshot = new MemoryStream();
        WorldSerializer.WriteWorld(snapshot, world, registry);

        int matched = 0;
        world.ExecuteQuery(query, cursor =>
        {
            foreach (var row in cursor.Rows)
            {
                Assert.Equal(entity, row.Entity);
                matched++;
            }
        });
        Assert.Equal(1, matched);
    }


    [Fact]
    public void CanonicalWorldStateHash_IsStableAndChangesWithState()
    {
        var registry = new SerializationRegistry()
            .Register<SerPosition>()
            .RegisterSparse<SerSparse>();
        var first = new World();
        Entity firstEntity = first.CreateEntity(new SerPosition { X = 1, Y = 2 });
        first.AddSparse(firstEntity, new SerSparse { Value = 3 });
        var second = new World();
        Entity secondEntity = second.CreateEntity(new SerPosition { X = 1, Y = 2 });
        second.AddSparse(secondEntity, new SerSparse { Value = 3 });

        Digest256 firstHash = WorldSerializer.ComputeWorldStateHash(first, registry);
        Digest256 repeatedHash = WorldSerializer.ComputeWorldStateHash(first, registry);
        Digest256 equivalentHash = WorldSerializer.ComputeWorldStateHash(second, registry);

        Assert.Equal(firstHash, repeatedHash);
        Assert.Equal(firstHash, equivalentHash);

        second.Replace(secondEntity, new SerPosition { X = 9, Y = 2 });
        Digest256 mutatedHash = WorldSerializer.ComputeWorldStateHash(second, registry);
        Assert.NotEqual(firstHash, mutatedHash);
    }

    [Fact]
    public async Task BlockedOutput_AllowsSuccessorMutationAndKeepsSerializedBytesStable()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity captured = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var output = new GateWriteStream();

        Task write = Task.Run(() => WorldSerializer.WriteWorld(output, world, registry));
        Assert.True(output.WaitUntilWrite(TimeSpan.FromSeconds(10)));

        Task<Entity> mutate = Task.Run(() =>
        {
            world.Replace(captured, new SerPosition { X = 10, Y = 20 });
            return world.CreateEntity(new SerPosition { X = 30, Y = 40 });
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
    public async Task NarrowWrite_BlockedOutputAllowsMutationAndUsesPinnedRoot(
        NarrowCaptureOperation operation)
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        Entity first = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Entity second = world.CreateEntity(new SerPosition { X = 3, Y = 4 });
        Entity[] selected = [first, second];
        QueryHandle query = world.Query(world.QueryDefinition().Read<SerPosition>());

        using var baseline = new MemoryStream();
        WriteNarrow(operation, baseline, world, first, selected, query, registry);

        using var output = new GateWriteStream();
        Task write = Task.Run(() =>
            WriteNarrow(operation, output, world, first, selected, query, registry));
        Assert.True(output.WaitUntilWrite(TimeSpan.FromSeconds(10)));

        try
        {
            await Task.Run(() =>
            {
                world.Replace(first, new SerPosition { X = 10, Y = 20 });
                world.DestroyEntity(second);
                world.CreateEntity(new SerPosition { X = 30, Y = 40 });
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
    public async Task WorldWrite_BlockedOutputMakesDisposeWaitThenCompletes()
    {
        var registry = new SerializationRegistry().Register<SerPosition>();
        var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var output = new GateWriteStream();

        Task write = Task.Run(() => WorldSerializer.WriteWorld(output, world, registry));
        Assert.True(output.WaitUntilWrite(TimeSpan.FromSeconds(10)));
        Task dispose = Task.Run(world.Dispose);
        try
        {
            Task completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.NotSame(dispose, completed);
        }
        finally
        {
            output.Release();
        }

        await write.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<ObjectDisposedException>(() => world.CreateEntity());
    }

    [Fact]
    public void WorldWrite_InvokesComponentAndBufferCodecsExactlyOncePerValue()
    {
        CountingWorldComponentCodec.Reset();
        CountingWorldBufferCodec.Reset();
        var registry = new SerializationRegistry()
            .Register<CountingWorldComponent, CountingWorldComponentCodec>()
            .RegisterBuffer<CountingWorldBuffer, CountingWorldBufferCodec>();
        var world = new World();
        world.CreateEntity(new CountingWorldComponent { Value = 1 });
        world.CreateEntity(new CountingWorldComponent { Value = 2 });
        Entity buffered = world.CreateEntity();
        world.AddBuffer<CountingWorldBuffer>(buffered);
        int[] values = [3, 4, 5];
        world.ExecuteBufferWrite<CountingWorldBuffer, int[]>(
            buffered,
            ref values,
            static (DynamicBuffer<CountingWorldBuffer> buffer, ref int[] source) =>
            {
                for (int i = 0; i < source.Length; i++)
                    buffer.Add(new CountingWorldBuffer { Value = source[i] });
            });

        using var output = new MemoryStream();
        WorldSerializer.WriteWorld(output, world, registry);

        Assert.Equal(2, CountingWorldComponentCodec.WriteCount);
        Assert.Equal(3, CountingWorldBufferCodec.WriteCount);
    }

    [Fact]
    public void LargeArchetypeAndSparseWorld_WritesThroughBoundedNonSeekableFrames()
    {
        const int entityCount = 20_000;
        var registry = new SerializationRegistry()
            .Register<SerPosition>()
            .RegisterSparse<SerSparse>();
        var world = new World(entityCount);
        for (int i = 0; i < entityCount; i++)
        {
            Entity entity = world.CreateEntity(new SerPosition { X = i, Y = -i });
            if (i % 3 == 0)
                world.AddSparse(entity, new SerSparse { Value = i });
        }

        using var output = new CountingNonSeekableWriteStream();
        WorldSerializer.WriteWorld(output, world, registry);

        Assert.True(output.BytesWritten > entityCount * 20L);
        // Whole-world buffering would arrive as a multi-megabyte write. The streaming writer's
        // largest request is one header string or one item frame, independent of entity count.
        Assert.InRange(output.MaximumWriteSize, 1, 512);
    }

    [Fact]
    public void SparseMembershipWorkingSet_RespectsConfiguredLimit()
    {
        var registry = new SerializationRegistry().RegisterSparse<SerSparse>();
        var world = new World();
        for (int i = 0; i < 3; i++)
        {
            Entity entity = world.CreateEntity();
            world.AddSparse(entity, new SerSparse { Value = i });
        }

        using var rejected = new MemoryStream();
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                rejected,
                world,
                registry,
                new SerializeOptions(MaximumSparseMemberships: 2)));
        Assert.Contains("sparse membership count", error.Message);

        using var accepted = new MemoryStream();
        WorldSerializer.WriteWorld(
            accepted,
            world,
            registry,
            new SerializeOptions(MaximumSparseMemberships: 3));
        Assert.True(accepted.Length > 0);
    }

    [Fact]
    public async Task WholeWorldSnapshot_RejectsManagedReferenceValuesBeforeOutput()
    {
        var registry = new SerializationRegistry()
            .Register<SnapshotReferenceComponent, SnapshotReferenceCodec>();
        int[] values = [1, 2, 3];
        var world = new World();
        Entity entity = world.CreateEntity(new SnapshotReferenceComponent { Values = values });
        using var output = new GateWriteStream();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Task.Run(() => WorldSerializer.WriteWorld(output, world, registry)));

        Assert.Contains("deep snapshot-clone contract", error.Message);
        Assert.False(output.WaitUntilWrite(TimeSpan.FromMilliseconds(100)));
        values[0] = 99;
        Assert.Equal(99, world.Read<SnapshotReferenceComponent>(entity).Values![0]);
    }

    [Fact]
    public void WholeWorldSnapshot_AllowsRegisteredManagedReferenceTypeWhenAbsent()
    {
        var registry = new SerializationRegistry()
            .Register<SnapshotReferenceComponent, SnapshotReferenceCodec>();
        var world = new World();
        Entity entity = world.CreateEntity();
        using var output = new MemoryStream();

        WorldSerializer.WriteWorld(output, world, registry);

        Assert.True(output.Length > 0);
        Assert.True(world.IsAlive(entity));
        Assert.False(world.Has<SnapshotReferenceComponent>(entity));
    }

    [Fact]
    public void TopologyWorkingSet_RespectsRecordAndPayloadLimits_OnNonSeekableOutput()
    {
        var registry = new SerializationRegistry()
            .RegisterHierarchyDomain<DefaultHierarchyDomain>();
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        DefaultHierarchy.SetParent(world, first, parent);
        DefaultHierarchy.SetParent(world, second, parent);

        using var recordRejected = new CountingNonSeekableWriteStream();
        InvalidOperationException recordError = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                recordRejected,
                world,
                registry,
                new SerializeOptions(MaximumTopologyRecords: 1)));
        Assert.Contains("topology record count", recordError.Message);

        using var payloadRejected = new CountingNonSeekableWriteStream();
        InvalidOperationException payloadError = Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                payloadRejected,
                world,
                registry,
                new SerializeOptions(
                    MaximumTopologyRecords: 16,
                    MaximumTopologyPayloadBytes: 1)));
        Assert.Contains("topology payload bytes", payloadError.Message);

        using var accepted = new CountingNonSeekableWriteStream();
        WorldSerializer.WriteWorld(
            accepted,
            world,
            registry,
            new SerializeOptions(
                MaximumTopologyRecords: 16,
                MaximumTopologyPayloadBytes: 1024));
        Assert.InRange(accepted.MaximumWriteSize, 1, 512);
    }

    [Fact]
    public void HierarchyRecordBudget_RejectsTheDirectWriteWithoutSnapshotDto()
    {
        var registry = new SerializationRegistry()
            .RegisterHierarchyDomain<DefaultHierarchyDomain>();
        var world = new World();
        Entity parent = world.CreateEntity();
        Entity first = world.CreateEntity();
        Entity second = world.CreateEntity();
        Hierarchy<DefaultHierarchyDomain>.SetChildOrderPolicy(
            world,
            parent,
            ChildOrderPolicy.Ordered);
        Hierarchy<DefaultHierarchyDomain>.SetParent(world, first, parent);
        Hierarchy<DefaultHierarchyDomain>.SetParent(world, second, parent);
        using var rejected = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                rejected,
                world,
                registry,
                new SerializeOptions(MaximumTopologyRecords: 4)));

        using var accepted = new MemoryStream();
        WorldSerializer.WriteWorld(
            accepted,
            world,
            registry,
            new SerializeOptions(MaximumTopologyRecords: 5));
        Assert.True(accepted.Length > 0);
    }

    [Fact]
    public void RelationTopologyWrite_VisitsOnlyEdgesAndActualOrderedShards()
    {
        var registry = new SerializationRegistry()
            .Register<StreamingRelation>()
            .RegisterRelationTopology<StreamingRelation>();
        var world = new World();
        Entity source = world.CreateEntity();
        Entity target = world.CreateEntity();
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
            world.CreateEntity();

        using var rejected = new MemoryStream();
        Assert.Throws<InvalidOperationException>(() =>
            WorldSerializer.WriteWorld(
                rejected,
                world,
                registry,
                new SerializeOptions(MaximumTopologyRecords: 4)));

        using var accepted = new MemoryStream();
        WorldSerializer.WriteWorld(
            accepted,
            world,
            registry,
            new SerializeOptions(MaximumTopologyRecords: 5));
        accepted.Position = 0;
        using World loaded = WorldSerializer.ReadWorld(accepted, registry);
        Assert.Single(loaded.GetOutgoingRelations<StreamingRelation>(source).Entries.ToArray());
    }



    [Fact]
    public void RepeatedWorldSerializationKeepsLatestClockAndBytes()
    {
        var registry = new SerializationRegistry()
            .Register<SerPosition>()
            .RegisterSparse<SerSparse>();
        var world = new World();
        Entity first = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        Entity second = world.CreateEntity(new SerPosition { X = 3, Y = 4 });
        world.AddSparse(second, new SerSparse { Value = 5 });
        uint versionBefore = world.WriteVersion<SerPosition>(first);
        uint tickBefore = world.CurrentTick;

        using var beforeMutation = new MemoryStream();
        WorldSerializer.WriteWorld(beforeMutation, world, registry);

        world.Replace(first, new SerPosition { X = 100, Y = 200 });
        world.Replace(first, new SerPosition { X = 1, Y = 2 });
        uint versionAfter = world.WriteVersion<SerPosition>(first);
        Assert.NotEqual(versionBefore, versionAfter);

        using var afterMutation = new MemoryStream();
        WorldSerializer.WriteWorld(afterMutation, world, registry);
        using var repeated = new MemoryStream();
        WorldSerializer.WriteWorld(repeated, world, registry);
        Assert.Equal(afterMutation.ToArray(), repeated.ToArray());

        beforeMutation.Position = 0;
        afterMutation.Position = 0;
        using var earlier = WorldSerializer.ReadWorld(beforeMutation, registry);
        using var later = WorldSerializer.ReadWorld(afterMutation, registry);
        Assert.Equal(tickBefore, earlier.CurrentTick);
        Assert.Equal(world.CurrentTick, later.CurrentTick);
        Assert.Equal(1, later.Read<SerPosition>(first).X);
        Assert.Equal(2, later.Read<SerPosition>(first).Y);
    }




    private static async Task AssertMutationCompletesAsync(World world)
    {
        Entity created = await Task.Run(() => world.CreateEntity())
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(world.IsAlive(created));
    }

    private static void WriteNarrow(
        NarrowCaptureOperation operation,
        Stream output,
        World world,
        Entity entity,
        Entity[] entities,
        QueryHandle query,
        SerializationRegistry registry)
    {
        switch (operation)
        {
            case NarrowCaptureOperation.Entity:
                WorldSerializer.WriteEntity(output, world, entity, registry);
                break;
            case NarrowCaptureOperation.EntitySet:
                WorldSerializer.WriteEntities(output, world, entities, registry);
                break;
            case NarrowCaptureOperation.Query:
                WorldSerializer.WriteQuery(output, world, query, registry);
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

    private struct ReentrantWorldComponentCodec : IComponentCodec<ReentrantWorldComponent>
    {
        internal static World? Target { get; set; }

        public void Write(ref DataWriter writer, in ReentrantWorldComponent value)
        {
            Target!.CreateEntity();
            writer.WriteInt32(value.Value);
        }

        public void Read(ref DataReader reader, out ReentrantWorldComponent value) =>
            value = new ReentrantWorldComponent { Value = reader.ReadInt32() };
    }

    private struct CountingWorldComponent : IComponent
    {
        public int Value;
    }

    private struct CountingWorldComponentCodec : IComponentCodec<CountingWorldComponent>
    {
        private static int s_writeCount;

        internal static int WriteCount => Volatile.Read(ref s_writeCount);
        internal static void Reset() => Volatile.Write(ref s_writeCount, 0);

        public void Write(ref DataWriter writer, in CountingWorldComponent value)
        {
            Interlocked.Increment(ref s_writeCount);
            writer.WriteInt32(value.Value);
        }

        public void Read(ref DataReader reader, out CountingWorldComponent value) =>
            value = new CountingWorldComponent { Value = reader.ReadInt32() };
    }

    private struct CountingWorldBuffer : IBufferElement
    {
        public int Value;
    }

    private struct CountingWorldBufferCodec : IComponentCodec<CountingWorldBuffer>
    {
        private static int s_writeCount;

        internal static int WriteCount => Volatile.Read(ref s_writeCount);
        internal static void Reset() => Volatile.Write(ref s_writeCount, 0);

        public void Write(ref DataWriter writer, in CountingWorldBuffer value)
        {
            Interlocked.Increment(ref s_writeCount);
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
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BlockFirstWrite();
            if (_failure is not null)
                throw _failure;
            _inner.Write(buffer);
        }

        public override void WriteByte(byte value)
        {
            BlockFirstWrite();
            if (_failure is not null)
                throw _failure;
            _inner.WriteByte(value);
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



    private sealed class CountingNonSeekableWriteStream : Stream
    {
        internal long BytesWritten { get; private set; }

        internal int MaximumWriteSize { get; private set; }

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

        public override void Write(byte[] buffer, int offset, int count) => Count(count);

        public override void Write(ReadOnlySpan<byte> buffer) => Count(buffer.Length);

        public override void WriteByte(byte value) => Count(1);

        public override int Read(byte[] buffer, int offset, int count) =>
            throw new NotSupportedException();

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        private void Count(int count)
        {
            BytesWritten += count;
            MaximumWriteSize = Math.Max(MaximumWriteSize, count);
        }
    }


}
