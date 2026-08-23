namespace SomeEngine.RenderGraph;

public sealed partial class RenderGraph : IDisposable
{
    private static long s_nextOwnerIdentity;

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Queue[] _queues;
    private readonly FrameSlot[] _frameSlots;
    private readonly CommandContextPool _commandContexts;
    private readonly HeapByteBudget _heapByteBudget;
    private readonly PersistentResourceAllocator _persistentResources;
    private GraphStructure _structure = new();
    private GraphStructureIndex _structureIndex = GraphStructureIndex.Empty;
    private long _editToken;
    private long _nextEditIdentity;
    private long _frameToken;
    private int _nextFrameSlot;
    private bool _disposed;
    private bool _lost;

    public RenderGraph(
        IGraphicsBackend backend,
        Device device,
        ReadOnlySpan<Queue> queues,
        in RenderGraphDesc description = default)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        _device = device ?? throw new ArgumentNullException(nameof(device));
        if (queues.IsEmpty) throw new ArgumentException("At least one Queue is required.", nameof(queues));
        uint maximumFramesInFlight = description.MaximumFramesInFlight == 0
            ? 3u
            : description.MaximumFramesInFlight;
        ulong maximumHeapBytes = description.MaximumHeapBytes == 0
            ? ulong.MaxValue
            : description.MaximumHeapBytes;
        _queues = queues.ToArray();
        for (int i = 0; i < _queues.Length; i++)
        {
            Queue queue = _queues[i] ?? throw new ArgumentNullException(nameof(queues));
            if (!ReferenceEquals(queue.Device, device))
                throw new ArgumentException("Every Queue must belong to the RenderGraph Device.", nameof(queues));
            for (int j = 0; j < i; j++)
                if (ReferenceEquals(queue, _queues[j]))
                    throw new ArgumentException("A Queue cannot be registered twice.", nameof(queues));
        }

        Identity = AllocateOwnerIdentity();
        Description = new RenderGraphDesc(
            maximumFramesInFlight,
            maximumHeapBytes,
            description.Label);
        _frameSlots = new FrameSlot[checked((int)maximumFramesInFlight)];
        for (int i = 0; i < _frameSlots.Length; i++)
            _frameSlots[i] = new FrameSlot(i);
        _commandContexts = new CommandContextPool(_backend, _device);
        _heapByteBudget = new HeapByteBudget(maximumHeapBytes);
        _persistentResources = new PersistentResourceAllocator(
            _backend,
            _device,
            _heapByteBudget);
    }

    internal ulong Identity { get; }
    internal IGraphicsBackend Backend => _backend;
    internal Device Device => _device;
    internal GraphStructureIndex StructureIndex => _structureIndex;
    internal ReadOnlySpan<Queue> Queues => _queues;
    internal CommandContextPool CommandContexts => _commandContexts;
    internal PersistentResourceAllocator PersistentResources => _persistentResources;
    internal HeapByteBudget HeapByteBudget => _heapByteBudget;
    internal int FrameSlotCount => _frameSlots.Length;
    internal int InFlightCompletionCount
    {
        get
        {
            int count = 0;
            foreach (FrameSlot slot in _frameSlots) count += slot.Completions.Count;
            return count;
        }
    }

    public RenderGraphDesc Description { get; }
    public ulong StructureVersion { get; private set; }
    public string? Label => Description.Label;
    public int MaximumQueueCompletionCount => _queues.Length;

    public RenderGraphEdit BeginEdit()
    {
        EnsureAvailable();
        if (_frameToken != 0)
            throw new InvalidOperationException("A frame is active.");
        long token = Interlocked.Increment(ref _nextEditIdentity);
        if (token <= 0)
            throw new InvalidOperationException("The render graph edit identity space is exhausted.");
        if (Interlocked.CompareExchange(ref _editToken, token, 0) != 0)
            throw new InvalidOperationException("A render graph edit is already active.");
        return new RenderGraphEdit(this, token, _structure.Clone());
    }

    public bool TryBeginFrame(
        out RenderGraphFrame frame,
        in RenderGraphFrameOptions options = default)
    {
        EnsureAvailable();
        if (_editToken != 0)
            throw new InvalidOperationException("A structural edit is active.");
        if (_frameToken != 0)
            throw new InvalidOperationException("A CPU frame is already active.");

        CollectCompleted();
        for (int probe = 0; probe < _frameSlots.Length; probe++)
        {
            int index = (_nextFrameSlot + probe) % _frameSlots.Length;
            FrameSlot slot = _frameSlots[index];
            if (!slot.IsReady(_backend)) continue;
            slot.Reset(_backend);
            _nextFrameSlot = (index + 1) % _frameSlots.Length;
            ulong identity = AllocateOwnerIdentity();
            _frameToken = checked((long)identity);
            frame = new RenderGraphFrame(slot.BeginExecution(this, identity, options));
            return true;
        }

        frame = default;
        return false;
    }

    public RenderGraphFrame BeginFrame(in RenderGraphFrameOptions options = default)
    {
        if (!TryBeginFrame(out RenderGraphFrame frame, options))
            throw new InvalidOperationException("No frame slot is available. Call WaitForFrameSlot explicitly.");
        return frame;
    }

    public WaitStatus WaitForFrameSlot(TimeSpan timeout)
    {
        EnsureAvailable();
        DateTime deadline = timeout == Timeout.InfiniteTimeSpan
            ? DateTime.MaxValue
            : DateTime.UtcNow + timeout;
        foreach (FrameSlot slot in _frameSlots)
        {
            foreach (QueueCompletion completion in slot.Completions)
            {
                TimeSpan remaining = deadline == DateTime.MaxValue
                    ? Timeout.InfiniteTimeSpan
                    : deadline - DateTime.UtcNow;
                if (remaining < TimeSpan.Zero) return WaitStatus.Timeout;
                WaitStatus status = _backend.WaitCpu(completion, remaining);
                if (status != WaitStatus.Completed) return status;
            }
            if (slot.IsReady(_backend)) return WaitStatus.Completed;
        }
        return WaitStatus.Completed;
    }

    public void CollectCompleted()
    {
        EnsureAvailable();
        _backend.CollectCompleted(_device);
        _persistentResources.CollectCompleted();
        foreach (FrameSlot slot in _frameSlots)
            slot.CollectCompleted(_backend);
    }

    internal Queue[] ResolveQueueCandidates(in PassQueueSelection policy, GraphPassKind kind)
    {
        var result = new List<Queue>(_queues.Length);
        ResolveQueueCandidates(policy, kind, result);
        return result.ToArray();
    }

    internal void ValidateQueueSelection(in PassQueueSelection policy, GraphPassKind kind)
    {
        if (policy.IsExact)
        {
            Queue queue = policy.Queue!;
            if (!ReferenceEquals(queue.Device, _device) || !_queues.Contains(queue))
                throw new ArgumentException("The exact Queue is not registered with this RenderGraph.");
            ValidateQueueKind(queue.Type, kind);
            return;
        }

        foreach (Queue queue in _queues)
            if (queue.Type == policy.Type && QueueSupports(kind, queue.Type))
                return;
        throw new NotSupportedException($"No registered {policy.Type} Queue can execute the pass.");
    }

    internal void ResolveQueueCandidates(
        in PassQueueSelection policy,
        GraphPassKind kind,
        List<Queue> destination)
    {
        destination.Clear();
        if (policy.IsExact)
        {
            Queue queue = policy.Queue!;
            if (!ReferenceEquals(queue.Device, _device) || !_queues.Contains(queue))
                throw new ArgumentException("The exact Queue is not registered with this RenderGraph.");
            ValidateQueueKind(queue.Type, kind);
            destination.Add(queue);
            return;
        }

        foreach (Queue queue in _queues)
            if (queue.Type == policy.Type && QueueSupports(kind, queue.Type))
                destination.Add(queue);
        if (destination.Count == 0)
            throw new NotSupportedException($"No registered {policy.Type} Queue can execute the pass.");
    }

    private static bool QueueSupports(GraphPassKind kind, QueueType queueType) => kind switch
    {
        GraphPassKind.Raster => queueType == QueueType.Graphics,
        GraphPassKind.Compute => queueType is QueueType.Graphics or QueueType.Compute,
        GraphPassKind.Copy => true,
        // The general scope intentionally exposes the complete portable command set,
        // including raster state. A Graphics Queue is therefore the only queue family
        // that can satisfy the scope contract without per-command capability holes.
        GraphPassKind.General => queueType == QueueType.Graphics,
        _ => false,
    };

    private static void ValidateQueueKind(QueueType type, GraphPassKind kind)
    {
        if (!QueueSupports(kind, type))
            throw new NotSupportedException($"A {kind} pass cannot execute on a {type} Queue.");
    }

    internal void CommitEdit(long token, GraphStructure staging)
    {
        EnsureEdit(token);
        GraphStructureIndex index = GraphStructureIndex.Build(this, staging);
        _persistentResources.ApplyStructure(_structure, staging);
        _structure = staging;
        _structureIndex = index;
        StructureVersion++;
        EndEdit(token);
    }

    internal void AbandonEdit(long token)
    {
        if (Volatile.Read(ref _editToken) == token)
            EndEdit(token);
    }

    internal void EnsureEdit(long token)
    {
        EnsureAvailable();
        if (token <= 0 || Volatile.Read(ref _editToken) != token)
            throw new InvalidOperationException("The render graph edit is no longer active.");
    }

    private void EndEdit(long token)
    {
        if (Interlocked.CompareExchange(ref _editToken, 0, token) != token)
            throw new InvalidOperationException("The render graph edit token is no longer active.");
    }

    internal void EndFrame(ulong identity)
    {
        if (Interlocked.CompareExchange(ref _frameToken, 0, checked((long)identity)) != checked((long)identity))
            throw new InvalidOperationException("The render graph frame token is no longer active.");
    }

    internal void EnsureFrame(ulong identity)
    {
        EnsureAvailable();
        if (identity == 0 || Volatile.Read(ref _frameToken) != checked((long)identity))
            throw new InvalidOperationException("The render graph frame is no longer active.");
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_lost) throw new GraphicsException(GraphicsError.DeviceLost, "The RenderGraph Device is lost.");
    }

    internal void MarkDeviceLost() => _lost = true;

    private static ulong AllocateOwnerIdentity()
    {
        long identity = Interlocked.Increment(ref s_nextOwnerIdentity);
        if (identity <= 0)
            throw new InvalidOperationException("The render graph owner identity space is exhausted.");
        return checked((ulong)identity);
    }

    public void Dispose()
    {
        if (_disposed) return;
        if (_editToken != 0 || _frameToken != 0)
            throw new InvalidOperationException("A render graph edit or frame is active.");
        if (!_lost)
            WaitForSubmittedWork();
        _disposed = true;
        foreach (FrameSlot slot in _frameSlots)
            slot.Dispose();
        _commandContexts.Dispose();
        foreach (GraphView view in _structure.Views.Rows)
        {
            view.PersistentView?.Dispose();
            view.PersistentView = null;
        }
        foreach (GraphBuffer buffer in _structure.Buffers.Rows)
        {
            buffer.PersistentResource?.Dispose();
            buffer.PersistentResource = null;
        }
        foreach (GraphTexture texture in _structure.Textures.Rows)
        {
            texture.PersistentResource?.Dispose();
            texture.PersistentResource = null;
        }
        _persistentResources.Dispose();
        foreach (GraphPass pass in _structure.Passes.Rows)
            for (int slot = 0; slot < _frameSlots.Length; slot++)
                pass.CallbackStorage.ClearFrameData(slot);
    }

    private void WaitForSubmittedWork()
    {
        var completions = new List<QueueCompletion>();
        foreach (FrameSlot slot in _frameSlots)
            foreach (QueueCompletion completion in slot.Completions)
                AddCompletion(completions, completion);
        foreach (GraphBuffer buffer in _structure.Buffers.Rows)
            foreach (BufferBoundaryState endpoint in buffer.BoundaryStates)
                if (endpoint.ReadyAfter.HasValue)
                    AddCompletion(completions, endpoint.ReadyAfter.Value);
        foreach (GraphTexture texture in _structure.Textures.Rows)
            foreach (TextureBoundaryState endpoint in texture.BoundaryStates)
                if (endpoint.ReadyAfter.HasValue)
                    AddCompletion(completions, endpoint.ReadyAfter.Value);
        foreach (QueueCompletion completion in completions)
            _ = _backend.WaitCpu(completion, Timeout.InfiniteTimeSpan);
    }

    private static void AddCompletion(List<QueueCompletion> values, QueueCompletion completion)
    {
        for (int index = 0; index < values.Count; index++)
        {
            if (!ReferenceEquals(values[index].Queue, completion.Queue)) continue;
            if (completion.Value > values[index].Value) values[index] = completion;
            return;
        }
        values.Add(completion);
    }
}
