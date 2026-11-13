using SomeEngine.Graphics;
using SomeEngine.Graphics.Direct3D12;

namespace SomeEngine.Render.Tests;

/// <summary>A graphics device and its shared pipeline collection for binning and layer checks.</summary>
public sealed class BinningTestDevice : IDisposable
{
    private readonly string _cacheDirectory = Path.Combine(Path.GetTempPath(),
        "SomeEngine-Binning-" + Guid.NewGuid().ToString("N"));

    public IGraphicsBackend Backend { get; } = D3D12GraphicsBackend.Create();
    public Device Device { get; }
    public RenderPipelines Pipelines { get; }

    public BinningTestDevice(DeviceFeatures features = DeviceFeatures.None, AdapterType adapterType = AdapterType.Cpu)
    {
        var options = new AdapterEnumerationOptions(AdapterPreference.HighPerformance, IncludeSoftware: true);
        Backend.TryEnumerateAdapters(options, [], out int count);
        var adapters = new AdapterInfo[count];
        Backend.TryEnumerateAdapters(options, adapters, out _);
        AdapterInfo adapter = adapters.First(item => item.Type == adapterType);
        Device = Backend.CreateDevice(new DeviceDesc(adapter.Id,
            [new DeviceQueueDesc(QueueType.Graphics)], requiredFeatures: features,
            label: "Binning behavior checks"));
        Pipelines = new RenderPipelines(Backend, Device, _cacheDirectory);
    }

    public void Dispose()
    {
        try { Pipelines.Dispose(); }
        finally
        {
            Device.Dispose();
            Backend.Dispose();
            if (Directory.Exists(_cacheDirectory)) Directory.Delete(_cacheDirectory, recursive: true);
        }
    }
}
