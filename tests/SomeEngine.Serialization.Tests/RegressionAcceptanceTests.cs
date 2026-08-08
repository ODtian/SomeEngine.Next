using System.Buffers.Binary;
using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Tests;

public sealed class RegressionAcceptanceTests
{
    [Fact]
    public void CurrentApiHasNoBufferedBuilderOrViewMaterializationCompatibilitySurface()
    {
        Assert.DoesNotContain(
            typeof(BinaryDocumentWriter).GetMethods(),
            static method => method.Name is "Build" or "Materialize");
        Assert.DoesNotContain(
            typeof(BinaryDocumentView<,>).GetMethods(),
            static method => method.Name == "Materialize");
        Assert.DoesNotContain(
            typeof(GeneratedExactContract.View).GetMethods(),
            static method => method.Name == "Materialize");
        Assert.DoesNotContain(
            typeof(BinaryDataReader).GetMethods(),
            static method => method.Name is "Skip" or "SkipRemaining");

        var documentWrite = Assert.Single(
            typeof(BinaryDocumentWriter).GetMethods(),
            static method => method.Name == nameof(BinaryDocumentWriter.WriteAsync));
        Assert.Equal(typeof(FileStream), documentWrite.GetParameters()[0].ParameterType);
        var packWrite = Assert.Single(
            typeof(AssetPackBuilder).GetMethods(),
            static method => method.Name == nameof(AssetPackBuilder.WriteAsync));
        Assert.Equal(typeof(FileStream), packWrite.GetParameters()[0].ParameterType);
    }

    [Theory]
    [InlineData("SEIDX001")]
    [InlineData("SEIDX002")]
    public async Task PreviousBinaryEnvelopeFailsClosed(string previousMagic)
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();
        System.Text.Encoding.ASCII.GetBytes(previousMagic, bytes.AsSpan(0, 8));
        RefreshHeaderChecksum(bytes.AsSpan());

        await using var source = new MemoryRangeSource(bytes);
        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDocument<TestRoot>.OpenAsync(source).AsTask());

        Assert.Contains("magic", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BinaryDocumentTypeIdMismatchFailsBeforeRootDecode()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();
        await using var source = new MemoryRangeSource(bytes);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDocument<DifferentTypeWithMatchingSchema>.OpenAsync(source).AsTask());

        Assert.Contains("root type id", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BinaryDocumentSchemaEpochMismatchFailsClosed()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(16, sizeof(uint)),
            TestRoot.SchemaEpoch + 1);
        RefreshHeaderChecksum(bytes.AsSpan());
        await using var source = new MemoryRangeSource(bytes);

        BinarySchemaMismatchException exception = await Assert.ThrowsAsync<BinarySchemaMismatchException>(() =>
            BinaryDocument<TestRoot>.OpenAsync(source).AsTask());

        Assert.Contains("epoch", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BinaryDocumentUnknownCompatibilityModeFailsClosed()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();
        bytes[12] = byte.MaxValue;
        RefreshHeaderChecksum(bytes.AsSpan());
        await using var source = new MemoryRangeSource(bytes);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            BinaryDocument<TestRoot>.OpenAsync(source).AsTask());

        Assert.Contains("compatibility", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BinaryDocumentReservedMetadataBitsFailClosed()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(0xA1, [1, 2, 3, 4])
            .BuildMapped();
        int directoryOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(64, 8)));
        bytes[directoryOffset + 88] = 1;
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes),
            ownsSource: true);
        InvalidDataException entryError = await Assert.ThrowsAsync<InvalidDataException>(() =>
            document.AcquireChunkAsync(0xA1).AsTask());
        Assert.Contains("reserved", entryError.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MemoryRangeSourceBorrowsCallerMemoryForLeaseLifetime()
    {
        byte[] input = [1, 2, 3, 4, 5, 6];
        await using var source = new MemoryRangeSource(input);

        input.AsSpan().Fill(0xCC);
        using RangeLease lease = await source.AcquireAsync(offset: 1, length: 4);
        input.AsSpan().Fill(0xDD);

        Assert.True(source.LeasesAreImmutable);
        Assert.Equal(new byte[] { 0xDD, 0xDD, 0xDD, 0xDD }, lease.Memory.ToArray());
    }

    [Fact]
    public async Task DerivedGenerationChangesWithEveryChunkDescriptorMetadataField()
    {
        byte[] payload = new byte[509];
        new Random(0x5EED).NextBytes(payload);

        Guid baseline = await ReadGenerationAsync(BuildDocument(
            payload,
            typeFingerprint: 0x10,
            compression: ChunkCompression.None,
            alignment: 16,
            ordinal: 0));
        Guid changedFingerprint = await ReadGenerationAsync(BuildDocument(
            payload,
            typeFingerprint: 0x11,
            compression: ChunkCompression.None,
            alignment: 16,
            ordinal: 0));
        Guid changedCompression = await ReadGenerationAsync(BuildDocument(
            payload,
            typeFingerprint: 0x10,
            compression: ChunkCompression.Brotli,
            alignment: 16,
            ordinal: 0));
        Guid changedAlignment = await ReadGenerationAsync(BuildDocument(
            payload,
            typeFingerprint: 0x10,
            compression: ChunkCompression.None,
            alignment: 32,
            ordinal: 0));
        Guid changedOrdinal = await ReadGenerationAsync(BuildDocument(
            payload,
            typeFingerprint: 0x10,
            compression: ChunkCompression.None,
            alignment: 16,
            ordinal: 1));

        Guid[] generations =
        [
            baseline,
            changedFingerprint,
            changedCompression,
            changedAlignment,
            changedOrdinal,
        ];
        Assert.Equal(generations.Length, generations.Distinct().Count());
    }

    [Fact]
    public void AssetPackRejectsCatalogFingerprintThatDisagreesWithNestedDocument()
    {
        using MappedTestDocument nestedDocument = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();
        var builder = new AssetPackBuilder();

        ArgumentException exception = Assert.Throws<ArgumentException>(() => builder.AddAsset(
            Guid.Parse("a09c1df5-866c-458b-93de-4d4f611b7e30"),
            "mesh",
            nestedDocument,
            TestRoot.Fingerprint ^ 1UL));

        Assert.Equal("schemaFingerprint", exception.ParamName);
        Assert.Contains("does not match nested document", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, builder.Count);
    }

    [Fact]
    public void ResidencyBudgetApisRejectUndefinedResidencyClass()
    {
        ResidencyClass invalid = (ResidencyClass)byte.MaxValue;
        var budgets = new ResidencyBudgets();
        var ledger = new ResidencyBudgetLedger(budgets);

        Assert.Throws<ArgumentOutOfRangeException>(() => budgets.For(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Budget(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Used(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Available(invalid));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.TryReserve(invalid, 1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => ledger.Reserve(invalid, 1));
    }

    [Fact]
    public async Task WritingToSeekableStreamTruncatesPreexistingTail()
    {
        BinaryDocumentWriter builder = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(0x51, [3, 1, 4, 1, 5, 9]);
        using MappedTestDocument expected = builder.BuildMapped();
        using MappedTestDocument actual = builder.BuildMappedOverExistingTail(expected.Length + 1024);

        Assert.Equal(expected.LongLength, actual.LongLength);
        Assert.Equal(expected, actual);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(actual),
            ownsSource: true);
        TestRoots.AssertEquivalent(TestRoots.Canonical(), document.Root);
    }

    private static MappedTestDocument BuildDocument(
        byte[] payload,
        ulong typeFingerprint,
        ChunkCompression compression,
        int alignment,
        uint ordinal)
        => BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(
                key: 0xCAFE,
                payload,
                typeFingerprint,
                compression,
                alignment,
                ordinal)
            .BuildMapped();

    private static async ValueTask<Guid> ReadGenerationAsync(MappedTestDocument documentBytes)
    {
        using (documentBytes)
        {
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(documentBytes),
            ownsSource: true);
        return document.Generation;
        }
    }

    private static void RefreshHeaderChecksum(Span<byte> document)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(document[..124], hash);
        BinaryPrimitives.WriteUInt32LittleEndian(
            document[124..],
            BinaryPrimitives.ReadUInt32LittleEndian(hash));
    }
}

internal sealed record DifferentTypeWithMatchingSchema : IBinaryContract<DifferentTypeWithMatchingSchema>
{
    internal static readonly Guid StableTypeId = Guid.Parse("314e8c17-692d-443f-ac38-a71dbc5db88a");

    public static Guid TypeId => StableTypeId;
    public static ulong SchemaFingerprint => TestRoot.Fingerprint;
    public static BinaryCompatibility Compatibility => TestRoot.Compatibility;
    public static uint SchemaEpoch => TestRoot.SchemaEpoch;

    public static void Write(ref BinaryDataWriter writer, DifferentTypeWithMatchingSchema value)
        => throw new InvalidOperationException("The type-id mismatch must be rejected before writing this contract.");

    public static DifferentTypeWithMatchingSchema Read(ref BinaryDataReader reader)
        => throw new InvalidOperationException("The type-id mismatch must be rejected before decoding this contract.");
}
