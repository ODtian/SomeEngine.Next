using SomeEngine.Graphics.Null;
using Xunit;

namespace SomeEngine.Graphics.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void Null_validates_object_names_debug_group_balance_and_point_markers()
    {
        using Device device = new();
        using Device other = new();
        BufferHandle buffer = device.CreateBuffer(new BufferDesc(4, BufferUsage.CopySource, "created"), MemoryType.Upload);
        device.SetName(buffer, "renamed");
        Assert.Throws<ArgumentException>(() => other.SetName(buffer, "cross-device"));
        using (ICommandContext unbalanced = device.AcquireCommandContext(QueueType.Copy, "unbalanced"))
        {
            unbalanced.PushDebugGroup("open");
            Assert.Throws<InvalidOperationException>(() => unbalanced.Finish());
            unbalanced.PopDebugGroup();
        }
        using (ICommandContext commands = device.AcquireCommandContext(QueueType.Copy, "labels"))
        {
            commands.PushDebugGroup("group");
            commands.InsertDebugMarker("before");
            commands.InsertDebugMarker("after");
            commands.PopDebugGroup();
            device.Submit(QueueType.Copy, [commands.Finish()]);
        }
        Assert.Equal(2, device.Statistics.DebugMarkers);
        device.DestroyBuffer(buffer);
        Assert.Throws<InvalidOperationException>(() => device.SetName(buffer, "stale"));
    }

    [Fact]
    public void Null_reports_failures_and_device_loss_without_native_types()
    {
        using (var device = new Device())
        using (ICommandContext context = device.AcquireCommandContext(QueueType.Graphics))
        {
            Assert.Throws<InvalidOperationException>(() => context.Dispatch(1, 1, 1));
            Assert.Equal(DeviceErrorKind.Validation, device.LastError.Kind);
            Assert.Contains(device.DrainDiagnostics(), static diagnostic =>
                diagnostic.Severity == GraphicsDiagnosticSeverity.Error && diagnostic.Source == "Null");
            Assert.Equal(DeviceErrorKind.Validation, device.LastError.Kind);

            Assert.Throws<NotSupportedException>(() =>
                device.CreateBindlessTable(new BindlessTableDesc(BindingKind.SampledTexture, 4)));
            Assert.Equal(DeviceErrorKind.Unsupported, device.LastError.Kind);
        }

        using (var lost = new Device(new Options { PresentStatus = PresentStatus.DeviceLost }))
        {
            SwapchainHandle swapchain = lost.CreateSwapchain(new SwapchainDesc(
                0,
                2,
                2,
                Format.R8G8B8A8UNorm));
            SwapchainImage image = lost.AcquireNextImage(swapchain);
            PresentResult result = lost.Present(swapchain, image.ImageIndex);
            Assert.Equal(PresentStatus.DeviceLost, result.Status);
            Assert.Equal(DeviceErrorKind.DeviceLost, result.Error.Kind);
            Assert.Equal(DeviceErrorKind.DeviceLost, lost.LastError.Kind);
            Assert.Contains(lost.DrainDiagnostics(), static diagnostic =>
                diagnostic.Message.Contains("device loss", StringComparison.OrdinalIgnoreCase));
        }
    }
}
