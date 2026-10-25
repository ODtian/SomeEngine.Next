namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanPresentationTests
{
    [WindowsFact]
    public void RequestingPresentationProvidesSwapchainCapability()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.Presentation));
        Assert.True(backend.TryGetCapability(device, out Presentation? presentation));
        Assert.Same(device, presentation!.Device);
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
