using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Shadows;

internal sealed class VirtualShadowPageCacheResources : IDisposable
{
    internal const uint UnownedPage = uint.MaxValue;
    internal const int PageMetadataStride = 16;

    private readonly IGraphicsBackend _backend;
    private readonly Buffer _pageMetadata;
    private readonly Buffer _pageLists;
    private BufferBoundaryState[] _pageMetadataEndpoint;
    private BufferBoundaryState[] _pageListsEndpoint;
    private bool _inFlight;
    private bool _disposed;

    internal VirtualShadowPageCacheResources(
        IGraphicsBackend backend,
        Device device,
        VirtualShadowMapSettings settings)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(settings);
        Buffer? pageMetadata = null;
        Buffer? pageLists = null;
        try
        {
            pageMetadata = Create(
                device,
                checked((ulong)settings.MaxPhysicalPages * PageMetadataStride),
                "Virtual shadow page age metadata");
            pageLists = Create(device, VirtualShadowMap.CalculatePageListBufferSize(settings.MaxPhysicalPages),
                "Virtual shadow page lists");
        }
        catch
        {
            pageLists?.Dispose();
            pageMetadata?.Dispose();
            throw;
        }

        _pageMetadata = pageMetadata;
        _pageLists = pageLists;
        _pageMetadataEndpoint = [Initial(_pageMetadata)];
        _pageListsEndpoint = [Initial(_pageLists)];
    }

    internal VirtualShadowPageCacheFrameResources BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inFlight)
            throw new InvalidOperationException("The previous virtual-shadow page-cache frame is still active.");
        _inFlight = true;
        return new VirtualShadowPageCacheFrameResources(
            new VirtualShadowBufferResource(_pageMetadata, _pageMetadataEndpoint),
            new VirtualShadowBufferResource(_pageLists, _pageListsEndpoint));
    }

    internal void FinishFrame(RenderGraphFrameResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Store(_pageMetadata, ref _pageMetadataEndpoint, result);
        Store(_pageLists, ref _pageListsEndpoint, result);
        _inFlight = false;
    }

    private static void Store(Buffer buffer, ref BufferBoundaryState[] states, RenderGraphFrameResult result)
    {
        ReadOnlySpan<BufferBoundaryState> current = result.GetBufferStates(buffer);
        if (!current.IsEmpty) states = current.ToArray();
    }

    internal void Discard()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _inFlight = false;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        Dispose(_pageLists, ref failures);
        Dispose(_pageMetadata, ref failures);
        _inFlight = false;
        _disposed = true;
        if (failures is not null)
            throw failures.Count == 1 ? failures[0] : new AggregateException(failures);
    }

    private Buffer Create(Device device, ulong size, string label)
        => _backend.CreateBuffer(
            device,
            new BufferDesc(
                size,
                BufferUsages.ShaderRead | BufferUsages.ShaderWrite |
                BufferUsages.CopySource | BufferUsages.CopyDestination,
                label),
            MemoryType.DeviceLocal);

    private static BufferBoundaryState Initial(Buffer buffer) => new(
        new BufferRange(0, buffer.Info.Size),
        buffer.InitialSync,
        buffer.InitialAccess,
        ResourceContentState.Undefined);

    private static void Dispose(IDisposable value, ref List<Exception>? failures)
    {
        try { value.Dispose(); }
        catch (Exception failure) { (failures ??= []).Add(failure); }
    }
}

internal readonly record struct VirtualShadowPageCacheFrameResources(
    VirtualShadowBufferResource PageMetadata,
    VirtualShadowBufferResource PageLists);
