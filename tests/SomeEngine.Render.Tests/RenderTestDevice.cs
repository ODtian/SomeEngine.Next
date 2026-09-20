using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Frame;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class RenderTestDevice : IDisposable
{
    public IGraphicsBackend Backend { get; } = D3D12GraphicsBackend.Create();
    public Device Device { get; }
    public RenderDevice RenderDevice { get; }
    public RenderFrameCoordinator Coordinator { get; }

    public RenderTestDevice()
    {
        var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        Backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Backend.TryEnumerateAdapters(options, adapters, out _);
        AdapterInfo warp = adapters.First(adapter => !adapter.HardwareAccelerated);
        Device = Backend.CreateDevice(new DeviceDesc(warp.Id, [new DeviceQueueDesc(QueueType.Graphics)], label: "Render public behavior tests"));
        RenderDevice = new RenderDevice(Backend, Device);
        Coordinator = new RenderFrameCoordinator(RenderDevice);
    }

    public bool Apply(RenderExtractionSystems extraction, RenderWorld world)
    {
        var prepare = new RenderPrepareSystems(world, extraction);
        Assert.True(Coordinator.TryBeginPrepare(out var scope));
        using (scope)
        {
            bool changed;
            try { changed = prepare.ApplyExtraction(scope); }
            finally { prepare.Shutdown(scope); prepare.Dispose(); }
            scope.Commit();
            return changed;
        }
    }

    public void Dispose()
    {
        Coordinator.Dispose();
        RenderDevice.Dispose();
        Device.Dispose();
        Backend.Dispose();
    }
}
