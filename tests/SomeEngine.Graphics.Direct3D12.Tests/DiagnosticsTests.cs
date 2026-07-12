using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Vortice.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void Warp_applies_native_names_groups_and_markers_without_info_queue_errors()
    {
        Assert.True(OperatingSystem.IsWindows());
        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });
        using Device other = new(new Options { UseWarpAdapter = true });
        BufferHandle buffer = device.CreateBuffer(
            new BufferDesc(16, BufferUsage.CopySource, "created-buffer"), MemoryType.Upload);
        Assert.Equal("created-buffer", device.GetBuffer(buffer).Resource.Name);
        device.SetName(buffer, "renamed-buffer");
        Assert.Equal("renamed-buffer", device.GetBuffer(buffer).Resource.Name);
        Assert.Throws<ArgumentException>(() => other.SetName(buffer, "cross-device"));

        using (ICommandContext unbalanced = device.AcquireCommandContext(QueueType.Graphics, "unbalanced"))
        {
            unbalanced.PushDebugGroup("open");
            Assert.Throws<InvalidOperationException>(() => unbalanced.Finish());
            unbalanced.PopDebugGroup();
        }

        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics, "named-command-list"))
        {
            commands.PushDebugGroup("outer");
            commands.InsertDebugMarker("point");
            commands.PopDebugGroup();
            CommandListHandle list = commands.Finish();
            device.SetName(list, "renamed-command-list");
            device.Submit(QueueType.Graphics, [list]);
        }
        Assert.True(device.WaitIdle(TimeSpan.FromSeconds(10)));
        device.DestroyBuffer(buffer);
        device.CollectGarbage();
        Assert.DoesNotContain(device.DrainDiagnostics(), static diagnostic =>
            diagnostic.Severity is GraphicsDiagnosticSeverity.Error or GraphicsDiagnosticSeverity.Corruption);
    }

    [Fact]
    public void Warp_drains_infoqueue_and_captures_dred_device_removed_context()
    {
        Assert.True(OperatingSystem.IsWindows(), "The required WARP diagnostics lane must execute on Windows.");
        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });

        using ICommandContext commands = device.AcquireCommandContext(QueueType.Graphics, "controlled-device-removal");
        commands.InsertDebugMarker("before-controlled-device-removal");
        CommandListHandle commandList = commands.Finish();

        using (ID3D12Device5 removal = device.NativeDevice.QueryInterface<ID3D12Device5>())
        {
            removal.RemoveDevice();
        }

        InvalidOperationException failure = Assert.Throws<InvalidOperationException>(() =>
            device.Submit(QueueType.Graphics, [commandList]));
        Assert.Contains("device execution domain is no longer usable", failure.Message, StringComparison.Ordinal);
        Assert.Equal(DeviceErrorKind.DeviceLost, device.LastError.Kind);
        Assert.NotEqual(0, device.LastError.NativeCode);

        GraphicsDiagnostic[] diagnostics = device.DrainDiagnostics();
        Assert.Contains(diagnostics, static item => item.Source == "D3D12 DRED DeviceState");
        Assert.Contains(diagnostics, static item => item.Source == "D3D12 DRED PageFault");
        Assert.Contains(diagnostics, static item => item.Source == "D3D12 DRED Breadcrumbs");
        Assert.Contains(diagnostics, static item => item.Source == "D3D12 DRED AllocationContext");
        Assert.Contains(diagnostics, static item =>
            item.Source == "D3D12" && item.Severity == GraphicsDiagnosticSeverity.Error);
        Assert.DoesNotContain(diagnostics, static item =>
            item.Source.StartsWith("D3D12 DRED", StringComparison.Ordinal) &&
            (item.Severity != GraphicsDiagnosticSeverity.Information ||
             item.Message.Contains("unavailable", StringComparison.OrdinalIgnoreCase)));
        Assert.All(diagnostics, static item => Assert.False(string.IsNullOrWhiteSpace(item.Message)));
    }
}
