using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;
using SomeEngine.Render.Systems;

namespace SomeEngine.Render.Tests;

public sealed class RenderTestDevice : IDisposable
{
    public IGraphicsBackend Backend { get; } = D3D12GraphicsBackend.Create();
    public Device Device { get; }
    public RenderDevice RenderDevice { get; }
    public global::SomeEngine.RenderGraph.RenderGraph Graph { get; }
    public RenderPipelines Pipelines { get; }

    public RenderTestDevice() : this(DeviceFeatures.None) { }

    internal RenderTestDevice(DeviceFeatures features, DeviceQueueDesc[]? queues = null)
    {
        var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        Backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Backend.TryEnumerateAdapters(options, adapters, out _);
        AdapterInfo warp = adapters.First(adapter => !adapter.HardwareAccelerated);
        Device = Backend.CreateDevice(new DeviceDesc(warp.Id, queues ?? [new DeviceQueueDesc(QueueType.Graphics)],
            requiredFeatures: features, label: "Render public behavior tests"));
        Graph = new(Backend, Device, [Backend.GetQueue(Device, QueueType.Graphics)]);
        Pipelines = new(Backend, Device);
        RenderDevice = new RenderDevice(Graph, Pipelines);
    }

    public bool Apply(RenderExtractionSystems extraction, RenderWorld world)
    {
        using var prepare = new RenderPrepareSystems(world, extraction);
        return prepare.ApplyExtraction();
    }

    public void Dispose()
    {
        RenderDevice.Dispose();
        Graph.Dispose();
        Pipelines.Dispose();
        Device.Dispose();
        Backend.Dispose();
    }
}
