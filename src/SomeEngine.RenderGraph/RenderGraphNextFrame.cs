namespace SomeEngine.RenderGraph;

/// <summary>Reserves completed frame resources while data and graph edits are assembled.</summary>
public sealed class RenderGraphNextFrame : IDisposable
{
    private const int Open = 0;
    private const int Started = 1;
    private const int Discarded = 2;
    private int _state;
    public RenderGraph Graph { get; }
    internal FrameSlot FrameStorage { get; }
    internal ulong Identity { get; }

    internal RenderGraphNextFrame(RenderGraph graph, FrameSlot frameStorage, ulong identity)
    {
        Graph = graph;
        FrameStorage = frameStorage;
        Identity = identity;
    }

    internal void Start()
    {
        Graph.EnsureFrame(Identity);
        int previous = Interlocked.CompareExchange(ref _state, Started, Open);
        DevelopmentCheck.Require(previous == Open,
            "The next frame must be open when it is started.");
    }

    /// <summary>Returns mapped upload bytes for a range of a persistent device buffer.</summary>
    public Span<byte> GetWriteSpan(GraphBufferId buffer, ulong offset, int length)
    {
        EnsureOpen();
        Graph.ValidateBufferUpload(buffer, offset, length);
        return FrameStorage.GetBufferTransfers(Graph).GetWriteSpan(buffer, offset, length);
    }

    public void WriteBuffer(GraphBufferId buffer, ulong offset, scoped ReadOnlySpan<byte> data) =>
        data.CopyTo(GetWriteSpan(buffer, offset, data.Length));

    /// <summary>Groups direct upload writes so they can be committed or discarded together.</summary>
    public RenderGraphBufferWrites BeginBufferWrites()
    {
        EnsureOpen();
        return new RenderGraphBufferWrites(this, FrameStorage.GetBufferTransfers(Graph));
    }

    internal void EnsureOpen()
    {
        Graph.EnsureFrame(Identity);
        DevelopmentCheck.Require(Volatile.Read(ref _state) == Open, "The next frame must be open for writing.");
    }

    public void Dispose()
    {
        if (Interlocked.CompareExchange(ref _state, Discarded, Open) != Open) return;
        FrameStorage.GetBufferTransfers(Graph).ResetUploads();
        Graph.EndFrame(Identity);
    }
}

public sealed partial class RenderGraph
{
    /// <summary>Collects completed work and waits within the timeout for the selected frame resources.</summary>
    public bool TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan timeout)
    {
        EnsureAvailable();
        DevelopmentCheck.Require(timeout >= TimeSpan.Zero || timeout == Timeout.InfiniteTimeSpan,
            "A frame wait requires a positive timeout or an infinite wait.");
        CollectCompleted();
        FrameSlot storage;
        ulong identity;
        lock (_structureGate)
        {
            DevelopmentCheck.Require(_frameToken == 0, "A CPU frame must finish before opening another frame.");
            int index = _preferredFrameIndex;
            for (int probe = 0; probe < _frameSlots.Length; probe++)
            {
                int candidate = (_preferredFrameIndex + probe) % _frameSlots.Length;
                if (!_frameSlots[candidate].IsReady(_backend)) continue;
                index = candidate;
                break;
            }
            storage = _frameSlots[index];
            identity = AllocateOwnerIdentity();
            _frameToken = checked((long)identity);
        }
        try
        {
            long started = System.Diagnostics.Stopwatch.GetTimestamp();
            foreach (QueueCompletion completion in storage.Completions)
            {
                if (_backend.IsComplete(completion)) continue;
                TimeSpan remaining = timeout == Timeout.InfiniteTimeSpan ? timeout :
                    timeout - System.Diagnostics.Stopwatch.GetElapsedTime(started);
                if (remaining < TimeSpan.Zero && timeout != Timeout.InfiniteTimeSpan) remaining = TimeSpan.Zero;
                if (_backend.WaitCpu(completion, remaining) == WaitStatus.Completed) continue;
                EndFrame(identity);
                next = null!;
                return false;
            }
            storage.Reset(_backend);
            _ = storage.GetBufferTransfers(this);
            _preferredFrameIndex = storage.Index;
            next = new RenderGraphNextFrame(this, storage, identity);
            return true;
        }
        catch
        {
            EndFrame(identity);
            throw;
        }
    }

    /// <summary>Consumes the next frame's writes and takes the graph version after committed edits.</summary>
    public RenderGraphFrame BeginFrame(RenderGraphNextFrame next, in RenderGraphFrameOptions options = default)
    {
        DevelopmentCheck.Require(next is not null && ReferenceEquals(next.Graph, this),
            "The next frame must belong to this graph.");
        next.Start();
        RenderGraphFrameState? execution = null;
        try
        {
            FrameBufferTransfers.BufferUpload[] uploads = next.FrameStorage.GetBufferTransfers(this).TakeUploads();
            EnsureBufferUploadPasses(uploads);
            lock (_structureGate)
            {
                execution = next.FrameStorage.BeginExecution(this, next.Identity, options, _structureIndex, StructureVersion);
                execution.CollectBufferUploads(uploads);
                return new RenderGraphFrame(execution);
            }
        }
        catch
        {
            if (execution is not null) execution.Cancel(next.Identity);
            else EndFrame(next.Identity);
            throw;
        }
    }
}
