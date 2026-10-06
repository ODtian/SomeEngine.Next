using System.Numerics;
using SomeEngine.Render.Cluster.Tests;

namespace SomeEngine.Runtime.Output.Tests;

public sealed class RayShadowComparisonTests
{
    [Fact]
    public void ContinuousVisibilityKeepsRawErrorOutsideTheUnitRange()
    {
        string directory = Path.Combine(Path.GetTempPath(), "someengine-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            RayShadowComparison result = RayShadowComparison.Write(directory, [1u, 0u, 1u, 0u], 4, 1,
                [new(1.5f, 1, 1), new(-.5f, 1, 1), new(.75f, 1, 1), new(.25f, 1, 1)]);
            Assert.Equal(.375f, result.AllReceivers.MeanVisibilityError);
            Assert.Equal(.5f, result.AllReceivers.MaximumVisibilityError);
            Assert.Equal(4, result.AllReceivers.MismatchedPixels);
            Assert.Contains(",1.5,", File.ReadAllText(Path.Combine(directory, "ray-comparison.csv")));
            Assert.Contains(",-0.5,", File.ReadAllText(Path.Combine(directory, "ray-comparison.csv")));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void MissingAndNonfiniteCoverageRemainInTheFullReceiverPopulation()
    {
        string directory = Path.Combine(Path.GetTempPath(), "someengine-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            RayShadowComparison result = RayShadowComparison.Write(directory, [1u, 0u, 1u, 0u], 4, 1,
                [new(0, 0, 0), new(0, float.PositiveInfinity, 1), new(1, float.NaN, 1), new(0, 1, 1)]);
            Assert.Equal(4, result.AllReceivers.ComparedPixels);
            Assert.Equal(3, result.MissingRenderedReceiverPixels);
            Assert.Equal(.25f, result.RenderedReceiverCoverage);
            Assert.Equal(.75f, result.AllReceivers.MeanVisibilityError);
            Assert.Equal(3, result.AllReceivers.MismatchedPixels);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void NonfiniteVisibilityIsFullErrorEvenWhenClampingWouldMatchTruth()
    {
        string directory = Path.Combine(Path.GetTempPath(), "someengine-comparison-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            RayShadowComparison result = RayShadowComparison.Write(directory, [1u, 0u, 1u], 3, 1,
                [new(float.PositiveInfinity, 1, 1), new(float.NegativeInfinity, 1, 1), new(float.NaN, 1, 1)]);
            Assert.Equal(3, result.AllReceivers.ComparedPixels);
            Assert.Equal(3, result.AllReceivers.MismatchedPixels);
            Assert.Equal(1f, result.AllReceivers.MeanVisibilityError);
            Assert.Equal(1f, result.FalseShadowFraction);
            Assert.Equal(1f, result.MissedShadowFraction);
            string csv = File.ReadAllText(Path.Combine(directory, "ray-comparison.csv"));
            Assert.Contains("Infinity", csv);
            Assert.Contains("-Infinity", csv);
            Assert.Contains("NaN", csv);
        }
        finally { Directory.Delete(directory, true); }
    }
}
