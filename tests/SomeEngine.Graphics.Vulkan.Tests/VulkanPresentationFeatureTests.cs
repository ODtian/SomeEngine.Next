namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanPresentationFeatureTests
{
    [WindowsFact]
    public void Required_presentation_exposes_the_presentation_capability()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            requiredFeatures: DeviceFeatures.Presentation));

        Assert.True(backend.TryGetCapability(device, out Presentation? presentation));
        Assert.NotNull(presentation);
    }

    [WindowsFact]
    public void Unrequested_presentation_does_not_expose_the_presentation_capability()
    {
        using IGraphicsBackend backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));

        Assert.False(backend.TryGetCapability(device, out Presentation? presentation));
        Assert.Null(presentation);
    }
}
