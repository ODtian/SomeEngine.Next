using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Runtime.Tests;

[Trait("Category", "Software")]
public sealed class SceneAdapterTests
{
    [Fact]
    public void SoftwareAdapterReportsItsPublicRenderingCapabilities()
    {
        using var backend = D3D12GraphicsBackend.Create();
        var enumeration = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        backend.TryEnumerateAdapters(enumeration, [], out int count);
        var adapters = new AdapterInfo[count];
        Assert.True(backend.TryEnumerateAdapters(enumeration, adapters, out _));
        AdapterInfo adapter = adapters.Single(value => !value.HardwareAccelerated);
        using var device = backend.CreateDevice(new DeviceDesc(adapter.Id,
            [new(QueueType.Graphics), new(QueueType.Compute), new(QueueType.Copy)],
            requiredFeatures: DeviceFeatures.Presentation | DeviceFeatures.IndirectCommands,
            optionalFeatures: DeviceFeatures.MeshShaders));
        Assert.False(device.Adapter.HardwareAccelerated);
        Console.WriteLine($"Software adapter: {device.Adapter.Name}; driver={device.Adapter.DriverVersion}; " +
            $"buffer64={device.Capabilities.SupportsBufferInt64Atomics}; image64={device.Capabilities.SupportsImageInt64Atomics}; " +
            $"forwardProgress={device.Capabilities.SupportsComputeForwardProgress}; mesh={backend.TryGetCapability(device, out MeshShaders? mesh)}");
    }
}
