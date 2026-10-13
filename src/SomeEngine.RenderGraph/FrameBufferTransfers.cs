namespace SomeEngine.RenderGraph;

internal sealed class FrameBufferTransfers : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly HeapByteBudget _budget;
    private readonly List<ReadbackPage> _readbackPages = [];
    private readonly List<ReadbackResult> _readbackResults = [];
    private readonly List<UploadPage> _uploadPages = [];
    private readonly ThreadLocal<List<BufferUpload>> _uploads = new(static () => [], trackAllValues: true);
    private readonly object _uploadPageGate = new();
    private UploadPage? _uploadPage;
    private long _uploadSequence;

    internal BufferUpload[] TakeUploads()
    {
        IList<List<BufferUpload>> writes = _uploads.Values;
        BufferUpload[] uploads = writes.SelectMany(values => values).ToArray();
        Array.Sort(uploads, static (left, right) => left.Sequence.CompareTo(right.Sequence));
        foreach (List<BufferUpload> values in writes) values.Clear();
        return uploads;
    }

    internal sealed class UploadPage
    {
        internal required Buffer Buffer;
        internal required MappedBuffer Mapping;
        internal required ulong CommittedBytes;
        internal BufferBoundaryState[] BoundaryStates = [];
        internal long Used;

        internal bool TryReserve(long size, out ulong offset)
        {
            long start = Interlocked.Add(ref Used, size) - size;
            offset = checked((ulong)start);
            return start <= (long)Buffer.Info.Size - size;
        }
    }

    internal readonly record struct BufferUpload(GraphBufferId Destination, ulong Offset,
        UploadPage Page, BufferRange Range, long Sequence);

    internal Span<byte> GetWriteSpan(GraphBufferId buffer, ulong offset, int length)
    {
        BufferUpload upload = ReserveUpload(buffer, offset, length);
        _uploads.Value!.Add(upload);
        return GetUploadSpan(upload);
    }

    internal void AddUploads(IEnumerable<BufferUpload> uploads)
    {
        List<BufferUpload> writes = _uploads.Value!;
        // A group joins frame order at Commit while retaining its own write order.
        foreach (BufferUpload upload in uploads.OrderBy(upload => upload.Sequence))
            writes.Add(upload with { Sequence = Interlocked.Increment(ref _uploadSequence) });
    }

    internal void AddUploadRanges(GraphBufferId buffer, List<BufferRange> ranges)
    {
        foreach (List<BufferUpload> uploads in _uploads.Values)
            foreach (BufferUpload upload in uploads)
                if (upload.Destination == buffer)
                    ranges.Add(new BufferRange(upload.Offset, upload.Range.Size));
    }

    internal static Span<byte> GetUploadSpan(scoped in BufferUpload upload) =>
        upload.Page.Mapping.Bytes.Slice(checked((int)upload.Range.Offset), checked((int)upload.Range.Size));

    internal BufferUpload ReserveUpload(GraphBufferId buffer, ulong offset, int length)
    {
        // Reject invalid ranges before reserving page bytes. A negative length must never
        // move Used backwards and alias bytes already assigned to an earlier upload.
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(length);
        if (offset > ulong.MaxValue - (ulong)length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        long size = ((long)length + 3) & ~3L;
        // Pages round up to 64 KiB and must still fit in the int-sized mapped span.
        const long maximumMappedPageSize = int.MaxValue & ~65535L;
        if (size > maximumMappedPageSize)
            throw new ArgumentOutOfRangeException(nameof(length));
        UploadPage? target = Volatile.Read(ref _uploadPage);
        ulong start;
        if (target is null || !target.TryReserve(size, out start))
        {
            lock (_uploadPageGate)
            {
                target = null;
                start = 0;
                foreach (UploadPage page in _uploadPages)
                {
                    if (!page.TryReserve(size, out start)) continue;
                    target = page;
                    break;
                }
                if (target is null)
                {
                    target = CreateUploadPage(checked((ulong)size));
                    _ = target.TryReserve(size, out start);
                }
                Volatile.Write(ref _uploadPage, target);
            }
        }
        return new BufferUpload(buffer, offset, target, new BufferRange(start, checked((ulong)length)),
            Interlocked.Increment(ref _uploadSequence));
    }

    private UploadPage CreateUploadPage(ulong size)
    {
        ulong capacity = Math.Max(4UL << 20, (size + 65535) & ~65535UL);
        DevelopmentCheck.Require(capacity <= int.MaxValue, "An upload page must fit in a mapped byte span.");
        var description = new BufferDesc(capacity, BufferUsages.CopySource, "Frame buffer upload page");
        MemoryRequirements requirements = _backend.GetBufferMemoryRequirements(_device, description, MemoryType.Upload);
        if (!_budget.TryReserve(requirements.Size))
            throw new InvalidOperationException("The render graph upload memory budget is exhausted.");
        Buffer? resource = null;
        try
        {
            resource = _backend.CreateBuffer(_device, description, MemoryType.Upload);
            var page = new UploadPage
            {
                Buffer = resource,
                Mapping = _backend.Map(resource, MapType.Write, BufferRange.Whole),
                CommittedBytes = requirements.Size,
                BoundaryStates = [new BufferBoundaryState(BufferRange.Whole, resource.InitialSync,
                    resource.InitialAccess, ResourceContentState.Defined)],
            };
            _uploadPages.Add(page);
            return page;
        }
        catch { resource?.Dispose(); _budget.Release(requirements.Size); throw; }
    }

    internal void FlushUploads()
    {
        foreach (UploadPage page in _uploadPages)
            if (page.Used != 0) page.Mapping.Flush(new BufferRange(0,
                Math.Min(checked((ulong)page.Used), page.Buffer.Info.Size)));
    }

    internal void ResetUploads()
    {
        foreach (List<BufferUpload> values in _uploads.Values) values.Clear();
        foreach (UploadPage page in _uploadPages) page.Used = 0;
        _uploadPage = _uploadPages.FirstOrDefault();
        _uploadSequence = 0;
    }

    internal FrameBufferTransfers(IGraphicsBackend backend, Device device, HeapByteBudget budget)
    {
        _backend = backend;
        _device = device;
        _budget = budget;
    }

    private sealed class ReadbackPage
    {
        internal required Buffer Buffer;
        internal required ulong CommittedBytes;
        internal BufferBoundaryState[] BoundaryStates = [];
        internal ulong Used;
    }

    private readonly record struct ReadbackResult(
        Buffer Buffer, BufferRange Range, IGraphReadbackReceiver Receiver, ulong Tag);

    internal (BufferRegion Destination, BufferBoundaryState[] BoundaryStates) ReserveReadback(ulong size)
    {
        foreach (ReadbackPage page in _readbackPages)
        {
            if (size > page.Buffer.Info.Size - page.Used) continue;
            ulong offset = page.Used;
            page.Used += size;
            return (new BufferRegion(page.Buffer, new BufferRange(offset, size)), page.BoundaryStates);
        }
        ulong capacity = Math.Max(64UL << 10, (size + 65535) & ~65535UL);
        var description = new BufferDesc(capacity, BufferUsages.CopyDestination, "Frame readback page");
        MemoryRequirements requirements = _backend.GetBufferMemoryRequirements(_device, description, MemoryType.Readback);
        if (!_budget.TryReserve(requirements.Size))
            throw new InvalidOperationException("The render graph readback memory budget is exhausted.");
        Buffer buffer;
        try { buffer = _backend.CreateBuffer(_device, description, MemoryType.Readback); }
        catch { _budget.Release(requirements.Size); throw; }
        var created = new ReadbackPage
        {
            Buffer = buffer,
            CommittedBytes = requirements.Size,
            Used = size,
            BoundaryStates = [new BufferBoundaryState(BufferRange.Whole, buffer.InitialSync,
                buffer.InitialAccess, ResourceContentState.Undefined)],
        };
        _readbackPages.Add(created);
        return (new BufferRegion(buffer, new BufferRange(0, size)), created.BoundaryStates);
    }

    internal void SaveBufferStates(RenderGraphFrameResult result)
    {
        foreach (UploadPage page in _uploadPages)
        {
            ReadOnlySpan<BufferBoundaryState> states = result.GetBufferStates(page.Buffer);
            if (!states.IsEmpty) page.BoundaryStates = states.ToArray();
        }
        foreach (ReadbackPage page in _readbackPages)
        {
            ReadOnlySpan<BufferBoundaryState> states = result.GetBufferStates(page.Buffer);
            if (!states.IsEmpty) page.BoundaryStates = states.ToArray();
        }
    }

    internal void AddReadbackResult(Buffer buffer, BufferRange range, IGraphReadbackReceiver receiver, ulong tag) =>
        _readbackResults.Add(new ReadbackResult(buffer, range, receiver, tag));

    internal void CollectReadbacks()
    {
        ReadbackResult[] results = [.. _readbackResults];
        _readbackResults.Clear();
        List<Exception>? errors = null;
        foreach (ReadbackResult result in results)
        {
            try
            {
                using MappedBuffer mapping = _backend.Map(result.Buffer, MapType.Read, result.Range);
                mapping.Invalidate(result.Range);
                result.Receiver.OnReadback(mapping.Bytes, result.Tag);
            }
            catch (Exception error) { (errors ??= []).Add(error); }
        }
        foreach (ReadbackPage page in _readbackPages) page.Used = 0;
        if (errors is not null) throw new AggregateException("Readback receivers failed.", errors);
    }

    public void Dispose()
    {
        foreach (UploadPage page in _uploadPages)
        {
            page.Mapping.Dispose();
            page.Buffer.Dispose();
            _budget.Release(page.CommittedBytes);
        }
        _uploadPages.Clear();
        _uploads.Dispose();
        foreach (ReadbackPage page in _readbackPages)
        {
            page.Buffer.Dispose();
            _budget.Release(page.CommittedBytes);
        }
        _readbackPages.Clear();
        _readbackResults.Clear();
    }
}
