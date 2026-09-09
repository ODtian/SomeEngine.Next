namespace SomeEngine.RenderGraph;

internal sealed class FrameSlot : IDisposable
{
    private readonly List<IDisposable> _objects = [];
    private readonly List<QueueCompletion> _completions = [];
    private RenderGraphFrameState? _state;
    private readonly List<GraphEditChanges> _edits = [];

    internal void ApplyEdit(GraphEditChanges changes)
    {
        if (_state is not null) _edits.Add(changes);
    }

    internal void ChangeProperty(GraphIdentity identity, GraphStoreKind kind) => _state?.ChangeProperty(identity, kind);
    internal void BoundaryChanged(GraphIdentity identity, GraphStoreKind kind) => _state?.BoundaryChanged(identity, kind);

    internal FrameSlot(int index)
    {
        Index = index;
    }

    internal int Index { get; }
    internal IReadOnlyList<QueueCompletion> Completions => _completions;
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
        if (compacted)
        {
            var changes = new GraphEditChanges { CompactTransientPlacements = true };
            foreach (GraphIdentity resource in structureIndex.TransientResources)
            {
                if (resource.Domain == GraphIdentityDomain.Buffer) changes.Buffers.Add(resource);
                else if (resource.Domain == GraphIdentityDomain.Texture) changes.Textures.Add(resource);
                changes.Views.UnionWith(structureIndex.ResourceViews.GetValueOrDefault(resource, []));
            }
            _state.ApplyEdit(changes);
        }
        foreach (GraphEditChanges changes in _edits) _state.ApplyEdit(changes);
        _edits.Clear();
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
            throw new InvalidOperationException("The frame slot is still in flight.");
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            _objects[i].Dispose();
            _objects.RemoveAt(i);
        }
        _completions.Clear();
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
        _state?.ReleaseRecordingContexts();
        for (int i = _objects.Count - 1; i >= 0; i--)
        {
            _objects[i].Dispose();
            _objects.RemoveAt(i);
        }
        _completions.Clear();
        TransientResources?.Dispose();
    }
}
