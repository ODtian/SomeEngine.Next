namespace SomeEngine.RenderGraph;

internal sealed class HeapByteBudget
{
    private readonly object _gate = new();
    private readonly ulong _maximumBytes;
    private ulong _committedBytes;

    internal HeapByteBudget(ulong maximumBytes) => _maximumBytes = maximumBytes;
    internal ulong CommittedBytes { get { lock (_gate) return _committedBytes; } }

    internal bool TryReserve(ulong size)
    {
        lock (_gate)
        {
            if (size > _maximumBytes - _committedBytes) return false;
            _committedBytes += size;
            return true;
        }
    }

    internal void Release(ulong size)
    {
        lock (_gate)
        {
            if (size > _committedBytes)
                throw new InvalidOperationException("The RenderGraph memory budget is inconsistent.");
            _committedBytes -= size;
        }
    }
}

internal readonly record struct HeapCompatibilityKey(
    MemoryType MemoryType,
    HeapFlags Flags,
    uint CreationNodeMask,
    uint VisibleNodeMask);

internal readonly record struct ResourcePlacement(
    Heap Heap,
    ulong Offset,
    ulong Size,
    Resource? AliasingPredecessor = null,
    PersistentHeapPage? PersistentPage = null,
    int PersistentRange = -1,
    bool Aliases = false);

internal sealed class PersistentResourceAllocator : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly HeapByteBudget _budget;
    private readonly List<PersistentHeapPage> _pages = [];
    private readonly HashSet<PersistentHeapPage> _discardedPages = [];
    private readonly List<PendingResourceRetirement> _retirements = [];
    private readonly Dictionary<Resource, ResourcePlacement> _allocations =
        new(ReferenceEqualityComparer.Instance);

    internal PersistentResourceAllocator(
        IGraphicsBackend backend,
        Device device,
        HeapByteBudget budget)
    {
        _backend = backend;
        _device = device;
        _budget = budget;
    }

    internal void ApplyStructure(
        GraphStructure previous,
        GraphStructure next,
        GraphEditChanges changes,
        bool retireRemoved)
    {
        int previousPageCount = _pages.Count;
        var createdObjects = new List<IDisposable>();
        var createdPlacements = new List<ResourcePlacement>();
        try
        {
            foreach (GraphIdentity id in changes.Buffers)
            {
                if (!next.Buffers.Contains(id)) continue;
                GraphBuffer buffer = next.Buffers.GetMutable(id);
                if (buffer.Ownership != RenderGraphResourceOwnership.GraphOwned ||
                    buffer.Lifetime != RenderGraphResourceLifetime.Persistent ||
                    buffer.PersistentResource is not null)
                    continue;
                ResourcePlacement placement = Allocate(
                    buffer.Requirements,
                    buffer.MemoryType,
                    buffer.Description.NodePlacement,
                    buffer.Description.Usages.HasFlag(BufferUsages.Shareable));
                createdPlacements.Add(placement);
                Buffer resource = _backend.CreatePlacedBuffer(
                    _device,
                    placement.Heap,
                    placement.Offset,
                    buffer.Description);
                buffer.PersistentResource = resource;
                buffer.BoundaryStates =
                [
                    new BufferBoundaryState(
                        new BufferRange(0, buffer.Description.Size),
                        resource.InitialSync,
                        resource.InitialAccess,
                        ResourceContentState.Undefined,
                        null,
                        null),
                ];
                _allocations.Add(resource, placement);
                createdObjects.Add(resource);
            }

            foreach (GraphIdentity id in changes.Textures)
            {
                if (!next.Textures.Contains(id)) continue;
                GraphTexture texture = next.Textures.GetMutable(id);
                if (texture.Ownership != RenderGraphResourceOwnership.GraphOwned ||
                    texture.Lifetime != RenderGraphResourceLifetime.Persistent ||
                    texture.PersistentResource is not null)
                    continue;
                TextureDesc description = texture.BorrowDescription();
                ResourcePlacement placement = Allocate(
                    texture.Requirements,
                    MemoryType.DeviceLocal,
                    texture.NodePlacement,
                    texture.Usages.HasFlag(TextureUsages.Shareable));
                createdPlacements.Add(placement);
                Texture resource = _backend.CreatePlacedTexture(
                    _device,
                    placement.Heap,
                    placement.Offset,
                    description);
                texture.PersistentResource = resource;
                texture.BoundaryStates =
                [
                    new TextureBoundaryState(
                        new TextureSubresourceRange(
                            0,
                            texture.MipLevelCount,
                            0,
                            texture.ArrayLayerCount,
                            TextureFormatRules.Aspects(texture.Format)),
                        resource.InitialSync,
                        resource.InitialAccess,
                        resource.InitialLayout,
                        ResourceContentState.Undefined,
                        null,
                        null),
                ];
                _allocations.Add(resource, placement);
                createdObjects.Add(resource);
            }

            foreach (GraphIdentity id in changes.Views)
            {
                if (!next.Views.Contains(id)) continue;
                GraphView view = next.Views.GetMutable(id);
                if (view.PersistentView is not null) continue;
                DeviceResource? resourceView = CreatePersistentView(next, view);
                if (resourceView is null) continue;
                view.PersistentView = resourceView;
                createdObjects.Add(resourceView);
            }
        }
        catch (Exception failure)
        {
            for (int index = previousPageCount; index < _pages.Count; index++)
                _discardedPages.Add(_pages[index]);
            // The unpublished declaration must be retryable before cleanup calls user/backend
            // code. Disposal failures retain their objects and placements in the usual queue.
            var created = new HashSet<IDisposable>(createdObjects, ReferenceEqualityComparer.Instance);
            foreach (GraphIdentity id in changes.Views)
                if (next.Views.Contains(id) && next.Views.Get(id).PersistentView is { } view && created.Contains(view))
                    next.Views.GetMutable(id).PersistentView = null;
            foreach (GraphIdentity id in changes.Buffers)
                if (next.Buffers.Contains(id) && next.Buffers.Get(id).PersistentResource is { } buffer && created.Contains(buffer))
                {
                    GraphBuffer value = next.Buffers.GetMutable(id);
                    value.PersistentResource = null;
                    value.BoundaryStates = [];
                }
            foreach (GraphIdentity id in changes.Textures)
                if (next.Textures.Contains(id) && next.Textures.Get(id).PersistentResource is { } texture && created.Contains(texture))
                {
                    GraphTexture value = next.Textures.GetMutable(id);
                    value.PersistentResource = null;
                    value.BoundaryStates = [];
                }
            foreach (IDisposable value in createdObjects)
            {
                if (value is Resource resource && _allocations.TryGetValue(resource, out ResourcePlacement placement))
                    createdPlacements.Remove(placement);
                Retire(value, []);
            }
            // A placement whose native creation threw never acquired a resource.
            foreach (ResourcePlacement placement in createdPlacements) Release(placement);
            try { CollectCompleted(); }
            catch (Exception cleanupFailure) { throw new AggregateException(failure, cleanupFailure); }
            throw;
        }

        if (retireRemoved) RetireRemoved(previous, next, changes);
    }

    private DeviceResource? CreatePersistentView(GraphStructure structure, GraphView view)
    {
        if (view.BufferInput is not null || view.TextureInput is not null) return null;
        switch (view.Kind)
        {
            case GraphViewKind.BufferSrv:
                {
                    Buffer? buffer = ResolvePersistentBuffer(structure, view.Buffer);
                    return buffer is null ? null : _backend.CreateBufferSrv(
                        _device,
                        new BufferSrvDesc(buffer, view.BufferRange, view.BufferFormat,
                            view.StructureStride, view.Label));
                }
            case GraphViewKind.BufferUav:
                {
                    Buffer? buffer = ResolvePersistentBuffer(structure, view.Buffer);
                    if (buffer is null) return null;
                    Buffer? counter = view.AdditionalBuffer.IsValid
                        ? ResolvePersistentBuffer(structure, view.AdditionalBuffer)
                        : null;
                    if (view.AdditionalBuffer.IsValid && counter is null) return null;
                    return _backend.CreateBufferUav(
                        _device,
                        new BufferUavDesc(buffer, view.BufferRange, view.BufferFormat,
                            view.StructureStride, counter, view.CounterOffset, view.Label));
                }
            case GraphViewKind.TextureSrv:
                {
                    Texture? texture = ResolvePersistentTexture(structure, view.Texture);
                    return texture is null ? null : _backend.CreateTextureSrv(
                        _device,
                        new TextureSrvDesc(texture, view.TextureRange, view.TextureFormat,
                            view.Dimension, view.Label));
                }
            case GraphViewKind.TextureUav:
                {
                    Texture? texture = ResolvePersistentTexture(structure, view.Texture);
                    return texture is null ? null : _backend.CreateTextureUav(
                        _device,
                        new TextureUavDesc(texture, view.TextureRange, view.TextureFormat,
                            view.Dimension, view.Label));
                }
            case GraphViewKind.ColorAttachment:
                {
                    Texture? texture = ResolvePersistentTexture(structure, view.Texture);
                    return texture is null ? null : _backend.CreateColorAttachmentView(
                        _device,
                        new ColorAttachmentViewDesc(texture, view.TextureRange, view.TextureFormat,
                            view.Dimension, view.Label));
                }
            case GraphViewKind.DepthStencil:
                {
                    Texture? texture = ResolvePersistentTexture(structure, view.Texture);
                    return texture is null ? null : _backend.CreateDepthStencilView(
                        _device,
                        new DepthStencilViewDesc(texture, view.TextureRange, view.TextureFormat,
                            view.Dimension, view.ReadOnlyDepth, view.ReadOnlyStencil, view.Label));
                }
            default:
                return null;
        }
    }

    private static Buffer? ResolvePersistentBuffer(GraphStructure structure, in GraphIdentity id)
    {
        GraphBuffer buffer = structure.Buffers.Get(id);
        return buffer.PersistentResource ?? buffer.RegisteredResource;
    }

    private static Texture? ResolvePersistentTexture(GraphStructure structure, in GraphIdentity id)
    {
        GraphTexture texture = structure.Textures.Get(id);
        return texture.PersistentResource ?? texture.RegisteredResource;
    }

    internal void RetireRemoved(GraphStructure previous, GraphStructure next, GraphEditChanges changes)
    {
        foreach (GraphIdentity id in changes.Views)
        {
            if (!previous.Views.Contains(id)) continue;
            GraphView view = previous.Views.Get(id);
            if (view.PersistentView is not null &&
                (!next.Views.Contains(id) || !ReferenceEquals(view.PersistentView, next.Views.Get(id).PersistentView)))
                Retire(view.PersistentView, CollectViewCompletions(previous, view));
        }
        foreach (GraphIdentity id in changes.Buffers)
        {
            if (!previous.Buffers.Contains(id)) continue;
            GraphBuffer buffer = previous.Buffers.Get(id);
            if (buffer.PersistentResource is null ||
                (next.Buffers.Contains(id) && ReferenceEquals(buffer.PersistentResource, next.Buffers.Get(id).PersistentResource)))
                continue;
            Retire(buffer.PersistentResource, Collect(buffer.BoundaryStates));
        }
        foreach (GraphIdentity id in changes.Textures)
        {
            if (!previous.Textures.Contains(id)) continue;
            GraphTexture texture = previous.Textures.Get(id);
            if (texture.PersistentResource is null ||
                (next.Textures.Contains(id) && ReferenceEquals(texture.PersistentResource, next.Textures.Get(id).PersistentResource)))
                continue;
            Retire(texture.PersistentResource, Collect(texture.BoundaryStates));
        }
    }

    private static QueueCompletion[] CollectViewCompletions(
        GraphStructure structure,
        GraphView view)
    {
        var result = new List<QueueCompletion>();
        if (view.Buffer.IsValid && structure.Buffers.Contains(view.Buffer))
            foreach (QueueCompletion completion in Collect(structure.Buffers.Get(view.Buffer).BoundaryStates))
                AddCompletion(result, completion);
        if (view.AdditionalBuffer.IsValid && structure.Buffers.Contains(view.AdditionalBuffer))
            foreach (QueueCompletion completion in Collect(structure.Buffers.Get(view.AdditionalBuffer).BoundaryStates))
                AddCompletion(result, completion);
        if (view.Texture.IsValid && structure.Textures.Contains(view.Texture))
            foreach (QueueCompletion completion in Collect(structure.Textures.Get(view.Texture).BoundaryStates))
                AddCompletion(result, completion);
        return result.ToArray();
    }

    private static QueueCompletion[] Collect(BufferBoundaryState[] boundaryStates)
    {
        var result = new List<QueueCompletion>();
        foreach (BufferBoundaryState endpoint in boundaryStates)
            if (endpoint.ReadyAfter.HasValue)
                AddCompletion(result, endpoint.ReadyAfter.Value);
        return result.ToArray();
    }

    private static QueueCompletion[] Collect(TextureBoundaryState[] boundaryStates)
    {
        var result = new List<QueueCompletion>();
        foreach (TextureBoundaryState endpoint in boundaryStates)
            if (endpoint.ReadyAfter.HasValue)
                AddCompletion(result, endpoint.ReadyAfter.Value);
        return result.ToArray();
    }

    private static void AddCompletion(List<QueueCompletion> values, QueueCompletion value)
    {
        for (int i = 0; i < values.Count; i++)
        {
            if (!ReferenceEquals(values[i].Queue, value.Queue)) continue;
            if (value.Value > values[i].Value) values[i] = value;
            return;
        }
        values.Add(value);
    }

    internal ResourcePlacement Allocate(
        in MemoryRequirements requirements,
        MemoryType memoryType,
        in ResourceNodePlacement placement,
        bool shareable)
    {
        uint creationMask = placement.CreationNodeMask == 0 ? 1u : placement.CreationNodeMask;
        uint visibleMask = placement.VisibleNodeMask == 0 ? creationMask : placement.VisibleNodeMask;
        HeapFlags flags = requirements.CompatibleHeapFlags;
        if (shareable) flags |= HeapFlags.Shareable;
        var key = new HeapCompatibilityKey(memoryType, flags, creationMask, visibleMask);

        PersistentHeapPage? bestPage = null;
        int bestRange = -1;
        ulong bestWaste = ulong.MaxValue;
        foreach (PersistentHeapPage page in _pages)
        {
            if (page.Key != key || _discardedPages.Contains(page)) continue;
            if (!page.TryFind(requirements.Size, requirements.Alignment, out int range, out ulong waste))
                continue;
            if (waste >= bestWaste) continue;
            bestWaste = waste;
            bestPage = page;
            bestRange = range;
        }

        if (bestPage is null)
        {
            ulong minimum = memoryType == MemoryType.DeviceLocal ? 128UL << 20 : 32UL << 20;
            ulong size = AlignUp(Math.Max(minimum, NextPowerOfTwo(requirements.Size)), requirements.Alignment);
            if (!_budget.TryReserve(size))
                throw new GraphicsException(GraphicsError.OutOfMemory, "The RenderGraph heap budget is exhausted.");
            Heap heap;
            try
            {
                heap = _backend.CreateHeap(_device, new HeapDesc(
                    size,
                    requirements.Alignment,
                    memoryType,
                    flags,
                    creationMask,
                    visibleMask,
                    "RenderGraph persistent heap"));
            }
            catch
            {
                _budget.Release(size);
                throw;
            }
            bestPage = new PersistentHeapPage(heap, key);
            _pages.Add(bestPage);
            _ = bestPage.TryFind(requirements.Size, requirements.Alignment, out bestRange, out _);
        }

        (ulong offset, ulong sizeValue) = bestPage.Allocate(bestRange, requirements.Size, requirements.Alignment);
        return new ResourcePlacement(
            bestPage.Heap,
            offset,
            sizeValue,
            PersistentPage: bestPage,
            PersistentRange: bestRange);
    }

    internal void Retire(IDisposable value, ReadOnlySpan<QueueCompletion> completions)
    {
        ResourcePlacement? placement = value is Resource resource && _allocations.Remove(resource, out ResourcePlacement found)
            ? found
            : null;
        _retirements.Add(new PendingResourceRetirement(value, placement, completions.ToArray()));
    }

    private void Release(in ResourcePlacement placement)
    {
        if (placement.PersistentPage is not null)
            placement.PersistentPage.Free(placement.Offset, placement.Size);
    }

    internal void CollectCompleted()
    {
        for (int i = _retirements.Count - 1; i >= 0; i--)
        {
            PendingResourceRetirement retirement = _retirements[i];
            bool ready = true;
            foreach (QueueCompletion completion in retirement.Completions)
                if (!_backend.IsComplete(completion)) { ready = false; break; }
            if (!ready) continue;
            retirement.Value.Dispose();
            if (retirement.Placement.HasValue) Release(retirement.Placement.Value);
            _retirements.RemoveAt(i);
        }
        foreach (PersistentHeapPage page in _discardedPages.ToArray())
        {
            if (!page.IsEmpty) continue;
            page.Heap.Dispose();
            _budget.Release(page.Heap.Info.Size);
            _pages.Remove(page);
            _discardedPages.Remove(page);
        }
    }

    public void Dispose()
    {
        for (int index = _retirements.Count - 1; index >= 0; index--)
        {
            PendingResourceRetirement retirement = _retirements[index];
            retirement.Value.Dispose();
            if (retirement.Placement.HasValue) Release(retirement.Placement.Value);
            _retirements.RemoveAt(index);
        }
        for (int index = _pages.Count - 1; index >= 0; index--)
        {
            PersistentHeapPage page = _pages[index];
            page.Heap.Dispose();
            _budget.Release(page.Heap.Info.Size);
            _pages.RemoveAt(index);
            _discardedPages.Remove(page);
        }
        _allocations.Clear();
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        ulong mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }

    private static ulong NextPowerOfTwo(ulong value)
    {
        if (value <= 1) return 1;
        value--;
        value |= value >> 1;
        value |= value >> 2;
        value |= value >> 4;
        value |= value >> 8;
        value |= value >> 16;
        value |= value >> 32;
        return checked(value + 1);
    }

    private readonly record struct PendingResourceRetirement(
        IDisposable Value,
        ResourcePlacement? Placement,
        QueueCompletion[] Completions);
}

internal sealed class PersistentHeapPage
{
    private readonly List<(ulong Offset, ulong Size)> _free = [];

    internal PersistentHeapPage(Heap heap, in HeapCompatibilityKey key)
    {
        Heap = heap;
        Key = key;
        _free.Add((0, heap.Info.Size));
    }

    internal Heap Heap { get; }
    internal HeapCompatibilityKey Key { get; }
    internal bool IsEmpty => _free.Count == 1 && _free[0] == (0UL, Heap.Info.Size);

    internal bool TryFind(ulong size, ulong alignment, out int range, out ulong waste)
    {
        range = -1;
        waste = ulong.MaxValue;
        for (int i = 0; i < _free.Count; i++)
        {
            (ulong offset, ulong available) = _free[i];
            ulong aligned = AlignUp(offset, alignment);
            ulong prefix = aligned - offset;
            if (prefix > available || size > available - prefix) continue;
            ulong candidateWaste = available - prefix - size;
            if (candidateWaste >= waste) continue;
            waste = candidateWaste;
            range = i;
        }
        return range >= 0;
    }

    internal (ulong Offset, ulong Size) Allocate(int range, ulong size, ulong alignment)
    {
        (ulong offset, ulong available) = _free[range];
        ulong aligned = AlignUp(offset, alignment);
        ulong prefix = aligned - offset;
        ulong suffix = available - prefix - size;
        _free.RemoveAt(range);
        if (prefix != 0) _free.Insert(range++, (offset, prefix));
        if (suffix != 0) _free.Insert(range, (aligned + size, suffix));
        return (aligned, size);
    }

    internal void Free(ulong offset, ulong size)
    {
        int index = 0;
        while (index < _free.Count && _free[index].Offset < offset) index++;
        _free.Insert(index, (offset, size));
        if (index > 0 && _free[index - 1].Offset + _free[index - 1].Size == _free[index].Offset)
        {
            (ulong leftOffset, ulong leftSize) = _free[index - 1];
            _free[index - 1] = (leftOffset, leftSize + _free[index].Size);
            _free.RemoveAt(index--);
        }
        if (index + 1 < _free.Count && _free[index].Offset + _free[index].Size == _free[index + 1].Offset)
        {
            _free[index] = (_free[index].Offset, _free[index].Size + _free[index + 1].Size);
            _free.RemoveAt(index + 1);
        }
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        ulong mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }
}

internal sealed class FrameTransientResourceAllocator : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly HeapByteBudget _budget;
    private readonly List<TransientHeapPage> _pages = [];
    private readonly List<TransientHeapPage> _newPages = [];
    private readonly Dictionary<GraphIdentity, TransientAllocation> _allocations = [];
    private readonly HashSet<Resource> _resources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<ViewDescription, CachedView> _views = [];
    private readonly Dictionary<Resource, HashSet<ViewDescription>> _resourceViews = new(ReferenceEqualityComparer.Instance);
    private readonly List<Resource> _releasedViewResources = [];
    private readonly HashSet<TransientHeapPage.Region> _changedRegions = [];
    private readonly HashSet<GraphIdentity> _changedLifetimes = [];
    private readonly List<GraphIdentity> _invalidated = [], _changedPlacements = [];
    private readonly List<TransientAllocation> _activeAllocations = [];
    private readonly List<TransientHeapPage.Region> _regionWork = [];
    private FrameSubmissionMode _submissionMode;
    private RenderGraphDebugOptions _debug;
    private bool _initialized;
    private bool _cleanupRequired;

    internal FrameTransientResourceAllocator(IGraphicsBackend backend, Device device, HeapByteBudget budget)
    {
        _backend = backend; _device = device; _budget = budget;
    }

    internal bool Reset(FrameSubmissionMode submissionMode, RenderGraphDebugOptions debug)
    {
        bool compacted = _cleanupRequired || _initialized && (_submissionMode != submissionMode || _debug != debug);
        if (compacted) Compact();
        _submissionMode = submissionMode; _debug = debug; _initialized = true;
        ResetUploads();
        _releasedViewResources.Clear();
        foreach (Resource resource in _resourceViews.Keys)
            if (resource.IsDisposed) _releasedViewResources.Add(resource);
        foreach (Resource resource in _releasedViewResources) RemoveResourceViews(resource);
        _releasedViewResources.Clear();
        return compacted;
    }

    internal void UpdateLifetime(GraphIdentity identity, int[] begin, int[] end, Queue firstQueue, Queue lastQueue)
    {
        if (!_allocations.TryGetValue(identity, out TransientAllocation? allocation)) return;
        TransientAllocationRequest old = allocation.Request;
        if (allocation.Active && old.Begin.AsSpan().SequenceEqual(begin) && old.End.AsSpan().SequenceEqual(end) &&
            ReferenceEquals(old.FirstQueue, firstQueue) && ReferenceEquals(old.LastQueue, lastQueue)) return;
        allocation.Active = true;
        allocation.Request = old with { Begin = begin.ToArray(), End = end.ToArray(), FirstQueue = firstQueue, LastQueue = lastQueue };
        _changedLifetimes.Add(identity);
        _changedRegions.Add(allocation.Region);
    }

    internal void Deactivate(GraphIdentity identity)
    {
        if (!_allocations.TryGetValue(identity, out TransientAllocation? allocation) || !allocation.Active) return;
        allocation.Active = false;
        _changedRegions.Add(allocation.Region);
    }

    internal IReadOnlyList<GraphIdentity> ReconcileLifetimes()
    {
        _invalidated.Clear();
        _regionWork.Clear(); _regionWork.AddRange(_changedRegions);
        foreach (TransientHeapPage.Region region in _regionWork)
        {
            for (int left = 0; left < region.Allocations.Count; left++)
            {
                TransientAllocation before = region.Allocations[left];
                if (!before.Active) continue;
                for (int right = left + 1; right < region.Allocations.Count; right++)
                {
                    TransientAllocation after = region.Allocations[right];
                    if (!after.Active || TransientHeapPage.CanShare(before.Request, after.Request)) continue;
                    bool moveBefore = _changedLifetimes.Contains(before.Request.Identity) && !_changedLifetimes.Contains(after.Request.Identity);
                    GraphIdentity removed = moveBefore ? before.Request.Identity : after.Request.Identity;
                    _invalidated.Add(removed);
                    RemoveResource(default, removed);
                    if (moveBefore) { left--; break; }
                    right--;
                }
            }
        }
        _changedLifetimes.Clear();
        return _invalidated;
    }

    internal void Allocate(List<TransientAllocationRequest> requests)
    {
        if (requests.Count == 0) return;
        requests.Sort(static (left, right) => right.Requirements.Size.CompareTo(left.Requirements.Size));
        foreach (TransientAllocationRequest request in requests)
        {
            if (_allocations.ContainsKey(request.Identity)) continue;
            uint creation = request.NodePlacement.CreationNodeMask == 0 ? 1u : request.NodePlacement.CreationNodeMask;
            uint visible = request.NodePlacement.VisibleNodeMask == 0 ? creation : request.NodePlacement.VisibleNodeMask;
            HeapCompatibilityKey key = new(request.MemoryType, request.Requirements.CompatibleHeapFlags, creation, visible);
            TransientHeapPage? best = null;
            TransientHeapPage.Region? region = null;
            ulong waste = ulong.MaxValue;
            foreach (TransientHeapPage page in _pages)
            {
                if (page.Key != key || page.Alignment != request.Requirements.Alignment) continue;
                TransientHeapPage.Region? candidate = page.Find(request, out ulong remaining);
                if (candidate is not null && remaining < waste)
                {
                    best = page; region = candidate; waste = remaining;
                }
            }
            if (best is null)
                foreach (TransientHeapPage page in _pages)
                    if (page.Key == key && page.Alignment == request.Requirements.Alignment && page.CanAppend(request))
                    { best = page; break; }
            if (best is null)
            {
                best = new TransientHeapPage(key, request.Requirements.Alignment);
                _pages.Add(best);
                _newPages.Add(best);
            }
            region = best.Allocate(region, request);
            TransientAllocation allocation = new()
            {
                Request = request with { Begin = request.Begin.ToArray(), End = request.End.ToArray() },
                Page = best, Region = region,
            };
            region.Allocations.Add(allocation);
            _allocations.Add(request.Identity, allocation);
            _changedRegions.Add(region);
        }
        foreach (TransientHeapPage page in _newPages)
        {
            if (page.Heap is not null) continue;
            ulong size = TransientHeapPage.AlignUp(page.Extent, page.Alignment);
            if (!_budget.TryReserve(size))
                throw new GraphicsException(GraphicsError.OutOfMemory, "The transient RenderGraph heap budget is exhausted.");
            try
            {
                page.Heap = _backend.CreateHeap(_device, new HeapDesc(size, page.Alignment, page.Key.MemoryType,
                    page.Key.Flags, page.Key.CreationNodeMask, page.Key.VisibleNodeMask, "RenderGraph transient heap"));
            }
            catch { _budget.Release(size); throw; }
        }
        _newPages.Clear();
    }

    internal ResourcePlacement GetPlacement(GraphIdentity identity)
    {
        TransientAllocation allocation = _allocations[identity];
        return new ResourcePlacement(allocation.Page.Heap!, allocation.Region.Offset, allocation.Request.Requirements.Size,
            allocation.Predecessor, Aliases: allocation.Aliases);
    }

    internal IReadOnlyList<GraphIdentity> CompletePlacement()
    {
        _changedPlacements.Clear();
        foreach (TransientHeapPage.Region region in _changedRegions)
        {
            _activeAllocations.Clear();
            foreach (TransientAllocation allocation in region.Allocations)
                if (allocation.Active && allocation.Resource is not null) _activeAllocations.Add(allocation);
            _activeAllocations.Sort(static (left, right) =>
                TransientHeapPage.Before(left.Request.End, right.Request.Begin) ? -1 : 1);
            for (int index = 0; index < _activeAllocations.Count; index++)
            {
                TransientAllocation allocation = _activeAllocations[index];
                // The preceding execution has completed before this frame's heap is reused.
                // Its first resource only needs activation; before-side deactivation belongs
                // to resources whose last access occurs earlier in this execution.
                Resource? predecessor = index > 0 ? _activeAllocations[index - 1].Resource : null;
                bool aliases = _activeAllocations.Count > 1 || region.Used && !ReferenceEquals(region.LastUsed, allocation.Resource);
                if (ReferenceEquals(allocation.Predecessor, predecessor) && allocation.Aliases == aliases) continue;
                allocation.Predecessor = predecessor; allocation.Aliases = aliases;
                _changedPlacements.Add(allocation.Request.Identity);
            }
        }
        _changedRegions.Clear();
        return _changedPlacements;
    }

    internal void SetResource(GraphIdentity identity, Resource resource)
    {
        TransientAllocation allocation = _allocations[identity];
        allocation.Resource = resource; _resources.Add(resource); _changedRegions.Add(allocation.Region);
    }

    internal bool IsCached(Resource resource) => _resources.Contains(resource);

    internal BufferBoundaryState[]? FindCachedBufferStates(in GraphIdentity identity, Resource resource) =>
        _allocations.TryGetValue(identity, out TransientAllocation? allocation) && ReferenceEquals(allocation.Resource, resource) ? allocation.BufferStates : null;

    internal TextureBoundaryState[]? FindCachedTextureStates(in GraphIdentity identity, Resource resource) =>
        _allocations.TryGetValue(identity, out TransientAllocation? allocation) && ReferenceEquals(allocation.Resource, resource) ? allocation.TextureStates : null;

    internal void StoreCachedBufferStates(in GraphIdentity identity, Resource resource, BufferBoundaryState[] states)
    {
        if (!_allocations.TryGetValue(identity, out TransientAllocation? allocation) || !ReferenceEquals(allocation.Resource, resource)) return;
        allocation.BufferStates = states; Used(allocation);
    }

    internal void StoreCachedTextureStates(in GraphIdentity identity, Resource resource, TextureBoundaryState[] states)
    {
        if (!_allocations.TryGetValue(identity, out TransientAllocation? allocation) || !ReferenceEquals(allocation.Resource, resource)) return;
        allocation.TextureStates = states; Used(allocation);
    }

    private void Used(TransientAllocation allocation)
    {
        TransientHeapPage.Region region = allocation.Region;
        region.Used = true; region.LastUsed = allocation.Resource;
        if (allocation.Aliases && region.Allocations.Count == 1) _changedRegions.Add(region);
    }

    // A view belongs to its actual resources and description. A changing graph row or
    // swapchain image binding does not end any other image's descriptors.
    internal DeviceResource? FindCachedView(in FrameView view, Resource primary, Resource? secondary) =>
        _views.TryGetValue(DescribeView(view, primary, secondary), out CachedView cached) ? cached.View : null;

    internal void CacheView(in FrameView definition, DeviceResource view, Resource primary, Resource? secondary)
    {
        ViewDescription key = DescribeView(definition, primary, secondary);
        _views.Add(key, new(view, primary, secondary));
        Add(primary);
        if (secondary is not null) Add(secondary);
        void Add(Resource resource)
        {
            if (!_resourceViews.TryGetValue(resource, out HashSet<ViewDescription>? views))
                _resourceViews.Add(resource, views = []);
            views.Add(key);
        }
    }

    private void RemoveResourceViews(Resource resource)
    {
        if (!_resourceViews.TryGetValue(resource, out HashSet<ViewDescription>? keys)) return;
        foreach (ViewDescription key in keys.ToArray())
        {
            if (_views.TryGetValue(key, out CachedView view))
            {
                ReleaseObject(view.View);
                _views.Remove(key);
                RemoveReference(view.Primary);
                if (view.Secondary is not null) RemoveReference(view.Secondary);
            }
            keys.Remove(key);
            void RemoveReference(Resource other)
            {
                if (ReferenceEquals(other, resource) || !_resourceViews.TryGetValue(other, out var views)) return;
                views.Remove(key);
                if (views.Count == 0) _resourceViews.Remove(other);
            }
        }
        _resourceViews.Remove(resource);
    }

    private void ReleaseObject(IDisposable value)
    {
        try { value.Dispose(); }
        catch { _cleanupRequired = true; throw; }
    }

    private static ViewDescription DescribeView(in FrameView view, Resource primary, Resource? secondary) =>
        new(view.Kind, primary, secondary,
            primary is Buffer buffer ? GraphStructureIndex.ResolveRange(view.BufferRange, buffer.Info.Size) : default,
            view.BufferFormat, view.StructureStride, view.CounterOffset, view.TextureRange,
            view.TextureFormat, view.Dimension, view.ReadOnlyDepth, view.ReadOnlyStencil);

    internal void RemoveResource(GraphAccessTargetKind kind, GraphIdentity identity)
    {
        if (!_allocations.TryGetValue(identity, out TransientAllocation? allocation)) return;
        if (allocation.Resource is { } resource)
        {
            RemoveResourceViews(resource);
            ReleaseObject(resource);
            _resources.Remove(resource);
        }
        _allocations.Remove(identity);
        _changedRegions.Add(allocation.Region);
        allocation.Page.Release(allocation);
        if (allocation.Page.AllocationCount == 0)
        {
            if (allocation.Page.Heap is { } heap) { ReleaseObject(heap); _budget.Release(heap.Info.Size); }
            _pages.Remove(allocation.Page); _newPages.Remove(allocation.Page);
        }
    }

    internal ulong CapacityBytes => _pages.Aggregate(0UL, static (sum, page) => checked(sum + (page.Heap?.Info.Size ?? 0)));
    internal ulong OccupiedBytes => _pages.Aggregate(0UL, static (sum, page) => checked(sum + page.Regions.Values.Aggregate(0UL,
        static (used, region) => checked(used + (region.Allocations.Count == 0 ? 0 : region.Allocations.Max(static allocation => allocation.Request.Requirements.Size))))));

    internal void Compact()
    {
        _cleanupRequired = true;
        foreach (ViewDescription key in _views.Keys.ToArray())
        {
            _views[key].View.Dispose();
            _views.Remove(key);
        }
        _resourceViews.Clear();
        DisposeUploads();
        while (_resources.Count != 0)
        {
            Resource resource = _resources.First();
            ReleaseObject(resource);
            _resources.Remove(resource);
        }
        _allocations.Clear();
        for (int index = _pages.Count - 1; index >= 0; index--)
        {
            TransientHeapPage page = _pages[index];
            if (page.Heap is { } heap)
            {
                heap.Dispose();
                _budget.Release(heap.Info.Size);
            }
            _pages.RemoveAt(index);
            _newPages.Remove(page);
        }
        _changedRegions.Clear(); _changedLifetimes.Clear(); _changedPlacements.Clear();
        _cleanupRequired = false;
    }

    public void Dispose() => Compact();
    private readonly record struct CachedView(DeviceResource View, Resource Primary, Resource? Secondary);
    private readonly record struct ViewDescription(GraphViewKind Kind, Resource Primary, Resource? Secondary,
        BufferRange BufferRange, Format? BufferFormat, uint StructureStride, ulong CounterOffset,
        TextureSubresourceRange TextureRange, Format TextureFormat, TextureViewDimension Dimension,
        bool ReadOnlyDepth, bool ReadOnlyStencil);

    private readonly List<UploadPage> _uploadPages = [];
    private const BufferUsages UploadReadUsages = BufferUsages.Constant | BufferUsages.ShaderRead;

    internal sealed class UploadPage
    {
        internal required Buffer Buffer;
        internal required MappedBuffer Mapping;
        internal required ulong CommittedBytes;
        internal required BufferBoundaryState[] BoundaryStates;
        internal GraphBufferId GraphBuffer;
        internal ulong Used;
    }

    private void ResetUploads()
    {
        foreach (UploadPage page in _uploadPages)
        {
            page.Used = 0;
            page.GraphBuffer = default;
        }
    }

    internal (UploadPage UploadPage, BufferRange Range) WriteUpload(ReadOnlySpan<byte> data, BufferUsages usages, uint elementAlignment)
    {
        if (_cleanupRequired) throw new InvalidOperationException("Transient storage cleanup must finish before another upload.");
        DevelopmentCheck.Require(!data.IsEmpty, "Upload data cannot be empty.");
        DevelopmentCheck.Require(usages != 0 && (usages & ~(UploadReadUsages | BufferUsages.CopySource)) == 0,
            "Upload pages support GPU reads only.");

        bool staging = (usages & UploadReadUsages) == 0;
        BufferUsages pageUsages = staging ? BufferUsages.CopySource : UploadReadUsages | BufferUsages.CopySource;
        DeviceLimits limits = _device.Capabilities.Limits;
        ulong alignment = LeastCommonMultiple(4, Math.Max(1u, elementAlignment));
        if ((usages & BufferUsages.ShaderRead) != 0)
            alignment = LeastCommonMultiple(alignment, Math.Max(1u, limits.ShaderBufferAlignment));
        if (staging)
            alignment = LeastCommonMultiple(alignment, Math.Max(1u, limits.TextureDataPlacementAlignment));
        if ((usages & BufferUsages.Constant) != 0)
            alignment = LeastCommonMultiple(alignment, Math.Max(1u, limits.ConstantBufferAlignment));
        ulong size = checked((ulong)data.Length);
        if ((usages & BufferUsages.Constant) != 0)
            size = AlignUp(size, Math.Max(1u, limits.ConstantBufferAlignment));

        UploadPage? target = null;
        ulong offset = 0;
        foreach (UploadPage page in _uploadPages)
        {
            if (page.Buffer.Info.Usages != pageUsages) continue;
            offset = AlignUp(page.Used, alignment);
            if (offset <= page.Buffer.Info.Size && size <= page.Buffer.Info.Size - offset)
            {
                target = page;
                break;
            }
        }
        if (target is null)
        {
            ulong capacity = Math.Max(staging ? 4UL << 20 : 1UL << 20, AlignUp(size, 65536));
            DevelopmentCheck.Require(capacity <= int.MaxValue, "An upload page must fit in a mapped Span.");
            var description = new BufferDesc(capacity, pageUsages, staging ? "Frame staging page" : "Frame parameter page");
            MemoryRequirements requirements = _backend.GetBufferMemoryRequirements(_device, description, MemoryType.Upload);
            if (!_budget.TryReserve(requirements.Size))
                throw new InvalidOperationException("The render graph upload memory budget is exhausted.");
            Buffer buffer;
            try { buffer = _backend.CreateBuffer(_device, description, MemoryType.Upload); }
            catch { _budget.Release(requirements.Size); throw; }
            target = new UploadPage
            {
                Buffer = buffer,
                Mapping = default,
                CommittedBytes = requirements.Size,
                BoundaryStates = [new BufferBoundaryState(new BufferRange(0, capacity),
                    buffer.InitialSync, buffer.InitialAccess, ResourceContentState.Defined)],
            };
            _uploadPages.Add(target);
            try
            {
                target.Mapping = _backend.Map(buffer, MapType.Write, new BufferRange(0, capacity));
                target.Mapping.Bytes.Clear();
            }
            catch (Exception failure)
            {
                try { DisposeUpload(_uploadPages.Count - 1); }
                catch (Exception cleanupFailure)
                {
                    _cleanupRequired = true;
                    throw new AggregateException(failure, cleanupFailure);
                }
                throw;
            }
            offset = 0;
        }
        Span<byte> destination = target.Mapping.Bytes.Slice(checked((int)offset), checked((int)size));
        data.CopyTo(destination);
        destination[data.Length..].Clear();
        target.Used = checked(offset + size);
        return (target, new BufferRange(offset, size));
    }

    internal void FlushUploads()
    {
        if (_cleanupRequired) throw new InvalidOperationException("Transient storage cleanup must finish before execution.");
        foreach (UploadPage page in _uploadPages)
            if (page.Used != 0) page.Mapping.Flush(new BufferRange(0, page.Used));
    }

    private void DisposeUploads()
    {
        for (int index = _uploadPages.Count - 1; index >= 0; index--) DisposeUpload(index);
    }

    private void DisposeUpload(int index)
    {
        UploadPage page = _uploadPages[index];
        page.Mapping.Dispose();
        page.Buffer.Dispose();
        _budget.Release(page.CommittedBytes);
        _uploadPages.RemoveAt(index);
    }

    private static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) / alignment * alignment);

    private static ulong LeastCommonMultiple(ulong left, ulong right)
    {
        ulong a = left, b = right;
        while (b != 0) (a, b) = (b, a % b);
        return checked(left / a * right);
    }
}

internal readonly record struct TransientAllocationRequest(GraphIdentity Identity, MemoryRequirements Requirements,
    MemoryType MemoryType, ResourceNodePlacement NodePlacement, int[] Begin, int[] End, Queue FirstQueue, Queue LastQueue);

internal sealed class TransientAllocation
{
    internal required TransientAllocationRequest Request;
    internal required TransientHeapPage Page;
    internal required TransientHeapPage.Region Region;
    internal Resource? Resource, Predecessor;
    internal BufferBoundaryState[]? BufferStates;
    internal TextureBoundaryState[]? TextureStates;
    internal bool Active = true, Aliases;
}

internal sealed class TransientHeapPage
{
    internal sealed class Region(ulong offset, ulong size)
    {
        internal readonly ulong Offset = offset;
        internal ulong Size = size;
        internal readonly List<TransientAllocation> Allocations = [];
        internal Resource? LastUsed;
        internal bool Used;
    }

    internal readonly HeapCompatibilityKey Key;
    internal readonly ulong Alignment;
    internal Heap? Heap;
    internal readonly SortedList<ulong, Region> Regions = [];
    internal ulong Extent;
    internal int AllocationCount;

    internal TransientHeapPage(HeapCompatibilityKey key, ulong alignment)
    {
        Key = key;
        Alignment = alignment;
    }

    internal Region? Find(in TransientAllocationRequest request, out ulong waste)
    {
        Region? best = null;
        waste = ulong.MaxValue;
        foreach (Region region in Regions.Values)
        {
            ulong offset = region.Allocations.Count == 0 ? AlignUp(region.Offset, request.Requirements.Alignment) : region.Offset;
            if (offset % request.Requirements.Alignment != 0 || offset - region.Offset > region.Size ||
                request.Requirements.Size > region.Size - (offset - region.Offset)) continue;
            bool compatible = true;
            foreach (TransientAllocation allocation in region.Allocations)
                if (allocation.Active && !CanShare(allocation.Request, request))
                {
                    compatible = false;
                    break;
                }
            ulong remaining = region.Size - request.Requirements.Size;
            if (!compatible || remaining >= waste) continue;
            best = region;
            waste = remaining;
        }
        return best;
    }

    internal bool CanAppend(in TransientAllocationRequest request)
    {
        ulong offset = AlignUp(Extent, request.Requirements.Alignment);
        return Heap is null || offset <= Heap.Info.Size && request.Requirements.Size <= Heap.Info.Size - offset;
    }

    internal Region Allocate(Region? region, in TransientAllocationRequest request)
    {
        AllocationCount++;
        ulong size = request.Requirements.Size;
        if (region is null)
        {
            ulong offset = AlignUp(Extent, request.Requirements.Alignment);
            if (offset > Extent) Regions.Add(Extent, new Region(Extent, offset - Extent));
            region = new Region(offset, size);
            Regions.Add(offset, region);
            Extent = checked(offset + size);
        }
        else if (region.Allocations.Count == 0)
        {
            ulong offset = AlignUp(region.Offset, request.Requirements.Alignment);
            ulong end = region.Offset + region.Size;
            Regions.Remove(region.Offset);
            if (offset > region.Offset) Regions.Add(region.Offset, new Region(region.Offset, offset - region.Offset) { Used = region.Used });
            Region selected = new(offset, size) { Used = region.Used, LastUsed = region.LastUsed };
            Regions.Add(offset, selected);
            if (offset + size < end) Regions.Add(offset + size, new Region(offset + size, end - offset - size) { Used = region.Used });
            region = selected;
        }
        return region;
    }

    internal void Release(TransientAllocation allocation)
    {
        Region region = allocation.Region;
        region.Allocations.Remove(allocation);
        AllocationCount--;
        if (ReferenceEquals(region.LastUsed, allocation.Resource)) region.LastUsed = null;
        if (region.Allocations.Count != 0) return;
        int index = Regions.IndexOfKey(region.Offset);
        if (index > 0 && Regions.Values[index - 1].Allocations.Count == 0)
        {
            Region previous = Regions.Values[index - 1];
            previous.Size += region.Size;
            previous.Used |= region.Used;
            previous.LastUsed = null;
            Regions.RemoveAt(index);
            region = previous;
            index--;
        }
        if (index + 1 < Regions.Count && Regions.Values[index + 1].Allocations.Count == 0)
        {
            Region next = Regions.Values[index + 1];
            region.Size += next.Size;
            region.Used |= next.Used;
            region.LastUsed = null;
            Regions.RemoveAt(index + 1);
        }
    }

    internal static bool Before(int[] end, int[] begin)
    {
        if (end.Length != begin.Length) return false;
        for (int lane = 0; lane < end.Length; lane++) if (end[lane] > begin[lane]) return false;
        return true;
    }

    internal static bool CanShare(in TransientAllocationRequest left, in TransientAllocationRequest right) =>
        // Uploads are populated before GPU execution; readbacks are consumed after it.
        // Their host access intervals span the entire execution, even when GPU uses do not.
        left.MemoryType == MemoryType.DeviceLocal && right.MemoryType == MemoryType.DeviceLocal &&
        (Before(left.End, right.Begin) && ReferenceEquals(left.LastQueue, right.FirstQueue) ||
         Before(right.End, left.Begin) && ReferenceEquals(right.LastQueue, left.FirstQueue));

    internal static ulong AlignUp(ulong value, ulong alignment) => checked((value + alignment - 1) & ~(alignment - 1));
}
