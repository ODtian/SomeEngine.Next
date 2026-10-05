namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class FrameAllocationReportTests
{
    [Fact]
    public void DelayedTickWithZeroIntervalBytesIsNotAttributedToTheFrame()
    {
        Assert.Equal(0, new FrameSample(0, 0, 0, null, 0, 1, 0).EtwAllocationEvents);
    }

    [Fact]
    public void TickIsRetainedWhenTheExactThreadCounterObservedAllocation()
    {
        Assert.Equal(2, new FrameSample(0, 0, 0, null, 64, 2, 0).EtwAllocationEvents);
        Assert.Equal(0, new FrameSample(0, 0, 0, null, 64, 0, 0).EtwAllocationEvents);
    }

    [Theory]
    [InlineData(-1, 0, nameof(FrameSample.ManagedAllocatedBytes))]
    [InlineData(0, -1, nameof(FrameSample.EtwAllocationEvents))]
    public void NegativeCountersAreRejected(long bytes, long events, string parameter)
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(
            () => new FrameSample(0, 0, 0, null, bytes, events, 0));
        Assert.Equal(parameter, error.ParamName);
    }
}
