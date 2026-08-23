namespace SomeEngine.RenderGraph;

internal sealed partial class FrameExecutor
{
    private int[][] _resourceBeginFrontiers = [];
    private int[][] _resourceEndFrontiers = [];

    private void Materialize()
    {
        FrameTransientResourceAllocator memory = _frame.Slot.TransientResources
            ?? throw new InvalidOperationException("The frame slot has no transient resource allocator.");
        int resourceCount = checked(_buffers.Length + _textures.Length);
        PrepareArray(ref _resourceBeginFrontiers, resourceCount);
        PrepareArray(ref _resourceEndFrontiers, resourceCount);

        for (int bufferIndex = 0; bufferIndex < _buffers.Length; bufferIndex++)
        {
            FrameBuffer buffer = _buffers[bufferIndex];
            if (buffer.LastUse < 0 || buffer.Resource is not null) continue;
            if (buffer.Ownership == RenderGraphResourceOwnership.CallerOwned)
                throw new InvalidOperationException("An external Buffer was not bound.");
            if (buffer.Lifetime == RenderGraphResourceLifetime.Persistent)
                throw new InvalidOperationException("A persistent Buffer was not materialized by the structural commit.");

            if (buffer.Requirements.Size == 0)
            {
                buffer.Requirements = _frame.Backend.GetBufferMemoryRequirements(
                    _frame.Graph.Device,
                    buffer.Description,
                    buffer.MemoryType);
            }
            ResolveResourceFrontiers(GraphAccessTargetKind.Buffer, bufferIndex, bufferIndex,
                out int[] begin, out int[] end);
            buffer.Placement = memory.Place(
                buffer.Requirements,
                buffer.MemoryType,
                buffer.Description.NodePlacement,
                begin,
                end,
                bufferIndex);
            bool cacheable = _stableExecutionEligible &&
                memory.CanCacheResources &&
                buffer.Definition is not null &&
                buffer.Ownership == RenderGraphResourceOwnership.GraphOwned &&
                buffer.Lifetime == RenderGraphResourceLifetime.PerFrame &&
                buffer.InitialData is null;
            Buffer? resource = cacheable
                ? memory.FindCachedResource(
                    GraphAccessTargetKind.Buffer,
                    buffer.Identity,
                    buffer.Placement) as Buffer
                : null;
            if (resource is null)
            {
                resource = _frame.Backend.CreatePlacedBuffer(
                    _frame.Graph.Device,
                    buffer.Placement.Heap,
                    buffer.Placement.Offset,
                    buffer.Description);
                if (cacheable)
                    _ = memory.CacheResource(
                        GraphAccessTargetKind.Buffer,
                        buffer.Identity,
                        buffer.Placement,
                        resource);
            }
            if (ReferenceEquals(buffer.Placement.AliasingPredecessor, resource))
                buffer.Placement = buffer.Placement with { AliasingPredecessor = null };
            buffer.Resource = resource;
            if (cacheable)
                buffer.EntryBoundaryStates = memory.FindCachedBufferStates(
                    buffer.Identity,
                    resource);
            if (buffer.InitialData is { Length: > 0 } initialData)
            {
                BufferRange range = new(0, checked((ulong)initialData.Length));
                using MappedBuffer mapping = _frame.Backend.Map(resource, MapType.Write, range);
                initialData.CopyTo(mapping.Bytes);
                mapping.Flush(range);
                buffer.EntryBoundaryStates =
                [
                    new BufferBoundaryState(
                        range,
                        resource.InitialSync,
                        resource.InitialAccess,
                        ResourceContentState.Defined),
                ];
            }
            memory.SetResource(buffer.Placement, resource);
            RetireAliasingPredecessor(memory, buffer.Placement);
            _buffers[bufferIndex] = buffer;
        }

        for (int textureIndex = 0; textureIndex < _textures.Length; textureIndex++)
        {
            FrameTexture texture = _textures[textureIndex];
            if (texture.LastUse < 0 || texture.Resource is not null) continue;
            if (texture.Ownership == RenderGraphResourceOwnership.CallerOwned)
                throw new InvalidOperationException("An external Texture was not bound.");
            if (texture.Lifetime == RenderGraphResourceLifetime.Persistent)
                throw new InvalidOperationException("A persistent Texture was not materialized by the structural commit.");

            TextureDesc description = texture.BorrowDescription();
            if (texture.Requirements.Size == 0)
                texture.Requirements = _frame.Backend.GetTextureMemoryRequirements(
                    _frame.Graph.Device, description);
            ResolveResourceFrontiers(GraphAccessTargetKind.Texture, textureIndex,
                checked(_buffers.Length + textureIndex),
                out int[] begin, out int[] end);
            texture.Placement = memory.Place(
                texture.Requirements,
                MemoryType.DeviceLocal,
                texture.NodePlacement,
                begin,
                end,
                checked(_buffers.Length + textureIndex));
            bool cacheable = _stableExecutionEligible &&
                memory.CanCacheResources &&
                texture.Definition is not null &&
                texture.Ownership == RenderGraphResourceOwnership.GraphOwned &&
                texture.Lifetime == RenderGraphResourceLifetime.PerFrame;
            Texture? resource = cacheable
                ? memory.FindCachedResource(
                    GraphAccessTargetKind.Texture,
                    texture.Identity,
                    texture.Placement) as Texture
                : null;
            if (resource is null)
            {
                resource = _frame.Backend.CreatePlacedTexture(
                    _frame.Graph.Device,
                    texture.Placement.Heap,
                    texture.Placement.Offset,
                    description);
                if (cacheable)
                    _ = memory.CacheResource(
                        GraphAccessTargetKind.Texture,
                        texture.Identity,
                        texture.Placement,
                        resource);
            }
            if (ReferenceEquals(texture.Placement.AliasingPredecessor, resource))
                texture.Placement = texture.Placement with { AliasingPredecessor = null };
            texture.Resource = resource;
            if (cacheable)
                texture.EntryBoundaryStates = memory.FindCachedTextureStates(
                    texture.Identity,
                    resource);
            memory.SetResource(texture.Placement, resource);
            RetireAliasingPredecessor(memory, texture.Placement);
            _textures[textureIndex] = texture;
        }

        for (int viewIndex = 0; viewIndex < _views.Length; viewIndex++)
        {
            FrameView view = _views[viewIndex];
            if (view.View is not null) continue;
            bool resolved = TryResolveViewResources(view, out Resource? primary, out Resource? secondary);
            bool cacheable = view.Definition is not null && resolved &&
                memory.IsCached(primary!) &&
                (secondary is null || memory.IsCached(secondary));
            DeviceResource? materialized = cacheable
                ? memory.FindCachedView(view.Identity, primary!, secondary)
                : null;
            bool cached = materialized is not null;
            materialized ??= CreateView(view);
            if (materialized is null) continue;
            view.View = materialized;
            _views[viewIndex] = view;
            if (!cached)
            {
                cached = cacheable && memory.CacheView(
                    view.Identity,
                    materialized,
                    primary!,
                    secondary);
                if (!cached) _frame.Slot.Own(materialized);
            }
        }

        _frame.Backend.PublishDescriptors(_frame.Graph.Device);
        memory.CompletePlacement();
    }

    private DeviceResource? CreateView(in FrameView view)
    {
        switch (view.Kind)
        {
            case GraphViewKind.BufferCbv:
                {
                    Buffer buffer = _buffers[ResolveBuffer(view.Buffer)].Resource!;
                    BufferRange range = GraphStructureIndex.ResolveRange(view.BufferRange, buffer.Info.Size);
                    return _frame.Backend.CreateBufferCbv(
                        _frame.Graph.Device,
                        new BufferCbvDesc(buffer, range, view.Label));
                }
            case GraphViewKind.BufferSrv:
                {
                    Buffer buffer = _buffers[ResolveBuffer(view.Buffer)].Resource!;
                    BufferRange range = GraphStructureIndex.ResolveRange(view.BufferRange, buffer.Info.Size);
                    return _frame.Backend.CreateBufferSrv(
                        _frame.Graph.Device,
                        new BufferSrvDesc(buffer, range, view.BufferFormat,
                            view.StructureStride, view.Label));
                }
            case GraphViewKind.BufferUav:
                {
                    Buffer buffer = _buffers[ResolveBuffer(view.Buffer)].Resource!;
                    BufferRange range = GraphStructureIndex.ResolveRange(view.BufferRange, buffer.Info.Size);
                    Buffer? counter = view.AdditionalBuffer.IsValid
                        ? _buffers[ResolveBuffer(view.AdditionalBuffer)].Resource
                        : null;
                    return _frame.Backend.CreateBufferUav(
                        _frame.Graph.Device,
                        new BufferUavDesc(buffer, range, view.BufferFormat,
                            view.StructureStride, counter, view.CounterOffset, view.Label));
                }
            case GraphViewKind.TextureSrv:
                {
                    Texture texture = _textures[ResolveTexture(view.Texture)].Resource!;
                    return _frame.Backend.CreateTextureSrv(
                        _frame.Graph.Device,
                        new TextureSrvDesc(texture, view.TextureRange,
                            view.TextureFormat, view.Dimension, view.Label));
                }
            case GraphViewKind.TextureUav:
                {
                    Texture texture = _textures[ResolveTexture(view.Texture)].Resource!;
                    return _frame.Backend.CreateTextureUav(
                        _frame.Graph.Device,
                        new TextureUavDesc(texture, view.TextureRange,
                            view.TextureFormat, view.Dimension, view.Label));
                }
            case GraphViewKind.ColorAttachment:
                {
                    Texture texture = _textures[ResolveTexture(view.Texture)].Resource!;
                    return _frame.Backend.CreateColorAttachmentView(
                        _frame.Graph.Device,
                        new ColorAttachmentViewDesc(texture, view.TextureRange,
                            view.TextureFormat, view.Dimension, view.Label));
                }
            case GraphViewKind.DepthStencil:
                {
                    Texture texture = _textures[ResolveTexture(view.Texture)].Resource!;
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
                primary = _buffers[ResolveBuffer(view.Buffer)].Resource;
                if (view.AdditionalBuffer.IsValid)
                    secondary = _buffers[ResolveBuffer(view.AdditionalBuffer)].Resource;
                return primary is not null;
            case GraphViewKind.TextureSrv:
            case GraphViewKind.TextureUav:
            case GraphViewKind.ColorAttachment:
            case GraphViewKind.DepthStencil:
                primary = _textures[ResolveTexture(view.Texture)].Resource;
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
        for (int accessIndex = 0; accessIndex < _accesses.Length; accessIndex++)
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

    private void RetireAliasingPredecessor(
        FrameTransientResourceAllocator memory,
        in ResourcePlacement placement)
    {
        if (placement.AliasingPredecessor is not null &&
            !memory.IsCached(placement.AliasingPredecessor))
            _frame.Slot.Own(placement.AliasingPredecessor);
    }
}

