namespace SomeEngine.Graphics.Vulkan.Tests;

using Xunit;
using Xunit.Abstractions;

public sealed class VulkanAdapterTests(ITestOutputHelper output)
{
    [Fact]
    public void Runtime_enumerates_hardware_adapters_with_stable_identity()
    {
        using var backend = VulkanGraphicsBackend.Create();
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
        using var backend = VulkanGraphicsBackend.Create();
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

    [Fact]
    public void Image_int64_atomics_require_R64UInt_load_store_and_atomic_support()
    {
        using var backend = VulkanGraphicsBackend.Create();
        DeviceQueueDesc[] queues = [new DeviceQueueDesc(QueueType.Graphics)];
        using Device device = backend.CreateDevice(new DeviceDesc(default, queues));

        DeviceCapabilities capabilities = device.Capabilities;
        FormatFeatures r64Features =
            capabilities.GetFormatSupport(Format.R64UInt).Features;
        FormatFeatures r32g32Features =
            capabilities.GetFormatSupport(Format.R32G32UInt).Features;
        const FormatFeatures required =
            FormatFeatures.ShaderLoad |
            FormatFeatures.Storage |
            FormatFeatures.StorageStore |
            FormatFeatures.StorageAtomic;

        output.WriteLine($"Adapter={device.Adapter.Name}");
        output.WriteLine($"SupportsImageInt64Atomics={capabilities.SupportsImageInt64Atomics}");
        output.WriteLine($"R64UInt.Features=0x{(ulong)r64Features:X}");
        output.WriteLine($"R32G32UInt.Features=0x{(ulong)r32g32Features:X}");
        output.WriteLine($"RequiredFeatures=0x{(ulong)required:X}");

        Assert.True(
            !capabilities.SupportsImageInt64Atomics ||
            (r64Features & required) == required);
    }
}
