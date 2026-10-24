namespace SomeEngine.Graphics.Benchmarks.Tests;

public sealed class BenchmarkOptionsTests
{

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
    public void GraphCpuAcceptsTheVulkanBackend()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "graph-cpu", "--adapter", "1:0",
            "--backend", "vulkan",
        ]);

        Assert.Equal(GraphicsBackendKind.Vulkan, options.GraphicsBackend);
    }

    [Fact]
    public void GraphCpuAcceptsUniqueSortedResourceCounts()
    {
        BenchmarkOptions options = BenchmarkOptions.Parse([
            "graph-cpu", "--adapter", "1:0",
            "--resource-counts", "200,75,200",
        ]);

        Assert.Equal([75, 200], options.GraphicsCpuResourceCounts);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("invalid")]
    public void GraphCpuRejectsUnsupportedResourceCounts(string value)
    {
        Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse([
                "graph-cpu", "--adapter", "1:0",
                "--resource-counts", value,
            ]));
    }

    [Fact]
    public void GraphCpuResourceSelectionIsNotAcceptedByOtherCommands()
    {
        Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse([
                "warp", "--resource-counts", "25",
            ]));
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
        Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse([
                "probe", "--adapter", "1:0", "--variants", "native-cpp",
            ]));
    }

    [Fact]
    public void DiagnosticRequiresHardwareAdapter()
    {
        Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["diagnose", "--native-runner", "native.exe"]));

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
    public void ResumeIsRestrictedToHardwareControllers()
    {
        Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--resume", "raw-run"]));

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
    public void ManagedRunnerIsRestrictedToDiagnosticCommand()
    {
        Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--managed-runner", "old-release.exe"]));

    }

    [Fact]
    public void WarpAlwaysSelectsWarp()
    {
        Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--adapter", "1:0"]));

    }
}
