namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;

public sealed class VulkanAdapterTests
{
    [Fact]
    public void Runtime_enumerates_hardware_adapters_with_stable_identity()
    {
        using var backend = new VulkanBackend();
        AdapterInfo[] adapters = new AdapterInfo[8];

        bool complete = backend.TryEnumerateAdapters(
            new AdapterEnumerationOptions(AdapterPreference.HighPerformance),
            adapters,
            out int requiredCount);

        Assert.True(complete);
        Assert.InRange(requiredCount, 1, adapters.Length);
        Assert.All(adapters.AsSpan(0, requiredCount).ToArray(), static adapter =>
        {
            Assert.False(adapter.Id.IsDefault);
            Assert.False(string.IsNullOrWhiteSpace(adapter.Name));
            Assert.NotEqual(0u, adapter.VendorId);
            Assert.NotEqual(0u, adapter.DeviceId);
            Assert.True(adapter.HardwareAccelerated);
        });
    }

    [Fact]
    public void Runtime_creates_vulkan_13_device_and_requested_queues()
    {
        using var backend = new VulkanBackend();
        DeviceQueueDesc[] queues =
        [
            new DeviceQueueDesc(QueueType.Graphics),
            new DeviceQueueDesc(QueueType.Compute),
            new DeviceQueueDesc(QueueType.Copy),
        ];
        using Device device = backend.CreateDevice(new DeviceDesc(
            default,
            queues,
            optionalFeatures: DeviceFeatures.IndirectCommands));

        Assert.Equal(QueueType.Graphics, backend.GetQueue(device, QueueType.Graphics).Type);
        Assert.Equal(QueueType.Compute, backend.GetQueue(device, QueueType.Compute).Type);
        Assert.Equal(QueueType.Copy, backend.GetQueue(device, QueueType.Copy).Type);
        Assert.True(backend.TryGetCapability(device, out PipelineCreationSupport? pipelines));
        Assert.NotNull(pipelines);
        Assert.True(backend.TryGetCapability(device, out IndirectCommands? indirect));
        Assert.NotNull(indirect);
        Assert.True(indirect.Supports(IndirectArgumentType.Draw));
        Assert.Equal(DeviceStatus.Active, device.Status);
    }
}
