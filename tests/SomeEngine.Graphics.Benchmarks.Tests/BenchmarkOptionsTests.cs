namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class BenchmarkOptionsTests
{
    [Fact]
    public void CertificationRequiresNativeRunner()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["certify", "--adapter", "1:0"]));

        Assert.Contains("native-runner", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CertificationCountsCannotBeOverridden()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse([
                "certify",
                "--adapter", "1:0",
                "--native-runner", "native.exe",
                "--samples", "1",
            ]));

        Assert.Contains("fixed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CertificationCommandSelectsVendorProfileAndFixedCounts()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "certify",
            "--adapter", "1:0",
            "--native-runner", "native.exe",
        ]);

        Assert.Equal(BenchmarkProfile.VendorCertification, options.Profile);
        Assert.Equal(FixedGraphicsProtocol.WarmupFrames, options.WarmupFrames);
        Assert.Equal(FixedGraphicsProtocol.MeasuredFrames, options.MeasuredFrames);
        Assert.Equal(FixedGraphicsProtocol.DrawCount, options.DrawCount);
        Assert.Equal(FixedGraphicsProtocol.BarrierCount, options.BarrierCount);
    }

    [Fact]
    public void WarpUsesReducedFunctionalCounts()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse(["warp"]);

        Assert.Equal(FixedGraphicsProtocol.WarpWarmupFrames, options.WarmupFrames);
        Assert.Equal(FixedGraphicsProtocol.WarpMeasuredFrames, options.MeasuredFrames);
        Assert.Equal(FixedGraphicsProtocol.WarpDrawCount, options.DrawCount);
        Assert.Equal(FixedGraphicsProtocol.WarpBarrierCount, options.BarrierCount);
    }

    [Fact]
    public void ResumeIsRestrictedToCertification()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--resume", "raw-run"]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ManagedRunnerIsRestrictedToCertification()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--managed-runner", "old-release.exe"]));

        Assert.Contains("certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WarpAlwaysSelectsWarp()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--adapter", "1:0"]));

        Assert.Contains("WARP", exception.Message, StringComparison.Ordinal);
    }
}
