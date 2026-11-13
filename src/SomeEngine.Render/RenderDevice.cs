using SomeEngine.Graphics;

namespace SomeEngine.Render;

/// <summary>Device-specific texture views, sampler descriptors and material resource indices.</summary>
public sealed partial class RenderDevice : IDisposable
{
    private readonly global::SomeEngine.RenderGraph.RenderGraph _graph;
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private bool _disposed;

    public RenderDevice(global::SomeEngine.RenderGraph.RenderGraph graph, RenderPipelines pipelines)
    {
        _graph = graph;
        Pipelines = pipelines;
        _backend = graph.Backend;
        _device = graph.Device;
        CreateMissingTexture();
    }

    public global::SomeEngine.RenderGraph.RenderGraph Graph => _graph;
    public RenderPipelines Pipelines { get; }
    public IGraphicsBackend Backend => _backend;
    public Device Device => _device;

    public void CollectCompleted()
    {
        ThrowIfDisposed();
        _graph.CollectCompleted();
        CollectTextureReleases();
    }

    internal void Retire(IDisposable value) => _graph.RetireAfterSubmittedFrames(value);

    public void Dispose()
    {
        if (_disposed) return;
        if (!_graph.IsDisposed && _device.Status == DeviceStatus.Active)
            _graph.WaitForFrame(_graph.LastSubmittedFrame, TimeSpan.FromSeconds(30));
        DisposeTextures();
        DisposeSamplers();
        _disposed = true;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);
}
