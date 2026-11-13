namespace SomeEngine.RenderGraph;

internal sealed class FrameResources : IDisposable
{
    private readonly List<IDisposable> _objects = [];
    private readonly Dictionary<object, object> _featureData = new(ReferenceEqualityComparer.Instance);
    private readonly List<QueueCompletion> _completions = [];
    private QueueCompletion[] _completionBuffer = [];
    private RenderGraphFrameState? _state;
    private FrameBufferTransfers? _bufferTransfers;

    internal T GetFrameData<T>(object feature, Func<T> create) where T : class
    {
        if (_featureData.TryGetValue(feature, out object? data)) return (T)data;
        T value = create();
        _featureData.Add(feature, value);
        return value;
    }

    internal void ClearFeatureData() => _featureData.Clear();

    internal FrameBufferTransfers GetBufferTransfers(RenderGraph graph) =>
        _bufferTransfers ??= new FrameBufferTransfers(graph.Backend, graph.Device, graph.HeapByteBudget);

    internal void SaveBufferTransferStates(RenderGraphFrameState result) =>
        _bufferTransfers?.SaveBufferStates(result);

    internal void ChangeProperty(GraphIdentity identity, GraphStoreKind kind, bool refreshViews) =>
        _state?.ChangeProperty(identity, kind, refreshViews);
    internal void BoundaryChanged(GraphIdentity identity, GraphStoreKind kind) => _state?.BoundaryChanged(identity, kind);
    internal void CommandDataChanged(GraphIdentity identity) => _state?.CommandDataChanged(identity);
    internal void ParameterInputChanged(GraphParameterInput input) => _state?.ParameterInputChanged(input);
    internal void ConnectionsChanged(IEnumerable<int> passes) => _state?.ConnectionsChanged(passes);

    internal FrameResources(int index)
    {
        Index = index;
    }

    internal int Index { get; }
    internal ulong ScheduleVersion { get; set; }
    internal ulong FrameIdentity => _state?.Identity ?? 0;
    internal IReadOnlyList<QueueCompletion> Completions => _completions;
    internal Span<QueueCompletion> GetCompletionBuffer(int count)
    {
        if (_completionBuffer.Length != count) _completionBuffer = new QueueCompletion[count];
        return _completionBuffer;
    }
    internal FrameTransientResourceAllocator? TransientResources { get; private set; }

    internal void EnsureTransientResources(
        IGraphicsBackend backend,
        Device device,
        HeapByteBudget budget)
    {
        TransientResources ??= new FrameTransientResourceAllocator(backend, device, budget);
    }

    internal RenderGraphFrameState BeginExecution(
        RenderGraph graph,
        ulong identity,
        in RenderGraphFrameOptions options,
        GraphStructureIndex structureIndex,
        ulong structureVersion)
    {
        _state ??= new RenderGraphFrameState(graph, this);
        bool compacted = TransientResources!.Reset(options.SubmissionMode, options.Debug);
        _state.Begin(identity, options, structureIndex, structureVersion);
        if (compacted || ScheduleVersion != graph.Schedule.Version)
            _state.RefreshStructure();
        return _state;
    }

    internal bool IsReady(IGraphicsBackend backend)
    {
        foreach (QueueCompletion completion in _completions)
            if (!backend.IsComplete(completion)) return false;
        return true;
    }

    internal void Reset(IGraphicsBackend backend)
    {
        if (!IsReady(backend))
            throw new InvalidOperationException("The frame index is still in flight.");
        _featureData.Clear();
        _state?.CollectGpuTimes();
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            _objects[i].Dispose();
            _objects.RemoveAt(i);
        }
        _completions.Clear();
        _bufferTransfers?.CollectReadbacks();
        _bufferTransfers?.ResetUploads();
    }

    internal void Own(IDisposable value) => _objects.Add(value);

    internal void SetCompletions(ReadOnlySpan<QueueCompletion> completions)
    {
        _completions.Clear();
        foreach (QueueCompletion completion in completions)
        {
            bool found = false;
            for (int i = 0; i < _completions.Count; i++)
            {
                if (!ReferenceEquals(_completions[i].Queue, completion.Queue)) continue;
                if (completion.Value > _completions[i].Value) _completions[i] = completion;
                found = true;
                break;
            }
            if (!found) _completions.Add(completion);
        }
    }

    internal void CollectCompleted(IGraphicsBackend backend)
    {
        if (IsReady(backend) && _completions.Count != 0) Reset(backend);
    }

    public void Dispose()
    {
        _featureData.Clear();
        _state?.DisposeGpuTimes();
        _state?.ReleaseRecordingContexts();
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            _objects[i].Dispose();
            _objects.RemoveAt(i);
        }
        _completions.Clear();
        TransientResources?.Dispose();
        _bufferTransfers?.Dispose();
    }
}
