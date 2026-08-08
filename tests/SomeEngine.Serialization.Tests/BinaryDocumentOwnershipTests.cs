using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using SomeEngine.Serialization;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

public sealed class BinaryDocumentOwnershipTests
{
    [Fact]
    public void DirectoryMetadataUsesInlineDigestsAndRetainsNoPerEntryBacking()
    {
        Assert.False(RuntimeHelpers.IsReferenceOrContainsReferences<BinaryChunkEntry>());
        Assert.Equal(
            typeof(Digest256),
            typeof(BinaryChunkEntry).GetProperty(nameof(BinaryChunkEntry.ContentHash))!.PropertyType);

        Type prepared = Assert.Single(
            typeof(BinaryDocumentWriter).GetNestedTypes(BindingFlags.NonPublic),
            static type => type.Name == "PreparedChunk");
        Assert.DoesNotContain(
            prepared.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic),
            static field => field.FieldType.IsArray);
    }

    [Fact]
    public async Task RootContractIsWrittenOnceWithoutAFullRootSpool()
    {
        var root = new CountingRoot(new byte[8 * 1024 * 1024]);
        string path = TemporaryPath();
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            long before = GC.GetAllocatedBytesForCurrentThread();
            await BinaryDocumentWriter.Create(root).WriteAsync(stream);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(1, root.WriteCalls);
            Assert.True(allocated < 1024 * 1024, $"Writer allocated {allocated:N0} bytes for an 8 MiB root.");
            Assert.True(stream.Length > root.Data.Length);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MemoryRangeSourceBorrowsTheOriginalArray()
    {
        byte[] bytes = [1, 2, 3, 4];
        await using var source = new MemoryRangeSource(bytes);
        using RangeLease lease = await source.AcquireAsync(0, bytes.Length);

        Assert.True(MemoryMarshal.TryGetArray(lease.Memory, out ArraySegment<byte> segment));
        Assert.Same(bytes, segment.Array);
    }

    [Fact]
    public async Task BrotliReadUsesOneCallerDestinationAndBoundedStoredRanges()
    {
        byte[] payload = new byte[2 * 1024 * 1024];
        new Random(0x51A1).NextBytes(payload);
        byte[] expectedHash = SHA256.HashData(payload);
        string path = TemporaryPath();
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await BinaryDocumentWriter.Create(TestRoots.Canonical())
                    .AddChunk(0xB001, payload, compression: ChunkCompression.Brotli)
                    .WriteAsync(stream);
            }

            Array.Clear(payload);
            var source = new TrackingFileRangeSource(path);
            await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
                source,
                ownsSource: true);
            BinaryChunkEntry? found = await document.FindChunkAsync(0xB001);
            Assert.True(found.HasValue);
            BinaryChunkEntry descriptor = found.GetValueOrDefault();
            source.Reset();
            int factoryCalls = 0;

            Memory<byte>? result = await document.TryReadChunkAsync(
                0xB001,
                length =>
                {
                    factoryCalls++;
                    Assert.Equal(payload.Length, length);
                    return payload;
                });

            Assert.True(result.HasValue);
            Assert.Equal(1, factoryCalls);
            Assert.True(MemoryMarshal.TryGetArray((ReadOnlyMemory<byte>)result.Value, out ArraySegment<byte> segment));
            Assert.Same(payload, segment.Array);
            Assert.Equal(expectedHash, SHA256.HashData(result.Value.Span));
            RangeOperation[] payloadReads = source.Operations
                .Where(operation => operation.Offset >= descriptor.Offset && operation.Offset < descriptor.EndOffset)
                .ToArray();
            Assert.NotEmpty(payloadReads);
            Assert.All(payloadReads, operation => Assert.InRange(operation.Length, 1, 128 * 1024));
            Assert.DoesNotContain(payloadReads, operation => operation.Length == descriptor.StoredLength);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MemoryMappedDocumentRejectsSecondOwnedPayloadBacking()
    {
        string path = TemporaryPath();
        try
        {
            using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await BinaryDocumentWriter.Create(TestRoots.Canonical())
                    .AddChunk(0xB002, new byte[4096])
                    .WriteAsync(stream);
            }

            await using BinaryDocument<TestRoot> document = await BinaryDocument<TestRoot>.OpenAsync(
                MemoryMappedRangeSource.Open(path),
                ownsSource: true);
            int factoryCalls = 0;
            NotSupportedException error = await Assert.ThrowsAsync<NotSupportedException>(
                () => document.TryReadChunkAsync(
                    0xB002,
                    length =>
                    {
                        factoryCalls++;
                        return new byte[length];
                    }).AsTask());

            Assert.Equal(0, factoryCalls);
            Assert.Contains("two physical payload backings", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TemporaryPath()
        => Path.Combine(Path.GetTempPath(), $"SomeEngine-binary-document-{Guid.NewGuid():N}.bin");

    private sealed class CountingRoot(ReadOnlyMemory<byte> data) : IBinaryContract<CountingRoot>
    {
        public int WriteCalls { get; private set; }
        public ReadOnlyMemory<byte> Data { get; } = data;
        public static Guid TypeId { get; } = Guid.Parse("58aac27c-9d5c-4c21-9f5e-6b05a44de297");
        public static ulong SchemaFingerprint => 0x84C696DD2936FA11UL;
        public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
        public static uint SchemaEpoch => 1;

        public static void Write(ref BinaryDataWriter writer, CountingRoot value)
        {
            value.WriteCalls++;
            writer.WriteMemory(value.Data);
        }

        public static CountingRoot Read(ref BinaryDataReader reader)
            => new(reader.ReadMemory() ?? ReadOnlyMemory<byte>.Empty);
    }

    private sealed class TrackingFileRangeSource : IRangeSource
    {
        private readonly FileRangeSource _inner;
        private readonly ConcurrentQueue<RangeOperation> _operations = new();

        internal TrackingFileRangeSource(string path) => _inner = FileRangeSource.Open(path);

        public long Length => _inner.Length;
        public string Generation => _inner.Generation;
        public bool LeasesAreImmutable => _inner.LeasesAreImmutable;
        public bool RetainsResidentBacking => false;
        internal RangeOperation[] Operations => _operations.ToArray();

        internal void Reset()
        {
            while (_operations.TryDequeue(out _))
            {
            }
        }

        public async ValueTask ReadExactlyAsync(
            long offset,
            Memory<byte> destination,
            CancellationToken cancellationToken = default)
        {
            _operations.Enqueue(new RangeOperation(offset, destination.Length));
            await _inner.ReadExactlyAsync(offset, destination, cancellationToken);
        }

        public async ValueTask<RangeLease> AcquireAsync(
            long offset,
            int length,
            CancellationToken cancellationToken = default)
        {
            _operations.Enqueue(new RangeOperation(offset, length));
            return await _inner.AcquireAsync(offset, length, cancellationToken);
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
