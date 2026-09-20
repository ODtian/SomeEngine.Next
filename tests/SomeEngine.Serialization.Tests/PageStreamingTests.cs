using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;
using Xunit;

namespace SomeEngine.Serialization.Tests;

public sealed class PageStreamingTests
{
    private static PageArena CreateArena(long budget = 8192) => new(
        new ResidencyBudgetLedger(new ResidencyBudgets { DecodedCpuBytes = budget }),
        new() { MinimumPageSize = 64, MaximumPageSize = 512, AllocationSize = 4096 });

    [Theory]
    [InlineData(ChunkCompression.None)]
    [InlineData(ChunkCompression.Brotli)]
    public async Task FinalDecodedPagesAreChargedOnceAndRetainedUsersOutliveTheCache(ChunkCompression compression)
    {
        byte[] payload = new byte[1173];
        new Random(123).NextBytes(payload);
        using var file = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(1, payload, compression: compression).BuildMapped();
        using var arena = CreateArena();
        await using var document = await BinaryDocument<TestRoot>.OpenAsync(new MemoryRangeSource(file), ownsSource: true);
        await using var scheduler = ChunkRequestScheduler.CreateForDocument(document, arena, 8192);
        await document.DisposeAsync();
        using var acquired = await scheduler.AcquireAsync(1);
        Assert.Equal(1280, arena.Residency.Used(ResidencyClass.DecodedCpu));
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.Compressed));
        using var branch = acquired.DuplicatePages();
        using var read = acquired.RetainPages();
        Assert.Equal(payload, read.ToArray());
        Assert.Equal(1280, arena.Residency.Used(ResidencyClass.DecodedCpu));
        branch[519] ^= 0xff;
        Assert.Equal(1792, arena.Residency.Used(ResidencyClass.DecodedCpu));
        acquired.Dispose();
        await scheduler.DisposeAsync();
        arena.Trim();
        Assert.Equal(payload, read.ToArray());
        Assert.NotEqual(payload[519], branch[519]);
        branch.Dispose();
        Assert.Equal(1280, arena.Residency.Used(ResidencyClass.DecodedCpu));
        read.Dispose();
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public async Task EditingASharedResultCannotChangeTheCleanCacheOrAReopenedVersion()
    {
        byte[] first = Enumerable.Range(0, 700).Select(i => (byte)i).ToArray();
        using var file = BinaryDocumentWriter.Create(TestRoots.Canonical()).AddChunk(1, first).BuildMapped();
        using var secondFile = BinaryDocumentWriter.Create(TestRoots.Canonical()).AddChunk(1, new byte[700]).BuildMapped();
        using var arena = CreateArena();
        await using var document = await BinaryDocument<TestRoot>.OpenAsync(new MemoryRangeSource(file), ownsSource: true);
        await using var other = await BinaryDocument<TestRoot>.OpenAsync(new MemoryRangeSource(secondFile), ownsSource: true);
        await using var scheduler = ChunkRequestScheduler.CreateForDocument(document, arena, 4096);
        await using var otherScheduler = ChunkRequestScheduler.CreateForDocument(other, arena, 4096);
        using var acquired = await scheduler.AcquireAsync(1);
        using var edited = acquired.DuplicatePages();
        edited[99] = 1;
        using var sameKey = await scheduler.AcquireAsync(1);
        using var sameRead = sameKey.RetainPages();
        Assert.Equal(first, sameRead.ToArray());
        using var otherKey = await otherScheduler.AcquireAsync(1);
        using var otherRead = otherKey.RetainPages();
        Assert.Equal(new byte[700], otherRead.ToArray());
        await using var reopened = await BinaryDocument<TestRoot>.OpenAsync(new MemoryRangeSource(file), ownsSource: true);
        await using var reopenedScheduler = ChunkRequestScheduler.CreateForDocument(reopened, arena, 4096);
        using var reopenedKey = await reopenedScheduler.AcquireAsync(1);
        using var reopenedRead = reopenedKey.RetainPages();
        Assert.Equal(first, reopenedRead.ToArray());
        Assert.NotEqual(document.Generation, other.Generation);
    }

    [Fact]
    public async Task ConcurrentWaitersSharePageReadsAndCancellationIsIsolated()
    {
        byte[] payload = new byte[1173];
        new Random(36).NextBytes(payload);
        using var file = BinaryDocumentWriter.Create(TestRoots.Canonical()).AddChunk(1, payload).BuildMapped();
        var source = new DelayedSource(new MemoryRangeSource(file));
        await using var document = await BinaryDocument<TestRoot>.OpenAsync(source, ownsSource: true);
        var descriptor = (await document.FindChunkAsync(1))!.Value;
        source.Delay(descriptor);
        using var arena = CreateArena();
        await using var scheduler = ChunkRequestScheduler.CreateForDocument(document, arena, 8192);
        using var cancel = new CancellationTokenSource();
        Task<ResidentChunkLease> canceled = scheduler.AcquireAsync(1, cancellationToken: cancel.Token).AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task<ResidentChunkLease>[] requests = Enumerable.Range(0, 16).Select(_ => scheduler.AcquireAsync(1).AsTask()).ToArray();
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
        source.Finish.TrySetResult();
        ResidentChunkLease[] results = await Task.WhenAll(requests).WaitAsync(TimeSpan.FromSeconds(10));
        foreach (var result in results)
        {
            using var read = result.RetainPages();
            Assert.Equal(payload, read.ToArray());
            result.Dispose();
        }
        Assert.Equal(payload.Length, source.PayloadBytesRead);
        Assert.Equal(1280, arena.Residency.Used(ResidencyClass.DecodedCpu));
        await scheduler.DisposeAsync();
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    [Fact]
    public async Task ShutdownWaitsForActualIoBeforeReturningPages()
    {
        using var file = BinaryDocumentWriter.Create(TestRoots.Canonical()).AddChunk(1, new byte[1173]).BuildMapped();
        var source = new DelayedSource(new MemoryRangeSource(file));
        await using var document = await BinaryDocument<TestRoot>.OpenAsync(source, ownsSource: true);
        source.Delay((await document.FindChunkAsync(1))!.Value);
        using var arena = CreateArena();
        await using var scheduler = ChunkRequestScheduler.CreateForDocument(document, arena, 8192);
        Task<ResidentChunkLease> request = scheduler.AcquireAsync(1).AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Task shutdown = scheduler.DisposeAsync().AsTask();
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => request);
            Assert.False(shutdown.IsCompleted);
            Assert.Equal(1280, arena.Statistics.ActiveBytes);
            Assert.Equal(1280, arena.Residency.Used(ResidencyClass.DecodedCpu));
        }
        finally { source.Finish.TrySetResult(); }
        await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(0, arena.Statistics.ActiveBytes);
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.Compressed));
    }

    [Fact]
    public async Task CrossCacheEvictionReleasesOnlyUnretainedPagesAndWakesAfterReadEnds()
    {
        using var file = BinaryDocumentWriter.Create(TestRoots.Canonical())
            .AddChunk(1, new byte[512]).AddChunk(2, new byte[512]).BuildMapped();
        await using var document = await BinaryDocument<TestRoot>.OpenAsync(new MemoryRangeSource(file), ownsSource: true);
        using var arena = CreateArena(512);
        await using var first = ChunkRequestScheduler.CreateForDocument(document, arena, 512);
        await using var second = ChunkRequestScheduler.CreateForDocument(document, arena, 512);
        using var acquired = await first.AcquireAsync(1);
        using var read = acquired.RetainPages();
        acquired.Dispose();
        Task<ResidentChunkLease> waiting = second.AcquireAsync(2).AsTask();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (first.ResidentCount != 0) await Task.Delay(1, deadline.Token);
        Assert.False(waiting.IsCompleted);
        Assert.Equal(512, arena.Residency.Used(ResidencyClass.DecodedCpu));
        read.Dispose();
        using var result = await waiting.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(512, result.Memory.Length);
    }

    [Fact]
    public async Task FailedReadReturnsAllFundedPagesAndCanRetry()
    {
        using var file = BinaryDocumentWriter.Create(TestRoots.Canonical()).AddChunk(1, new byte[1173]).BuildMapped();
        var source = new DelayedSource(new MemoryRangeSource(file));
        await using var document = await BinaryDocument<TestRoot>.OpenAsync(source, ownsSource: true);
        source.Delay((await document.FindChunkAsync(1))!.Value);
        source.FailNextRead = true;
        source.Finish.TrySetResult();
        using var arena = CreateArena();
        await using var scheduler = ChunkRequestScheduler.CreateForDocument(document, arena, 8192);
        await Assert.ThrowsAsync<IOException>(() => scheduler.AcquireAsync(1).AsTask());
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.Compressed));
        using var retried = await scheduler.AcquireAsync(1);
        using var read = retried.RetainPages();
        Assert.Equal(new byte[1173], read.ToArray());
    }

    [Fact]
    public async Task EvictingTheOnlyOtherCacheReaderAllowsAnInPlaceWriteAtTheBudgetLimit()
    {
        using var file = BinaryDocumentWriter.Create(TestRoots.Canonical()).AddChunk(1, new byte[512]).BuildMapped();
        await using var document = await BinaryDocument<TestRoot>.OpenAsync(new MemoryRangeSource(file), ownsSource: true);
        using var arena = CreateArena(512);
        await using var scheduler = ChunkRequestScheduler.CreateForDocument(document, arena, 512);
        using var acquired = await scheduler.AcquireAsync(1);
        using var edited = acquired.DuplicatePages();
        acquired.Dispose();
        edited[27] = 9;
        Assert.Equal(9, edited[27]);
        Assert.Equal(0, scheduler.ResidentCount);
        Assert.Equal(512, arena.Residency.Used(ResidencyClass.DecodedCpu));
        Assert.Equal(0, arena.Statistics.CopiedBytes);
    }

    private sealed class DelayedSource(IRangeSource inner) : IRangeSource
    {
        private long _start = long.MaxValue, _end;
        internal readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource Finish = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal int PayloadBytesRead;
        internal bool FailNextRead;
        internal void Delay(BinaryChunkEntry entry) { _start = entry.Offset; _end = entry.Offset + entry.StoredLength; }
        public long Length => inner.Length;
        public string Generation => inner.Generation;
        public bool LeasesAreImmutable => inner.LeasesAreImmutable;
        public bool RetainsResidentBacking => inner.RetainsResidentBacking;
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            if (offset >= _start && offset < _end)
            {
                Started.TrySetResult();
                // Models an operation which has accepted the destination but cannot abort the
                // underlying access immediately when the request is canceled.
                await Finish.Task;
                if (FailNextRead) { FailNextRead = false; throw new IOException("Read failed."); }
                await inner.ReadExactlyAsync(offset, destination, CancellationToken.None);
                PayloadBytesRead += destination.Length;
            }
            else await inner.ReadExactlyAsync(offset, destination, cancellationToken);
        }
        public ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
            => inner.AcquireAsync(offset, length, cancellationToken);
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
