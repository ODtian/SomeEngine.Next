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
    public void DiagnosticRequiresHardwareAdapterAndNativeRunner()
    {
        BenchmarkUsageException missingAdapter = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["diagnose", "--native-runner", "native.exe"]));
        Assert.Contains("adapter", missingAdapter.Message, StringComparison.OrdinalIgnoreCase);

        BenchmarkUsageException missingNative = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["diagnose", "--adapter", "1:0"]));
        Assert.Contains("native-runner", missingNative.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnosticUsesFixedDrawOnlyCounts()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "diagnose",
            "--adapter", "1:0",
            "--native-runner", "native.exe",
            "--managed-runner", "managed.exe",
        ]);

        Assert.Equal(BenchmarkProfile.FastDiagnostic, options.Profile);
        Assert.Equal(FixedGraphicsProtocol.DiagnosticWarmupFrames, options.WarmupFrames);
        Assert.Equal(FixedGraphicsProtocol.DiagnosticMeasuredFrames, options.MeasuredFrames);
        Assert.Equal(FixedGraphicsProtocol.DiagnosticDrawCount, options.DrawCount);
        Assert.Equal(0, options.BarrierCount);
    }

    [Fact]
    public void DiagnosticCountsCannotBeOverridden()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse([
                "diagnose",
                "--adapter", "1:0",
                "--native-runner", "native.exe",
                "--warmup", "256",
            ]));

        Assert.Contains("fixed", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticWorkerAcceptsZeroUnusedBarriers()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "worker",
            "--profile", "diagnose",
            "--variant", "generic-rhi",
            "--adapter", "1:0",
            "--barriers", "0",
        ]);

        Assert.Equal(BenchmarkProfile.FastDiagnostic, options.Profile);
        Assert.Equal(0, options.BarrierCount);
    }

    [Fact]
    public void ResumeIsRestrictedToHardwareControllers()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--resume", "raw-run"]));

        Assert.Contains("diagnose or certify", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DiagnosticAcceptsResumeDirectory()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "diagnose",
            "--adapter", "1:0",
            "--native-runner", "native.exe",
            "--resume", "raw-run",
        ]);

        Assert.Equal(BenchmarkProfile.FastDiagnostic, options.Profile);
        Assert.EndsWith("raw-run", options.ResumeDirectory, StringComparison.OrdinalIgnoreCase);
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
