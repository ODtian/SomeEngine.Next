namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanPresentationTests
{
    [WindowsFact]
    public void RequestingPresentationIsRejected()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        Assert.Throws<NotSupportedException>(() => backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.Presentation)));
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
