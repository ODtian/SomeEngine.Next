using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

/// <summary>
/// Native evidence gate for the APIs restored by grill run 0004. Public API/Null behavior is
/// covered in SomeEngine.Graphics.Tests; these checks prevent a portable facade from being
/// declared complete while the D3D12 backend still contains stubs or CPU emulation.
/// </summary>
public sealed class NativeRhiMigrationContractTests
{
    private static readonly Lazy<string> BackendSources = new(ReadBackendSources);

    [Fact]
    public void Indirect_execution_uses_native_execute_indirect_and_cached_command_signatures()
    {
        string source = BackendSources.Value;
        Assert.Contains("ExecuteIndirect(", source, StringComparison.Ordinal);
        Assert.Contains("CreateCommandSignature", source, StringComparison.Ordinal);
        Assert.Contains("CommandSignature", source, StringComparison.Ordinal);
        Assert.True(
            source.Contains("Dictionary<", StringComparison.Ordinal) ||
            source.Contains("ConcurrentDictionary<", StringComparison.Ordinal),
            "D3D12 indirect command signatures must be cached rather than created per recording call.");
    }

    [Fact]
    public void Queries_use_native_heaps_resolve_and_queue_clock_calibration()
    {
        string source = BackendSources.Value;
        foreach (string evidence in new[]
        {
            "CreateQueryHeap",
            "BeginQuery(",
            "EndQuery(",
            "ResolveQueryData(",
            "GetClockCalibration",
            "GetTimestampFrequency",
        })
        {
            Assert.Contains(evidence, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Swapchain_uses_dxgi_acquire_present_resize_and_back_buffer_ownership()
    {
        string source = BackendSources.Value;
        foreach (string evidence in new[]
        {
            "CreateSwapChainForHwnd",
            "GetCurrentBackBufferIndex",
            "Present(",
            "ResizeBuffers",
        })
        {
            Assert.Contains(evidence, source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Device_loss_reads_the_native_removed_reason()
    {
        Assert.Contains("GetDeviceRemovedReason", BackendSources.Value, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_warp_lane_is_never_silently_skipped()
    {
        Assert.True(
            OperatingSystem.IsWindows(),
            "The required Direct3D12/WARP lane must run on Windows; it may not return early as a passing test.");

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        Assert.Equal(SomeEngine.Graphics.BackendKind.Direct3D12, device.Info.Backend);
        Assert.False(device.Info.HardwareAccelerated);
    }

    private static string ReadBackendSources()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string sourceRoot = Path.Combine(directory.FullName, "src", "SomeEngine.Graphics.Direct3D12");
            if (Directory.Exists(sourceRoot))
            {
                return string.Join(
                    "\n",
                    Directory.EnumerateFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
                        .Where(static path =>
                            !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase) &&
                            !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                        .OrderBy(static path => path, StringComparer.Ordinal)
                        .Select(File.ReadAllText));
            }
            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            "Could not locate src/SomeEngine.Graphics.Direct3D12 from the test output directory.");
    }
}
