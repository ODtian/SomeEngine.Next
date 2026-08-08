namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class AllocationEventCounterTests
{
    [Fact]
    public void DelayedTickWithZeroIntervalBytesIsNotAttributedToTheFrame()
    {
        Assert.Equal(0, AllocationEventCounter.AttributeIntervalEvents(0, 1));
    }

    [Fact]
    public void TickIsRetainedWhenTheExactThreadCounterObservedAllocation()
    {
        Assert.Equal(2, AllocationEventCounter.AttributeIntervalEvents(64, 2));
        Assert.Equal(0, AllocationEventCounter.AttributeIntervalEvents(64, 0));
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, -1)]
    public void NegativeCountersAreRejected(long bytes, long events)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AllocationEventCounter.AttributeIntervalEvents(bytes, events));
    }
}
