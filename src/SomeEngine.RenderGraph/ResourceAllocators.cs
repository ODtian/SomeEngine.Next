namespace SomeEngine.RenderGraph;

internal sealed class HeapByteBudget
{
    private readonly object _gate = new();
    private readonly ulong _maximumBytes;
    private ulong _committedBytes;

    internal HeapByteBudget(ulong maximumBytes) => _maximumBytes = maximumBytes;

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
    TransientHeapPage? TransientPage = null,
    int TransientRegion = -1);

internal sealed class PersistentResourceAllocator : IDisposable
{
    private readonly IGraphicsBackend _backend;
    private readonly Device _device;
    private readonly HeapByteBudget _budget;
    private readonly List<PersistentHeapPage> _pages = [];
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
        GraphStructure next)
    {
        int originalPageCount = _pages.Count;
        var createdObjects = new List<IDisposable>();
        var createdPlacements = new List<ResourcePlacement>();
        try
        {
            foreach (GraphBuffer buffer in next.Buffers.Rows)
            {
                if (buffer.Ownership != RenderGraphResourceOwnership.GraphOwned ||
                    buffer.Lifetime != RenderGraphResourceLifetime.Persistent ||
                    buffer.PersistentResource is not null)
                    continue;
                ResourcePlacement placement = Allocate(
                    buffer.Requirements,
                    buffer.MemoryType,
                    buffer.Description.NodePlacement,
                    buffer.Description.Usages.HasFlag(BufferUsages.Shareable));
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
                createdPlacements.Add(placement);
            }

            foreach (GraphTexture texture in next.Textures.Rows)
            {
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
                createdPlacements.Add(placement);
            }

            foreach (GraphView view in next.Views.Rows)
            {
                if (view.PersistentView is not null) continue;
                DeviceResource? resourceView = CreatePersistentView(next, view);
                if (resourceView is null) continue;
                view.PersistentView = resourceView;
                createdObjects.Add(resourceView);
            }
        }
        catch
        {
            foreach (IDisposable value in createdObjects)
                if (value is Resource resource)
                    _allocations.Remove(resource);
            for (int i = createdObjects.Count - 1; i >= 0; i--)
                createdObjects[i].Dispose();
            foreach (ResourcePlacement placement in createdPlacements)
                Release(placement);
            for (int page = _pages.Count - 1; page >= originalPageCount; page--)
            {
                PersistentHeapPage created = _pages[page];
                _budget.Release(created.Heap.Info.Size);
                created.Heap.Dispose();
                _pages.RemoveAt(page);
            }
            throw;
        }

        RetireRemoved(previous, next);
    }

    private DeviceResource? CreatePersistentView(GraphStructure structure, GraphView view)
    {
        switch (view.Kind)
        {
            case GraphViewKind.BufferCbv:
                {
                    Buffer? buffer = ResolvePersistentBuffer(structure, view.Buffer);
                    return buffer is null ? null : _backend.CreateBufferCbv(
                        _device,
                        new BufferCbvDesc(buffer, view.BufferRange, view.Label));
                }
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

    private void RetireRemoved(GraphStructure previous, GraphStructure next)
    {
        var liveResources = new HashSet<DeviceResource>(ReferenceEqualityComparer.Instance);
        foreach (GraphBuffer buffer in next.Buffers.Rows)
            if (buffer.PersistentResource is not null) liveResources.Add(buffer.PersistentResource);
        foreach (GraphTexture texture in next.Textures.Rows)
            if (texture.PersistentResource is not null) liveResources.Add(texture.PersistentResource);
        foreach (GraphView view in next.Views.Rows)
            if (view.PersistentView is not null) liveResources.Add(view.PersistentView);

        foreach (GraphView view in previous.Views.Rows)
            if (view.PersistentView is not null && !liveResources.Contains(view.PersistentView))
                Retire(view.PersistentView, []);
        foreach (GraphBuffer buffer in previous.Buffers.Rows)
        {
            if (buffer.PersistentResource is null || liveResources.Contains(buffer.PersistentResource))
                continue;
            Retire(buffer.PersistentResource, Collect(buffer.BoundaryStates));
        }
        foreach (GraphTexture texture in previous.Textures.Rows)
        {
            if (texture.PersistentResource is null || liveResources.Contains(texture.PersistentResource))
                continue;
            Retire(texture.PersistentResource, Collect(texture.BoundaryStates));
        }
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
            if (page.Key != key) continue;
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
    }

    public void Dispose()
    {
        foreach (PendingResourceRetirement retirement in _retirements)
            retirement.Value.Dispose();
        _retirements.Clear();
        foreach (PersistentHeapPage page in _pages)
        {
            _budget.Release(page.Heap.Info.Size);
            page.Heap.Dispose();
        }
        _pages.Clear();
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
    private readonly Dictionary<TransientResourceCacheKey, CachedTransientResource> _resourceCache = [];
    private readonly HashSet<Resource> _cachedResources = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<GraphIdentity, CachedTransientView> _viewCache = [];
    private readonly Dictionary<int, PlacementSignature> _previousPlacements = [];
    private readonly Dictionary<int, PlacementSignature> _currentPlacements = [];
    private ulong _structureVersion;
    private FrameSubmissionMode _submissionMode;
    private RenderGraphDebugOptions _debug;
    private bool _placementHistoryReady;

    internal FrameTransientResourceAllocator(IGraphicsBackend backend, Device device, HeapByteBudget budget)
    {
        _backend = backend;
        _device = device;
        _budget = budget;
    }

    internal void Reset(
        ulong structureVersion,
        FrameSubmissionMode submissionMode,
        RenderGraphDebugOptions debug)
    {
        bool invalidated = _structureVersion != 0 &&
            (_structureVersion != structureVersion ||
             _submissionMode != submissionMode ||
             _debug != debug);
        if (invalidated)
        {
            ClearCache();
            _previousPlacements.Clear();
            _currentPlacements.Clear();
            _placementHistoryReady = false;
        }
        _structureVersion = structureVersion;
        _submissionMode = submissionMode;
        _debug = debug;
        _currentPlacements.Clear();
        foreach (TransientHeapPage page in _pages) page.Reset();
    }

    internal bool CanCacheResources => _placementHistoryReady;

    internal void CompletePlacement()
    {
        bool samePlacement = _previousPlacements.Count != 0 &&
            _previousPlacements.Count == _currentPlacements.Count;
        if (samePlacement)
        {
            foreach ((int resourceIndex, PlacementSignature placement) in _currentPlacements)
            {
                if (_previousPlacements.TryGetValue(resourceIndex, out PlacementSignature previous) &&
                    previous == placement)
                    continue;
                samePlacement = false;
                break;
            }
        }

        _placementHistoryReady = samePlacement;
        _previousPlacements.Clear();
        foreach ((int resourceIndex, PlacementSignature placement) in _currentPlacements)
            _previousPlacements.Add(resourceIndex, placement);
    }

    internal void InvalidatePlacementHistory()
    {
        ClearCache();
        _previousPlacements.Clear();
        _currentPlacements.Clear();
        _placementHistoryReady = false;
    }

    internal Resource? FindCachedResource(
        GraphAccessTargetKind kind,
        in GraphIdentity identity,
        in ResourcePlacement placement)
    {
        var key = new TransientResourceCacheKey(kind, identity);
        if (!_resourceCache.TryGetValue(key, out CachedTransientResource? cached) ||
            !ReferenceEquals(cached.Heap, placement.Heap) ||
            cached.Offset != placement.Offset)
            return null;
        return cached.Resource;
    }

    internal BufferBoundaryState[]? FindCachedBufferStates(
        in GraphIdentity identity,
        Resource resource)
    {
        var key = new TransientResourceCacheKey(GraphAccessTargetKind.Buffer, identity);
        return _resourceCache.TryGetValue(key, out CachedTransientResource? cached) &&
            ReferenceEquals(cached.Resource, resource)
                ? cached.BufferStates
                : null;
    }

    internal TextureBoundaryState[]? FindCachedTextureStates(
        in GraphIdentity identity,
        Resource resource)
    {
        var key = new TransientResourceCacheKey(GraphAccessTargetKind.Texture, identity);
        return _resourceCache.TryGetValue(key, out CachedTransientResource? cached) &&
            ReferenceEquals(cached.Resource, resource)
                ? cached.TextureStates
                : null;
    }

    internal void StoreCachedBufferStates(
        in GraphIdentity identity,
        Resource resource,
        ReadOnlySpan<BufferBoundaryState> states)
    {
        var key = new TransientResourceCacheKey(GraphAccessTargetKind.Buffer, identity);
        if (!_resourceCache.TryGetValue(key, out CachedTransientResource? cached) ||
            !ReferenceEquals(cached.Resource, resource))
            return;
        var result = new BufferBoundaryState[states.Length];
        for (int index = 0; index < states.Length; index++)
            result[index] = states[index] with { Contents = ResourceContentState.Undefined };
        cached.BufferStates = result;
    }

    internal void StoreCachedTextureStates(
        in GraphIdentity identity,
        Resource resource,
        ReadOnlySpan<TextureBoundaryState> states)
    {
        var key = new TransientResourceCacheKey(GraphAccessTargetKind.Texture, identity);
        if (!_resourceCache.TryGetValue(key, out CachedTransientResource? cached) ||
            !ReferenceEquals(cached.Resource, resource))
            return;
        var result = new TextureBoundaryState[states.Length];
        for (int index = 0; index < states.Length; index++)
            result[index] = states[index] with { Contents = ResourceContentState.Undefined };
        cached.TextureStates = result;
    }

    internal bool CacheResource(
        GraphAccessTargetKind kind,
        in GraphIdentity identity,
        in ResourcePlacement placement,
        Resource resource)
    {
        var key = new TransientResourceCacheKey(kind, identity);
        if (_resourceCache.ContainsKey(key))
            throw new InvalidOperationException("A stable transient Resource changed placement without a structural invalidation.");
        _resourceCache.Add(key, new CachedTransientResource(resource, placement.Heap, placement.Offset));
        _cachedResources.Add(resource);
        return true;
    }

    internal bool IsCached(Resource resource) => _cachedResources.Contains(resource);

    internal DeviceResource? FindCachedView(
        in GraphIdentity identity,
        Resource primary,
        Resource? secondary)
    {
        if (!_viewCache.TryGetValue(identity, out CachedTransientView cached) ||
            !ReferenceEquals(cached.Primary, primary) ||
            !ReferenceEquals(cached.Secondary, secondary))
            return null;
        return cached.View;
    }

    internal bool CacheView(
        in GraphIdentity identity,
        DeviceResource view,
        Resource primary,
        Resource? secondary)
    {
        if (_viewCache.ContainsKey(identity)) return false;
        _viewCache.Add(identity, new CachedTransientView(view, primary, secondary));
        return true;
    }

    internal ResourcePlacement Place(
        in MemoryRequirements requirements,
        MemoryType memoryType,
        in ResourceNodePlacement nodePlacement,
        int[] beginFrontier,
        int[] endFrontier,
        int resourceIndex)
    {
        uint creationMask = nodePlacement.CreationNodeMask == 0 ? 1u : nodePlacement.CreationNodeMask;
        uint visibleMask = nodePlacement.VisibleNodeMask == 0 ? creationMask : nodePlacement.VisibleNodeMask;
        var key = new HeapCompatibilityKey(memoryType, requirements.CompatibleHeapFlags, creationMask, visibleMask);
        TransientHeapPage? best = null;
        int region = -1;
        int priority = int.MaxValue;
        ulong waste = ulong.MaxValue;
        foreach (TransientHeapPage page in _pages)
        {
            if (page.Key != key) continue;
            if (!page.TryFind(requirements.Size, requirements.Alignment, beginFrontier, resourceIndex,
                    out int candidate, out ulong candidateWaste, out int candidatePriority))
                continue;
            if (candidatePriority > priority ||
                (candidatePriority == priority && candidateWaste >= waste))
                continue;
            best = page;
            region = candidate;
            priority = candidatePriority;
            waste = candidateWaste;
        }

        if (best is null)
        {
            ulong minimum = memoryType == MemoryType.DeviceLocal ? 128UL << 20 : 32UL << 20;
            ulong size = Math.Max(minimum, NextPowerOfTwo(requirements.Size));
            size = AlignUp(size, requirements.Alignment);
            if (!_budget.TryReserve(size))
                throw new GraphicsException(GraphicsError.OutOfMemory, "The transient RenderGraph heap budget is exhausted.");
            Heap heap;
            try
            {
                heap = _backend.CreateHeap(_device, new HeapDesc(
                    size, requirements.Alignment, memoryType, requirements.CompatibleHeapFlags,
                    creationMask, visibleMask, "RenderGraph transient heap"));
            }
            catch
            {
                _budget.Release(size);
                throw;
            }
            best = new TransientHeapPage(heap, key);
            _pages.Add(best);
            _ = best.TryFind(
                requirements.Size,
                requirements.Alignment,
                beginFrontier,
                resourceIndex,
                out region,
                out _,
                out _);
        }

        (ulong offset, ulong sizeValue, int chosen, Resource? predecessor) = best.Assign(
            region,
            requirements.Size,
            requirements.Alignment,
            endFrontier,
            resourceIndex);
        var signature = new PlacementSignature(best.Heap, offset, sizeValue);
        if (_placementHistoryReady &&
            (!_previousPlacements.TryGetValue(resourceIndex, out PlacementSignature expected) ||
             expected != signature))
        {
            throw new InvalidOperationException(
                "A stable transient placement changed without a RenderGraph structural invalidation.");
        }
        _currentPlacements.Add(resourceIndex, signature);
        return new ResourcePlacement(
            best.Heap,
            offset,
            sizeValue,
            predecessor,
            TransientPage: best,
            TransientRegion: chosen);
    }

    internal void SetResource(in ResourcePlacement placement, Resource resource)
    {
        if (placement.TransientPage is null || placement.TransientRegion < 0)
            throw new ArgumentException("The placement is not transient.", nameof(placement));
        placement.TransientPage.SetResource(placement.TransientRegion, resource);
    }

    public void Dispose()
    {
        foreach (CachedTransientView cached in _viewCache.Values)
            cached.View.Dispose();
        _viewCache.Clear();
        foreach (TransientHeapPage page in _pages)
        {
            page.DisposeResources(_cachedResources);
            _budget.Release(page.Heap.Info.Size);
            page.Heap.Dispose();
        }
        _pages.Clear();
        foreach (CachedTransientResource cached in _resourceCache.Values)
            cached.Resource.Dispose();
        _resourceCache.Clear();
        _cachedResources.Clear();
    }

    private void ClearCache()
    {
        foreach (CachedTransientView cached in _viewCache.Values)
            cached.View.Dispose();
        _viewCache.Clear();
        foreach (TransientHeapPage page in _pages)
            page.DisposeResources(_cachedResources);
        foreach (CachedTransientResource cached in _resourceCache.Values)
            cached.Resource.Dispose();
        _resourceCache.Clear();
        _cachedResources.Clear();
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

    private readonly record struct TransientResourceCacheKey(
        GraphAccessTargetKind Kind,
        GraphIdentity Identity);

    private readonly record struct PlacementSignature(Heap Heap, ulong Offset, ulong Size);

    private sealed class CachedTransientResource
    {
        internal CachedTransientResource(Resource resource, Heap heap, ulong offset)
        {
            Resource = resource;
            Heap = heap;
            Offset = offset;
        }

        internal Resource Resource { get; }
        internal Heap Heap { get; }
        internal ulong Offset { get; }
        internal BufferBoundaryState[]? BufferStates { get; set; }
        internal TextureBoundaryState[]? TextureStates { get; set; }
    }

    private readonly record struct CachedTransientView(
        DeviceResource View,
        Resource Primary,
        Resource? Secondary);
}

internal sealed class TransientHeapPage
{
    private sealed class AllocationRegion
    {
        internal ulong Offset;
        internal ulong Size;
        internal int[] EndFrontier = [];
        internal int ResourceIndex = -1;
        internal int LastResourceIndex = -1;
        internal Resource? Resource;
    }

    private readonly List<AllocationRegion> _regions = [];
    private ulong _tail;

    internal TransientHeapPage(Heap heap, in HeapCompatibilityKey key)
    {
        Heap = heap;
        Key = key;
    }

    internal Heap Heap { get; }
    internal HeapCompatibilityKey Key { get; }

    internal void Reset()
    {
        foreach (AllocationRegion region in _regions)
        {
            region.ResourceIndex = -1;
        }
    }

    internal bool TryFind(
        ulong size,
        ulong alignment,
        int[] beginFrontier,
        int resourceIndex,
        out int region,
        out ulong waste,
        out int priority)
    {
        region = -1;
        waste = ulong.MaxValue;
        priority = int.MaxValue;
        for (int i = 0; i < _regions.Count; i++)
        {
            AllocationRegion candidate = _regions[i];
            if ((candidate.ResourceIndex >= 0 && !Before(candidate.EndFrontier, beginFrontier)) ||
                candidate.LastResourceIndex != resourceIndex ||
                candidate.Offset % alignment != 0 || candidate.Size < size)
                continue;
            ulong candidateWaste = candidate.Size - size;
            if (candidateWaste >= waste) continue;
            region = i;
            waste = candidateWaste;
        }
        if (region >= 0)
        {
            priority = 0;
            return true;
        }

        ulong offset = AlignUp(_tail, alignment);
        if (offset <= Heap.Info.Size && size <= Heap.Info.Size - offset)
        {
            region = -2;
            waste = Heap.Info.Size - offset - size;
            priority = 1;
            return true;
        }

        for (int i = 0; i < _regions.Count; i++)
        {
            AllocationRegion candidate = _regions[i];
            if ((candidate.ResourceIndex >= 0 && !Before(candidate.EndFrontier, beginFrontier)) ||
                candidate.Offset % alignment != 0 || candidate.Size < size)
                continue;
            ulong candidateWaste = candidate.Size - size;
            if (candidateWaste >= waste) continue;
            region = i;
            waste = candidateWaste;
        }
        if (region < 0)
            return false;
        priority = 2;
        return true;
    }

    internal (ulong Offset, ulong Size, int RegionIndex, Resource? Predecessor) Assign(
        int region,
        ulong size,
        ulong alignment,
        int[] endFrontier,
        int resourceIndex)
    {
        AllocationRegion selected;
        if (region == -2)
        {
            ulong offset = AlignUp(_tail, alignment);
            selected = new AllocationRegion { Offset = offset, Size = size };
            _regions.Add(selected);
            region = _regions.Count - 1;
            _tail = offset + size;
        }
        else
        {
            selected = _regions[region];
        }
        Resource? predecessor = selected.Resource;
        selected.Resource = null;
        if (selected.EndFrontier.Length != endFrontier.Length)
            selected.EndFrontier = new int[endFrontier.Length];
        Array.Copy(endFrontier, selected.EndFrontier, endFrontier.Length);
        selected.ResourceIndex = resourceIndex;
        selected.LastResourceIndex = resourceIndex;
        return (selected.Offset, selected.Size, region, predecessor);
    }

    internal void SetResource(int region, Resource resource)
    {
        AllocationRegion selected = _regions[region];
        if (selected.Resource is not null)
            throw new InvalidOperationException("The transient region already owns a Resource.");
        selected.Resource = resource;
    }

    internal void DisposeResources(HashSet<Resource> excluded)
    {
        foreach (AllocationRegion region in _regions)
        {
            if (region.Resource is not null && !excluded.Contains(region.Resource))
                region.Resource.Dispose();
            region.Resource = null;
            region.ResourceIndex = -1;
        }
    }

    private static bool Before(int[] end, int[] begin)
    {
        if (end.Length == 0) return true;
        if (end.Length != begin.Length) return false;
        for (int lane = 0; lane < end.Length; lane++)
            if (end[lane] > begin[lane]) return false;
        return true;
    }

    private static ulong AlignUp(ulong value, ulong alignment)
    {
        ulong mask = alignment - 1;
        return checked((value + mask) & ~mask);
    }
}

