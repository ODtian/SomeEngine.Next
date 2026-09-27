using System.Buffers;
using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Tests;

public sealed class PageBufferStreamTests
{
    [Fact]
    public void SeekTruncateAndExtendMatchMemoryStreamWithoutChangingRetainedBytes()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger(), new() { MaximumPageSize = 1024 });
        using var bytes = new PageBuffer(arena);
        using var actual = new PageBufferStream(bytes);
        using var expected = new MemoryStream();
        byte[] payload = Enumerable.Range(0, 9001).Select(static value => (byte)value).ToArray();
        actual.Write(payload);
        expected.Write(payload);
        using PageRange<byte> original = bytes.Retain(0, actual.Length);
        foreach (Stream stream in new Stream[] { actual, expected })
        {
            stream.Seek(1030, SeekOrigin.Begin);
            stream.WriteByte(21);
            stream.SetLength(1077);
            stream.SetLength(1201);
            stream.Seek(2049, SeekOrigin.Begin);
            stream.WriteByte(91);
            stream.Seek(-7, SeekOrigin.End);
            stream.Write(new byte[] { 1, 2, 3 });
            stream.Position = 0;
        }
        byte[] decoded = new byte[actual.Length];
        actual.ReadExactly(decoded);
        Assert.Equal(expected.ToArray(), decoded);
        Assert.Equal(-1, actual.ReadByte());
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(payload, original.Sequence.ToArray());
        actual.Dispose();
        Assert.Equal(decoded[1030], bytes[1030]);
    }

    [Fact]
    public async Task DocumentBackfillAndVersionInspectionWorkOnPages()
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        using var bytes = new PageBuffer(arena);
        using var stream = new PageBufferStream(bytes);
        var writer = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(71, new byte[] { 1, 4, 9 }, compression: ChunkCompression.Brotli);
        await writer.WriteAsync(stream);
        using var reference = new MemoryStream();
        await writer.WriteAsync(reference);
        stream.Position = 17;
        Guid version = await BinaryDocument.ReadVersionAsync(stream);
        Assert.NotEqual(Guid.Empty, version);
        Assert.Equal(17, stream.Position);
        using PageRange<byte> content = bytes.Retain(0, stream.Length);
        Assert.Equal(reference.ToArray(), content.Sequence.ToArray());
        using var readOnly = new PageBufferStream(bytes, writable: false);
        Assert.Throws<NotSupportedException>(() => readOnly.WriteByte(1));
        Assert.Throws<NotSupportedException>(() => readOnly.SetLength(0));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            stream.WriteAsync(new byte[] { 1 }, new CancellationToken(true)).AsTask());
    }
}
