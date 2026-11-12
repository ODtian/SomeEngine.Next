using System.Collections.Concurrent;
using SomeEngine.Serialization;
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
        newest.Create(new SerPosition { X = 1, Y = 2 });
        SaveWorld(store, newest, writeRegistry);

        Assert.Throws<InvalidDataException>(() =>
            LoadWorld(store, new SerializationRegistry()));
    }

    [Fact]
    public void DigestValidTrailingPayload_DoesNotFallBackToOlderGeneration()
    {
        DurableSaveStore store = CreateStore();
        WriteWorldWithEntityCount(store, 1);
        using World newest = CreateWorldWithEntityCount(2);
        var registry = new SerializationRegistry();
        using (DurableSaveStore.SaveWrite write = store.BeginWrite())
        {
            new WorldSerializer(registry).Save(newest, write.Stream, SavePurpose.DurableSave);
            write.Stream.WriteByte(0xA5);
            write.Commit();
        }

        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            LoadWorld(store, registry));
        Assert.Equal(SerializationError.InvalidData, SerializationErrors.GetError(error));
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
            new DurableSaveStoreOptions());

        using var newerWorld = CreateWorldWithEntityCount(2);
        Assert.Throws<SimulatedCrashException>(() =>
        {
            using DurableSaveStore.SaveWrite write = failing.BeginWrite();
            new WorldSerializer(new SerializationRegistry()).Save(newerWorld, write.Stream, SavePurpose.DurableSave);
            var stages = new FailSaveStage(faultStage);
            write.Commit(ref stages);
        });

        Assert.Equal(newGenerationWasPublished ? 2 : 1, ReadWorldEntityCount(baseline));
    }

    [Fact]
    public void PayloadWriterFailure_LeavesPreviousGenerationUntouched()
    {
        DurableSaveStore store = CreateStore();
        WriteWorldWithEntityCount(store, 1);

        Assert.Throws<SimulatedCrashException>((Action)(() =>
        {
            using DurableSaveStore.SaveWrite write = store.BeginWrite();
            write.Stream.Write("partial"u8);
            throw new SimulatedCrashException(DurableSaveWriteStage.PayloadWritten);
        }));

        Assert.Equal(1, ReadWorldEntityCount(store));
    }

    [Fact]
    public void PayloadStreamRejectsAsyncWritesBeforeCommit()
    {
        DurableSaveStore store = CreateStore();
        using (DurableSaveStore.SaveWrite write = store.BeginWrite())
            Assert.Throws<NotSupportedException>(() =>
            {
                _ = write.Stream.WriteAsync(new byte[] { 1 }, 0, 1);
            });
        Assert.False(File.Exists(store.PrimaryPath));
        Assert.False(File.Exists(store.PreviousPath));
    }

    [Fact]
    public void PayloadStream_CannotEscapeTheSynchronousCallback()
    {
        DurableSaveStore store = CreateStore();
        Stream? escaped = null;

        using (DurableSaveStore.SaveWrite write = store.BeginWrite())
        {
            escaped = write.Stream;
            write.Stream.WriteByte(7);
            write.Commit();
        }

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
            WriteBytes(store, "replacement"u8));
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
            WriteBytes(store, "12345"u8));
        Assert.False(File.Exists(store.PrimaryPath));
        Assert.False(File.Exists(store.PreviousPath));
    }

    [Fact]
    public void WorldConvenienceApi_RoundTripsDurableWorld()
    {
        DurableSaveStore store = CreateStore();
        var source = new World();
        source.Create();
        source.Create();
        var registry = new SerializationRegistry();

        DurableSaveCommit commit = SaveWorld(store, source, registry);
        World restored = LoadWorld(store, registry);

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
            LoadWorld(store, new SerializationRegistry()));
        Assert.Equal(SerializationError.UnsupportedVersion, SerializationErrors.GetError(error));
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
            LoadWorld(wrongKeyReader, new SerializationRegistry()));
        Assert.Equal(SerializationError.IntegrityMismatch, SerializationErrors.GetError(error));
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
            LoadWorld(store, new SerializationRegistry()));
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
            LoadWorld(floorReader, new SerializationRegistry()));
        Assert.Equal(SerializationError.GenerationRejected, SerializationErrors.GetError(error));
        Assert.Throws<InvalidDataException>(() =>
            WriteBytes(floorReader, "must-not-rebase"u8));
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
    public void AuthenticationDoesNotModifyCallerKey()
    {
        byte[] callerKey = Enumerable.Range(1, 32).Select(static value => (byte)value).ToArray();
        var store = new DurableSaveStore(
            Path.Combine(_directory, "key-lifetime.save"),
            new DurableSaveStoreOptions { AuthenticationKey = callerKey });
        _ = WriteWorldWithEntityCount(store, 1);

        store.Dispose();
        Assert.DoesNotContain((byte)0, callerKey);
    }

    [Fact]
    public async Task DisposeWaitsForActiveWrite()
    {
        var events = new ConcurrentQueue<string>();
        byte[] key = Enumerable.Repeat((byte)0x5C, 32).ToArray();
        var store = new DurableSaveStore(
            Path.Combine(_directory, "dispose-drain.save"),
            new DurableSaveStoreOptions { AuthenticationKey = key });
        using var entered = new ManualResetEventSlim(initialState: false);
        using var release = new ManualResetEventSlim(initialState: false);

        Task write = Task.Run(() =>
        {
            using DurableSaveStore.SaveWrite write = store.BeginWrite();
            entered.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            write.Stream.WriteByte(7);
            events.Enqueue("write");
            write.Commit();
        });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        Task dispose = Task.Run(() =>
        {
            store.Dispose();
            events.Enqueue("disposed");
        });
        try
        {
            await DisposalChecks.WaitForReadRejectionAsync(() =>
            {
                try { using World loaded = LoadWorld(store, new SerializationRegistry()); }
                catch (FileNotFoundException) { }
            });
            Assert.False(dispose.IsCompleted);
        }
        finally
        {
            release.Set();
        }
        await write.WaitAsync(TimeSpan.FromSeconds(10));
        await dispose.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { "write", "disposed" }, events);
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

        Assert.Throws<ObjectDisposedException>(() => store.BeginWrite());
        Assert.Throws<ObjectDisposedException>(() => SaveWorld(store, world, registry));
        Assert.Throws<ObjectDisposedException>(() => LoadWorld(store, registry));
        store.Dispose();
    }

    [Fact]
    public void ReadWorldReportsTruncatedGenerationAsAnIntegrityFailure()
    {
        DurableSaveStore store = CreateStore();
        WriteWorldWithEntityCount(store, 1);
        byte[] bytes = File.ReadAllBytes(store.PrimaryPath);
        File.WriteAllBytes(store.PrimaryPath, bytes[..16]);
        InvalidDataException error = Assert.Throws<InvalidDataException>(() =>
            LoadWorld(store, new SerializationRegistry()));
        Assert.Equal(SerializationError.IntegrityMismatch, SerializationErrors.GetError(error));
    }

    [Fact]
    public void ReadWorld_WhenNoGenerationExists_ReportsMissingSave()
    {
        DurableSaveStore store = CreateStore();

        FileNotFoundException error = Assert.Throws<FileNotFoundException>(() =>
            LoadWorld(store, new SerializationRegistry()));
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
        var calls = new CodecCalls();
        var readRegistry = new SerializationRegistry()
            .RegisterCodec<SerPosition, CancelingPositionCodec>(key, new CancelingPositionCodec(calls));

        Assert.Throws<OperationCanceledException>(
            () => LoadWorld(store, readRegistry));

        Assert.Equal(1, calls.Reads);
        using World recovered = LoadWorld(store, writeRegistry);
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
        var calls = new CodecCalls();
        var readRegistry = new SerializationRegistry()
            .RegisterCodec<SerPosition, OutOfMemoryPositionCodec>(key, new OutOfMemoryPositionCodec(calls));

        Assert.Throws<OutOfMemoryException>(
            () => LoadWorld(store, readRegistry));

        Assert.Equal(1, calls.Reads);
        using World recovered = LoadWorld(store, writeRegistry);
        Assert.Equal(1, recovered.EntityCount);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    private DurableSaveStore CreateStore(DurableSaveStoreOptions? options = null) =>
        new(Path.Combine(_directory, "world.save"), options);

    private static DurableSaveCommit SaveWorld(DurableSaveStore store, World world, SerializationRegistry registry)
    {
        using DurableSaveStore.SaveWrite write = store.BeginWrite();
        new WorldSerializer(registry).Save(world, write.Stream, SavePurpose.DurableSave);
        return write.Commit();
    }

    private static World LoadWorld(DurableSaveStore store, SerializationRegistry registry)
    {
        using DurableSaveStore.SaveRead read = store.BeginRead();
        return WorldSerializer.ReadWorld(read.Stream, registry,
            new WorldLoadOptions(RequiredPurpose: SavePurpose.DurableSave, RequireEndOfInput: true));
    }

    private static void WriteBytes(DurableSaveStore store, ReadOnlySpan<byte> bytes)
    {
        using DurableSaveStore.SaveWrite write = store.BeginWrite();
        write.Stream.Write(bytes);
        write.Commit();
    }

    private static DurableSaveCommit WriteWorldWithEntityCount(DurableSaveStore store, int count)
    {
        using World world = CreateWorldWithEntityCount(count);
        return SaveWorld(store, world, new SerializationRegistry());
    }

    private static int ReadWorldEntityCount(DurableSaveStore store)
    {
        using World world = LoadWorld(store, new SerializationRegistry());
        return world.EntityCount;
    }

    private static World CreateWorldWithEntityCount(int count)
    {
        var world = new World();
        for (int i = 0; i < count; i++)
            world.Create();
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
            world.Create(new SerPosition
            {
                X = generation,
                Y = -generation,
            });
            SaveWorld(store, world, registry);
        }
    }

    private struct CancelingPositionCodec(CodecCalls calls) : IComponentCodec<SerPosition>
    {
        public void Write(ref DataWriter writer, in SerPosition value) =>
            throw new NotSupportedException();

        public void Read(ref DataReader reader, out SerPosition value)
        {
            _ = reader;
            Interlocked.Increment(ref calls.Reads);
            throw new OperationCanceledException("codec cancellation");
        }
    }

    private struct OutOfMemoryPositionCodec(CodecCalls calls) : IComponentCodec<SerPosition>
    {
        public void Write(ref DataWriter writer, in SerPosition value) =>
            throw new NotSupportedException();

        public void Read(ref DataReader reader, out SerPosition value)
        {
            _ = reader;
            Interlocked.Increment(ref calls.Reads);
            throw new OutOfMemoryException("codec allocation");
        }
    }

    private sealed class SimulatedCrashException(DurableSaveWriteStage stage)
        : Exception($"Simulated crash at {stage}.");

    private readonly struct FailSaveStage(DurableSaveWriteStage expected) : ISaveWriteStages
    {
        public void Execute(DurableSaveWriteStage stage)
        {
            if (stage == expected) throw new SimulatedCrashException(stage);
        }
    }
}
