namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class MetricDistributionTests
{
    [Fact]
    public void FromUsesR7LinearInterpolationGoldenValues()
    {
        double[] values = Enumerable.Range(1, 10).Select(static value => (double)value).ToArray();

        MetricDistribution distribution = MetricDistribution.From(values);

        Assert.Equal(5.5, distribution.P50, 12);
        Assert.Equal(9.55, distribution.P95, 12);
        Assert.Equal(9.91, distribution.P99, 12);
        Assert.Equal(10.0, distribution.Maximum, 12);
    }

    [Theory]
    [InlineData(-0.01)]
    [InlineData(1.01)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void PercentileRejectsValuesOutsideFiniteUnitInterval(double percentile)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => MetricDistribution.PercentileR7([1.0, 2.0], percentile));
    }
}

