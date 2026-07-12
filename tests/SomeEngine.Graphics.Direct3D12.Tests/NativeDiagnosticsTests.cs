using System.Reflection;
using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using Vortice.Direct3D12.Debug;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

public sealed class NativeDiagnosticsTests
{
    [Fact]
    public void DrainDiagnostics_reads_and_clears_filtered_native_messages()
    {

        using Device device = new(new Options
        {
            UseWarpAdapter = true,
            EnableDebugLayer = true,
            EnableGpuValidation = false,
        });

        ID3D12InfoQueue infoQueue = GetNativeInfoQueue(device);
        string marker = Guid.NewGuid().ToString("N");
        string filteredMessage = $"SomeEngine filtered diagnostic {marker}";
        string retainedMessage = $"SomeEngine retained diagnostic {marker}";

        infoQueue.AddApplicationMessage(MessageSeverity.Info, filteredMessage);
        infoQueue.AddApplicationMessage(MessageSeverity.Error, retainedMessage);

        GraphicsDiagnostic[] diagnostics = device.DrainDiagnostics();

        Assert.DoesNotContain(diagnostics, item => item.Message.Contains(filteredMessage, StringComparison.Ordinal));
        GraphicsDiagnostic retained = Assert.Single(
            diagnostics,
            item => item.Message.Contains(retainedMessage, StringComparison.Ordinal));
        Assert.Equal(GraphicsDiagnosticSeverity.Error, retained.Severity);
        Assert.Equal("D3D12 Debug Layer", retained.Source);

        Assert.DoesNotContain(
            device.DrainDiagnostics(),
            item => item.Message.Contains(marker, StringComparison.Ordinal));
    }

    private static ID3D12InfoQueue GetNativeInfoQueue(Device device)
    {
        FieldInfo nativeField = typeof(Device).GetField("_native", BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("The D3D12 device no longer owns a native context field.");
        object nativeContext = nativeField.GetValue(device)
            ?? throw new InvalidOperationException("The D3D12 native context is unavailable.");
        PropertyInfo infoQueueProperty = nativeContext.GetType().GetProperty("InfoQueue", BindingFlags.Instance | BindingFlags.Public)
            ?? throw new InvalidOperationException("The D3D12 native context does not expose its information queue internally.");

        return (ID3D12InfoQueue)(infoQueueProperty.GetValue(nativeContext)
            ?? throw new InvalidOperationException("The D3D12 debug information queue was not created."));
    }
}
