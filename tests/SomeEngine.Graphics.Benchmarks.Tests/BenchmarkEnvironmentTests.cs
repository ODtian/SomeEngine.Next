namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class BenchmarkEnvironmentTests
{
    [Theory]
    [InlineData(1L, 1L)]
    [InlineData(0b10_1100L, 0b10_0000L)]
    [InlineData(0x7FFF_FFFFL, 0x4000_0000L)]
    [InlineData(long.MinValue, long.MinValue)]
    public void SchedulingSelectsTheHighestAvailableAffinityBit(long available, long expected)
    {
        Assert.Equal(expected, BenchmarkEnvironment.SelectHighestAffinityBit(available));
    }

    [Fact]
    public void SchedulingRejectsAnEmptyAffinityMask()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => BenchmarkEnvironment.SelectHighestAffinityBit(0));
    }
}
