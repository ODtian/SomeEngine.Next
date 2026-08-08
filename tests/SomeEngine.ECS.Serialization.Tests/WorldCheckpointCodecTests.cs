using System.Buffers.Binary;
using System.Reflection;
using System.Security.Cryptography;
using SomeEngine.ECS;
using SomeEngine.ECS.Entities;
using SomeEngine.ECS.Hierarchy;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class WorldCheckpointCodecTests
{
    [Fact]
    public void CurrentCheckpoint_RoundTripsIdentityAndEveryStoragePath()
    {
        SerializationRegistry registry = FullRegistry();
        using var source = new World();
        Entity first = source.CreateEntity(new SerPosition { X = 1.25f, Y = -3.5f });
        source.Add(first, new SerVisible { Value = 17 });
        source.AddTag<SerPlayerTag>(first);
        source.AddShared(first, new SerScene { Value = 91 });
        source.AddBuffer<SerElement>(first);
        WriteBuffer(source, first, 2, 4, 8, 16);
        source.AddSparse(first, new SerSparse { Value = 123 });
        Entity second = source.CreateEntity(new SerPosition { X = 7, Y = 9 });
        source.DestroyEntity(second);

        using var checkpoint = new MemoryStream();
        WorldCheckpointCodec.Write(checkpoint, source, registry);
        checkpoint.Position = 0;
        WorldCheckpointInfo info = WorldCheckpointCodec.Inspect(checkpoint);
        Assert.Equal((ulong)WorldCheckpointCodec.HeaderSize, info.PayloadOffset);
        Assert.Equal((ulong)checkpoint.Length, info.TotalLength);
        Assert.Equal(info.PayloadOffset + info.PayloadLength, info.TotalLength);

        checkpoint.Position = 0;
        using World loaded = WorldCheckpointCodec.Read(checkpoint, registry);
        Assert.True(loaded.IsAlive(first));
        Assert.False(loaded.IsAlive(second));
        Assert.Equal(1.25f, loaded.Read<SerPosition>(first).X);
        Assert.Equal(-3.5f, loaded.Read<SerPosition>(first).Y);
        Assert.Equal(17, loaded.Read<SerVisible>(first).Value);
        Assert.True(loaded.Has<SerPlayerTag>(first));
        Assert.Equal(91, loaded.GetShared<SerScene>(first).Value);
        Assert.Equal(new[] { 2, 4, 8, 16 }, ReadBuffer(loaded, first));
        Assert.Equal(123, loaded.ReadSparse<SerSparse>(first).Value);
    }

    [Fact]
    public void CheckpointPayload_IsExactlyTheSingleCanonicalWorldWire()
    {
        SerializationRegistry registry = PositionRegistry();
        using var world = new World();
        world.CreateEntity(new SerPosition { X = 3, Y = 4 });

        using var checkpoint = new MemoryStream();
        WorldCheckpointCodec.Write(checkpoint, world, registry);
        using var canonical = new MemoryStream();
        WorldSerializer.WriteCheckpointWorld(canonical, world, registry);

        ReadOnlySpan<byte> checkpointBytes = checkpoint.GetBuffer().AsSpan(0, checked((int)checkpoint.Length));
        Assert.Equal("SEWCP003"u8.ToArray(), checkpointBytes[..8].ToArray());
        Assert.Equal(
            canonical.GetBuffer().AsSpan(0, checked((int)canonical.Length)).ToArray(),
            checkpointBytes[WorldCheckpointCodec.HeaderSize..].ToArray());
    }

    [Fact]
    public void Checkpoint_IsByteDeterministic()
    {
        SerializationRegistry registry = PositionRegistry();
        using var world = new World();
        world.CreateEntity(new SerPosition { X = 3, Y = 4 });
        using var first = new MemoryStream();
        using var second = new MemoryStream();

        WorldCheckpointCodec.Write(first, world, registry);
        WorldCheckpointCodec.Write(second, world, registry);

        Assert.Equal(first.ToArray(), second.ToArray());
    }

    [Fact]
    public void LatePayloadAuthenticationFailure_DoesNotReturnDecodedWorld()
    {
        SerializationRegistry registry = PositionRegistry();
        using var source = new World();
        source.CreateEntity(new SerPosition { X = 10, Y = 20 });
        using var output = new MemoryStream();
        WorldCheckpointCodec.Write(output, source, registry);
        byte[] bytes = output.ToArray();

        bytes[72] ^= 0x40;
        RewriteHeaderHash(bytes);

        using var input = new MemoryStream(bytes, writable: false);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            WorldCheckpointCodec.Read(input, registry));
        Assert.Contains("payload SHA-256", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadIntoAndCaptureAreNotPartOfThePublicApi()
    {
        Assert.DoesNotContain(
            typeof(WorldCheckpointCodec).GetMethods(),
            static method => method.Name == "LoadInto");
        Assert.DoesNotContain(
            typeof(SerializationRegistry).GetMethods(),
            static method => method.Name is "Capture" or "TryCapture");
    }

    [Fact]
    public void Checkpoint_InvokesComponentBufferAndTopologyCodecsExactlyOnce()
    {
        CountingPositionCodec.Reset();
        CountingCheckpointBufferCodec.Reset();
        var registry = new SerializationRegistry()
            .Register<SerPosition, CountingPositionCodec>()
            .RegisterBuffer<CountingCheckpointBuffer, CountingCheckpointBufferCodec>();
        var topology = new CountingCheckpointTopologyRuntime();
        MethodInfo registerTopology = typeof(SerializationRegistry).GetMethod(
            "RegisterTopology",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        registerTopology.Invoke(registry, [topology]);
        using var source = new World();
        for (int i = 0; i < 4; i++)
            source.CreateEntity(new SerPosition { X = i + 0.25f, Y = -i });
        Entity buffered = source.CreateEntity();
        source.AddBuffer<CountingCheckpointBuffer>(buffered);
        int[] values = [10, 20, 30];
        source.ExecuteBufferWrite<CountingCheckpointBuffer, int[]>(
            buffered,
            ref values,
            static (DynamicBuffer<CountingCheckpointBuffer> buffer, ref int[] input) =>
            {
                for (int i = 0; i < input.Length; i++)
                    buffer.Add(new CountingCheckpointBuffer { Value = input[i] });
            });
        long revisionBefore = source.PublishedTopologyRevision;

        using var checkpoint = new MemoryStream();
        WorldCheckpointCodec.Write(checkpoint, source, registry);
        Assert.Equal(4, CountingPositionCodec.WriteCount);
        Assert.Equal(3, CountingCheckpointBufferCodec.WriteCount);
        Assert.Equal(1, topology.WriteCount);
        Assert.Equal(revisionBefore, source.PublishedTopologyRevision);

        CountingPositionCodec.ResetRead();
        CountingCheckpointBufferCodec.ResetRead();
        checkpoint.Position = 0;
        using World loaded = WorldCheckpointCodec.Read(checkpoint, registry);
        Assert.Equal(4, CountingPositionCodec.ReadCount);
        Assert.Equal(3, CountingCheckpointBufferCodec.ReadCount);
        Assert.Equal(1, topology.ReadCount);
    }

    [Fact]
    public void Checkpoint_RejectsNonSeekableDestinationWithoutInvokingCodec()
    {
        CountingPositionCodec.Reset();
        var registry = new SerializationRegistry().Register<SerPosition, CountingPositionCodec>();
        using var source = new World();
        source.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var destination = new NonSeekableWriteStream();

        ArgumentException error = Assert.Throws<ArgumentException>(() =>
            WorldCheckpointCodec.Write(destination, source, registry));

        Assert.Contains("seekable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, CountingPositionCodec.WriteCount);
        Assert.Equal(0, destination.BytesWritten);
    }

    [Fact]
    public async Task CheckpointWrite_BlockedDestinationAllowsSuccessorMutationAndKeepsImageStable()
    {
        SerializationRegistry registry = PositionRegistry();
        using var world = new World();
        Entity captured = world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        long revisionBefore = world.PublishedTopologyRevision;
        using var destination = new GateSeekableWriteStream();

        Task write = Task.Run(() => WorldCheckpointCodec.Write(destination, world, registry));
        Assert.True(destination.WaitUntilWrite(TimeSpan.FromSeconds(10)));
        Task<Entity> mutation = Task.Run(() => world.CreateEntity());
        try
        {
            Entity created = await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(world.IsAlive(created));
            Assert.Equal(revisionBefore + 1, world.PublishedTopologyRevision);
        }
        finally
        {
            destination.Release();
        }
        await write.WaitAsync(TimeSpan.FromSeconds(10));
        destination.Position = 0;
        using World restored = WorldCheckpointCodec.Read(destination, registry);
        Assert.Equal(1, restored.EntityCount);
        Assert.True(restored.IsAlive(captured));
        Assert.Equal(revisionBefore + 1, world.PublishedTopologyRevision);
    }

    [Fact]
    public async Task CheckpointWrite_BlockedDestinationMakesDisposeWaitThenCompletes()
    {
        SerializationRegistry registry = PositionRegistry();
        var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var destination = new GateSeekableWriteStream();

        Task write = Task.Run(() => WorldCheckpointCodec.Write(destination, world, registry));
        Assert.True(destination.WaitUntilWrite(TimeSpan.FromSeconds(10)));
        Task dispose = Task.Run(world.Dispose);
        try
        {
            Task completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(200)));
            Assert.NotSame(dispose, completed);
        }
        finally
        {
            destination.Release();
        }

        await write.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Throws<ObjectDisposedException>(() => world.CreateEntity());
    }

    [Theory]
    [InlineData(CheckpointFailureKind.Io)]
    [InlineData(CheckpointFailureKind.Canceled)]
    [InlineData(CheckpointFailureKind.Disposed)]
    public async Task CheckpointWrite_StreamFailureReleasesAdmission(CheckpointFailureKind kind)
    {
        SerializationRegistry registry = PositionRegistry();
        using var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        long revisionBefore = world.PublishedTopologyRevision;
        Exception expected = kind switch
        {
            CheckpointFailureKind.Io => new IOException("Injected checkpoint output fault."),
            CheckpointFailureKind.Canceled => new OperationCanceledException("Injected checkpoint cancellation."),
            CheckpointFailureKind.Disposed => new ObjectDisposedException("checkpoint destination"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        using var destination = new FaultingSeekableWriteStream(expected);

        Exception? actual = Record.Exception(() =>
            WorldCheckpointCodec.Write(destination, world, registry));
        Assert.NotNull(actual);
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(revisionBefore, world.PublishedTopologyRevision);
        await AssertMutationCompletesAsync(world);
        Assert.Equal(revisionBefore + 1, world.PublishedTopologyRevision);
    }

    [Fact]
    public async Task CheckpointWrite_ComponentCodecFailureReleasesAdmission()
    {
        var registry = new SerializationRegistry()
            .Register<CheckpointFaultComponent, ThrowingCheckpointCodec>();
        using var world = new World();
        world.CreateEntity(new CheckpointFaultComponent { Value = 1 });
        using var destination = new MemoryStream();

        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            WorldCheckpointCodec.Write(destination, world, registry));
        Assert.Contains("Injected checkpoint codec fault", error.Message);
        await AssertMutationCompletesAsync(world);
    }

    [Fact]
    public void CheckpointWrite_ComponentCodecReentryMutatesSuccessorWithoutChangingRetainedOutput()
    {
        var registry = new SerializationRegistry()
            .Register<CheckpointReentrantComponent, ReentrantCheckpointCodec>();
        using var world = new World();
        world.CreateEntity(new CheckpointReentrantComponent { Value = 1 });
        using var destination = new MemoryStream();
        ReentrantCheckpointCodec.Target = world;
        try
        {
            WorldCheckpointCodec.Write(destination, world, registry);
        }
        finally
        {
            ReentrantCheckpointCodec.Target = null;
        }

        Assert.Equal(2, world.EntityCount);
        destination.Position = 0;
        using World restored = WorldCheckpointCodec.Read(destination, registry);
        Assert.Equal(1, restored.EntityCount);
    }

    [Fact]
    public void CheckpointWrite_OutputCallbackMutatesPublishedSuccessor()
    {
        SerializationRegistry registry = PositionRegistry();
        using var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var destination = new CallbackSeekableWriteStream(() => world.CreateEntity());

        WorldCheckpointCodec.Write(destination, world, registry);

        Assert.Equal(2, world.EntityCount);
        destination.Position = 0;
        using World restored = WorldCheckpointCodec.Read(destination, registry);
        Assert.Equal(1, restored.EntityCount);
    }

    [Fact]
    public void ExactCheckpoint_RejectsChangedAndAdditionalRegistryEntries()
    {
        Guid id = Guid.Parse("A74BDB5E-B61E-4B74-ABDC-B63979B8E54A");
        var writeRegistry = new SerializationRegistry().Register<SerPosition, SerPositionFullCodec>(
            new SerializationTypeKey(id, "Tests.Position", 0x1111111111111111));
        using var source = new World();
        source.CreateEntity(new SerPosition { X = 4, Y = 5 });
        using var output = new MemoryStream();
        WorldCheckpointCodec.Write(output, source, writeRegistry);
        byte[] bytes = output.ToArray();

        var changed = new SerializationRegistry().Register<SerPosition, SerPositionFullCodec>(
            new SerializationTypeKey(id, "Tests.Position", 0x2222222222222222));
        using var changedInput = new MemoryStream(bytes, writable: false);
        Assert.Throws<InvalidDataException>(() => WorldCheckpointCodec.Read(changedInput, changed));

        var additional = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(
                new SerializationTypeKey(id, "Tests.Position", 0x1111111111111111))
            .Register<SerVisible, VisibleCheckpointCodec>();
        using var additionalInput = new MemoryStream(bytes, writable: false);
        Assert.Throws<InvalidDataException>(() => WorldCheckpointCodec.Read(additionalInput, additional));
    }

    [Fact]
    public void ExactCheckpoint_RejectsAbsentComponentStableNameChange()
    {
        Guid id = Guid.Parse("B85CEC6F-C72F-4C85-BCED-C74080C9F65B");
        const ulong fingerprint = 0x5151515151515151ul;
        var writeRegistry = new SerializationRegistry().Register<SerPosition, SerPositionFullCodec>(
            new SerializationTypeKey(id, "Tests.Absent.A", fingerprint));
        var readRegistry = new SerializationRegistry().Register<SerPosition, SerPositionFullCodec>(
            new SerializationTypeKey(id, "Tests.Absent.B", fingerprint));
        using var source = new World();
        using var output = new MemoryStream();
        WorldCheckpointCodec.Write(output, source, writeRegistry);
        output.Position = 0;

        World? unexpected = null;
        try
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                unexpected = WorldCheckpointCodec.Read(output, readRegistry));
            Assert.Contains("registry identity", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            unexpected?.Dispose();
        }
    }

    [Fact]
    public void ExactCheckpoint_RejectsTopologyStableNameChangeAtRegistryGate()
    {
        Guid id = Guid.Parse("C96DFD70-D830-4D96-CDFE-D85191DA076C");
        const ulong fingerprint = 0x6161616161616161ul;
        var writeRegistry = new SerializationRegistry()
            .RegisterHierarchyDomain<CheckpointHierarchyDomain>(
                new SerializationTypeKey(id, "Checkpoint.Topology.A", fingerprint));
        var readRegistry = new SerializationRegistry()
            .RegisterHierarchyDomain<CheckpointHierarchyDomain>(
                new SerializationTypeKey(id, "Checkpoint.Topology.B", fingerprint));
        using var source = new World();
        using var output = new MemoryStream();
        WorldCheckpointCodec.Write(output, source, writeRegistry);
        output.Position = 0;

        World? unexpected = null;
        try
        {
            InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
                unexpected = WorldCheckpointCodec.Read(output, readRegistry));
            Assert.Contains("registry identity", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            unexpected?.Dispose();
        }
    }

    [Fact]
    public void TruncationReadLimitsAndLengthOverflowFailClosed()
    {
        SerializationRegistry registry = PositionRegistry();
        using var source = new World();
        for (int i = 0; i < 3; i++)
            source.CreateEntity(new SerPosition { X = i, Y = -i });
        using var output = new MemoryStream();
        WorldCheckpointCodec.Write(output, source, registry);
        byte[] bytes = output.ToArray();

        using var truncated = new MemoryStream(bytes[..^1], writable: false);
        Assert.Throws<EndOfStreamException>(() => WorldCheckpointCodec.Read(truncated, registry));

        using var limited = new MemoryStream(bytes, writable: false);
        Assert.Throws<InvalidDataException>(() => WorldCheckpointCodec.Read(
            limited,
            registry,
            new SerializationReadLimits { MaxEntitySlots = 2, MaxEntities = 2 }));

        byte[] overflowed = (byte[])bytes.Clone();
        BinaryPrimitives.WriteUInt64LittleEndian(overflowed.AsSpan(24, 8), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64LittleEndian(overflowed.AsSpan(32, 8), ulong.MaxValue);
        RewriteHeaderHash(overflowed);
        using var overflowedInput = new MemoryStream(overflowed, writable: false);
        Assert.Throws<InvalidDataException>(() => WorldCheckpointCodec.Inspect(overflowedInput));
    }

    [Fact]
    public void ReadAndInspectRejectPreviousEnvelopeAndUnauthenticatedHeader()
    {
        SerializationRegistry registry = PositionRegistry();
        using var world = new World();
        world.CreateEntity(new SerPosition { X = 1, Y = 2 });
        using var output = new MemoryStream();
        WorldCheckpointCodec.Write(output, world, registry);

        byte[] previous = output.ToArray();
        "SEWCP002"u8.CopyTo(previous);
        using var readInput = new MemoryStream(previous, writable: false);
        InvalidDataException readError = Assert.Throws<InvalidDataException>(() =>
            WorldCheckpointCodec.Read(readInput, registry));
        Assert.Contains("only current SEWCP003", readError.Message);

        using var inspectInput = new MemoryStream(previous, writable: false);
        Assert.Throws<InvalidDataException>(() => WorldCheckpointCodec.Inspect(inspectInput));

        byte[] unauthenticated = output.ToArray();
        unauthenticated[32] ^= 1;
        using var headerInput = new MemoryStream(unauthenticated, writable: false);
        InvalidDataException headerError = Assert.Throws<InvalidDataException>(() =>
            WorldCheckpointCodec.Inspect(headerInput));
        Assert.Contains("header authentication", headerError.Message);
    }

    private static SerializationRegistry FullRegistry() =>
        new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>()
            .Register<SerVisible, VisibleCheckpointCodec>()
            .RegisterTag<SerPlayerTag>()
            .RegisterShared<SerScene, SceneCheckpointCodec>()
            .RegisterBuffer<SerElement, ElementCheckpointCodec>()
            .RegisterSparse<SerSparse, SparseCheckpointCodec>();

    private static SerializationRegistry PositionRegistry() =>
        new SerializationRegistry().Register<SerPosition, SerPositionFullCodec>();

    private readonly struct CheckpointHierarchyDomain : IHierarchyDomain { }

    private static async Task AssertMutationCompletesAsync(World world)
    {
        Entity created = await Task.Run(() => world.CreateEntity())
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(world.IsAlive(created));
    }

    private static void RewriteHeaderHash(byte[] bytes)
    {
        byte[] hash = SHA256.HashData(bytes.AsSpan(0, 104));
        hash.AsSpan(0, 24).CopyTo(bytes.AsSpan(104, 24));
    }

    public enum CheckpointFailureKind
    {
        Io,
        Canceled,
        Disposed,
    }

    private struct CheckpointFaultComponent : SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    private struct ThrowingCheckpointCodec : ICanonicalComponentCodec<CheckpointFaultComponent>
    {
        public void Write(ref DataWriter writer, in CheckpointFaultComponent value) =>
            throw new InvalidOperationException("Injected checkpoint codec fault.");
        public void Read(ref DataReader reader, out CheckpointFaultComponent value) =>
            value = new CheckpointFaultComponent { Value = reader.ReadInt32() };
    }

    private struct CheckpointReentrantComponent : SomeEngine.ECS.IComponent
    {
        public int Value;
    }

    private struct ReentrantCheckpointCodec : ICanonicalComponentCodec<CheckpointReentrantComponent>
    {
        internal static World? Target { get; set; }
        public void Write(ref DataWriter writer, in CheckpointReentrantComponent value)
        {
            Target!.CreateEntity();
            writer.WriteInt32(value.Value);
        }
        public void Read(ref DataReader reader, out CheckpointReentrantComponent value) =>
            value = new CheckpointReentrantComponent { Value = reader.ReadInt32() };
    }

    private struct VisibleCheckpointCodec : ICanonicalComponentCodec<SerVisible>
    {
        public void Write(ref DataWriter writer, in SerVisible value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out SerVisible value) =>
            value = new SerVisible { Value = reader.ReadInt32() };
    }

    private struct SceneCheckpointCodec : ICanonicalComponentCodec<SerScene>
    {
        public void Write(ref DataWriter writer, in SerScene value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out SerScene value) =>
            value = new SerScene { Value = reader.ReadInt32() };
    }

    private struct ElementCheckpointCodec : ICanonicalComponentCodec<SerElement>
    {
        public void Write(ref DataWriter writer, in SerElement value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out SerElement value) =>
            value = new SerElement { Value = reader.ReadInt32() };
    }

    private struct SparseCheckpointCodec : ICanonicalComponentCodec<SerSparse>
    {
        public void Write(ref DataWriter writer, in SerSparse value) => writer.WriteInt32(value.Value);
        public void Read(ref DataReader reader, out SerSparse value) =>
            value = new SerSparse { Value = reader.ReadInt32() };
    }

    private struct CountingPositionCodec : ICanonicalComponentCodec<SerPosition>
    {
        private static int s_writeCount;
        private static int s_readCount;
        internal static int WriteCount => Volatile.Read(ref s_writeCount);
        internal static int ReadCount => Volatile.Read(ref s_readCount);
        internal static void Reset()
        {
            Volatile.Write(ref s_writeCount, 0);
            Volatile.Write(ref s_readCount, 0);
        }
        internal static void ResetRead() => Volatile.Write(ref s_readCount, 0);
        public void Write(ref DataWriter writer, in SerPosition value)
        {
            Interlocked.Increment(ref s_writeCount);
            writer.WriteSingle(value.X);
            writer.WriteSingle(value.Y);
        }
        public void Read(ref DataReader reader, out SerPosition value)
        {
            Interlocked.Increment(ref s_readCount);
            value = new SerPosition { X = reader.ReadSingle(), Y = reader.ReadSingle() };
        }
    }

    private struct CountingCheckpointBuffer : SomeEngine.ECS.Components.IBufferElement
    {
        public int Value;
    }

    private struct CountingCheckpointBufferCodec : ICanonicalComponentCodec<CountingCheckpointBuffer>
    {
        private static int s_writeCount;
        private static int s_readCount;
        internal static int WriteCount => Volatile.Read(ref s_writeCount);
        internal static int ReadCount => Volatile.Read(ref s_readCount);
        internal static void Reset()
        {
            Volatile.Write(ref s_writeCount, 0);
            Volatile.Write(ref s_readCount, 0);
        }
        internal static void ResetRead() => Volatile.Write(ref s_readCount, 0);
        public void Write(ref DataWriter writer, in CountingCheckpointBuffer value)
        {
            Interlocked.Increment(ref s_writeCount);
            writer.WriteInt32(value.Value);
        }
        public void Read(ref DataReader reader, out CountingCheckpointBuffer value)
        {
            Interlocked.Increment(ref s_readCount);
            value = new CountingCheckpointBuffer { Value = reader.ReadInt32() };
        }
    }

    private sealed class CountingCheckpointTopologyRuntime : TopologySerializationRuntime
    {
        private int _writeCount;
        private int _readCount;
        internal CountingCheckpointTopologyRuntime()
            : base(
                TopologySerializationKind.Relation,
                typeof(CountingCheckpointTopologyRuntime),
                new SerializationTypeKey(
                    Guid.Parse("F6C4F0E1-FF11-4A1C-8A4D-7368B333E2BA"),
                    "tests.checkpoint.topology-once",
                    0xBDA1D418F0545D43ul))
        {
        }
        internal int WriteCount => Volatile.Read(ref _writeCount);
        internal int ReadCount => Volatile.Read(ref _readCount);
        internal override void ValidateWriteState(AdmittedWorldWrite admitted) { }
        internal override void WriteAdmitted(
            BinaryWriter writer,
            AdmittedWorldWrite admitted,
            TopologyCaptureBudget budget)
        {
            budget.ReserveRecords(1, TypeKey.StableName);
            Interlocked.Increment(ref _writeCount);
            writer.Write((byte)0x5A);
        }
        internal override void ReadApply(
            BinaryReader reader,
            SerializationReadBudget budget,
            World world,
            IReferenceRemapper? remapper)
        {
            Assert.Equal((byte)0x5A, reader.ReadByte());
            Interlocked.Increment(ref _readCount);
        }
    }

    private static void WriteBuffer(World world, Entity entity, params int[] values)
    {
        world.ExecuteBufferWrite<SerElement, int[]>(
            entity,
            ref values,
            static (DynamicBuffer<SerElement> buffer, ref int[] source) =>
            {
                for (int i = 0; i < source.Length; i++)
                    buffer.Add(new SerElement { Value = source[i] });
            });
    }

    private static int[] ReadBuffer(World world, Entity entity)
    {
        int[] values = null!;
        world.ExecuteBufferRead<SerElement, int[]>(
            entity,
            ref values,
            static (BufferView<SerElement> buffer, ref int[] destination) =>
            {
                destination = new int[buffer.Count];
                ReadOnlySpan<SerElement> source = buffer.AsSpan();
                for (int i = 0; i < source.Length; i++)
                    destination[i] = source[i].Value;
            });
        return values;
    }

    private sealed class NonSeekableWriteStream : Stream
    {
        internal long BytesWritten { get; private set; }
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) =>
            BytesWritten = checked(BytesWritten + count);
        public override void Write(ReadOnlySpan<byte> buffer) =>
            BytesWritten = checked(BytesWritten + buffer.Length);
    }

    private sealed class GateSeekableWriteStream : MemoryStream
    {
        private readonly ManualResetEventSlim _entered = new(false);
        private readonly ManualResetEventSlim _release = new(false);
        private int _blocked;
        internal bool WaitUntilWrite(TimeSpan timeout) => _entered.Wait(timeout);
        internal void Release() => _release.Set();
        public override void Write(byte[] buffer, int offset, int count)
        {
            BlockFirstWrite();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            BlockFirstWrite();
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            BlockFirstWrite();
            base.WriteByte(value);
        }
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _release.Set();
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

    private sealed class FaultingSeekableWriteStream : MemoryStream
    {
        private readonly Exception _failure;
        internal FaultingSeekableWriteStream(Exception failure) => _failure = failure;
        public override void Write(byte[] buffer, int offset, int count) => throw _failure;
        public override void Write(ReadOnlySpan<byte> buffer) => throw _failure;
        public override void WriteByte(byte value) => throw _failure;
    }

    private sealed class CallbackSeekableWriteStream : MemoryStream
    {
        private readonly Action _callback;
        private int _invoked;
        internal CallbackSeekableWriteStream(Action callback) => _callback = callback;
        public override void Write(byte[] buffer, int offset, int count)
        {
            Invoke();
            base.Write(buffer, offset, count);
        }
        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Invoke();
            base.Write(buffer);
        }
        public override void WriteByte(byte value)
        {
            Invoke();
            base.WriteByte(value);
        }
        private void Invoke()
        {
            if (Interlocked.Exchange(ref _invoked, 1) == 0)
                _callback();
        }
    }
}
