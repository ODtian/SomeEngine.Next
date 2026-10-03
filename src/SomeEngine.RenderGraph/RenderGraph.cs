namespace SomeEngine.RenderGraph;

public sealed partial class RenderGraph : IDisposable
{
    internal void PropertyChanged(GraphIdentity identity, GraphStoreKind kind, bool refreshViews = true)
    {
        foreach (FrameSlot frame in _frameSlots) frame.ChangeProperty(identity, kind, refreshViews);
    }

    internal void BoundaryChanged(GraphIdentity identity, GraphStoreKind kind)
    {
        foreach (FrameSlot frame in _frameSlots) frame.BoundaryChanged(identity, kind);
    }

    internal void CommandDataChanged(GraphIdentity identity)
    {
        foreach (FrameSlot frame in _frameSlots) frame.CommandDataChanged(identity);
    }

    private static long s_nextOwnerIdentity;

    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly Queue[] _queues;
    private readonly FrameSlot[] _frameSlots;
    private readonly CommandContextPool _commandContexts;
    private readonly HeapByteBudget _heapByteBudget;
    private readonly PersistentResourceAllocator _persistentResources;
    private readonly object _structureGate = new();
    private readonly List<StructureTransition> _deferredRetirements = [];
    private GraphStructure _structure = new();
    private GraphStructure? _editSource;
    private GraphStructure? _editStructure;
    private GraphEditChanges? _editChanges;
    private GraphStructureIndex _structureIndex = GraphStructureIndex.Empty;
    private long _editToken;
    private long _nextEditIdentity;
    private long _frameToken;
    private int _preferredFrameIndex;
    private bool _disposed;
    private bool _closing;
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
    public Device Device => _device;
    internal GraphStructureIndex StructureIndex => _structureIndex;
    public bool TryGetBufferId(Buffer buffer, out GraphBufferId id)
    {
        bool found = _structureIndex.ResourceIdentities.TryGetValue(buffer, out GraphIdentity identity);
        id = found ? new GraphBufferId(identity) : default;
        return found;
    }

    public bool TryGetTextureId(Texture texture, out GraphTextureId id)
    {
        bool found = _structureIndex.ResourceIdentities.TryGetValue(texture, out GraphIdentity identity);
        id = found ? new GraphTextureId(identity) : default;
        return found;
    }
    internal ReadOnlySpan<Queue> Queues => _queues;
    internal CommandContextPool CommandContexts => _commandContexts;
    internal PersistentResourceAllocator PersistentResources => _persistentResources;
    internal HeapByteBudget HeapByteBudget => _heapByteBudget;
    internal int FrameSlotCount => _frameSlots.Length;
    public RenderGraphDesc Description { get; }
    public ulong StructureVersion { get; private set; }
    public string? Label => Description.Label;
    public int MaximumQueueCompletionCount => _queues.Length;
#if DEBUG
    public RenderGraphEditStatistics LastEditStatistics { get; private set; }
#endif

    public RenderGraphEdit BeginEdit()
    {
        EnsureAvailable();
        long token = Interlocked.Increment(ref _nextEditIdentity);
        if (token <= 0)
            throw new InvalidOperationException("The render graph edit identity space is exhausted.");
        if (Interlocked.CompareExchange(ref _editToken, token, 0) != 0)
            throw new InvalidOperationException("A render graph edit is already active.");
        try
        {
            lock (_structureGate)
            {
                _editSource = _structure;
                return new RenderGraphEdit(this, token);
            }
        }
        catch
        {
            EndEdit(token);
            throw;
        }
    }

    internal GraphStructure ReadEditStructure => _editStructure ?? _editSource!;

    internal GraphStructure EditStructure
    {
        get
        {
            if (_editStructure is not null) return _editStructure;
            lock (_structureGate) return _editStructure ??= _editSource!.Fork();
        }
    }

    internal GraphEditChanges EditChanges => _editChanges ??= new GraphEditChanges();

    public bool TryBeginFrame(
        out RenderGraphFrame frame,
        in RenderGraphFrameOptions options = default)
    {
        if (!TryOpenNextFrame(out RenderGraphNextFrame next, TimeSpan.Zero))
        {
            frame = default;
            return false;
        }
        using (next) frame = BeginFrame(next, options);
        return true;
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
        lock (_structureGate)
            _persistentResources.CollectCompleted();
        foreach (FrameSlot slot in _frameSlots)
            slot.CollectCompleted(_backend);
    }

    public int GetRangeCompletionCount(GraphBufferId buffer, in BufferRange range) =>
        GetRangeCompletionCount(GetPersistentBuffer(buffer), range);

    public int CopyRangeCompletions(GraphBufferId buffer, in BufferRange range, Span<QueueCompletion> destination) =>
        CopyRangeCompletions(GetPersistentBuffer(buffer), range, destination);

    private Buffer GetPersistentBuffer(GraphBufferId buffer)
    {
        EnsureAvailable();
        lock (_structureGate)
        {
            DevelopmentCheck.Require(buffer.Value.Owner == Identity && _structure.Buffers.Contains(buffer.Value),
                "The buffer must belong to this graph.");
            GraphBuffer data = _structure.Buffers.Get(buffer.Value);
            return data.PersistentResource ?? data.RegisteredResource ??
                throw new InvalidOperationException("Range completions require a persistent buffer.");
        }
    }

    /// <summary>
    /// Returns the number of Queue completions that can still access the requested Buffer range.
    /// At most one (the maximum known value) is returned for each registered Queue.
    /// </summary>
    public int GetRangeCompletionCount(Buffer buffer, in BufferRange range)
    {
        var completions = new List<QueueCompletion>(_queues.Length);
        CollectRangeCompletions(buffer, range, completions);
        return completions.Count;
    }

    /// <summary>
    /// Copies the Queue completions that can still access the requested Buffer range.
    /// At most one (the maximum known value) is copied for each registered Queue.
    /// The caller must prevent future frames from declaring access to the range before using the
    /// copied completions as its complete retirement dependency set.
    /// </summary>
    public int CopyRangeCompletions(
        Buffer buffer,
        in BufferRange range,
        Span<QueueCompletion> destination)
    {
        var completions = new List<QueueCompletion>(_queues.Length);
        CollectRangeCompletions(buffer, range, completions);
        if (destination.Length < completions.Count)
            throw new ArgumentException(
                "The destination cannot hold every Queue completion.",
                nameof(destination));
        for (int i = 0; i < completions.Count; i++) destination[i] = completions[i];
        return completions.Count;
    }

    /// <summary>
    /// Discards retained transient placement history so the next frame can repack it. The graph
    /// and all submitted frames must be idle; ordinary structural commits never compact implicitly.
    /// </summary>
    public void CompactTransientResources()
    {
        EnsureAvailable();
        lock (_structureGate)
        {
            if (_editToken != 0 || _frameToken != 0)
                throw new InvalidOperationException(
                    "Transient compaction requires an idle RenderGraph with no edit or CPU frame.");
            foreach (FrameSlot slot in _frameSlots)
                if (!slot.IsReady(_backend))
                    throw new InvalidOperationException(
                        "Transient compaction requires every submitted frame to complete.");

            var changes = new GraphEditChanges { CompactTransientPlacements = true };
            foreach (GraphIdentity resource in _structureIndex.TransientResources)
            {
                if (resource.Domain == GraphIdentityDomain.Buffer) changes.Buffers.Add(resource);
                else if (resource.Domain == GraphIdentityDomain.Texture) changes.Textures.Add(resource);
                changes.Views.UnionWith(_structureIndex.ResourceViews.GetValueOrDefault(resource, []));
            }
            foreach (FrameSlot slot in _frameSlots)
            {
                slot.ApplyEdit(changes);
                slot.TransientResources?.Compact();
            }

        }
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
            if ((policy.AllowsAnyType || queue.Type == policy.Type) && QueueSupports(kind, queue.Type))
                return;
        throw new NotSupportedException(policy.AllowsAnyType
            ? $"No registered Queue supports {kind} commands."
            : $"No registered {policy.Type} Queue can execute the pass.");
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
            if ((policy.AllowsAnyType || queue.Type == policy.Type) && QueueSupports(kind, queue.Type))
                destination.Add(queue);
        if (destination.Count == 0)
            throw new NotSupportedException(policy.AllowsAnyType
                ? $"No registered Queue supports {kind} commands."
                : $"No registered {policy.Type} Queue can execute the pass.");
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

    internal void CommitEdit(long token)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.EditCommit);
        EnsureEdit(token);
        lock (_structureGate)
        {
            GraphStructure previous = _structure;
            GraphStructure staging = _editStructure ?? _editSource!;
            GraphEditChanges? changes = _editChanges;
            if (changes is null || changes.IsEmpty)
            {
#if DEBUG
                LastEditStatistics = default;
#endif
                EndEdit(token);
                return;
            }
            if (changes.LabelsOnly)
            {
                CarryFrameValues(previous, staging, changes);
                if (_frameToken != 0) _deferredRetirements.Add(new StructureTransition(previous, staging, changes));
                _structure = staging;
                _structureIndex = _structureIndex.WithStructure(staging);
                foreach (FrameSlot slot in _frameSlots) slot.ApplyEdit(changes);
#if DEBUG
                LastEditStatistics = new RenderGraphEditStatistics(changes.Labels.Count, 0, 1, 9, 0, 0, 0, 0);
#endif
                EndEdit(token);
                return;
            }
            CarryFrameValues(previous, staging, changes);
            GraphStructureIndex index = GraphStructureIndex.Update(
                this,
                _structureIndex,
                staging,
                changes,
                out GraphIndexUpdateStatistics updateStatistics);
            bool frameActive = _frameToken != 0;
            _persistentResources.ApplyStructure(previous, staging, changes, !frameActive);
            index.UpdateResourceIdentities(_structureIndex, changes);
            if (frameActive)
                _deferredRetirements.Add(new StructureTransition(previous, staging, changes));
            _structure = staging;
            _structureIndex = index;
            StructureVersion++;
            foreach (FrameSlot slot in _frameSlots) slot.ApplyEdit(changes);
#if DEBUG
            int sharedBlocks = staging.CountSharedBlocks(previous);
            LastEditStatistics = new RenderGraphEditStatistics(
                changes.Passes.Count,
                changes.EditedResourceCount,
                10 - sharedBlocks,
                sharedBlocks,
                updateStatistics.ResourcesReanalyzed,
                updateStatistics.HazardEdgesRebuilt,
                updateStatistics.ScheduleNodesRevisited,
                updateStatistics.TransientAllocationsMoved);
#endif
            EndEdit(token);
        }
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
        lock (_structureGate)
        {
            if (Volatile.Read(ref _editToken) != token)
                throw new InvalidOperationException("The render graph edit token is no longer active.");
            _editSource = null;
            _editStructure = null;
            _editChanges = null;
            Interlocked.Exchange(ref _editToken, 0);
        }
    }

    internal void EndFrame(ulong identity)
    {
        lock (_structureGate)
        {
            if (Interlocked.CompareExchange(ref _frameToken, 0, checked((long)identity)) != checked((long)identity))
                throw new InvalidOperationException("The render graph frame token is no longer active.");
            foreach (StructureTransition transition in _deferredRetirements)
            {
                CarryFrameValues(transition.Previous, transition.Next, transition.Changes);
                _persistentResources.RetireRemoved(transition.Previous, transition.Next, transition.Changes);
            }
            _deferredRetirements.Clear();
        }
    }

    public void RetireAfterSubmittedFrames(IDisposable value)
    {
        ArgumentNullException.ThrowIfNull(value);
        RetireAfterSubmittedFrames([value]);
    }

    internal void RetireAfterSubmittedFrames(ReadOnlySpan<IDisposable> values)
    {
        var completions = new List<QueueCompletion>();
        lock (_structureGate)
        {
            foreach (FrameSlot slot in _frameSlots)
                foreach (QueueCompletion completion in slot.Completions)
                    AddCompletion(completions, completion);
            _persistentResources.Retire(values, CollectionsMarshal.AsSpan(completions));
        }
    }

    private void CarryFrameValues(GraphStructure previous, GraphStructure next, GraphEditChanges changes)
    {
        next.RootRegion.Enabled = previous.RootRegion.Enabled;
        foreach (GraphIdentity identity in changes.Regions)
            if (previous.Regions.Contains(identity) && next.Regions.Contains(identity))
                next.Regions.Get(identity).Enabled = previous.Regions.Get(identity).Enabled;
        foreach (GraphIdentity identity in changes.Passes.Concat(changes.Labels))
            if (previous.Passes.Contains(identity) && next.Passes.Contains(identity))
                next.Passes.Get(identity).Enabled = previous.Passes.Get(identity).Enabled;
        foreach (GraphIdentity identity in changes.Views)
            if (previous.Views.Contains(identity) && next.Views.Contains(identity))
                next.Views.Get(identity).CurrentBufferRange = previous.Views.Get(identity).CurrentBufferRange;
        foreach (GraphIdentity identity in changes.Accesses)
            if (previous.Accesses.Contains(identity) && next.Accesses.Contains(identity))
            {
                PassResourceAccess source = previous.Accesses.Get(identity);
                PassResourceAccess destination = next.Accesses.Get(identity);
                destination.CurrentBufferRange = source.CurrentBufferRange;
                destination.CurrentTextureRange = source.CurrentTextureRange;
            }
        foreach (GraphIdentity identity in changes.Buffers)
        {
            if (!previous.Buffers.Contains(identity) || !next.Buffers.Contains(identity)) continue;
            GraphBuffer source = previous.Buffers.Get(identity);
            GraphBuffer destination = next.Buffers.Get(identity);
            destination.CurrentViewRange = source.CurrentViewRange;
            if (destination.RegisteredResource is null && destination.Ownership == RenderGraphResourceOwnership.CallerOwned)
                destination.BoundResource = source.BoundResource;
            if (ReferenceEquals(source.PersistentResource, destination.PersistentResource) &&
                ReferenceEquals(source.RegisteredResource, destination.RegisteredResource) &&
                ReferenceEquals(source.BoundResource, destination.BoundResource))
                destination.BoundaryStates = source.BoundaryStates;
        }
        foreach (GraphIdentity identity in changes.Textures)
        {
            if (!previous.Textures.Contains(identity) || !next.Textures.Contains(identity)) continue;
            GraphTexture source = previous.Textures.Get(identity);
            GraphTexture destination = next.Textures.Get(identity);
            if (destination.RegisteredResource is null && destination.Ownership == RenderGraphResourceOwnership.CallerOwned)
                destination.BoundResource = source.BoundResource;
            if (ReferenceEquals(source.PersistentResource, destination.PersistentResource) &&
                ReferenceEquals(source.RegisteredResource, destination.RegisteredResource) &&
                ReferenceEquals(source.BoundResource, destination.BoundResource))
                destination.BoundaryStates = source.BoundaryStates;
        }
        foreach (GraphIdentity identity in changes.QueryPools)
        {
            if (!previous.QueryPools.Contains(identity) || !next.QueryPools.Contains(identity)) continue;
            GraphQueryPool source = previous.QueryPools.Get(identity);
            GraphQueryPool destination = next.QueryPools.Get(identity);
            if (ReferenceEquals(source.Resource, destination.Resource))
                destination.BoundaryStates = source.BoundaryStates;
        }
        foreach (GraphIdentity identity in changes.ShaderTables)
        {
            if (!previous.ShaderTables.Contains(identity) || !next.ShaderTables.Contains(identity)) continue;
            GraphRayTracingShaderTable source = previous.ShaderTables.Get(identity);
            GraphRayTracingShaderTable destination = next.ShaderTables.Get(identity);
            if (ReferenceEquals(source.Resource, destination.Resource))
                destination.BoundaryStates = source.BoundaryStates;
        }
    }

    private readonly record struct StructureTransition(GraphStructure Previous, GraphStructure Next, GraphEditChanges Changes);

    internal void EnsureFrame(ulong identity)
    {
        EnsureAvailable();
        if (identity == 0 || Volatile.Read(ref _frameToken) != checked((long)identity))
            throw new InvalidOperationException("The render graph frame is no longer active.");
    }

    private void EnsureAvailable()
    {
        ObjectDisposedException.ThrowIf(_disposed || _closing, this);
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
        _closing = true;
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
        foreach (GraphPass pass in _structure.Passes.Rows) pass.CallbackStorage.ClearData();
        _disposed = true;
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
            if (_backend.WaitCpu(completion, Timeout.InfiniteTimeSpan) != WaitStatus.Completed)
                throw new TimeoutException("Graph shutdown could not confirm submitted work completion.");
    }

    private void CollectRangeCompletions(
        Buffer buffer,
        in BufferRange requested,
        List<QueueCompletion> destination)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!ReferenceEquals(buffer.Device, _device))
            throw new ArgumentException("The Buffer belongs to another Device.", nameof(buffer));
        BufferRange range = GraphStructureIndex.ResolveRange(requested, buffer.Info.Size);
        lock (_structureGate)
        {
            CollectFrom(_structure);
            foreach (StructureTransition transition in _deferredRetirements)
            {
                CollectFrom(transition.Previous);
                CollectFrom(transition.Next);
            }
        }
        return;

        void CollectFrom(GraphStructure structure)
        {
            foreach (GraphBuffer graphBuffer in structure.Buffers.Rows)
            {
                if (!ReferenceEquals(graphBuffer.PersistentResource, buffer) &&
                    !ReferenceEquals(graphBuffer.RegisteredResource, buffer))
                    continue;
                foreach (BufferBoundaryState boundary in graphBuffer.BoundaryStates)
                {
                    if (!boundary.ReadyAfter.HasValue) continue;
                    BufferRange endpoint = GraphStructureIndex.ResolveRange(
                        boundary.Range,
                        buffer.Info.Size);
                    if (endpoint.Offset >= range.Offset + range.Size ||
                        range.Offset >= endpoint.Offset + endpoint.Size)
                        continue;
                    AddCompletion(destination, boundary.ReadyAfter.Value);
                }
            }
        }
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
