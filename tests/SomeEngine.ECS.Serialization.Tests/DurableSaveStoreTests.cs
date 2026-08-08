using System.Reflection;
using SomeEngine.ECS;
using SomeEngine.ECS.Serialization;

namespace SomeEngine.ECS.Serialization.Tests;

public sealed class DurableSaveStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "SomeEngine-DurableSaveStoreTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void WriteAndReadWorld_RoundTripsStateAndGeneration()
    {
        DurableSaveStore store = CreateStore();

        DurableSaveCommit commit = WriteWorldWithEntityCount(store, 1);

        Assert.Equal(1UL, commit.Generation);
        Assert.Equal(1, ReadWorldEntityCount(store));
        Assert.Equal(store.PrimaryPath, commit.PublishedPath);
    }

    [Fact]
    public void SuccessiveWrites_AlternateSlotsAndSelectHighestGeneration()
    {
        DurableSaveStore store = CreateStore();

        DurableSaveCommit first = WriteWorldWithEntityCount(store, 1);
        DurableSaveCommit second = WriteWorldWithEntityCount(store, 2);
        DurableSaveCommit third = WriteWorldWithEntityCount(store, 3);

        Assert.Equal(store.PrimaryPath, first.PublishedPath);
        Assert.Equal(store.PreviousPath, second.PublishedPath);
        Assert.Equal(store.PrimaryPath, third.PublishedPath);

        Assert.Equal(3UL, third.Generation);
        Assert.Equal(3, ReadWorldEntityCount(store));
    }

    [Fact]
    public void CorruptNewestGeneration_FallsBackToPreviousVerifiedGeneration()
    {
        DurableSaveStore store = CreateStore();
        WriteWorldWithEntityCount(store, 1);
        DurableSaveCommit newest = WriteWorldWithEntityCount(store, 2);

        using (var stream = new FileStream(newest.PublishedPath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = stream.Length - 1;
            int value = stream.ReadByte();
            stream.Position--;
            stream.WriteByte((byte)(value ^ 0xFF));
            stream.Flush(flushToDisk: true);
        }

        Assert.Equal(1, ReadWorldEntityCount(store));
    }

    [Theory]
    [InlineData(16)]
    [InlineData(17)]
    [InlineData(18)]
    [InlineData(19)]
    [InlineData(20)]
    [InlineData(21)]
    [InlineData(22)]
    [InlineData(23)]
    [InlineData(24)]
    [InlineData(32)]
    public void CorruptNewestEnvelopeMetadataOrDigest_FallsBackToPreviousGeneration(
        int byteOffset)
    {
        DurableSaveStore store = CreateStore();
        WriteWorldWithEntityCount(store, 1);
        DurableSaveCommit newest = WriteWorldWithEntityCount(store, 2);

        using (var stream = new FileStream(newest.PublishedPath, FileMode.Open, FileAccess.ReadWrite))
        {
            stream.Position = byteOffset;
            int value = stream.ReadByte();
            stream.Position = byteOffset;
            stream.WriteByte((byte)(value ^ 0x01));
            stream.Flush(flushToDisk: true);
        }

        Assert.Equal(1, ReadWorldEntityCount(store));
    }

    [Fact]
    public void TruncatedNewestGeneration_FallsBackToPreviousVerifiedGeneration()
    {
        DurableSaveStore store = CreateStore();
        WriteWorldWithEntityCount(store, 1);
        DurableSaveCommit newest = WriteWorldWithEntityCount(store, 2);

        using (var stream = new FileStream(newest.PublishedPath, FileMode.Open, FileAccess.Write))
            stream.SetLength(17);

        Assert.Equal(1, ReadWorldEntityCount(store));
    }

    [Fact]
    public void DigestValidUnknownSchema_DoesNotFallBackToOlderGeneration()
    {
        DurableSaveStore store = CreateStore();
        WriteWorldWithEntityCount(store, 1);
        var key = new SerializationTypeKey(
            Guid.Parse("C8E3CC0A-7C7C-4EA5-A476-6D3214635677"),
            "tests.durable.unknown-schema",
            0xB30D6E1594A2C8F1ul);
        var writeRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(key);
        using var newest = new World();
        newest.CreateEntity(new SerPosition { X = 1, Y = 2 });
        store.WriteWorld(newest, writeRegistry);

        Assert.Throws<InvalidDataException>(() =>
            store.ReadWorld(new SerializationRegistry()));
    }

    [Fact]
    public void DigestValidTrailingPayload_DoesNotFallBackToOlderGeneration()
    {
        DurableSaveStore store = CreateStore();
        WriteWorldWithEntityCount(store, 1);
        using World newest = CreateWorldWithEntityCount(2);
        var registry = new SerializationRegistry();
        store.Write(stream =>
        {
            WorldSerializer.WriteDurableWorld(stream, newest, registry);
            stream.WriteByte(0xA5);
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.ReadWorld(registry));
        Assert.Contains("decoder consumed", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(DurableSaveWriteStage.PayloadWritten, false)]
    [InlineData(DurableSaveWriteStage.TemporaryFileFlushed, false)]
    [InlineData(DurableSaveWriteStage.TemporaryFileVerified, false)]
    [InlineData(DurableSaveWriteStage.BeforePublish, false)]
    [InlineData(DurableSaveWriteStage.Published, true)]
    public void FaultAtEveryCommitCutPoint_LeavesARecoverableGeneration(
        DurableSaveWriteStage faultStage,
        bool newGenerationWasPublished)
    {
        DurableSaveStore baseline = CreateStore();
        WriteWorldWithEntityCount(baseline, 1);

        var failing = new DurableSaveStore(
            baseline.PrimaryPath,
            new DurableSaveStoreOptions
            {
                WriteStageObserver = stage =>
                {
                    if (stage == faultStage)
                        throw new SimulatedCrashException(stage);
                },
            });

        using var newerWorld = CreateWorldWithEntityCount(2);
        Assert.Throws<SimulatedCrashException>(() =>
            failing.WriteWorld(newerWorld, new SerializationRegistry()));

        Assert.Equal(newGenerationWasPublished ? 2 : 1, ReadWorldEntityCount(baseline));
    }

    [Fact]
    public void PayloadWriterFailure_LeavesPreviousGenerationUntouched()
    {
        DurableSaveStore store = CreateStore();
        WriteWorldWithEntityCount(store, 1);

        Assert.Throws<SimulatedCrashException>(() => store.Write(stream =>
        {
            stream.Write("partial"u8);
            throw new SimulatedCrashException(DurableSaveWriteStage.PayloadWritten);
        }));

        Assert.Equal(1, ReadWorldEntityCount(store));
    }

    [Fact]
    public void AsyncVoidPayloadWriter_IsRejectedBeforeItCanPublish()
    {
        DurableSaveStore store = CreateStore();
        Action<Stream> writer = AsyncWriter;

        ArgumentException error = Assert.Throws<ArgumentException>(() => store.Write(writer));

        Assert.Contains("synchronous writer", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(store.PrimaryPath));
        Assert.False(File.Exists(store.PreviousPath));

        static async void AsyncWriter(Stream stream)
        {
            await Task.Yield();
            stream.WriteByte(1);
        }
    }

    [Fact]
    public void PayloadStream_CannotEscapeTheSynchronousCallback()
    {
        DurableSaveStore store = CreateStore();
        Stream? escaped = null;

        store.Write(stream =>
        {
            escaped = stream;
            stream.WriteByte(7);
        });

        Stream captured = Assert.IsAssignableFrom<Stream>(escaped);
        Assert.False(captured.CanWrite);
        Assert.Throws<ObjectDisposedException>(() => captured.WriteByte(8));
        Assert.Throws<NotSupportedException>(() =>
        {
            _ = captured.WriteAsync(new byte[] { 9 }, 0, 1);
        });
    }

    [Fact]
    public void Write_WhenEveryExistingSlotIsInvalid_RefusesToDestroyEvidence()
    {
        DurableSaveStore store = CreateStore();
        Directory.CreateDirectory(_directory);
        File.WriteAllText(store.PrimaryPath, "invalid-primary");
        File.WriteAllText(store.PreviousPath, "invalid-previous");

        byte[] primaryBefore = File.ReadAllBytes(store.PrimaryPath);
        byte[] previousBefore = File.ReadAllBytes(store.PreviousPath);

        Assert.Throws<InvalidDataException>(() =>
            store.Write(static stream => stream.Write("replacement"u8)));
        Assert.Equal(primaryBefore, File.ReadAllBytes(store.PrimaryPath));
        Assert.Equal(previousBefore, File.ReadAllBytes(store.PreviousPath));
    }

    [Fact]
    public void MaximumPayloadLimit_IsEnforcedBeforePublication()
    {
        DurableSaveStore store = CreateStore(new DurableSaveStoreOptions
        {
            MaximumPayloadBytes = 4,
        });

        Assert.Throws<InvalidDataException>(() =>
            store.Write(static stream => stream.Write("12345"u8)));
        Assert.False(File.Exists(store.PrimaryPath));
        Assert.False(File.Exists(store.PreviousPath));
    }

    [Fact]
    public void WorldConvenienceApi_RoundTripsDurableWorld()
    {
        DurableSaveStore store = CreateStore();
        var source = new World();
        source.CreateEntity();
        source.CreateEntity();
        var registry = new SerializationRegistry();

        DurableSaveCommit commit = store.WriteWorld(source, registry);
        World restored = store.ReadWorld(registry);

        Assert.Equal(1UL, commit.Generation);
        Assert.Equal(2, restored.EntityCount);
    }

    [Fact]
    public void HmacAuthentication_RoundTripsAndUsesCurrentEnvelope()
    {
        byte[] key = Enumerable.Range(0, 32).Select(static value => (byte)value).ToArray();
        DurableSaveStore store = CreateStore(new DurableSaveStoreOptions
        {
            AuthenticationKey = key,
        });

        DurableSaveCommit commit = WriteWorldWithEntityCount(store, 1);

        Assert.Equal(1, ReadWorldEntityCount(store));
        using var file = File.OpenRead(commit.PublishedPath);
        Span<byte> header = stackalloc byte[12];
        file.ReadExactly(header);
        Assert.Equal(4u, System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(header[8..]));
    }

    [Fact]
    public void ReadWorld_RejectsPreviousDurableEnvelopeVersion()
    {
        DurableSaveStore store = CreateStore();
        DurableSaveCommit commit = WriteWorldWithEntityCount(store, 1);

        using (var file = new FileStream(
                   commit.PublishedPath,
                   FileMode.Open,
                   FileAccess.ReadWrite,
                   FileShare.None))
        {
            Span<byte> previousVersion = stackalloc byte[sizeof(uint)];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(previousVersion, 3u);
            file.Position = 8;
            file.Write(previousVersion);
            file.Flush(flushToDisk: true);
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            store.ReadWorld(new SerializationRegistry()));
        Assert.Contains("Unsupported durable-save envelope version 3", error.Message);
    }

    [Fact]
    public void HmacAuthentication_WrongKeyRejectsEveryGeneration()
    {
        byte[] writeKey = Enumerable.Repeat((byte)0x11, 32).ToArray();
        DurableSaveStore writer = CreateStore(new DurableSaveStoreOptions
        {
            AuthenticationKey = writeKey,
        });
        WriteWorldWithEntityCount(writer, 1);

        DurableSaveStore wrongKeyReader = CreateStore(new DurableSaveStoreOptions
        {
            AuthenticationKey = Enumerable.Repeat((byte)0x22, 32).ToArray(),
        });

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            wrongKeyReader.ReadWorld(new SerializationRegistry()));
        Assert.Contains("No valid durable-save generation", error.Message);
    }

    [Fact]
    public void HmacAuthentication_TamperedPayloadIsRejected()
    {
        byte[] key = Enumerable.Repeat((byte)0x5A, 32).ToArray();
        DurableSaveStore store = CreateStore(new DurableSaveStoreOptions
        {
            AuthenticationKey = key,
        });
        DurableSaveCommit commit = WriteWorldWithEntityCount(store, 1);
        using (var file = new FileStream(commit.PublishedPath, FileMode.Open, FileAccess.ReadWrite))
        {
            file.Position = file.Length - 1;
            int value = file.ReadByte();
            file.Position--;
            file.WriteByte((byte)(value ^ 0x80));
            file.Flush(flushToDisk: true);
        }

        Assert.Throws<InvalidDataException>(() =>
            store.ReadWorld(new SerializationRegistry()));
    }

    [Fact]
    public void MinimumAcceptedGeneration_RejectsRolledBackValidSlot()
    {
        DurableSaveStore writer = CreateStore();
        DurableSaveCommit old = WriteWorldWithEntityCount(writer, 1);
        DurableSaveCommit newest = WriteWorldWithEntityCount(writer, 2);
        Assert.NotEqual(old.PublishedPath, newest.PublishedPath);

        DurableSaveStore floorReader = CreateStore(new DurableSaveStoreOptions
        {
            MinimumAcceptedGeneration = 2,
        });
        Assert.Equal(2, ReadWorldEntityCount(floorReader));

        // Simulate an attacker or stale cloud replica rolling storage back to the older, otherwise
        // digest-valid slot. The caller-persisted floor makes the rollback fail closed.
        File.Delete(newest.PublishedPath);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            floorReader.ReadWorld(new SerializationRegistry()));
        Assert.Contains("anti-rollback floor", error.Message);
        Assert.Throws<InvalidDataException>(() =>
            floorReader.Write(static stream => stream.Write("must-not-rebase"u8)));
    }

    [Fact]
    public void MinimumAcceptedGeneration_NewStoreStartsAboveFloor()
    {
        DurableSaveStore store = CreateStore(new DurableSaveStoreOptions
        {
            MinimumAcceptedGeneration = 40,
        });

        DurableSaveCommit commit = WriteWorldWithEntityCount(store, 1);

        Assert.Equal(41UL, commit.Generation);
        Assert.Equal(1, ReadWorldEntityCount(store));
    }

    [Fact]
    public void AuthenticationKey_IsBorrowedThenCopiedAndOwnedCopyIsClearedOnDispose()
    {
        byte[] callerKey = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        var store = new DurableSaveStore(
            Path.Combine(_directory, "key-lifetime.save"),
            new DurableSaveStoreOptions { AuthenticationKey = callerKey });
        byte[] retainedKey = (byte[])typeof(DurableSaveStore).GetField(
            "_authenticationKey",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        Assert.NotSame(callerKey, retainedKey);
        Assert.Equal(callerKey, retainedKey);
        _ = WriteWorldWithEntityCount(store, 1);

        store.Dispose();
        Assert.All(retainedKey, static value => Assert.Equal(0, value));
        Assert.DoesNotContain((byte)0, callerKey);
    }

    [Fact]
    public async Task Dispose_WaitsForActiveOperationThenClearsKey()
    {
        byte[] key = Enumerable.Repeat((byte)0x5C, 32).ToArray();
        var store = new DurableSaveStore(
            Path.Combine(_directory, "dispose-drain.save"),
            new DurableSaveStoreOptions { AuthenticationKey = key });
        byte[] retainedKey = (byte[])typeof(DurableSaveStore).GetField(
            "_authenticationKey",
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        using var entered = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);

        Task write = Task.Run(() => store.Write(stream =>
        {
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            stream.WriteByte(7);
        }));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Task dispose = Task.Run(store.Dispose);
        Task completed = await Task.WhenAny(dispose, Task.Delay(TimeSpan.FromMilliseconds(200)));
        Assert.NotSame(dispose, completed);

        release.Set();
        await write.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.All(retainedKey, static value => Assert.Equal(0, value));
    }

    [Fact]
    public void DisposedStore_RejectsEveryPublicReadWriteEntryPoint()
    {
        var store = CreateStore(new DurableSaveStoreOptions
        {
            AuthenticationKey = Enumerable.Repeat((byte)0x3A, 32).ToArray(),
        });
        store.Dispose();
        using var world = new World();
        var registry = new SerializationRegistry();

        Assert.Throws<ObjectDisposedException>(() => store.Write(static _ => { }));
        Assert.Throws<ObjectDisposedException>(() => store.WriteWorld(world, registry));
        Assert.Throws<ObjectDisposedException>(() => store.ReadWorld(registry));
        store.Dispose();
    }

    [Fact]
    public void ReadWorld_WhenNoGenerationExists_ReportsMissingSave()
    {
        DurableSaveStore store = CreateStore();

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(() =>
            store.ReadWorld(new SerializationRegistry()));
        Assert.Equal(store.PrimaryPath, error.FileName);
    }

    [Fact]
    public void ReadWorld_CodecCancellationPropagatesWithoutTryingTheOlderGeneration()
    {
        DurableSaveStore store = CreateStore();
        SerializationTypeKey key = FatalCodecKey();
        var writeRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(key);
        WritePositionGenerations(store, writeRegistry);
        var readRegistry = new SerializationRegistry()
            .Register<SerPosition, CancelingPositionCodec>(key);
        CancelingPositionCodec.Reset();

        OperationCanceledException error = Assert.Throws<OperationCanceledException>(
            () => store.ReadWorld(readRegistry));

        Assert.Contains("codec cancellation", error.Message);
        Assert.Equal(1, CancelingPositionCodec.ReadCount);
        using World recovered = store.ReadWorld(writeRegistry);
        Assert.Equal(1, recovered.EntityCount);
    }

    [Fact]
    public void ReadWorld_CodecOutOfMemoryPropagatesWithoutTryingTheOlderGeneration()
    {
        DurableSaveStore store = CreateStore();
        SerializationTypeKey key = FatalCodecKey();
        var writeRegistry = new SerializationRegistry()
            .Register<SerPosition, SerPositionFullCodec>(key);
        WritePositionGenerations(store, writeRegistry);
        var readRegistry = new SerializationRegistry()
            .Register<SerPosition, OutOfMemoryPositionCodec>(key);
        OutOfMemoryPositionCodec.Reset();

        OutOfMemoryException error = Assert.Throws<OutOfMemoryException>(
            () => store.ReadWorld(readRegistry));

        Assert.Contains("codec allocation", error.Message);
        Assert.Equal(1, OutOfMemoryPositionCodec.ReadCount);
        using World recovered = store.ReadWorld(writeRegistry);
        Assert.Equal(1, recovered.EntityCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private DurableSaveStore CreateStore(DurableSaveStoreOptions? options = null) =>
        new(Path.Combine(_directory, "world.save"), options);

    private static DurableSaveCommit WriteWorldWithEntityCount(DurableSaveStore store, int count)
    {
        using World world = CreateWorldWithEntityCount(count);
        return store.WriteWorld(world, new SerializationRegistry());
    }

    private static int ReadWorldEntityCount(DurableSaveStore store)
    {
        using World world = store.ReadWorld(new SerializationRegistry());
        return world.EntityCount;
    }

    private static World CreateWorldWithEntityCount(int count)
    {
        var world = new World();
        for (int i = 0; i < count; i++)
            world.CreateEntity();
        return world;
    }

    private static SerializationTypeKey FatalCodecKey() => new(
        Guid.Parse("24EC49CA-14FC-46BC-916C-914474F9E17F"),
        "tests.durable.fatal-codec",
        0xF82D4B56A3C1709Eul);

    private static void WritePositionGenerations(
        DurableSaveStore store,
        SerializationRegistry registry)
    {
        for (int generation = 1; generation <= 2; generation++)
        {
            using var world = new World();
            world.CreateEntity(new SerPosition
            {
                X = generation,
                Y = -generation,
            });
            store.WriteWorld(world, registry);
        }
    }

    private struct CancelingPositionCodec : IComponentCodec<SerPosition>
    {
        private static int s_readCount;

        internal static int ReadCount => Volatile.Read(ref s_readCount);

        internal static void Reset() => Volatile.Write(ref s_readCount, 0);

        public void Write(ref DataWriter writer, in SerPosition value) =>
            throw new NotSupportedException();

        public void Read(ref DataReader reader, out SerPosition value)
        {
            _ = reader;
            Interlocked.Increment(ref s_readCount);
            throw new OperationCanceledException("codec cancellation");
        }
    }

    private struct OutOfMemoryPositionCodec : IComponentCodec<SerPosition>
    {
        private static int s_readCount;

        internal static int ReadCount => Volatile.Read(ref s_readCount);

        internal static void Reset() => Volatile.Write(ref s_readCount, 0);

        public void Write(ref DataWriter writer, in SerPosition value) =>
            throw new NotSupportedException();

        public void Read(ref DataReader reader, out SerPosition value)
        {
            _ = reader;
            Interlocked.Increment(ref s_readCount);
            throw new OutOfMemoryException("codec allocation");
        }
    }

    private sealed class SimulatedCrashException(DurableSaveWriteStage stage)
        : Exception($"Simulated crash at {stage}.");
}
