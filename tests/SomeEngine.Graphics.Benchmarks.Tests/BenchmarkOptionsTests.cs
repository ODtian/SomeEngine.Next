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
    public void ProbeDefaultsToFastNonGatingInterfaceRhiRun()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse(["probe", "--adapter", "1:0"]);

        Assert.Equal(BenchmarkProfile.DeveloperProbe, options.Profile);
        Assert.Equal(64, options.WarmupFrames);
        Assert.Equal(256, options.MeasuredFrames);
        Assert.Equal(1_000, options.DrawCount);
        Assert.Equal([ReceiverVariant.InterfaceReceiver], options.Variants);
        Assert.Equal([GraphicsWorkload.PersistentDraw10000], options.Workloads);
    }

    [Fact]
    public void ProbeAcceptsSelectedWorkloadsAndManagedVariants()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "probe", "--adapter", "1:0",
            "--workloads", "empty-submit,state-suppression",
            "--variants", "interface-receiver,direct-silk",
        ]);

        Assert.Equal([GraphicsWorkload.EmptySubmit, GraphicsWorkload.StateSuppression10000], options.Workloads);
        Assert.Equal([ReceiverVariant.InterfaceReceiver, ReceiverVariant.DirectSilk], options.Variants);
    }

    [Fact]
    public void ProbeAcceptsDefaultAndOptimizedDirectSilkVariants()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "probe", "--adapter", "1:0",
            "--variants", "direct-silk-default,direct-silk",
        ]);

        Assert.Equal(
            [ReceiverVariant.DirectSilkDefault, ReceiverVariant.DirectSilk],
            options.Variants);
    }

    [Fact]
    public void GraphCpuDefaultsToTheFixedHighWatermarkCase()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "graph-cpu", "--adapter", "1:0",
        ]);

        Assert.Equal(BenchmarkProfile.GraphicsCpuDevelopment, options.Profile);
        Assert.Equal([200], options.GraphicsCpuResourceCounts);
        Assert.Equal(GraphicsBackendKind.Direct3D12, options.GraphicsBackend);
    }

    [Fact]
    public void GraphCpuAcceptsTheVulkanBackend()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "graph-cpu", "--adapter", "1:0",
            "--backend", "vulkan",
        ]);

        Assert.Equal(GraphicsBackendKind.Vulkan, options.GraphicsBackend);
    }

    [Fact]
    public void GraphCpuAcceptsAUniqueSortedOfficialResourceSubset()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "graph-cpu", "--adapter", "1:0",
            "--resource-counts", "200,75,200",
        ]);

        Assert.Equal([75, 200], options.GraphicsCpuResourceCounts);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("30")]
    [InlineData("225")]
    [InlineData("invalid")]
    public void GraphCpuRejectsResourceCountsOutsideTheOfficialSweep(string value)
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse([
                "graph-cpu", "--adapter", "1:0",
                "--resource-counts", value,
            ]));

        Assert.Contains("resource count", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GraphCpuResourceSelectionIsNotAcceptedByOtherCommands()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse([
                "warp", "--resource-counts", "25",
            ]));

        Assert.Contains("graph-cpu", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DiagnoseAcceptsExplicitDefaultDirectMode()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "diagnose",
            "--adapter", "1:0",
            "--native-runner", "native.exe",
            "--direct-mode", "default",
        ]);

        Assert.True(options.DefaultDirectCalls);
    }

    [Fact]
    public void ProbeRejectsNativeCppBecauseItCannotSelectWorkloads()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse([
                "probe", "--adapter", "1:0", "--variants", "native-cpp",
            ]));

        Assert.Contains("native-cpp", exception.Message, StringComparison.Ordinal);
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
            "--variant", "interface-receiver",
            "--adapter", "1:0",
            "--barriers", "0",
        ]);

        Assert.Equal(BenchmarkProfile.FastDiagnostic, options.Profile);
        Assert.Equal(0, options.BarrierCount);
    }

    [Fact]
    public void RepresentativeWorkerUsesPublicFrameShape()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "worker",
            "--profile", "representative",
            "--variant", "interface-receiver",
            "--adapter", "1:0",
            "--workloads", "representative-frame-serial,representative-frame-parallel",
        ]);

        Assert.Equal(BenchmarkProfile.RepresentativeCpuFrame, options.Profile);
        Assert.Equal(FixedGraphicsProtocol.RepresentativeWarmupFrames, options.WarmupFrames);
        Assert.Equal(FixedGraphicsProtocol.RepresentativeMeasuredFrames, options.MeasuredFrames);
        Assert.Equal(RepresentativeFrameProfile.DrawCount, options.DrawCount);
        Assert.Equal(RepresentativeFrameProfile.BarrierCount, options.BarrierCount);
        Assert.Equal(
            [GraphicsWorkload.RepresentativeFrameSerial, GraphicsWorkload.RepresentativeFrameParallel],
            options.Workloads);
    }

    [Fact]
    public void ResumeIsRestrictedToHardwareControllers()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--resume", "raw-run"]));

        Assert.Contains("probe", exception.Message, StringComparison.OrdinalIgnoreCase);
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
