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
    public void DiagnosticRequiresHardwareAdapter()
    {
        BenchmarkUsageException missingAdapter = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["diagnose", "--native-runner", "native.exe"]));
        Assert.Contains("adapter", missingAdapter.Message, StringComparison.OrdinalIgnoreCase);

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
    public void ManagedRunnerIsRestrictedToDiagnosticCommand()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--managed-runner", "old-release.exe"]));

        Assert.Contains("diagnose", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WarpAlwaysSelectsWarp()
    {
        BenchmarkUsageException exception = Assert.Throws<BenchmarkUsageException>(() =>
            BenchmarkOptions.Parse(["warp", "--adapter", "1:0"]));

        Assert.Contains("WARP", exception.Message, StringComparison.Ordinal);
    }
}
