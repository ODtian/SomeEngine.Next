using SomeEngine.Graphics.Direct3D12;
using Xunit;

namespace SomeEngine.Graphics.Direct3D12.Tests;

[Trait("Isolation", "DeviceLossProcess")]
public sealed unsafe class WarpDeviceLossTests
{
    [Fact]
    public void NativeDeviceRemovalRejectsSubmissionAndLaterResourceCreation()
    {
        using IGraphicsBackend backend = D3D12GraphicsBackend.Create();
        using Device device = D3D12TestSupport.CreateWarpDevice(backend);
        Assert.True(backend.TryGetCapability(device, out D3D12NativeAccess? native));
        Assert.NotNull(native);
        Queue queue = backend.GetQueue(device, QueueType.Graphics);
        using Buffer buffer = backend.CreateBuffer(device,
            new BufferDesc(64 * 1024, BufferUsages.CopySource | BufferUsages.CopyDestination));
        native.GetNativeDevice(device)->RemoveDevice();
        GraphicsException failure = Assert.Throws<GraphicsException>(() =>
            backend.Submit(queue, new QueueSubmitDesc([], [], [], [], [])));
        Assert.Equal(GraphicsError.DeviceLost, failure.Error);
        Assert.Equal(DeviceStatus.Lost, device.Status);
        GraphicsException later = Assert.Throws<GraphicsException>(() =>
            backend.CreateBuffer(device, new BufferDesc(16, BufferUsages.CopySource)));
        Assert.Equal(GraphicsError.DeviceLost, later.Error);
        device.Dispose();
        Assert.Equal(DeviceStatus.Disposed, device.Status);
    }
}
