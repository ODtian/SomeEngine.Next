using SomeEngine.Serialization.Containers;
using SomeEngine.Serialization.IO;
using SomeEngine.Serialization.Streaming;

namespace SomeEngine.Serialization.Tests;

public sealed class PageSourceTests
{
    [Fact]
    public async Task RangeReadsUseOwnedPagesAndKeepOnlyTheRequestedBytes()
    {
        byte[] input = Enumerable.Range(0, 1603).Select(static index => (byte)index).ToArray();
        using var arena = new PageArena(new ResidencyBudgetLedger(),
            new() { MinimumPageSize = 64, MaximumPageSize = 256, AllocationSize = 4096 });
        await using var source = new MemoryRangeSource(input);
        using PageBuffer bytes = await PageBuffer.ReadAsync(source, 93, 1107, arena);
        await source.DisposeAsync();
        using var read = bytes.Retain();
        using var branch = bytes.Duplicate();
        branch[511] ^= 0xff;
        bytes.Dispose();
        Assert.Equal(input[93..1200], read.ToArray());
        Assert.NotEqual(input[93 + 511], branch[511]);
        branch.Dispose();
        read.Dispose();
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancellationAndReadFailureReleasePagesOnlyAfterTheSourceStopsWriting(bool fail)
    {
        using var arena = new PageArena(new ResidencyBudgetLedger());
        await using var source = new PausedSource(fail);
        using var cancellation = new CancellationTokenSource();
        Task<PageBuffer> reading = PageBuffer.ReadAsync(source, 0, 256, arena, cancellation.Token).AsTask();
        await source.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        cancellation.Cancel();
        Assert.False(reading.IsCompleted);
        Assert.Equal(256, arena.Residency.Used(ResidencyClass.DecodedCpu));
        source.Continue.SetResult();
        if (fail) await Assert.ThrowsAsync<IOException>(() => reading);
        else await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        Assert.Equal(0, arena.Residency.Used(ResidencyClass.DecodedCpu));
    }

    private sealed class PausedSource(bool fail) : IRangeSource
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public long Length => 256;
        public string Generation => "page-source-test";
        public async ValueTask ReadExactlyAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            await Continue.Task;
            destination.Span.Fill(37);
            if (fail) throw new IOException("Injected read failure.");
        }
        public ValueTask<RangeLease> AcquireAsync(long offset, int length, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("This source supports caller destinations only.");
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
