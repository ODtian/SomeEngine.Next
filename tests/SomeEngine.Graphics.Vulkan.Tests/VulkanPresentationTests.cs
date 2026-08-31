namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanPresentationTests
{
    [Fact]
    public void Presentation_is_not_published_without_the_approved_wsi_contract()
    {
        if (!OperatingSystem.IsWindows())
            return;
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        Assert.Throws<NotSupportedException>(() => backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.Presentation)));
    }
}
