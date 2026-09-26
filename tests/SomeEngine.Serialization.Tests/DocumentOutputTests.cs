using System.Security.Cryptography;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Packs;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Tests;

public sealed class DocumentOutputTests
{
    [Theory]
    [InlineData(ChunkCompression.None)]
    [InlineData(ChunkCompression.Brotli)]
    public async Task SegmentedPagesProduceTheSameDocumentAsContinuousMemory(ChunkCompression compression)
    {
        byte[] payload = new byte[256 * 1024 + 173];
        new Random(218).NextBytes(payload);
        using var arena = new PageArena(new ResidencyBudgetLedger(),
            new() { MaximumPageSize = 1024, AllocationSize = 16384 });
        using var buffer = new PageBuffer(arena, payload.Length);
        buffer.Write(0, payload);
        using var pages = buffer.Retain();
        buffer.Dispose();
        using var continuous = new MemoryStream();
        await BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(1, payload, compression: compression).WriteAsync(continuous);
        using var segmented = new MemoryStream();
        await BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(1, pages.Sequence, compression: compression).WriteAsync(segmented);
        Assert.Equal(continuous.ToArray(), segmented.ToArray());
        await using var document = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(segmented.ToArray()), ownsSource: true);
        using var loaded = await document.AcquireChunkAsync(1);
        Assert.Equal(payload, loaded.Memory.ToArray());
    }

    [Fact]
    public async Task SeekableStreamMatchesFileOutputAndSupportsVersionInspectionAndCopy()
    {
        byte[] payload = new byte[256 * 1024];
        new Random(17).NextBytes(payload);
        var writer = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(1, payload, compression: ChunkCompression.Brotli);
        using MappedTestDocument file = writer.BuildMapped();
        using var target = new TestOutput();
        await writer.WriteAsync(target);
        Assert.Equal(file.AsSpan().ToArray(), target.Bytes);
        target.Position = 19;
        Guid version = await BinaryDocument.ReadVersionAsync(target);
        Assert.Equal(19, target.Position);
        await using var document = await BinaryDocument<TestRoot>.OpenAsync(new MemoryRangeSource(file), ownsSource: true);
        Assert.Equal(document.Generation, version);
        using var copy = new TestOutput();
        await document.CopyToAsync(copy);
        Assert.Equal(target.Bytes, copy.Bytes);
    }

    [Fact]
    public async Task SignedAndUnsignedPackStreamsMatchFileOutput()
    {
        using MappedTestDocument asset = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();
        var writer = new AssetPackBuilder().AddAsset(Guid.NewGuid(), "test", asset, TestRoot.SchemaFingerprint);
        using MappedTestDocument file = writer.BuildMapped();
        using var target = new TestOutput();
        await writer.WriteAsync(target);
        Assert.Equal(file.AsSpan().ToArray(), target.Bytes);

        using RSA key = RSA.Create(2048);
        using MappedTestDocument signedFile = writer.BuildSignedMapped(key);
        using var signedTarget = new TestOutput();
        await writer.WriteSignedAsync(signedTarget, key);
        Assert.Equal(signedFile.AsSpan().ToArray(), signedTarget.Bytes);
        await using var pack = await AssetPack.OpenVerifiedAsync(new MemoryRangeSource(signedTarget.Bytes), key, ownsSource: true);
        Assert.Equal(1, pack.Count);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task UnsupportedOutputFailsBeforeModifyingItsContent(bool writable, bool seekable)
    {
        using var output = new TestOutput(writable, seekable);
        using MappedTestDocument asset = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();
        var document = BinaryDocumentWriter.Create(TestRoots.Canonical());
        var pack = new AssetPackBuilder().AddAsset(Guid.NewGuid(), "test", asset, TestRoot.SchemaFingerprint);
        await Assert.ThrowsAsync<ArgumentException>(() => document.WriteAsync(output).AsTask());
        await Assert.ThrowsAsync<ArgumentException>(() => pack.WriteAsync(output).AsTask());
        Assert.Equal(0, output.Mutations);
    }

    [Fact]
    public async Task FailedAndCanceledPackPublicationPreservesThePreviousFile()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"SomeEngine-pack-output-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, "content.pack");
        try
        {
            using MappedTestDocument asset = BinaryDocumentWriter.Create(TestRoots.Canonical()).BuildMapped();
            var writer = new AssetPackBuilder().AddAsset(Guid.NewGuid(), "test", asset, TestRoot.SchemaFingerprint);
            await writer.PublishAsync(path);
            byte[] previous = await File.ReadAllBytesAsync(path);
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.PublishAsync(path, cancellationToken: cancellation.Token).AsTask());
            using RSA invalidKey = RSA.Create(2048);
            invalidKey.Dispose();
            await Assert.ThrowsAsync<ObjectDisposedException>(() => writer.PublishSignedAsync(path, invalidKey).AsTask());
            Assert.Equal(previous, await File.ReadAllBytesAsync(path));
            Assert.Single(Directory.EnumerateFiles(directory));

            await using var oldPack = await AssetPack.OpenAsync(FileRangeSource.Open(path), ownsSource: true);
            await writer.PublishAsync(path);
            Assert.Equal(1, oldPack.Count);
        }
        finally
        {
            foreach (string file in Directory.EnumerateFiles(directory)) File.Delete(file);
            Directory.Delete(directory);
        }
    }

    private sealed class TestOutput(bool writable = true, bool seekable = true) : Stream
    {
        private readonly MemoryStream _bytes = new();
        public override bool CanRead => true;
        public override bool CanSeek => seekable;
        public override bool CanWrite => writable;
        public override long Length => _bytes.Length;
        public override long Position { get => _bytes.Position; set => _bytes.Position = value; }
        internal byte[] Bytes => _bytes.ToArray();
        internal int Mutations { get; private set; }
        public override int Read(byte[] buffer, int offset, int count) => _bytes.Read(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => _bytes.Seek(offset, origin);
        public override void SetLength(long value) { Mutations++; _bytes.SetLength(value); }
        public override void Write(byte[] buffer, int offset, int count) { Mutations++; _bytes.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Mutations++; _bytes.Write(buffer); }
        public override void Flush() => _bytes.Flush();
        protected override void Dispose(bool disposing) { if (disposing) _bytes.Dispose(); base.Dispose(disposing); }
    }
}
