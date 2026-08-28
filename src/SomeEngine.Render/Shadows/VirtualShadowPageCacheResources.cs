using SomeEngine.Graphics;
using SomeEngine.RenderGraph;
using Buffer = SomeEngine.Graphics.Buffer;

namespace SomeEngine.Render.Shadows;

internal sealed class VirtualShadowPageCacheResources : IDisposable
{
    internal const uint UnownedPage = uint.MaxValue;
    internal const int PageMetadataStride = 16;

    private readonly IGraphicsBackend _backend;
    private readonly Buffer _freePages;
    private readonly Buffer _freePageCount;
    private readonly Buffer _pageMetadata;
    private readonly Buffer _compactRequests;
    private readonly Buffer _compactRequestCount;
    private readonly BufferBoundaryState[] _freePagesEndpoint;
    private readonly BufferBoundaryState[] _freePageCountEndpoint;
    private readonly BufferBoundaryState[] _pageMetadataEndpoint;
    private readonly BufferBoundaryState[] _compactRequestsEndpoint;
    private readonly BufferBoundaryState[] _compactRequestCountEndpoint;
    private bool _pending;
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
        Buffer? compactRequests = null;
        Buffer? compactRequestCount = null;
        try
        {
            freePages = Create(
                device,
                checked((ulong)settings.MaxPhysicalPages * sizeof(uint)),
                "Virtual shadow free-page stack");
            freePageCount = Create(
                device,
                sizeof(uint),
                "Virtual shadow free-page count");
            pageMetadata = Create(
                device,
                checked((ulong)settings.MaxPhysicalPages * PageMetadataStride),
                "Virtual shadow page LRU metadata");
            compactRequests = Create(
                device,
                checked((ulong)settings.PageTableEntryCount * sizeof(uint)),
                "Virtual shadow compact request list");
            compactRequestCount = Create(
                device,
                sizeof(uint),
                "Virtual shadow compact request count");
        }
        catch
        {
            compactRequestCount?.Dispose();
            compactRequests?.Dispose();
            pageMetadata?.Dispose();
            freePageCount?.Dispose();
            freePages?.Dispose();
            throw;
        }

        _freePages = freePages;
        _freePageCount = freePageCount;
        _pageMetadata = pageMetadata;
        _compactRequests = compactRequests;
        _compactRequestCount = compactRequestCount;
        _freePagesEndpoint = [Initial(_freePages)];
        _freePageCountEndpoint = [Initial(_freePageCount)];
        _pageMetadataEndpoint = [Initial(_pageMetadata)];
        _compactRequestsEndpoint = [Initial(_compactRequests)];
        _compactRequestCountEndpoint = [Initial(_compactRequestCount)];
    }

    internal VirtualShadowPageCacheFrameResources BeginFrame()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pending)
            throw new InvalidOperationException("The previous virtual-shadow page-cache frame is pending.");
        _pending = true;
        return new VirtualShadowPageCacheFrameResources(
            new VirtualShadowBufferResource(_freePages, _freePagesEndpoint),
            new VirtualShadowBufferResource(_freePageCount, _freePageCountEndpoint),
            new VirtualShadowBufferResource(_pageMetadata, _pageMetadataEndpoint),
            new VirtualShadowBufferResource(_compactRequests, _compactRequestsEndpoint),
            new VirtualShadowBufferResource(_compactRequestCount, _compactRequestCountEndpoint));
    }

    internal void Commit(QueueCompletion completion)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_pending)
            throw new InvalidOperationException("No virtual-shadow page-cache frame is pending.");
        SetEndpoint(_freePagesEndpoint, _freePages, completion);
        SetEndpoint(_freePageCountEndpoint, _freePageCount, completion);
        SetEndpoint(_pageMetadataEndpoint, _pageMetadata, completion);
        SetEndpoint(_compactRequestsEndpoint, _compactRequests, completion);
        SetEndpoint(_compactRequestCountEndpoint, _compactRequestCount, completion);
        _pending = false;
    }

    internal void Discard()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pending = false;
    }

    internal static uint[] BuildInitialFreePages(uint maxPhysicalPages)
    {
        var result = new uint[checked((int)maxPhysicalPages)];
        for (uint page = 0; page < maxPhysicalPages; page++)
            result[page] = maxPhysicalPages - page - 1u;
        return result;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        List<Exception>? failures = null;
        Dispose(_compactRequestCount, ref failures);
        Dispose(_compactRequests, ref failures);
        Dispose(_pageMetadata, ref failures);
        Dispose(_freePageCount, ref failures);
        Dispose(_freePages, ref failures);
        _pending = false;
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

    private static void SetEndpoint(
        BufferBoundaryState[] endpoint,
        Buffer buffer,
        QueueCompletion completion)
        => endpoint[0] = new BufferBoundaryState(
            new BufferRange(0, buffer.Info.Size),
            PipelineSync.ComputeShading,
            ResourceAccess.UnorderedAccess,
            ResourceContentState.Defined,
            completion.Queue,
            completion);

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
    VirtualShadowBufferResource CompactRequests,
    VirtualShadowBufferResource CompactRequestCount);