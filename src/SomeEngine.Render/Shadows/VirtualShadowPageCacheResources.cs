using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Shadows;

internal sealed class VirtualShadowPageCacheResources : IDisposable
{
    internal const uint UnownedPage = uint.MaxValue;
    internal const int PageMetadataStride = 16;
    internal const uint EvictionBucketCount = 8;

    private readonly IGraphicsBackend _backend;
    private readonly Buffer _freePages;
    private readonly Buffer _freePageCount;
    private readonly Buffer _pageMetadata;
    private readonly Buffer _evictionPages;
    private readonly Buffer _evictionBucketCounts;
    private BufferBoundaryState[] _freePagesEndpoint;
    private BufferBoundaryState[] _freePageCountEndpoint;
    private BufferBoundaryState[] _pageMetadataEndpoint;
    private BufferBoundaryState[] _evictionPagesEndpoint;
    private BufferBoundaryState[] _evictionBucketCountsEndpoint;
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
        Buffer? freePages = null;
        Buffer? freePageCount = null;
        Buffer? pageMetadata = null;
        Buffer? evictionPages = null;
        Buffer? evictionBucketCounts = null;
        try
        {
            freePages = Create(
                device,
                checked((ulong)settings.MaxPhysicalPages * sizeof(uint)),
                "Virtual shadow free pages");
            freePageCount = Create(device, sizeof(uint), "Virtual shadow free-page count");
            pageMetadata = Create(
                device,
                checked((ulong)settings.MaxPhysicalPages * PageMetadataStride),
                "Virtual shadow page age metadata");
            evictionPages = Create(
                device,
                checked((ulong)settings.MaxPhysicalPages * EvictionBucketCount * sizeof(uint)),
                "Virtual shadow eviction pages");
            evictionBucketCounts = Create(
                device,
                checked((ulong)EvictionBucketCount * sizeof(uint)),
                "Virtual shadow eviction bucket counts");
        }
        catch
        {
            evictionBucketCounts?.Dispose();
            evictionPages?.Dispose();
            pageMetadata?.Dispose();
            freePageCount?.Dispose();
            freePages?.Dispose();
            throw;
        }

        _freePages = freePages;
        _freePageCount = freePageCount;
        _pageMetadata = pageMetadata;
        _evictionPages = evictionPages;
        _evictionBucketCounts = evictionBucketCounts;
        _freePagesEndpoint = [Initial(_freePages)];
        _freePageCountEndpoint = [Initial(_freePageCount)];
        _pageMetadataEndpoint = [Initial(_pageMetadata)];
        _evictionPagesEndpoint = [Initial(_evictionPages)];
        _evictionBucketCountsEndpoint = [Initial(_evictionBucketCounts)];
    }

    internal VirtualShadowPageCacheFrameResources BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_inFlight)
            throw new InvalidOperationException("The previous virtual-shadow page-cache frame is still active.");
        _inFlight = true;
        return new VirtualShadowPageCacheFrameResources(
            new VirtualShadowBufferResource(_freePages, _freePagesEndpoint),
            new VirtualShadowBufferResource(_freePageCount, _freePageCountEndpoint),
            new VirtualShadowBufferResource(_pageMetadata, _pageMetadataEndpoint),
            new VirtualShadowBufferResource(_evictionPages, _evictionPagesEndpoint),
            new VirtualShadowBufferResource(_evictionBucketCounts, _evictionBucketCountsEndpoint));
    }

    internal void FinishFrame(RenderGraphFrameResult result)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Store(_freePages, ref _freePagesEndpoint, result);
        Store(_freePageCount, ref _freePageCountEndpoint, result);
        Store(_pageMetadata, ref _pageMetadataEndpoint, result);
        Store(_evictionPages, ref _evictionPagesEndpoint, result);
        Store(_evictionBucketCounts, ref _evictionBucketCountsEndpoint, result);
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
        Dispose(_evictionBucketCounts, ref failures);
        Dispose(_evictionPages, ref failures);
        Dispose(_pageMetadata, ref failures);
        Dispose(_freePageCount, ref failures);
        Dispose(_freePages, ref failures);
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
    VirtualShadowBufferResource FreePages,
    VirtualShadowBufferResource FreePageCount,
    VirtualShadowBufferResource PageMetadata,
    VirtualShadowBufferResource EvictionPages,
    VirtualShadowBufferResource EvictionBucketCounts);
