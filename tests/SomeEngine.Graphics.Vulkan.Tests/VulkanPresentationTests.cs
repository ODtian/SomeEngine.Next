namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanPresentationTests
{
    [WindowsFact]
    public void RequestingPresentationWithoutGraphicsQueueIsRejected()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Compute)];
        Assert.Throws<NotSupportedException>(() => backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.Presentation)));
    }

    [WindowsFact]
    public void OptionalPresentationWithoutGraphicsQueueRemainsUnavailable()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Compute)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            optionalFeatures: DeviceFeatures.Presentation));

        Assert.False(backend.TryGetCapability(device, out Presentation? presentation));
        Assert.Null(presentation);
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
