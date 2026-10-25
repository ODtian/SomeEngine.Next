namespace SomeEngine.RenderGraph;

public sealed partial class RenderGraph : IDisposable
{
    internal void SetParameterInputValue(GraphParameterInput input, in ParameterInputValue value)
    {
        if (input.PersistentValue == value) return;
        input.PersistentValue = value;
        foreach (FrameSlot frame in _frameSlots) frame.ParameterInputChanged(input);
    }

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
    // Transaction work only: new or released uses await accepted-membership reconciliation.
    // A failed queue transfer stays here for CollectCompleted/Dispose to retry.
    private readonly List<GraphTextureUse> _pendingTextureUses = [];
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
    /// <summary>Returns the canonical allocation of an accepted external texture registration.</summary>
    public Texture GetExternalTexture(GraphTextureId texture)
    {
        EnsureAvailable();
        lock (_structureGate)
        {
            if (!texture.Value.IsValid || texture.Value.Owner != Identity ||
                !_structure.Textures.TryGetDenseIndex(texture.Value, out int index))
                throw new ArgumentException("The texture must belong to this graph.", nameof(texture));
            return _structure.Textures.Rows[index].RegisteredResource ??
                throw new InvalidOperationException("The texture is not an external registration.");
        }
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
        {
            if (_editToken == 0) RetirePendingTextureUses();
            _persistentResources.CollectCompleted();
        }
        foreach (FrameSlot slot in _frameSlots)
            slot.CollectCompleted(_backend);
    }

    public QueueCompletion[] GetRangeCompletions(GraphBufferId buffer, in BufferRange range) =>
        GetRangeCompletions(GetPersistentBuffer(buffer), range);

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
    /// Returns the Queue completions that can still access the requested Buffer range.
    /// At most one (the maximum known value) is returned for each registered Queue.
    /// The caller must prevent future frames from declaring access to the range before using the
    /// returned completions as its complete retirement dependency set.
    /// </summary>
    public QueueCompletion[] GetRangeCompletions(Buffer buffer, in BufferRange range)
    {
        var completions = new List<QueueCompletion>(_queues.Length);
        CollectRangeCompletions(buffer, range, completions);
        return completions.ToArray();
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

    internal static bool QueueSupports(GraphPassKind kind, QueueType queueType) => kind switch
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

    internal void CommitEdit(long token, out bool accepted)
    {
        accepted = false;
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
                accepted = true;
                EndEdit(token);
                return;
            }
            if (changes.LabelsOnly)
            {
                CarryFrameValues(previous, staging, changes);
                if (HasActiveFrameExecution()) _deferredRetirements.Add(new StructureTransition(previous, staging, changes));
                _structure = staging;
                accepted = true;
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
            bool frameActive = HasActiveFrameExecution();
            _persistentResources.ApplyStructure(previous, staging, changes, !frameActive);
            index.UpdateResourceIdentities(_structureIndex, changes);
            if (frameActive)
                _deferredRetirements.Add(new StructureTransition(previous, staging, changes));
            _structure = staging;
            accepted = true;
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

    private bool HasActiveFrameExecution()
    {
        long frame = Volatile.Read(ref _frameToken);
        if (frame == 0) return false;
        foreach (FrameSlot slot in _frameSlots)
            if (slot.FrameIdentity == (ulong)frame) return true;
        return false;
    }

    internal void EnsureResourceUseEdit(long token)
    {
        EnsureEdit(token);
        if (HasActiveFrameExecution())
            throw new InvalidOperationException("External resource uses must change before BeginFrame.");
    }

    internal void EnsureTextureUseBoundaries(ReadOnlySpan<TextureBoundaryState> boundaries, bool allowSubmitted)
    {
        foreach (TextureBoundaryState boundary in boundaries)
        {
            if (!boundary.ReadyAfter.HasValue || _backend.IsComplete(boundary.ReadyAfter.Value)) continue;
            QueueCompletion required = boundary.ReadyAfter.Value;
            bool covered = false;
            if (allowSubmitted)
                foreach (FrameSlot slot in _frameSlots)
                    foreach (QueueCompletion completion in slot.Completions)
                        if (ReferenceEquals(completion.Queue, required.Queue) && completion.Value >= required.Value)
                            covered = true;
            if (!covered)
                throw new InvalidOperationException("External texture uses require completed incoming ReadyAfter dependencies.");
        }
    }

    internal void TrackTextureUse(GraphTextureUse use)
    {
        if (!_pendingTextureUses.Contains(use)) _pendingTextureUses.Add(use);
    }

    private bool IsTextureUseAccepted(GraphTextureUse use) =>
        _structure.Textures.TryGetDenseIndex(use.Texture.Value, out int index) &&
        Array.IndexOf(_structure.Textures.Rows[index].ExternalUses, use) >= 0;

    private void RetirePendingTextureUses(bool releaseAccepted = false)
    {
        if (!releaseAccepted)
        {
            int count = _pendingTextureUses.Count;
            int write = count;
            for (int read = count - 1; read >= 0; read--)
            {
                GraphTextureUse use = _pendingTextureUses[read];
                bool accepted;
                try { accepted = IsTextureUseAccepted(use); }
                catch
                {
                    // Restore the completed reverse-removal prefix before propagating failure.
                    int target = read + 1;
                    for (int source = write; source < count; source++)
                    {
                        if (target != source) _pendingTextureUses[target] = _pendingTextureUses[source];
                        target++;
                    }
                    if (target != count) _pendingTextureUses.RemoveRange(target, count - target);
                    throw;
                }
                if (accepted) continue;
                // Keep survivors in their original order without overwriting unread entries.
                --write;
                if (write != read) _pendingTextureUses[write] = use;
            }
            if (write != 0) _pendingTextureUses.RemoveRange(0, write);
        }
        if (_pendingTextureUses.Count == 0) return;
        IDisposable[] keepalives = _pendingTextureUses.Select(use => use.Keepalive!).ToArray();
        // Queue the entire batch before dropping ownership. This does not call user Dispose;
        // the existing allocator removes batches before invoking every disposal callback.
        RetireAfterSubmittedFrames(keepalives);
        foreach (GraphTextureUse use in _pendingTextureUses) use.Keepalive = null;
        _pendingTextureUses.Clear();
    }

    private void EndEdit(long token)
    {
        lock (_structureGate)
        {
            if (Volatile.Read(ref _editToken) != token)
                throw new InvalidOperationException("The render graph edit token is no longer active.");
            try { RetirePendingTextureUses(); }
            finally
            {
                _editSource = null;
                _editStructure = null;
                _editChanges = null;
                Interlocked.Exchange(ref _editToken, 0);
            }
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
            if (previous.Regions.TryGetDenseIndex(identity, out int previousRegionIndex) &&
                next.Regions.TryGetDenseIndex(identity, out int nextRegionIndex))
                next.Regions.Rows[nextRegionIndex].Enabled = previous.Regions.Rows[previousRegionIndex].Enabled;
        foreach (GraphIdentity identity in changes.Passes.Concat(changes.Labels))
            if (previous.Passes.TryGetDenseIndex(identity, out int previousPassIndex) &&
                next.Passes.TryGetDenseIndex(identity, out int nextPassIndex))
                next.Passes.Rows[nextPassIndex].Enabled = previous.Passes.Rows[previousPassIndex].Enabled;
        foreach (GraphIdentity identity in changes.Views)
            if (previous.Views.TryGetDenseIndex(identity, out int previousViewIndex) &&
                next.Views.TryGetDenseIndex(identity, out int nextViewIndex))
                next.Views.Rows[nextViewIndex].CurrentBufferRange =
                    previous.Views.Rows[previousViewIndex].CurrentBufferRange;
        foreach (GraphIdentity identity in changes.Accesses)
            if (previous.Accesses.TryGetDenseIndex(identity, out int previousAccessIndex) &&
                next.Accesses.TryGetDenseIndex(identity, out int nextAccessIndex))
            {
                PassResourceAccess source = previous.Accesses.Rows[previousAccessIndex];
                PassResourceAccess destination = next.Accesses.Rows[nextAccessIndex];
                destination.CurrentBufferRange = source.CurrentBufferRange;
                destination.CurrentTextureRange = source.CurrentTextureRange;
            }
        foreach (GraphIdentity identity in changes.Buffers)
        {
            if (!previous.Buffers.TryGetDenseIndex(identity, out int previousIndex) ||
                !next.Buffers.TryGetDenseIndex(identity, out int nextIndex)) continue;
            GraphBuffer source = previous.Buffers.Rows[previousIndex];
            GraphBuffer destination = next.Buffers.Rows[nextIndex];
            destination.CurrentViewRange = source.CurrentViewRange;
            if (destination.RegisteredResource is null && destination.Ownership == RenderGraphResourceOwnership.CallerOwned)
                destination.BoundResource = source.BoundResource;
            if (ReferenceEquals(source.PersistentResource, destination.PersistentResource) &&
                ReferenceEquals(source.RegisteredResource, destination.RegisteredResource) &&
                ReferenceEquals(source.BoundResource, destination.BoundResource))
                destination.Boundaries = source.Boundaries;
        }
        foreach (GraphIdentity identity in changes.Textures)
        {
            if (!previous.Textures.TryGetDenseIndex(identity, out int previousIndex) ||
                !next.Textures.TryGetDenseIndex(identity, out int nextIndex)) continue;
            GraphTexture source = previous.Textures.Rows[previousIndex];
            GraphTexture destination = next.Textures.Rows[nextIndex];
            if (destination.RegisteredResource is null && destination.Ownership == RenderGraphResourceOwnership.CallerOwned)
                destination.BoundResource = source.BoundResource;
            if (ReferenceEquals(source.PersistentResource, destination.PersistentResource) &&
                ReferenceEquals(source.RegisteredResource, destination.RegisteredResource) &&
                ReferenceEquals(source.BoundResource, destination.BoundResource))
                destination.BoundaryStates = source.BoundaryStates;
        }
        foreach (GraphIdentity identity in changes.QueryPools)
        {
            if (!previous.QueryPools.TryGetDenseIndex(identity, out int previousIndex) ||
                !next.QueryPools.TryGetDenseIndex(identity, out int nextIndex)) continue;
            GraphQueryPool source = previous.QueryPools.Rows[previousIndex];
            GraphQueryPool destination = next.QueryPools.Rows[nextIndex];
            if (ReferenceEquals(source.Resource, destination.Resource))
                destination.BoundaryStates = source.BoundaryStates;
        }
        foreach (GraphIdentity identity in changes.ShaderTables)
        {
            if (!previous.ShaderTables.TryGetDenseIndex(identity, out int previousIndex) ||
                !next.ShaderTables.TryGetDenseIndex(identity, out int nextIndex)) continue;
            GraphRayTracingShaderTable source = previous.ShaderTables.Rows[previousIndex];
            GraphRayTracingShaderTable destination = next.ShaderTables.Rows[nextIndex];
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
        {
            WaitForSubmittedWork();
            foreach (FrameSlot slot in _frameSlots) slot.ResetCompleted();
        }
        _closing = true;
        foreach (GraphTexture texture in _structure.Textures.Rows)
            foreach (GraphTextureUse use in texture.ExternalUses)
                if (use.Keepalive is not null) TrackTextureUse(use);
        RetirePendingTextureUses(releaseAccepted: true);
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
