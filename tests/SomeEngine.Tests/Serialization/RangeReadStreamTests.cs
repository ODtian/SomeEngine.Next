using SomeEngine.Serialization.IO;

namespace SomeEngine.Serialization.Tests;

public sealed class RangeReadStreamTests
{
    [Fact]
    public async Task ReadsAcrossRangesSeeksAndLeavesTheBorrowedSourceOpen()
    {
        byte[] bytes = Enumerable.Range(0, 170_003).Select(static value => (byte)value).ToArray();
        await using var source = new MemoryRangeSource(bytes);
        using (var stream = new RangeReadStream(source))
        {
            byte[] read = new byte[bytes.Length];
            stream.ReadExactly(read);
            Assert.Equal(bytes, read);
            Assert.Equal(-1, stream.ReadByte());
            Assert.Equal(bytes.Length - 7, stream.Seek(-7, SeekOrigin.End));
            Assert.Equal(bytes[^7], stream.ReadByte());
            stream.Position = 17;
            Assert.Equal(bytes[17], stream.ReadByte());
            Assert.Throws<IOException>(() => stream.Seek(-1, SeekOrigin.Begin));
        }
        byte[] remaining = new byte[3];
        await source.ReadExactlyAsync(0, remaining);
        Assert.Equal(bytes[..3], remaining);
    }

    [Fact]
    public async Task FailedReadCanBeRetriedWithoutReturningIncompleteBytes()
    {
        await using var source = new FailingSource();
        using var stream = new RangeReadStream(source);
        byte[] bytes = new byte[3];
        Assert.Throws<IOException>(() => stream.Read(bytes));
        Assert.Equal(0, stream.Position);
        stream.ReadExactly(bytes);
        Assert.Equal(new byte[] { 1, 2, 3 }, bytes);
    }

    [Fact]
    public async Task CanceledAndDisposedStreamsRejectReads()
    {
        await using var source = new MemoryRangeSource(new byte[] { 1 });
        using var stop = new CancellationTokenSource();
        using var stream = new RangeReadStream(source, stop.Token);
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() => stream.ReadByte());
        stream.Dispose();
        Assert.False(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.Throws<ObjectDisposedException>(() => stream.ReadByte());
    }

    private sealed class FailingSource : IRangeSource
    {
        private readonly MemoryRangeSource _bytes = new(new byte[] { 1, 2, 3 });
        private bool _failed;
        public long Length => _bytes.Length;
        public string Generation => _bytes.Generation;
        public bool LeasesAreImmutable => _bytes.LeasesAreImmutable;
        public bool RetainsResidentBacking => _bytes.RetainsResidentBacking;
        public ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (!_failed)
            {
                _failed = true;
                destination.Span[0] = 255;
                return ValueTask.FromException(new IOException("Interrupted source read."));
            }
            return _bytes.ReadExactlyAsync(offset, destination, cancellationToken);
        }
        public ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
            => _bytes.AcquireAsync(offset, length, cancellationToken);
        public ValueTask DisposeAsync() => _bytes.DisposeAsync();
    }
}
