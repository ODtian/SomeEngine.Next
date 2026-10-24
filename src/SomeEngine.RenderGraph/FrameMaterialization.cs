namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private int[][] _resourceBeginFrontiers = [];
    private int[][] _resourceEndFrontiers = [];
    private readonly HashSet<int> _materializeBuffers = [], _materializeTextures = [], _materializeViews = [];

    private void MaterializationChanged((GraphAccessTargetKind Kind, int Index) key, bool updateViewDescriptions = true)
    {
        if (_resourceAccessLists.TryGetValue(key, out List<int>? commandAccesses))
            foreach (int access in commandAccesses) _commandChanges.Add(_accesses[access].PassIndex);
        ChangeResourceParameters(key);
        GraphIdentity identity;
        if (key.Kind == GraphAccessTargetKind.Buffer && key.Index < _buffers.Count)
        {
            _materializeBuffers.Add(key.Index);
            identity = _buffers[key.Index].Identity;
        }
        else if (key.Kind == GraphAccessTargetKind.Texture && key.Index < _textures.Count)
        {
            _materializeTextures.Add(key.Index);
            identity = _textures[key.Index].Identity;
        }
        else return;
        if (_resourceViewRows.TryGetValue(key, out HashSet<int>? rows))
            foreach (int index in rows) InvalidateView(index);
        foreach (GraphIdentity view in _frame.StructureIndex.ResourceViews.GetValueOrDefault(identity, []))
            if (_viewIndices.TryGetValue(view, out int index)) InvalidateView(index);
        if (key.Kind == GraphAccessTargetKind.Texture &&
            _resourceAccessLists.TryGetValue(key, out List<int>? accesses))
            foreach (int access in accesses) InvalidateRasterRendering(_accesses[access].PassIndex);

        void InvalidateView(int index)
        {
            _materializeViews.Add(index);
            ref FrameView view = ref CollectionsMarshal.AsSpan(_views)[index];
            view.View = null;
            if (updateViewDescriptions && view.Definition is not null) _rowChanges.Views.Add(view.Identity);
        }
    }

    private void MaterializeMissingResource((GraphAccessTargetKind Kind, int Index) key)
    {
        // Access and liveness changes do not change existing resource views. Newly active
        // resources without backing memory still need allocation and dependent views.
        if ((key.Kind == GraphAccessTargetKind.Buffer && (uint)key.Index < (uint)_buffers.Count &&
             _buffers[key.Index].Resource is null) ||
            (key.Kind == GraphAccessTargetKind.Texture && (uint)key.Index < (uint)_textures.Count &&
             _textures[key.Index].Resource is null))
            MaterializationChanged(key, updateViewDescriptions: false);
    }

    private void Materialize()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.Materialize);
        FrameTransientResourceAllocator memory = _frame.Slot.TransientResources
            ?? throw new InvalidOperationException("The frame slot has no transient resource allocator.");
        int resourceCount = checked(_buffers.Count + _textures.Count);
        EnsureCapacity(ref _resourceBeginFrontiers, resourceCount);
        EnsureCapacity(ref _resourceEndFrontiers, resourceCount);
        UpdatePlacements(memory);

        foreach (int bufferIndex in _materializeBuffers)
        {
            if (bufferIndex >= _buffers.Count) continue;
            FrameBuffer buffer = _buffers[bufferIndex];
            if (buffer.LastPass < 0) continue;
            if (buffer.Resource is not null)
            {
                _buffers[bufferIndex] = buffer;
                continue;
            }
            if (buffer.Ownership == RenderGraphResourceOwnership.CallerOwned)
                throw new InvalidOperationException("An external Buffer was not bound.");
            if (buffer.Lifetime == RenderGraphResourceLifetime.Persistent)
                throw new InvalidOperationException("A persistent Buffer was not materialized by the structural commit.");

            buffer.Placement = memory.GetPlacement(buffer.AllocationIdentity);
            Buffer resource = _frame.Backend.CreatePlacedBuffer(_frame.Graph.Device,
                buffer.Placement.Heap, buffer.Placement.Offset, buffer.Description);
            buffer.Resource = resource;
            memory.SetResource(buffer.AllocationIdentity, resource);
            _buffers[bufferIndex] = buffer;
            _resourceRows[resource] = (GraphAccessTargetKind.Buffer, bufferIndex);
            ChangeResourceParameters((GraphAccessTargetKind.Buffer, bufferIndex));
        }

        foreach (int textureIndex in _materializeTextures)
        {
            if (textureIndex >= _textures.Count) continue;
            FrameTexture texture = _textures[textureIndex];
            if (texture.LastPass < 0 || texture.Resource is not null) continue;
            if (texture.Ownership == RenderGraphResourceOwnership.CallerOwned)
                throw new InvalidOperationException("An external Texture was not bound.");
            if (texture.Lifetime == RenderGraphResourceLifetime.Persistent)
                throw new InvalidOperationException("A persistent Texture was not materialized by the structural commit.");

            TextureDesc description = texture.BorrowDescription();
            texture.Placement = memory.GetPlacement(texture.AllocationIdentity);
            Texture resource = _frame.Backend.CreatePlacedTexture(_frame.Graph.Device,
                texture.Placement.Heap, texture.Placement.Offset, description);
            texture.Resource = resource;
            memory.SetResource(texture.AllocationIdentity, resource);
            _textures[textureIndex] = texture;
            _resourceRows[resource] = (GraphAccessTargetKind.Texture, textureIndex);
            ChangeResourceParameters((GraphAccessTargetKind.Texture, textureIndex));
        }

        foreach (int viewIndex in _materializeViews)
        {
            if (viewIndex >= _views.Count) continue;
            FrameView view = _views[viewIndex];
            if (view.Kind == GraphViewKind.BufferCbv)
            {
                ChangeViewParameters(viewIndex);
                continue;
            }
            if (view.View is not null) continue;
            if (!TryResolveViewResources(view, out Resource? primary, out Resource? secondary)) continue;
            DeviceResource? materialized = memory.FindCachedView(view, primary!, secondary);
            if (materialized is null)
            {
                materialized = CreateView(view);
                if (materialized is null) continue;
                memory.CacheView(view, materialized, primary!, secondary);
            }
            view.View = materialized;
            _views[viewIndex] = view;
            ChangeViewParameters(viewIndex);
        }

        _frame.Backend.PublishDescriptors(_frame.Graph.Device);
        foreach (GraphIdentity identity in memory.CompletePlacement())
        {
            if (!_allocationRows.TryGetValue(identity, out var key)) continue;
            ResourcePlacement placement = memory.GetPlacement(identity);
            if (key.Kind == GraphAccessTargetKind.Buffer) CollectionsMarshal.AsSpan(_buffers)[key.Index].Placement = placement;
            else CollectionsMarshal.AsSpan(_textures)[key.Index].Placement = placement;
            ChangeSynchronization(key);
        }
        _materializeBuffers.Clear(); _materializeTextures.Clear(); _materializeViews.Clear();
    }

    private DeviceResource? CreateView(in FrameView view)
    {
        switch (view.Kind)
        {
            case GraphViewKind.BufferSrv:
                {
                    Buffer buffer = _buffers[view.BufferIndex].Resource!;
                    BufferRange range = GraphStructureIndex.ResolveRange(view.BufferRange, buffer.Info.Size);
                    return _frame.Backend.CreateBufferSrv(
                        _frame.Graph.Device,
                        new BufferSrvDesc(buffer, range, view.BufferFormat,
                            view.StructureStride, view.Label));
                }
            case GraphViewKind.BufferUav:
                {
                    Buffer buffer = _buffers[view.BufferIndex].Resource!;
                    BufferRange range = GraphStructureIndex.ResolveRange(view.BufferRange, buffer.Info.Size);
                    Buffer? counter = view.AdditionalBuffer.IsValid
                        ? _buffers[view.AdditionalBufferIndex].Resource
                        : null;
                    return _frame.Backend.CreateBufferUav(
                        _frame.Graph.Device,
                        new BufferUavDesc(buffer, range, view.BufferFormat,
                            view.StructureStride, counter, view.CounterOffset, view.Label));
                }
            case GraphViewKind.TextureSrv:
                {
                    Texture texture = _textures[view.TextureIndex].Resource!;
                    return _frame.Backend.CreateTextureSrv(
                        _frame.Graph.Device,
                        new TextureSrvDesc(texture, view.TextureRange,
                            view.TextureFormat, view.Dimension, view.Label));
                }
            case GraphViewKind.TextureUav:
                {
                    Texture texture = _textures[view.TextureIndex].Resource!;
                    return _frame.Backend.CreateTextureUav(
                        _frame.Graph.Device,
                        new TextureUavDesc(texture, view.TextureRange,
                            view.TextureFormat, view.Dimension, view.Label));
                }
            case GraphViewKind.ColorAttachment:
                {
                    Texture texture = _textures[view.TextureIndex].Resource!;
                    return _frame.Backend.CreateColorAttachmentView(
                        _frame.Graph.Device,
                        new ColorAttachmentViewDesc(texture, view.TextureRange,
                            view.TextureFormat, view.Dimension, view.Label));
                }
            case GraphViewKind.DepthStencil:
                {
                    Texture texture = _textures[view.TextureIndex].Resource!;
                    return _frame.Backend.CreateDepthStencilView(
                        _frame.Graph.Device,
                        new DepthStencilViewDesc(texture, view.TextureRange,
                            view.TextureFormat, view.Dimension,
                            view.ReadOnlyDepth, view.ReadOnlyStencil, view.Label));
                }
            default:
                return null;
        }
    }

    private bool TryResolveViewResources(
        in FrameView view,
        out Resource? primary,
        out Resource? secondary)
    {
        secondary = null;
        switch (view.Kind)
        {
            case GraphViewKind.BufferCbv:
            case GraphViewKind.BufferSrv:
            case GraphViewKind.BufferUav:
                primary = _buffers[view.BufferIndex].Resource;
                if (view.AdditionalBufferIndex >= 0)
                    secondary = _buffers[view.AdditionalBufferIndex].Resource;
                return primary is not null;
            case GraphViewKind.TextureSrv:
            case GraphViewKind.TextureUav:
            case GraphViewKind.ColorAttachment:
            case GraphViewKind.DepthStencil:
                primary = _textures[view.TextureIndex].Resource;
                return primary is not null;
            default:
                primary = null;
                return false;
        }
    }

    private void ResolveResourceFrontiers(
        GraphAccessTargetKind kind,
        int resourceIndex,
        int frontierIndex,
        out int[] begin,
        out int[] end)
    {
        begin = PrepareFrontier(_resourceBeginFrontiers[frontierIndex], _queueLaneCount);
        end = PrepareFrontier(_resourceEndFrontiers[frontierIndex], _queueLaneCount);
        _resourceBeginFrontiers[frontierIndex] = begin;
        _resourceEndFrontiers[frontierIndex] = end;
        Array.Fill(begin, int.MaxValue);
        Array.Clear(end);
        bool used = false;
        foreach (int accessIndex in CollectResourceAccesses(kind, resourceIndex, enabledOnly: false))
        {
            FrameResourceAccess access = _accesses[accessIndex];
            if (!_live[access.PassIndex] || access.TargetKind != kind ||
                access.ResourceIndex != resourceIndex)
                continue;
            used = true;
            int[] start = _startFrontiers[access.PassIndex];
            int[] finish = _endFrontiers[access.PassIndex];
            for (int lane = 0; lane < _queueLaneCount; lane++)
            {
                if (start[lane] < begin[lane]) begin[lane] = start[lane];
                if (finish[lane] > end[lane]) end[lane] = finish[lane];
            }
        }
        if (!used) throw new InvalidOperationException("A transient resource has no live use.");
        for (int lane = 0; lane < begin.Length; lane++)
            if (begin[lane] == int.MaxValue) begin[lane] = 0;
    }

    private readonly Dictionary<GraphIdentity, (GraphAccessTargetKind Kind, int Index)> _allocationRows = [];
    private readonly Dictionary<Resource, (GraphAccessTargetKind Kind, int Index)> _resourceRows = new(ReferenceEqualityComparer.Instance);
    private readonly HashSet<(GraphAccessTargetKind Kind, int Index)> _placementResources = [];
    private readonly List<TransientAllocationRequest> _allocationRequests = [];

    private void UpdatePlacements(FrameTransientResourceAllocator memory)
    {
        foreach (var key in _placementResources)
        {
            if (!ResourceExists(key)) continue;
            GraphIdentity identity;
            int first, last;
            if (key.Kind == GraphAccessTargetKind.Buffer)
            {
                FrameBuffer row = _buffers[key.Index];
                if (row.Ownership != RenderGraphResourceOwnership.GraphOwned || row.Lifetime != RenderGraphResourceLifetime.PerFrame) continue;
                identity = row.AllocationIdentity; first = row.FirstPass; last = row.LastPass;
            }
            else if (key.Kind == GraphAccessTargetKind.Texture)
            {
                FrameTexture row = _textures[key.Index];
                if (row.Ownership != RenderGraphResourceOwnership.GraphOwned || row.Lifetime != RenderGraphResourceLifetime.PerFrame) continue;
                identity = row.AllocationIdentity; first = row.FirstPass; last = row.LastPass;
            }
            else continue;
            if (last < 0) { memory.Deactivate(identity); continue; }
            ResolveResourceFrontiers(key.Kind, key.Index, key.Kind == GraphAccessTargetKind.Buffer ? key.Index : _buffers.Count + key.Index,
                out int[] begin, out int[] end);
            memory.UpdateLifetime(identity, begin, end, _passes[first].Queue!, _passes[last].Queue!);
        }
        foreach (GraphIdentity identity in memory.ReconcileLifetimes())
        {
            if (!_allocationRows.TryGetValue(identity, out var key)) continue;
            GraphIdentity resourceIdentity;
            if (key.Kind == GraphAccessTargetKind.Buffer)
            {
                ref FrameBuffer row = ref CollectionsMarshal.AsSpan(_buffers)[key.Index];
                row.Resource = null; row.Placement = default; row.EntryBoundaryStates = null;
                resourceIdentity = row.Identity;
            }
            else
            {
                ref FrameTexture row = ref CollectionsMarshal.AsSpan(_textures)[key.Index];
                row.Resource = null; row.Placement = default; row.EntryBoundaryStates = null;
                resourceIdentity = row.Identity;
            }
            MaterializationChanged(key, updateViewDescriptions: false);
            ChangeSynchronization(key);
            if (_resourceViewRows.TryGetValue(key, out HashSet<int>? viewRows))
                foreach (int index in viewRows) CollectionsMarshal.AsSpan(_views)[index].View = null;
            foreach (GraphIdentity view in _frame.StructureIndex.ResourceViews.GetValueOrDefault(resourceIdentity, []))
                if (_viewIndices.TryGetValue(view, out int index)) CollectionsMarshal.AsSpan(_views)[index].View = null;
            if (_resourceAccessLists.TryGetValue(key, out List<int>? accesses))
                foreach (int index in accesses) InvalidateRasterRendering(_accesses[index].PassIndex);
        }
        _allocationRequests.Clear();
        foreach (int index in _materializeBuffers)
        {
            if (index >= _buffers.Count) continue;
            ref FrameBuffer row = ref CollectionsMarshal.AsSpan(_buffers)[index];
            if (row.LastPass < 0 || row.Resource is not null || row.Ownership != RenderGraphResourceOwnership.GraphOwned ||
                row.Lifetime != RenderGraphResourceLifetime.PerFrame) continue;
            if (row.Requirements.Size == 0)
                row.Requirements = _frame.Backend.GetBufferMemoryRequirements(_frame.Graph.Device, row.Description, row.MemoryType);
            ResolveResourceFrontiers(GraphAccessTargetKind.Buffer, index, index, out int[] begin, out int[] end);
            _allocationRequests.Add(new(row.AllocationIdentity, row.Requirements, row.MemoryType, row.Description.NodePlacement,
                begin, end, _passes[row.FirstPass].Queue!, _passes[row.LastPass].Queue!));
        }
        foreach (int index in _materializeTextures)
        {
            if (index >= _textures.Count) continue;
            ref FrameTexture row = ref CollectionsMarshal.AsSpan(_textures)[index];
            if (row.LastPass < 0 || row.Resource is not null || row.Ownership != RenderGraphResourceOwnership.GraphOwned ||
                row.Lifetime != RenderGraphResourceLifetime.PerFrame) continue;
            if (row.Requirements.Size == 0)
                row.Requirements = _frame.Backend.GetTextureMemoryRequirements(_frame.Graph.Device, row.BorrowDescription());
            ResolveResourceFrontiers(GraphAccessTargetKind.Texture, index, _buffers.Count + index, out int[] begin, out int[] end);
            _allocationRequests.Add(new(row.AllocationIdentity, row.Requirements, MemoryType.DeviceLocal, row.NodePlacement,
                begin, end, _passes[row.FirstPass].Queue!, _passes[row.LastPass].Queue!));
        }
        memory.Allocate(_allocationRequests);
        _placementResources.Clear();
    }
}
