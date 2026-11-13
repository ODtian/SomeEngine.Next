namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanPresentationTests
{
    [WindowsFact]
    public void RequiredPresentationReportsUnsupportedFeaturesForDefaultAndSelectedAdapters()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)];
        GraphicsException defaultFailure = Assert.Throws<GraphicsException>(() => backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands,
            optionalFeatures: DeviceFeatures.CalibratedTimestamps | DeviceFeatures.MeshShaders)));

        AdapterEnumerationOptions options = new(AdapterPreference.HighPerformance);
        backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        backend.TryEnumerateAdapters(options, adapters, out _);
        AdapterInfo selected = adapters.First(adapter => adapter.HardwareAccelerated);
        GraphicsException selectedFailure = Assert.Throws<GraphicsException>(() => backend.CreateDevice(new DeviceDesc(
            selected.Id,
            queues,
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands,
            optionalFeatures: DeviceFeatures.CalibratedTimestamps | DeviceFeatures.MeshShaders)));

        Assert.Equal(GraphicsError.UnsupportedDeviceFeatures, defaultFailure.Error);
        Assert.Equal(defaultFailure.Error, selectedFailure.Error);
    }
}

internal sealed class WindowsFactAttribute : FactAttribute
{
    public WindowsFactAttribute()
    {
        if (!OperatingSystem.IsWindows())
            Skip = "This WSI feature request is tested on Windows.";
    }
}
