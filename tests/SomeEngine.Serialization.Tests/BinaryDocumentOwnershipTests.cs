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
    public async Task LargeRootRoundTrips()
    {
        var root = new LargeRoot(new byte[8 * 1024 * 1024]);
        string path = TemporaryPath();
        try
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            await BinaryDocumentWriter.Create(root).WriteAsync(stream);

            await stream.DisposeAsync();
            await using var document = await BinaryDocument<LargeRoot>.OpenAsync(FileRangeSource.Open(path), ownsSource: true);
            Assert.Equal(root.Data.ToArray(), document.Root.Data.ToArray());
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task MemoryRangeSourceReturnsRequestedBytes()
    {
        byte[] bytes = [1, 2, 3, 4];
        await using var source = new MemoryRangeSource(bytes);
        using RangeLease lease = await source.AcquireAsync(0, bytes.Length);

        Assert.Equal(bytes, lease.Memory.ToArray());
    }

    [Fact]
    public async Task BrotliReadReturnsRequestedPayload()
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
            Assert.Equal(expectedHash, SHA256.HashData(result.Value.Span));
            RangeOperation[] payloadReads = source.Operations
                .Where(operation => operation.Offset >= descriptor.Offset && operation.Offset < descriptor.EndOffset)
                .ToArray();
            Assert.NotEmpty(payloadReads);
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
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string TemporaryPath()
        => Path.Combine(Path.GetTempPath(), $"SomeEngine-binary-document-{Guid.NewGuid():N}.bin");

    private sealed class LargeRoot(ReadOnlyMemory<byte> data) : IBinaryContract<LargeRoot>
    {
        public ReadOnlyMemory<byte> Data { get; } = data;
        public static Guid TypeId { get; } = Guid.Parse("58aac27c-9d5c-4c21-9f5e-6b05a44de297");
        public static ulong SchemaFingerprint => 0x84C696DD2936FA11UL;
        public static BinaryCompatibility Compatibility => BinaryCompatibility.ExactSchema;
        public static uint SchemaEpoch => 1;

        public static void Write(ref BinaryDataWriter writer, LargeRoot value)
        {
            writer.WriteMemory(value.Data);
        }

        public static LargeRoot Read(ref BinaryDataReader reader)
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
