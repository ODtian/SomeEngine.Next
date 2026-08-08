namespace SomeEngine.ECS.Benchmarks.Tests;

public sealed class MetricDistributionTests
{
    [Fact]
    public void FromUsesR7LinearInterpolationGoldenValues()
    {
        EcsBenchmarkSample[] samples = Enumerable.Range(1, 10)
            .Select(static value => BenchmarkTestData.Sample(value))
            .ToArray();

        MetricDistribution distribution = MetricDistribution.From(
            samples,
            static sample => sample.ElapsedMilliseconds);

        Assert.Equal(5.5, distribution.P50, 12);
        Assert.Equal(9.55, distribution.P95, 12);
        Assert.Equal(9.91, distribution.P99, 12);
        Assert.Equal(10.0, distribution.Max, 12);
    }
}
