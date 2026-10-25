using System.Buffers;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

public sealed class ChunkKeyTests
{
    [Fact]
    public async Task ExistingKeysAreRejectedThroughAllAddOverloadsWithoutReplacingTheirData()
    {
        byte[] first = [1, 2, 3];
        byte[] borrowed = [4, 5, 6];
        byte[] replacement = [9, 9, 9];
        using var sourceBytes = new MemoryStream();
        await BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(11, borrowed).WriteAsync(sourceBytes);
        await using var source = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(sourceBytes.ToArray()), ownsSource: true);
        var writer = BinaryDocumentWriter.Create(TestRoots.Canonical()).AddChunk(7, first);

        Assert.Throws<InvalidOperationException>(() => writer.AddChunk(7, replacement));
        Assert.Throws<InvalidOperationException>(() => writer.AddChunk(7, (ReadOnlyMemory<byte>)replacement));
        Assert.Throws<InvalidOperationException>(() => writer.AddChunk(7, new ReadOnlySequence<byte>(replacement)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AddChunkAsync(7, source, sourceKey: 11).AsTask());
        Assert.Equal(1, writer.ChunkCount);

        await writer.AddChunkAsync(8, source, sourceKey: 11);
        Assert.Throws<InvalidOperationException>(() => writer.AddChunk(8, replacement));
        Assert.Throws<InvalidOperationException>(() => writer.AddChunk(8, (ReadOnlyMemory<byte>)replacement));
        Assert.Throws<InvalidOperationException>(() => writer.AddChunk(8, new ReadOnlySequence<byte>(replacement)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => writer.AddChunkAsync(8, source, sourceKey: 11).AsTask());
        Assert.Equal(2, writer.ChunkCount);

        writer.AddChunk(9, replacement);
        Assert.Equal(3, writer.ChunkCount);
        using var actual = new MemoryStream();
        await writer.WriteAsync(actual);
        using var expected = new MemoryStream();
        await BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(7, first).AddChunk(8, borrowed).AddChunk(9, replacement).WriteAsync(expected);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    [Fact]
    public async Task InvalidArgumentsTakePrecedenceOverDuplicateKeysAndDoNotReserveNewKeys()
    {
        byte[] payload = [1, 2, 3];
        var writer = BinaryDocumentWriter.Create(TestRoots.Canonical()).AddChunk(7, payload);
        var sequence = new ReadOnlySequence<byte>(payload);
        ChunkCompression invalidCompression = (ChunkCompression)byte.MaxValue;

        ArgumentOutOfRangeException zeroKey = Assert.Throws<ArgumentOutOfRangeException>(
            () => writer.AddChunk(0, sequence, compression: invalidCompression, alignment: 3));
        Assert.Equal("key", zeroKey.ParamName);
        ArgumentOutOfRangeException compression = Assert.Throws<ArgumentOutOfRangeException>(
            () => writer.AddChunk(7, sequence, compression: invalidCompression, alignment: 3));
        Assert.Equal("compression", compression.ParamName);
        ArgumentOutOfRangeException alignment = Assert.Throws<ArgumentOutOfRangeException>(
            () => writer.AddChunk(7, sequence, alignment: 3));
        Assert.Equal("alignment", alignment.ParamName);
        ArgumentNullException bytes = Assert.Throws<ArgumentNullException>(
            () => writer.AddChunk(7, (byte[])null!));
        Assert.Equal("decodedBytes", bytes.ParamName);
        Assert.Throws<InvalidOperationException>(() => writer.AddChunk(7, sequence));
        ArgumentOutOfRangeException newKey = Assert.Throws<ArgumentOutOfRangeException>(
            () => writer.AddChunk(8, sequence, alignment: 3));
        Assert.Equal("alignment", newKey.ParamName);
        Assert.Equal(1, writer.ChunkCount);

        writer.AddChunk(8, sequence);
        Assert.Equal(2, writer.ChunkCount);
        using var actual = new MemoryStream();
        await writer.WriteAsync(actual);
        using var expected = new MemoryStream();
        await BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(7, payload).AddChunk(8, payload).WriteAsync(expected);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    [Fact]
    public async Task SourceLookupFailuresTakePrecedenceOverDuplicateKeysAndDoNotReserveNewKeys()
    {
        byte[] first = [1, 2, 3];
        byte[] borrowed = [4, 5, 6];
        using var sourceBytes = new MemoryStream();
        await BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(11, borrowed).WriteAsync(sourceBytes);
        await using var source = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(sourceBytes.ToArray()), ownsSource: true);
        var writer = BinaryDocumentWriter.Create(TestRoots.Canonical()).AddChunk(7, first);

        ArgumentNullException missingSource = await Assert.ThrowsAsync<ArgumentNullException>(
            () => writer.AddChunkAsync(7, null!).AsTask());
        Assert.Equal("source", missingSource.ParamName);
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => writer.AddChunkAsync(7, source, sourceKey: 12).AsTask());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => writer.AddChunkAsync(7, source, cancellation.Token, sourceKey: 11).AsTask());
        await using var closedSource = source.Duplicate();
        await closedSource.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(
            () => writer.AddChunkAsync(7, closedSource, sourceKey: 11).AsTask());
        await Assert.ThrowsAsync<KeyNotFoundException>(
            () => writer.AddChunkAsync(8, source, sourceKey: 12).AsTask());
        Assert.Equal(1, writer.ChunkCount);

        await writer.AddChunkAsync(8, source, sourceKey: 11);
        Assert.Equal(2, writer.ChunkCount);
        using var actual = new MemoryStream();
        await writer.WriteAsync(actual);
        using var expected = new MemoryStream();
        await BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(7, first).AddChunk(8, borrowed).WriteAsync(expected);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }

    [Theory]
    [InlineData(10, 20, 30)]
    [InlineData(10, 30, 20)]
    [InlineData(20, 10, 30)]
    [InlineData(20, 30, 10)]
    [InlineData(30, 10, 20)]
    [InlineData(30, 20, 10)]
    public async Task InsertionOrderDoesNotChangeDocumentBytes(int first, int second, int third)
    {
        byte[] alpha = [1, 2, 3, 4];
        byte[] beta = [5, 6, 7, 8];
        byte[] gamma = [9, 10, 11, 12];
        using var sourceBytes = new MemoryStream();
        await BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(30, gamma, 300, ChunkCompression.Brotli, alignment: 64, ordinal: 2)
            .WriteAsync(sourceBytes);
        await using var source = await BinaryDocument<TestRoot>.OpenAsync(
            new MemoryRangeSource(sourceBytes.ToArray()), ownsSource: true);
        var writer = BinaryDocumentWriter.Create(TestRoots.Canonical());

        foreach (int key in new[] { first, second, third })
        {
            switch (key)
            {
                case 10:
                    writer.AddChunk(10, (ReadOnlyMemory<byte>)alpha, 100, alignment: 16, ordinal: 0);
                    break;
                case 20:
                    writer.AddChunk(20, new ReadOnlySequence<byte>(beta), 200, alignment: 32, ordinal: 1);
                    break;
                case 30:
                    await writer.AddChunkAsync(30, source);
                    break;
            }
        }

        Assert.Equal(3, writer.ChunkCount);
        using var actual = new MemoryStream();
        await writer.WriteAsync(actual);
        using var expected = new MemoryStream();
        await BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(10, alpha, 100, alignment: 16, ordinal: 0)
            .AddChunk(20, beta, 200, alignment: 32, ordinal: 1)
            .AddChunk(30, gamma, 300, ChunkCompression.Brotli, alignment: 64, ordinal: 2)
            .WriteAsync(expected);
        Assert.Equal(expected.ToArray(), actual.ToArray());
    }
}
