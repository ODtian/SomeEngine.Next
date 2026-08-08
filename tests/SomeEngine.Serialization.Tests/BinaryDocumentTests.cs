using System.Buffers.Binary;
using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

public sealed class BinaryDocumentTests
{
    private const int HeaderSize = 128;
    private const int DirectoryEntrySize = 96;

    [Fact]
    public async Task HeaderIsFixedAt128BytesAndOpenReadsItAsOneRange()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();

        Assert.True(bytes.AsSpan(0, 8).SequenceEqual("SEBDOC03"u8));
        Assert.Equal(HeaderSize, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(10, 2)));
        uint catalogLength = BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(120, 4));
        long rootOffset = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48, 8));
        long rootLength = BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(56, 8));
        Assert.True(catalogLength >= 36);
        Assert.Equal(Align(HeaderSize + catalogLength, 16), rootOffset);

        var source = new CountingRangeSource(bytes);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            source,
            ownsSource: true);

        RangeOperation[] operations = source.Operations;
        Assert.Equal(new RangeOperation(0, HeaderSize), operations[0]);
        Assert.Equal(new RangeOperation(HeaderSize, checked((int)catalogLength)), operations[1]);
        Assert.Equal(rootOffset, operations[2].Offset);
        Assert.Equal(rootLength, operations[2].Length);
        TestRoots.AssertEquivalent(TestRoots.Canonical(), document.Root);
    }

    [Fact]
    public void BuildIsDeterministicAndCanonicalizesDirectoryOrder()
    {
        TestRoot root = TestRoots.Canonical();
        byte[] alpha = Enumerable.Range(0, 257).Select(static value => (byte)value).ToArray();
        byte[] beta = new byte[4096];
        new Random(0x51A1).NextBytes(beta);

        using MappedTestDocument first = BinaryDocumentWriter.Create(root)
            .AddChunk(30, beta, 300, ChunkCompression.Brotli, alignment: 64, ordinal: 2)
            .AddChunk(10, alpha, 100, ChunkCompression.None, alignment: 16, ordinal: 0)
            .AddChunk(20, [7, 8, 9], 200, ChunkCompression.None, alignment: 32, ordinal: 1)
            .BuildMapped();
        using MappedTestDocument second = BinaryDocumentWriter.Create(root)
            .AddChunk(20, [7, 8, 9], 200, ChunkCompression.None, alignment: 32, ordinal: 1)
            .AddChunk(10, alpha, 100, ChunkCompression.None, alignment: 16, ordinal: 0)
            .AddChunk(30, beta, 300, ChunkCompression.Brotli, alignment: 64, ordinal: 2)
            .BuildMapped();

        Assert.Equal(first, second);
        int directoryOffset = GetDirectoryOffset(first);
        Assert.Equal(10UL, ReadEntryKey(first, directoryOffset, 0));
        Assert.Equal(20UL, ReadEntryKey(first, directoryOffset, 1));
        Assert.Equal(30UL, ReadEntryKey(first, directoryOffset, 2));
    }

    [Fact]
    public async Task NoneAndBrotliChunksRoundTripAndExposeDecodedContentHashes()
    {
        byte[] plain = Enumerable.Range(0, 1025).Select(static value => (byte)(value * 31)).ToArray();
        byte[] compressed = new byte[32 * 1024];
        new Random(0x51A1).NextBytes(compressed);
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical(number: 42))
            .AddChunk(11, plain, typeFingerprint: 101, ChunkCompression.None)
            .AddChunk(22, compressed, typeFingerprint: 202, ChunkCompression.Brotli)
            .BuildMapped();
        Digest256 plainHash = Digest256.ComputeSha256(plain);
        Digest256 compressedHash = Digest256.ComputeSha256(compressed);
        TestFileRangeSource source = bytes.DetachToFileRangeSource();
        Array.Clear(plain);
        Array.Clear(compressed);

        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            source,
            ownsSource: true);
        using ChunkLease plainLease = await document.AcquireChunkAsync(11);
        using ChunkLease compressedLease = await document.AcquireChunkAsync(22);

        Assert.Equal(42, document.Root.Int32Value);
        Assert.Equal(plainHash, Digest256.ComputeSha256(plainLease.Memory.Span));
        Assert.Equal(compressedHash, Digest256.ComputeSha256(compressedLease.Memory.Span));
        Assert.Equal(ChunkCompression.None, plainLease.Descriptor.Compression);
        Assert.Equal(ChunkCompression.Brotli, compressedLease.Descriptor.Compression);
        Assert.Equal(plainHash, plainLease.Descriptor.ContentHash);
        Assert.Equal(compressedHash, compressedLease.Descriptor.ContentHash);
        Assert.Equal(101UL, plainLease.Descriptor.TypeFingerprint);
        Assert.Equal(202UL, compressedLease.Descriptor.TypeFingerprint);
    }

    [Fact]
    public async Task HighlyCompressibleBrotliRequiresExplicitNoneAndIsNeverReencoded()
    {
        byte[] zeros = new byte[32 * 1024];
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            BinaryDocumentWriter.Create(TestRoots.Canonical())
                .AddChunk(23, zeros, compression: ChunkCompression.Brotli)
                .BuildMapped());
        Assert.Contains("ChunkCompression.None", error.Message, StringComparison.Ordinal);
        Assert.Contains("implicit re-encoding is disabled", error.Message, StringComparison.Ordinal);

        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(23, zeros, compression: ChunkCompression.None)
            .BuildMapped();

        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes),
            ownsSource: true);
        using ChunkLease lease = await document.AcquireChunkAsync(23);

        Assert.Equal(zeros, lease.Memory.ToArray());
        Assert.Equal(ChunkCompression.None, lease.Descriptor.Compression);
        Assert.Equal(zeros.LongLength, lease.Descriptor.StoredLength);
        Assert.Equal(zeros.LongLength, lease.Descriptor.DecodedLength);
        Assert.Equal(Digest256.ComputeSha256(zeros), lease.Descriptor.ContentHash);
    }

    [Fact]
    public async Task AcquiringOneChunkDoesNotReadTheWholeDocumentPayload()
    {
        const int chunkLength = 128 * 1024;
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(10, Enumerable.Repeat((byte)0x10, chunkLength).ToArray())
            .AddChunk(20, Enumerable.Repeat((byte)0x20, chunkLength).ToArray())
            .AddChunk(30, Enumerable.Repeat((byte)0x30, chunkLength).ToArray())
            .BuildMapped();
        int directoryOffset = GetDirectoryOffset(bytes);
        long firstOffset = ReadEntryOffset(bytes, directoryOffset, 0);
        long targetOffset = ReadEntryOffset(bytes, directoryOffset, 1);
        long lastOffset = ReadEntryOffset(bytes, directoryOffset, 2);
        TestFileRangeSource source = bytes.DetachToFileRangeSource();

        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(source);
        source.ResetOperations();
        using ChunkLease lease = await document.AcquireChunkAsync(20);

        Assert.Equal(Enumerable.Repeat((byte)0x20, chunkLength), lease.Memory.ToArray());
        RangeOperation[] operations = source.Operations;
        Assert.Contains(operations, operation => operation == new RangeOperation(targetOffset, chunkLength));
        Assert.DoesNotContain(operations, operation => operation.Offset == firstOffset);
        Assert.DoesNotContain(operations, operation => operation.Offset == lastOffset);
        Assert.DoesNotContain(operations, operation => operation.Offset == 0 && operation.Length == source.Length);
        Assert.True(operations.Sum(static operation => (long)operation.Length) < source.Length / 2);
    }

    [Fact]
    public async Task OpeningVirtualEightGiBDocumentAndOneFaultReadsOnlyMetadataAndRequestedRange()
    {
        const long virtualLength = 8L * 1024 * 1024 * 1024;
        byte[] payload = [9, 8, 7, 6];
        using MappedTestDocument seed = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(0x8001, payload, alignment: 16)
            .BuildMapped();
        int directoryOffset = GetDirectoryOffset(seed);
        int metadataLength = directoryOffset + DirectoryEntrySize;
        long virtualChunkOffset = virtualLength - 16;
        BinaryPrimitives.WriteInt64LittleEndian(seed.AsSpan(80, 8), virtualLength);
        BinaryPrimitives.WriteInt64LittleEndian(
            seed.AsSpan(directoryOffset + 16, 8),
            virtualChunkOffset);
        RefreshEntryChecksum(seed, directoryOffset);
        RefreshHeaderChecksum(seed);
        var source = new SparseRangeSource(
            virtualLength,
            seed.AsMemory(0, metadataLength),
            virtualChunkOffset,
            payload);

        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            source,
            ownsSource: true);

        RangeOperation[] openOperations = source.Operations;
        Assert.Equal(virtualLength, source.Length);
        Assert.All(openOperations, operation => Assert.True(operation.Offset < metadataLength));
        Assert.True(openOperations.Sum(static operation => (long)operation.Length) < 1024);

        source.ResetOperations();
        using ChunkLease lease = await document.AcquireChunkAsync(0x8001);

        Assert.Equal(payload, lease.Memory.ToArray());
        Assert.Contains(source.Operations, operation =>
            operation == new RangeOperation(virtualChunkOffset, payload.Length));
        Assert.DoesNotContain(source.Operations, operation =>
            operation.Offset > metadataLength && operation.Offset != virtualChunkOffset);
    }

    [Fact]
    public async Task DirectoryLookupUsesBinarySearchRatherThanLinearScanning()
    {
        var builder = BinaryDocumentWriter.Create(TestRoots.Canonical());
        for (int index = 0; index < 1024; index++)
            builder.AddChunk(checked((ulong)(index * 2 + 1)), [(byte)index]);
        using MappedTestDocument bytes = builder.BuildMapped();
        var source = new CountingRangeSource(bytes);

        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(source);
        source.ResetOperations();
        BinaryChunkEntry? descriptor = await document.FindChunkAsync(1555);

        Assert.NotNull(descriptor);
        Assert.Equal(1555UL, descriptor.Value.Key);
        int entryReads = source.Operations.Count(static operation => operation.Length == DirectoryEntrySize);
        Assert.InRange(entryReads, 3, 12);
        Assert.DoesNotContain(source.Operations, static operation => operation.Length == 1);
    }

    [Fact]
    public async Task ConcurrentChunkLeasesHaveIndependentLifetimes()
    {
        byte[] payload = [4, 8, 15, 16, 23, 42];
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(7, payload)
            .BuildMapped();
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes),
            ownsSource: true);

        Task<ChunkLease> firstTask = document.AcquireChunkAsync(7).AsTask();
        Task<ChunkLease> secondTask = document.AcquireChunkAsync(7).AsTask();
        ChunkLease[] leases = await Task.WhenAll(firstTask, secondTask);

        leases[0].Dispose();
        Assert.Throws<ObjectDisposedException>(() => leases[0].Memory);
        Assert.Equal(payload, leases[1].Memory.ToArray());
        leases[1].Dispose();
    }

    [Fact]
    public async Task DocumentDisposalInvalidatesOutstandingLeasesAndOwnedSource()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(9, [1, 3, 5, 7])
            .BuildMapped();
        var source = new CountingRangeSource(bytes);
        BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(source, ownsSource: true);
        ChunkLease lease = await document.AcquireChunkAsync(9);

        await document.DisposeAsync();

        Assert.True(source.IsDisposed);
        Assert.Throws<ObjectDisposedException>(() => lease.Memory);
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => document.AcquireChunkAsync(9).AsTask());
        lease.Dispose();
    }

    [Fact]
    public async Task CorruptRootHashIsRejectedWhileOpening()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();
        int rootOffset = checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(48, 8)));
        bytes[rootOffset] ^= 0x80;

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => BinaryDocument<TestRoot>.OpenAsync(new MemoryRangeSource(bytes)).AsTask());

        Assert.Contains("SHA-256 validation failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CorruptChunkHashIsRejectedBeforeBytesAreLeased()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(5, [1, 2, 3, 4])
            .BuildMapped();
        int entryOffset = GetDirectoryOffset(bytes);
        int payloadOffset = checked((int)ReadEntryOffset(bytes, entryOffset, 0));
        bytes[payloadOffset] ^= 0x01;
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes),
            ownsSource: true);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => document.AcquireChunkAsync(5).AsTask());

        Assert.Contains("SHA-256 validation failed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ChunkOffsetOutsidePayloadRegionIsRejected()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(5, [1, 2, 3, 4])
            .BuildMapped();
        int entryOffset = GetDirectoryOffset(bytes);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(entryOffset + 16, 8), bytes.LongLength);
        RefreshEntryChecksum(bytes, entryOffset);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes),
            ownsSource: true);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => document.FindChunkAsync(5).AsTask());

        Assert.Contains("outside the binary payload region", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OverlappingChunkRangesAreRejected()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(5, new byte[64])
            .AddChunk(6, new byte[64])
            .BuildMapped();
        int directoryOffset = GetDirectoryOffset(bytes);
        long firstOffset = ReadEntryOffset(bytes, directoryOffset, 0);
        BinaryPrimitives.WriteInt64LittleEndian(
            bytes.AsSpan(directoryOffset + DirectoryEntrySize + 16, 8),
            firstOffset);
        RefreshEntryChecksum(bytes, directoryOffset + DirectoryEntrySize);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes),
            ownsSource: true);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => document.FindChunkAsync(6).AsTask());

        Assert.Contains("overlap", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DeclaredCompressionBombIsRejectedBeforeDecompression()
    {
        byte[] seed = new byte[1024];
        new Random(0xC0DE).NextBytes(seed);
        byte[] repeated = new byte[32 * 1024];
        for (int offset = 0; offset < repeated.Length; offset += seed.Length)
            seed.CopyTo(repeated, offset);
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(77, repeated, compression: ChunkCompression.Brotli)
            .BuildMapped();
        var limits = new BinaryReadLimits { MaxCompressionRatio = 2 };
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(bytes),
            ownsSource: true,
            limits);

        InvalidDataException exception = await Assert.ThrowsAsync<InvalidDataException>(
            () => document.AcquireChunkAsync(77).AsTask());

        Assert.Contains("compression ratio", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SourceGenerationChangeInvalidatesAnOpenDocument()
    {
        using MappedTestDocument bytes = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(91, [9, 1])
            .BuildMapped();
        var source = new CountingRangeSource(bytes);
        await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(source);

        source.AdvanceGeneration();

        IOException exception = await Assert.ThrowsAsync<IOException>(
            () => document.AcquireChunkAsync(91).AsTask());
        Assert.Contains("generation changed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    private static int GetDirectoryOffset(MappedTestDocument bytes)
        => checked((int)BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(64, 8)));

    private static ulong ReadEntryKey(MappedTestDocument bytes, int directoryOffset, int index)
        => BinaryPrimitives.ReadUInt64LittleEndian(
            bytes.AsSpan(directoryOffset + index * DirectoryEntrySize, 8));

    private static long ReadEntryOffset(MappedTestDocument bytes, int directoryOffset, int index)
        => BinaryPrimitives.ReadInt64LittleEndian(
            bytes.AsSpan(directoryOffset + index * DirectoryEntrySize + 16, 8));

    private static void RefreshEntryChecksum(MappedTestDocument bytes, int entryOffset)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes.AsSpan(entryOffset, 84), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(entryOffset + 84, 4),
            BinaryPrimitives.ReadUInt32LittleEndian(hash));
    }

    private static void RefreshHeaderChecksum(MappedTestDocument bytes)
    {
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(bytes.AsSpan(0, 124), hash);
        BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(124, 4),
            BinaryPrimitives.ReadUInt32LittleEndian(hash));
    }

    private static long Align(long value, int alignment)
    {
        long mask = alignment - 1L;
        return checked((value + mask) & ~mask);
    }
}
