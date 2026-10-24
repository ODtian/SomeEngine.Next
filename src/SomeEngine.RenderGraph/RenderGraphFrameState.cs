namespace SomeEngine.RenderGraph;

internal sealed partial class RenderGraphFrameState
{
    private RenderGraphFrameOptions _options;
    private readonly List<FrameBuffer> _dynamicBuffers = [];
    private readonly List<FrameTexture> _dynamicTextures = [];
    private readonly List<FrameView> _dynamicViews = [];
    private readonly List<FramePass> _dynamicPasses = [];
    private readonly List<FrameResourceAccess> _dynamicAccesses = [];
    private readonly List<BufferRangeRead> _bufferRangeReads = [];
    private readonly Dictionary<(GraphViewKind, GraphIdentity, BufferRange, Format?, uint, GraphIdentity, ulong, string?), GraphIdentity> _bufferViews = [];
    private readonly Dictionary<(GraphViewKind, GraphIdentity, TextureSubresourceRange, Format, TextureViewDimension, bool, bool, string?), GraphIdentity> _textureViews = [];
    private readonly List<ExplicitPassOrder> _dynamicOrders = [];
    private readonly Dictionary<FramePassCallbackStoreKey, FramePassCallbackStore> _callbackStores = [];
    private readonly List<(GraphIdentity Identity, SwapchainImage Image, Queue PresentQueue)> _swapchainImages = [];
    private readonly Dictionary<Buffer, GraphBufferId> _importedBuffers =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Texture, GraphTextureId> _importedTextures =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<IRenderGraphFrameExecutionObserver> _executionObservers = [];
    private readonly List<IDisposable> _frameRetirements = [];
    private readonly FrameExecutor _executor;
    private readonly Dictionary<GraphIdentity, FrameTransientResourceAllocator.UploadPage> _bufferData = [];
    private bool _executorActive;
    private bool _sealed;
    private bool _finished = true;

    internal RenderGraphFrameState(RenderGraph graph, FrameSlot slot)
    {
        Graph = graph;
        Slot = slot;
        _executor = new FrameExecutor(this);
        slot.EnsureTransientResources(graph.Backend, graph.Device, graph.HeapByteBudget);
    }

    internal void Begin(
        ulong identity,
        in RenderGraphFrameOptions options,
        GraphStructureIndex structureIndex,
        ulong structureVersion)
    {
        if (!_finished)
            throw new InvalidOperationException("The FrameSlot execution is already active.");
        Identity = identity;
        StructureIndex = structureIndex;
        StructureVersion = structureVersion;
        _options = options;
        _sealed = false;
        _finished = false;
        _executorActive = false;
    }

    internal RenderGraph Graph { get; }
    internal FrameSlot Slot { get; }
    internal ulong Identity { get; private set; }
    internal GraphStructureIndex StructureIndex { get; private set; } = GraphStructureIndex.Empty;
    internal ulong StructureVersion { get; private set; }
    internal int FrameSlot => Slot.Index;
    internal RenderGraphFrameOptions Options => _options;
    internal IReadOnlyList<FrameBuffer> DynamicBuffers => _dynamicBuffers;
    internal IReadOnlyList<FrameTexture> DynamicTextures => _dynamicTextures;
    internal IReadOnlyList<FrameView> DynamicViews => _dynamicViews;
    internal IReadOnlyList<FramePass> DynamicPasses => _dynamicPasses;
    internal IReadOnlyList<FrameResourceAccess> DynamicAccesses => _dynamicAccesses;
    internal IReadOnlyList<BufferRangeRead> BufferRangeReads => _bufferRangeReads;
    internal IReadOnlyList<ExplicitPassOrder> DynamicOrders => _dynamicOrders;
    internal IReadOnlyList<(GraphIdentity Identity, SwapchainImage Image, Queue PresentQueue)> SwapchainImages => _swapchainImages;
    internal void ReleaseRecordingContexts() => _executor.ReleaseRecordingContexts();
    internal void CollectGpuTimes() => _executor.CollectGpuTimes();
    internal void DisposeGpuTimes() => _executor.DisposeGpuTimes();

    internal FrameExecutor Executor => _executorActive
        ? _executor
        : throw new InvalidOperationException("The render graph frame has not been prepared for execution.");
    internal IGraphicsBackend Backend => Graph.Backend;

    internal void ChangeProperty(GraphIdentity identity, GraphStoreKind kind, bool refreshViews)
    {
        switch (kind)
        {
            case GraphStoreKind.Passes: _executor.ChangePass(identity); break;
            case GraphStoreKind.Buffers: _executor.ChangeBuffer(identity, refreshViews); break;
            case GraphStoreKind.Textures: _executor.ChangeTexture(identity, refreshViews); break;
            case GraphStoreKind.Views: _executor.ChangeView(identity); break;
            case GraphStoreKind.Accesses: _executor.ChangeAccess(identity); break;
        }
    }

    internal void ApplyEdit(GraphEditChanges changes)
    {
        if (!changes.IsEmpty && !changes.LabelsOnly)
        {
            _bufferInputs.Clear();
            _textureInputs.Clear();
            _parameterInputs.Clear();
        }
        _executor.ApplyEdit(changes);
    }
    internal void BoundaryChanged(GraphIdentity identity, GraphStoreKind kind) => _executor.BoundaryChanged(identity, kind);

    internal void EnsureLease(ulong identity)
    {
        if (_finished || identity == 0 || Identity != identity)
            throw new InvalidOperationException("The render graph frame lease is no longer active.");
        Graph.EnsureFrame(identity);
    }

    internal void RetireAfterSubmittedFrames(IDisposable value)
    {
        EnsureAuthoring();
        Graph.RetireAfterSubmittedFrames(value);
    }

    internal void RetireAfterFrame(IDisposable value)
    {
        EnsureAuthoring();
        _frameRetirements.Add(value);
    }

    internal void ObserveExecution(IRenderGraphFrameExecutionObserver observer)
    {
        EnsureAuthoring();
        _executionObservers.Add(observer);
    }

    internal Buffer GetBuffer(int passIndex, GraphBufferId buffer) =>
        Executor.GetBuffer(passIndex, buffer);
    internal Texture GetTexture(int passIndex, GraphTextureId texture) =>
        Executor.GetTexture(passIndex, texture);
    internal BufferSrv GetBufferSrv(int passIndex, GraphBufferSrvId view) =>
        Executor.GetBufferSrv(passIndex, view);
    internal BufferUav GetBufferUav(int passIndex, GraphBufferUavId view) =>
        Executor.GetBufferUav(passIndex, view);
    internal TextureSrv GetTextureSrv(int passIndex, GraphTextureSrvId view) =>
        Executor.GetTextureSrv(passIndex, view);
    internal TextureUav GetTextureUav(int passIndex, GraphTextureUavId view) =>
        Executor.GetTextureUav(passIndex, view);
    internal ColorAttachmentView GetColorAttachmentView(
        int passIndex,
        GraphColorAttachmentViewId view) =>
        Executor.GetColorAttachmentView(passIndex, view);
    internal DepthStencilView GetDepthStencilView(
        int passIndex,
        GraphDepthStencilViewId view) =>
        Executor.GetDepthStencilView(passIndex, view);

    internal void SetPassEnabled(GraphPassId pass, bool enabled)
    {
        EnsureAuthoring();
        ValidateStatic(pass.Value, StructureIndex.Structure.Passes);
        GraphPass row = StructureIndex.Structure.Passes.Get(pass.Value);
        if (row.Enabled == enabled) return;
        row.Enabled = enabled;
        Graph.PropertyChanged(pass.Value, GraphStoreKind.Passes);
    }

    internal PassDefinition GetPass(GraphPassId pass)
    {
        EnsureAuthoring();
        GraphIdentity identity = pass.Value;
        GraphPassKind kind;
        if (identity.Owner == Graph.Identity)
        {
            ValidateStatic(identity, StructureIndex.Structure.Passes);
            kind = StructureIndex.Structure.Passes.Get(identity).Kind;
        }
        else
        {
            kind = _dynamicPasses[ResolveDynamicPass(identity)].Kind;
        }
        return new PassDefinition(this, identity, kind);
    }

    internal void SetPassData<T>(GraphPassId pass, in T value, bool compareValue = false)
    {
        EnsureAuthoring();
        ValidateStatic(pass.Value, StructureIndex.Structure.Passes);
        GraphPass graphPass = StructureIndex.Structure.Passes.Get(pass.Value);
        if (graphPass.CallbackStorage is not IFramePassDataStorage<T> slot)
        {
            throw new ArgumentException(
                $"The pass expects frame data of type {graphPass.CallbackStorage.FrameDataType.FullName}.",
                nameof(value));
        }
        bool unchanged = graphPass.Options.RepeatCommands &&
            (compareValue ? slot.FrameValueEquals(value) : slot.FrameDataEquals(value));
        slot.SetFrameData(value);
        if (!unchanged) Graph.CommandDataChanged(pass.Value);
    }

    internal void CommandDataChanged(GraphIdentity identity) => _executor.ChangeCommandData(identity);

    internal void SetBufferRange(GraphBufferAccessId access, in BufferRange range)
    {
        EnsureAuthoring();
        ValidateStatic(access.Value, StructureIndex.Structure.Accesses);
        PassResourceAccess row = StructureIndex.Structure.Accesses.Get(access.Value);
        DevelopmentCheck.Require(row.TargetKind == GraphAccessTargetKind.Buffer && row.DynamicRange,
            "The Buffer access range is not dynamic.");
        if (row.CurrentBufferRange == range) return;
        row.CurrentBufferRange = range;
        Graph.PropertyChanged(access.Value, GraphStoreKind.Accesses);
    }

    internal void SetBufferRange(
        in GraphIdentity identity,
        GraphViewKind expectedKind,
        in BufferRange range)
    {
        EnsureAuthoring();
        ValidateStatic(identity, StructureIndex.Structure.Views);
        GraphView view = StructureIndex.Structure.Views.Get(identity);
        DevelopmentCheck.Require(view.Kind == expectedKind, "The Buffer view identity has the wrong kind.");
#if DEBUG
        if (view.Buffer.IsValid)
            _ = GraphStructureIndex.ResolveRange(range, StructureIndex.Structure.Buffers.Get(view.Buffer).Description.Size);
#endif
        if (view.CurrentBufferRange == range) return;
        view.CurrentBufferRange = range;
        Graph.PropertyChanged(identity, GraphStoreKind.Views);
    }

    internal void SetBufferViewRanges(GraphBufferId buffer, in BufferRange range)
    {
        EnsureAuthoring();
        ValidateStatic(buffer.Value, StructureIndex.Structure.Buffers);
        GraphBuffer definition = StructureIndex.Structure.Buffers.Get(buffer.Value);
        _ = GraphStructureIndex.ResolveRange(range, definition.Description.Size);
        if (definition.CurrentViewRange == range) return;
        definition.CurrentViewRange = range;
        foreach (GraphIdentity view in StructureIndex.ResourceViews.GetValueOrDefault(buffer.Value, []))
            Graph.PropertyChanged(view, GraphStoreKind.Views);
    }

    internal void SetTextureRange(GraphTextureAccessId access, in TextureSubresourceRange range)
    {
        EnsureAuthoring();
        ValidateStatic(access.Value, StructureIndex.Structure.Accesses);
        PassResourceAccess row = StructureIndex.Structure.Accesses.Get(access.Value);
        DevelopmentCheck.Require(row.TargetKind == GraphAccessTargetKind.Texture && row.DynamicRange,
            "The Texture access range is not dynamic.");
        if (row.CurrentTextureRange == range) return;
        row.CurrentTextureRange = range;
        Graph.PropertyChanged(access.Value, GraphStoreKind.Accesses);
    }

    internal void BindExternalBuffer(
        GraphBufferId slot,
        Buffer buffer,
        scoped ReadOnlySpan<BufferBoundaryState> boundaryStates)
    {
        EnsureAuthoring();
        ValidateStatic(slot.Value, StructureIndex.Structure.Buffers);
        GraphBuffer graphBuffer = StructureIndex.Structure.Buffers.Get(slot.Value);
        DevelopmentCheck.Require(graphBuffer.Ownership == RenderGraphResourceOwnership.CallerOwned &&
            graphBuffer.Lifetime == RenderGraphResourceLifetime.PerFrame && graphBuffer.RegisteredResource is null,
            "The GraphBufferId must declare an external buffer binding.");
        DevelopmentCheck.Require(buffer is not null && ReferenceEquals(buffer.Device, Graph.Device),
            "The Buffer must belong to the graph Device.");
        Buffer? previous = graphBuffer.BoundResource;
        if (previous is not null && !ReferenceEquals(previous, buffer) &&
            _importedBuffers.TryGetValue(previous, out GraphBufferId previousId) && previousId == slot)
            _importedBuffers.Remove(previous);
        _importedBuffers[buffer] = slot;
        if (_bufferData.Remove(slot.Value)) _executor.ChangeBuffer(slot.Value, refreshViews: false);
        if (ReferenceEquals(previous, buffer) && boundaryStates.SequenceEqual(graphBuffer.BoundaryStates)) return;
        BufferBoundaryState[] states = graphBuffer.BoundaryStates.Length == boundaryStates.Length
            ? graphBuffer.BoundaryStates : new BufferBoundaryState[boundaryStates.Length];
        boundaryStates.CopyTo(states);
        graphBuffer.BoundResource = buffer;
        graphBuffer.BoundaryStates = states;
        if (ReferenceEquals(previous, buffer))
            Graph.BoundaryChanged(slot.Value, GraphStoreKind.Buffers);
        else
            Graph.PropertyChanged(slot.Value, GraphStoreKind.Buffers, refreshViews: false);
    }

    internal void BindExternalTexture(
        GraphTextureId slot,
        Texture texture,
        scoped ReadOnlySpan<TextureBoundaryState> boundaryStates)
    {
        EnsureAuthoring();
        ValidateStatic(slot.Value, StructureIndex.Structure.Textures);
        GraphTexture graphTexture = StructureIndex.Structure.Textures.Get(slot.Value);
        DevelopmentCheck.Require(graphTexture.Ownership == RenderGraphResourceOwnership.CallerOwned &&
            graphTexture.Lifetime == RenderGraphResourceLifetime.PerFrame && graphTexture.RegisteredResource is null,
            "The GraphTextureId must declare an external texture binding.");
        DevelopmentCheck.Require(texture is not null && ReferenceEquals(texture.Device, Graph.Device),
            "The Texture must belong to the graph Device.");
        Texture? previous = graphTexture.BoundResource;
        if (previous is not null && !ReferenceEquals(previous, texture) &&
            _importedTextures.TryGetValue(previous, out GraphTextureId previousId) && previousId == slot)
            _importedTextures.Remove(previous);
        _importedTextures[texture] = slot;
        if (ReferenceEquals(previous, texture) && boundaryStates.SequenceEqual(graphTexture.BoundaryStates)) return;
        TextureBoundaryState[] states = graphTexture.BoundaryStates.Length == boundaryStates.Length
            ? graphTexture.BoundaryStates : new TextureBoundaryState[boundaryStates.Length];
        boundaryStates.CopyTo(states);
        graphTexture.BoundResource = texture;
        graphTexture.BoundaryStates = states;
        if (ReferenceEquals(previous, texture))
            Graph.BoundaryChanged(slot.Value, GraphStoreKind.Textures);
        else
            Graph.PropertyChanged(slot.Value, GraphStoreKind.Textures, refreshViews: false);
    }
    internal void BindExternalTexture(
        GraphTextureId slot,
        in SwapchainImage image,
        Queue presentQueue)
    {
        EnsureAuthoring();
        ArgumentNullException.ThrowIfNull(presentQueue);
        if (image.Status != SwapchainImageStatus.Acquired)
            throw new InvalidOperationException("Only an acquired SwapchainImage can enter the graph.");
        if (!ReferenceEquals(presentQueue.Device, Graph.Device))
            throw new ArgumentException("The present Queue belongs to another Device.", nameof(presentQueue));
        if (_swapchainImages.Any(item => item.Identity.Equals(slot.Value)))
            throw new InvalidOperationException("The external swapchain slot is already bound.");

        Texture texture = image.Texture;
        TextureSubresourceRange range = new(
            0,
            texture.Info.MipLevelCount,
            0,
            texture.Info.ArrayLayerCount,
            TextureFormatRules.Aspects(texture.Info.Format));
        BindExternalTexture(
            slot,
            texture,
            [new TextureBoundaryState(
                range,
                image.InitialSync,
                image.InitialAccess,
                image.InitialLayout,
                image.InitialLayout == TextureLayout.Undefined
                    ? ResourceContentState.Undefined
                    : ResourceContentState.Defined,
                presentQueue,
                null)]);
        _swapchainImages.Add((slot.Value, image, presentQueue));
    }

    internal GraphBufferId AddBuffer(
        in BufferDesc description,
        MemoryType memoryType,
        RenderGraphResourceOwnership ownership,
        RenderGraphResourceLifetime lifetime)
    {
        EnsureAuthoring();
        GraphIdentity identity = NewIdentity(
            _dynamicBuffers.Count,
            GraphIdentityDomain.Buffer);
        var buffer = new FrameBuffer
        {
            Identity = identity,
            Description = description,
            MemoryType = memoryType,
            Ownership = ownership,
            Lifetime = lifetime,
            FirstPass = -1,
            LastPass = -1,
        };
        _dynamicBuffers.Add(buffer);
        return new GraphBufferId(identity);
    }

    internal GraphBufferSlice Upload(
        scoped ReadOnlySpan<byte> data,
        BufferUsages usages,
        string? label,
        uint elementAlignment)
    {
        EnsureAuthoring();
        (FrameTransientResourceAllocator.UploadPage page, BufferRange range) = Slot.TransientResources!.WriteUpload(data, usages, elementAlignment);
        return RegisterUpload(page, range);
    }

    internal void SetPassOptions(GraphPassId pass, in PassOptions options)
    {
        EnsureAuthoring();
        ValidateStatic(pass.Value, StructureIndex.Structure.Passes);
        GraphPass definition = StructureIndex.Structure.Passes.Get(pass.Value);
        if (definition.Options == options) return;
        definition.Options = options;
        Graph.PropertyChanged(pass.Value, GraphStoreKind.Passes);
    }

    internal GraphBufferSlice Upload(
        scoped ReadOnlySpan<ReadOnlyMemory<byte>> segments,
        BufferUsages usages,
        string? label)
    {
        EnsureAuthoring();
        (FrameTransientResourceAllocator.UploadPage page, BufferRange range) = Slot.TransientResources!.WriteUpload(segments, usages);
        return RegisterUpload(page, range);
    }

    private GraphBufferSlice RegisterUpload(FrameTransientResourceAllocator.UploadPage page, BufferRange range)
    {
        if (page.GraphBuffer == default)
        {
            Buffer resource = page.Buffer;
            page.GraphBuffer = AddBuffer(
                new BufferDesc(resource.Info.Size, resource.Info.Usages, resource.Label),
                MemoryType.Upload, RenderGraphResourceOwnership.CallerOwned, RenderGraphResourceLifetime.PerFrame);
            ref FrameBuffer buffer = ref CollectionsMarshal.AsSpan(_dynamicBuffers)[page.GraphBuffer.Value.Slot];
            buffer.Resource = resource;
            buffer.EntryBoundaryStates = page.BoundaryStates;
            buffer.UploadData = page;
            _importedBuffers.Add(resource, page.GraphBuffer);
        }
        return new GraphBufferSlice(page.GraphBuffer, range);
    }

    internal GraphBufferSlice Upload<TData>(
        int byteCount,
        BufferUsages usages,
        TData data,
        System.Buffers.SpanAction<byte, TData> write,
        string? label)
    {
        EnsureAuthoring();
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(byteCount);
        ArgumentNullException.ThrowIfNull(write);
        (FrameTransientResourceAllocator.UploadPage page, BufferRange range) =
            Slot.TransientResources!.WriteUpload(byteCount, usages, data, write);
        return RegisterUpload(page, range);
    }

    internal void SetBufferData(GraphBufferId buffer, scoped ReadOnlySpan<byte> data)
    {
        EnsureAuthoring();
        ValidateStatic(buffer.Value, StructureIndex.Structure.Buffers);
        GraphBuffer declaration = StructureIndex.Structure.Buffers.Get(buffer.Value);
        DevelopmentCheck.Require(declaration.MemoryType == MemoryType.Upload &&
            declaration.RegisteredResource is null &&
            declaration.Ownership == RenderGraphResourceOwnership.CallerOwned &&
            (declaration.Description.Usages & ~(BufferUsages.ShaderRead | BufferUsages.Constant | BufferUsages.CopySource)) == 0,
            "Buffer data requires an external upload declaration used only for GPU reads.");
        if ((ulong)data.Length != declaration.Description.Size)
            throw new ArgumentException("Buffer data must cover the declared byte size.", nameof(data));
        FrameTransientResourceAllocator.UploadPage page = Slot.TransientResources!
            .WriteBufferData(buffer.Value, declaration.Description, data);
        if (_bufferData.TryGetValue(buffer.Value, out var previous) && !ReferenceEquals(previous.Buffer, page.Buffer))
            _importedBuffers.Remove(previous.Buffer);
        _bufferData[buffer.Value] = page;
        _importedBuffers[page.Buffer] = buffer;
        _executor.ChangeBuffer(buffer.Value, refreshViews: false);
    }

    internal bool TryGetBufferData(GraphIdentity id, out FrameTransientResourceAllocator.UploadPage page) =>
        _bufferData.TryGetValue(id, out page!);

    internal GraphTextureId AddTexture(
        in TextureDesc description,
        RenderGraphResourceOwnership ownership,
        RenderGraphResourceLifetime lifetime)
    {
        EnsureAuthoring();
        GraphIdentity identity = NewIdentity(
            _dynamicTextures.Count,
            GraphIdentityDomain.Texture);
        var texture = new FrameTexture
        {
            Identity = identity,
            Dimension = description.Dimension,
            Width = description.Width,
            Height = description.Height,
            Depth = description.Depth,
            MipLevelCount = description.MipLevelCount,
            ArrayLayerCount = description.ArrayLayerCount,
            SampleCount = description.SampleCount,
            Format = description.Format,
            Usages = description.Usages,
            PermittedViewFormats = description.PermittedViewFormats.ToArray(),
            Label = description.Label,
            NodePlacement = description.NodePlacement,
            Ownership = ownership,
            Lifetime = lifetime,
            FirstPass = -1,
            LastPass = -1,
        };
        _dynamicTextures.Add(texture);
        return new GraphTextureId(identity);
    }

    internal TextureSubresourceRange ResolveTextureRange(
        GraphTextureId texture,
        TextureSubresourceRange? requested)
    {
        GetTextureShape(texture, out _, out uint mipCount, out uint layerCount,
            out _, out Format format);
        TextureAspects allowedAspects = TextureFormatRules.Aspects(format);
        TextureSubresourceRange range = requested ??
            new TextureSubresourceRange(0, mipCount, 0, layerCount, allowedAspects);
        DevelopmentCheck.Require(range.MipLevelCount != 0 && range.ArrayLayerCount != 0 &&
            range.Aspects != TextureAspects.None &&
            (range.Aspects & ~allowedAspects) == TextureAspects.None &&
            range.FirstMipLevel < mipCount &&
            range.MipLevelCount <= mipCount - range.FirstMipLevel &&
            range.FirstArrayLayer < layerCount &&
            range.ArrayLayerCount <= layerCount - range.FirstArrayLayer,
            "The texture range must name valid aspects and contained mip/layer ranges.");
        return range;
    }

    internal Format ResolveTextureFormat(GraphTextureId texture, Format? requested)
    {
        GetTextureShape(texture, out _, out _, out _, out _, out Format format);
        return requested ?? format;
    }

    internal TextureViewDimension ResolveTextureViewDimension(
        GraphTextureId texture,
        TextureViewDimension? requested)
    {
        if (requested.HasValue) return requested.Value;
        GetTextureShape(texture, out TextureDimension dimension, out _, out uint layers,
            out uint samples, out _);
        return dimension switch
        {
            TextureDimension.Texture1D when layers > 1 => TextureViewDimension.Texture1DArray,
            TextureDimension.Texture1D => TextureViewDimension.Texture1D,
            TextureDimension.Texture2D when samples > 1 && layers > 1 =>
                TextureViewDimension.Texture2DMultisampledArray,
            TextureDimension.Texture2D when samples > 1 =>
                TextureViewDimension.Texture2DMultisampled,
            TextureDimension.Texture2D when layers > 1 => TextureViewDimension.Texture2DArray,
            TextureDimension.Texture2D => TextureViewDimension.Texture2D,
            TextureDimension.Texture3D => TextureViewDimension.Texture3D,
            _ => throw new ArgumentOutOfRangeException(nameof(texture)),
        };
    }

    private void GetTextureShape(
        GraphTextureId texture,
        out TextureDimension dimension,
        out uint mipCount,
        out uint layerCount,
        out uint sampleCount,
        out Format format)
    {
        ValidateResourceIdentity(texture.Value, false);
        if (texture.Value.Owner == Graph.Identity)
        {
            GraphTexture row = StructureIndex.Structure.Textures.Get(texture.Value);
            dimension = row.Dimension;
            mipCount = row.MipLevelCount;
            layerCount = row.ArrayLayerCount;
            sampleCount = row.SampleCount;
            format = row.Format;
            return;
        }
        FrameTexture dynamic = _dynamicTextures[texture.Value.Slot];
        dimension = dynamic.Dimension;
        mipCount = dynamic.MipLevelCount;
        layerCount = dynamic.ArrayLayerCount;
        sampleCount = dynamic.SampleCount;
        format = dynamic.Format;
    }

    internal GraphBufferId Import(Buffer buffer, scoped ReadOnlySpan<BufferBoundaryState> boundaryStates)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_importedBuffers.TryGetValue(buffer, out GraphBufferId existing)) return existing;
        if (StructureIndex.ResourceIdentities.TryGetValue(buffer, out GraphIdentity identity))
        {
            GraphBufferId registered = new(identity);
            _importedBuffers.Add(buffer, registered);
            GraphBuffer definition = StructureIndex.Structure.Buffers.Get(identity);
            if (!boundaryStates.SequenceEqual(definition.BoundaryStates))
            {
                definition.BoundaryStates = boundaryStates.ToArray();
                Graph.BoundaryChanged(identity, GraphStoreKind.Buffers);
            }
            return registered;
        }
        if (!ReferenceEquals(buffer.Device, Graph.Device))
            throw new ArgumentException("The Buffer belongs to another Device.", nameof(buffer));
        GraphBufferId id = AddBuffer(
            new BufferDesc(buffer.Info.Size, buffer.Info.Usages, buffer.Label,
                new ResourceNodePlacement(buffer.Info.CreationNodeMask, buffer.Info.VisibleNodeMask)),
            buffer.Info.MemoryType,
            RenderGraphResourceOwnership.CallerOwned,
            RenderGraphResourceLifetime.PerFrame);
        int index = id.Value.Slot;
        FrameBuffer row = _dynamicBuffers[index];
        row.Resource = buffer;
        row.EntryBoundaryStates = boundaryStates.ToArray();
        _dynamicBuffers[index] = row;
        _importedBuffers.Add(buffer, id);
        return id;
    }

    internal GraphTextureId Import(Texture texture, scoped ReadOnlySpan<TextureBoundaryState> boundaryStates)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (_importedTextures.TryGetValue(texture, out GraphTextureId existing)) return existing;
        if (StructureIndex.ResourceIdentities.TryGetValue(texture, out GraphIdentity identity))
        {
            GraphTextureId registered = new(identity);
            _importedTextures.Add(texture, registered);
            GraphTexture definition = StructureIndex.Structure.Textures.Get(identity);
            if (!boundaryStates.SequenceEqual(definition.BoundaryStates))
            {
                definition.BoundaryStates = boundaryStates.ToArray();
                Graph.BoundaryChanged(identity, GraphStoreKind.Textures);
            }
            return registered;
        }
        if (!ReferenceEquals(texture.Device, Graph.Device))
            throw new ArgumentException("The Texture belongs to another Device.", nameof(texture));
        TextureInfo info = texture.Info;
        TextureDesc description = new(
            info.Dimension, info.Width, info.Height, info.Depth, info.MipLevelCount,
            info.ArrayLayerCount, info.SampleCount, info.Format, info.Usages,
            info.PermittedViewFormats, texture.Label,
            new ResourceNodePlacement(info.CreationNodeMask, info.VisibleNodeMask));
        GraphTextureId id = AddTexture(
            description,
            RenderGraphResourceOwnership.CallerOwned,
            RenderGraphResourceLifetime.PerFrame);
        int index = id.Value.Slot;
        FrameTexture row = _dynamicTextures[index];
        row.Resource = texture;
        row.EntryBoundaryStates = boundaryStates.ToArray();
        _dynamicTextures[index] = row;
        _importedTextures.Add(texture, id);
        return id;
    }

    internal GraphBufferId GetImported(Buffer buffer)
    {
        EnsureAuthoring();
        bool found = _importedBuffers.TryGetValue(buffer, out GraphBufferId id);
        if (!found && StructureIndex.ResourceIdentities.TryGetValue(buffer, out GraphIdentity identity)) return new GraphBufferId(identity);
        DevelopmentCheck.Require(found, "The Buffer has not been imported by this frame.");
        return id;
    }

    internal GraphTextureId GetImported(Texture texture)
    {
        EnsureAuthoring();
        bool found = _importedTextures.TryGetValue(texture, out GraphTextureId id);
        if (!found && StructureIndex.ResourceIdentities.TryGetValue(texture, out GraphIdentity identity)) return new GraphTextureId(identity);
        DevelopmentCheck.Require(found, "The Texture has not been imported by this frame.");
        return id;
    }

    internal GraphTextureId Import(in SwapchainImage image, Queue presentQueue)
    {
        if (image.Status != SwapchainImageStatus.Acquired)
            throw new InvalidOperationException("Only an acquired SwapchainImage can enter the graph.");
        if (_importedTextures.ContainsKey(image.Texture))
            throw new InvalidOperationException("The SwapchainImage Texture is already imported.");
        GraphTextureId id = Import(
            image.Texture,
            [new TextureBoundaryState(
                new TextureSubresourceRange(0, image.Texture.Info.MipLevelCount, 0,
                    image.Texture.Info.ArrayLayerCount, TextureFormatRules.Aspects(image.Texture.Info.Format)),
                image.InitialSync,
                image.InitialAccess,
                image.InitialLayout,
                image.InitialLayout == TextureLayout.Undefined ? ResourceContentState.Undefined : ResourceContentState.Defined,
                presentQueue,
                null)]);
        _swapchainImages.Add((id.Value, image, presentQueue));
        return id;
    }

    internal GraphIdentity AddBufferView(
        GraphViewKind kind,
        GraphBufferId buffer,
        in BufferRange range,
        Format? format,
        uint stride,
        in GraphIdentity additionalBuffer,
        ulong counterOffset,
        string? label)
    {
        EnsureAuthoring();
        ValidateResourceIdentity(buffer.Value, true);
        if (additionalBuffer.IsValid) ValidateResourceIdentity(additionalBuffer, true);
        var key = (kind, buffer.Value, range, format, stride, additionalBuffer, counterOffset, label);
        if (_bufferViews.TryGetValue(key, out GraphIdentity existing)) return existing;
        GraphIdentity identity = NewIdentity(
            _dynamicViews.Count,
            GraphIdentityDomain.View);
        _dynamicViews.Add(new FrameView
        {
            Identity = identity,
            Kind = kind,
            Buffer = buffer.Value,
            AdditionalBuffer = additionalBuffer,
            BufferRange = range,
            BufferFormat = format,
            StructureStride = stride,
            CounterOffset = counterOffset,
            Label = label,
        });
        _bufferViews.Add(key, identity);
        return identity;
    }

    internal GraphIdentity AddTextureView(
        GraphViewKind kind,
        GraphTextureId texture,
        in TextureSubresourceRange range,
        Format format,
        TextureViewDimension dimension,
        bool readOnlyDepth,
        bool readOnlyStencil,
        string? label)
    {
        EnsureAuthoring();
        ValidateResourceIdentity(texture.Value, false);
        var key = (kind, texture.Value, range, format, dimension, readOnlyDepth, readOnlyStencil, label);
        if (_textureViews.TryGetValue(key, out GraphIdentity existing)) return existing;
        GraphIdentity identity = NewIdentity(
            _dynamicViews.Count,
            GraphIdentityDomain.View);
        _dynamicViews.Add(new FrameView
        {
            Identity = identity,
            Kind = kind,
            Texture = texture.Value,
            TextureRange = range,
            TextureFormat = format,
            Dimension = dimension,
            ReadOnlyDepth = readOnlyDepth,
            ReadOnlyStencil = readOnlyStencil,
            Label = label,
        });
        _textureViews.Add(key, identity);
        return identity;
    }

    internal GraphPassId AddRasterPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback,
        int extensionOrdinal) =>
        AddDynamicPass(label, GraphPassKind.Raster, queue, state, options,
            null, VariableLayoutReflection.Null, default,
            declaration, GetRasterCallbackStore<TState>(), callback, extensionOrdinal);

    internal GraphPassId AddRasterPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        Pipeline pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback,
        int extensionOrdinal) =>
        AddDynamicPass(label, GraphPassKind.Raster, queue, state, options,
            pipeline, parameterLayout, parameterBindings,
            declaration, GetRasterCallbackStore<TState>(), callback, extensionOrdinal);

    internal GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback,
        int extensionOrdinal) =>
        AddDynamicPass(label, GraphPassKind.Compute, queue, state, options,
            null, VariableLayoutReflection.Null, default,
            declaration, GetComputeCallbackStore<TState>(), callback, extensionOrdinal);

    internal GraphPassId AddComputePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        Pipeline pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback,
        int extensionOrdinal) =>
        AddDynamicPass(label, GraphPassKind.Compute, queue, state, options,
            pipeline, parameterLayout, parameterBindings,
            declaration, GetComputeCallbackStore<TState>(), callback, extensionOrdinal);

    internal GraphPassId AddCopyPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        CopyFrameCallback<TState> callback,
        int extensionOrdinal) =>
        AddDynamicPass(label, GraphPassKind.Copy, queue, state, options,
            null, VariableLayoutReflection.Null, default,
            declaration, GetCopyCallbackStore<TState>(), callback, extensionOrdinal);

    internal GraphPassId AddGeneralPass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        GeneralFrameCallback<TState> callback,
        int extensionOrdinal) =>
        AddDynamicPass(label, GraphPassKind.General, queue, state, options,
            null, VariableLayoutReflection.Null, default,
            declaration, GetGeneralCallbackStore<TState>(), callback, extensionOrdinal);

    internal void SetPassPipeline(in GraphIdentity pass, Pipeline pipeline)
    {
        EnsureAuthoring();
        DevelopmentCheck.Require(ReferenceEquals(pipeline.Device, Graph.Device),
            "The Pipeline must belong to the graph Device.");
        DevelopmentCheck.Require(pass.Owner == Identity,
            "The pipeline of a persistent pass must be declared in a graph edit.");
        int index = ResolveDynamicPass(pass);
        FramePass row = _dynamicPasses[index];
        DevelopmentCheck.Require(row.Pipeline is null, "The Pass pipeline is already assigned.");
        row.Pipeline = pipeline;
        _dynamicPasses[index] = row;
    }

    internal void SetPassParameterBlock(
        in GraphIdentity pass,
        VariableLayoutReflection layout,
        ReadOnlySpan<byte> ordinaryData)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(GraphTrace.ParameterUpdates);
        EnsureAuthoring();
        DevelopmentCheck.Require(pass.Owner == Identity,
            "The parameter layout of a persistent pass must be declared in a graph edit.");
        int index = ResolveDynamicPass(pass);
        FramePass row = _dynamicPasses[index];
        DevelopmentCheck.Require(row.ParameterLayout == VariableLayoutReflection.Null,
            "The Pass parameter block is already assigned.");
        row.ParameterLayout = layout;
        row.ParameterOrdinaryData = ordinaryData.ToArray();
        _dynamicPasses[index] = row;
    }

    internal void AddPassParameterBinding(
        in GraphIdentity pass,
        in GraphParameterResourceBinding binding)
    {
        EnsureAuthoring();
        ValidateParameterBinding(binding);
        DevelopmentCheck.Require(pass.Owner == Identity,
            "Shader resource connections for a persistent pass must be declared in a graph edit.");
        int index = ResolveDynamicPass(pass);
        FramePass row = _dynamicPasses[index];
        (row.ParameterBindings ??= []).Add(binding);
        _dynamicPasses[index] = row;
    }

    internal ReadOnlySpan<GraphParameterResourceBinding> UsePersistentParameterBindings(
        in GraphIdentity pass,
        GraphPersistentParameterBindingsId bindings)
    {
        EnsureAuthoring();
        ValidateStatic(bindings.Value, StructureIndex.Structure.PersistentBindings);
        DevelopmentCheck.Require(pass.Owner == Identity,
            "Persistent parameter connections for a persistent pass must be declared in a graph edit.");
        int index = ResolveDynamicPass(pass);
        FramePass row = _dynamicPasses[index];
        (row.PersistentBindings ??= []).Add(bindings.Value);
        _dynamicPasses[index] = row;
        return StructureIndex.Structure.PersistentBindings.Get(bindings.Value).Inventory;
    }

    internal void OrderAfter(GraphPassId pass, GraphPassId predecessor)
    {
        EnsureAuthoring();
        ValidatePassIdentity(pass.Value);
        ValidatePassIdentity(predecessor.Value);
        var order = new ExplicitPassOrder(predecessor.Value, pass.Value);
        _dynamicOrders.Add(order);
        _executor.ChangeDynamicOrder(order, true);
    }

    internal int ResolveExtensionPoint(GraphExtensionPointId point)
    {
        EnsureAuthoring();
        ValidateStatic(point.Value, StructureIndex.Structure.ExtensionPoints);
        return StructureIndex.Structure.ExtensionPoints.DenseIndex(point.Value);
    }

    internal void AddBufferRangeRead(
        in GraphIdentity pass,
        GraphBufferId buffer,
        BufferRangeSet ranges,
        PipelineSync sync,
        ResourceAccess access)
    {
        EnsureAuthoring();
        ValidatePassIdentity(pass);
        ValidateResourceIdentity(buffer.Value, true);
        if (ranges.Count != 0)
            _bufferRangeReads.Add(new BufferRangeRead(pass, buffer.Value, ranges, sync, access));
    }

    internal GraphBufferAccessId AddBufferAccess(
        in GraphIdentity pass,
        GraphBufferId buffer,
        GraphAccessMode mode,
        WriteCoverage coverage,
        PipelineSync sync,
        ResourceAccess access,
        in BufferRange range,
        ResourceContentState? resultContents,
        BufferContentScope contentScope,
        UnorderedAccessHazardMode unorderedAccessHazards)
    {
        EnsureAuthoring();
        ValidatePassIdentity(pass);
        ValidateResourceIdentity(buffer.Value, true);
        GraphIdentity identity = NewIdentity(
            _dynamicAccesses.Count,
            GraphIdentityDomain.Access);
        _dynamicAccesses.Add(new FrameResourceAccess
        {
            Identity = identity,
            Pass = pass,
            TargetKind = GraphAccessTargetKind.Buffer,
            Target = buffer.Value,
            Mode = mode,
            Coverage = coverage,
            Sync = sync,
            Access = access,
            BufferRange = range,
            ResultContents = resultContents,
            BufferContentScope = contentScope,
            UnorderedAccessHazards = unorderedAccessHazards,
        });
        return new GraphBufferAccessId(identity);
    }

    internal GraphTextureAccessId AddTextureAccess(
        in GraphIdentity pass,
        GraphTextureId texture,
        GraphAccessMode mode,
        WriteCoverage coverage,
        PipelineSync sync,
        ResourceAccess access,
        TextureLayout layout,
        in TextureSubresourceRange range,
        ResourceContentState? resultContents,
        UnorderedAccessHazardMode unorderedAccessHazards)
    {
        EnsureAuthoring();
        ValidatePassIdentity(pass);
        ValidateResourceIdentity(texture.Value, false);
        GraphIdentity identity = NewIdentity(
            _dynamicAccesses.Count,
            GraphIdentityDomain.Access);
        _dynamicAccesses.Add(new FrameResourceAccess
        {
            Identity = identity,
            Pass = pass,
            TargetKind = GraphAccessTargetKind.Texture,
            Target = texture.Value,
            Mode = mode,
            Coverage = coverage,
            Sync = sync,
            Access = access,
            TextureLayout = layout,
            TextureRange = range,
            ResultContents = resultContents,
            UnorderedAccessHazards = unorderedAccessHazards,
        });
        return new GraphTextureAccessId(identity);
    }

    internal void AddQueryAccess(
        in GraphIdentity pass,
        GraphQueryPoolId pool,
        GraphAccessMode mode,
        WriteCoverage coverage,
        in QueryRange range,
        ResourceContentState? resultContents)
    {
        EnsureAuthoring();
        ValidatePassIdentity(pass);
        ValidateStatic(pool.Value, StructureIndex.Structure.QueryPools);
        QueryPool resource = StructureIndex.Structure.QueryPools.Get(pool.Value).Resource;
        if (range.QueryCount == 0 || range.FirstQuery >= resource.Description.Count ||
            range.QueryCount > resource.Description.Count - range.FirstQuery)
            throw new ArgumentOutOfRangeException(nameof(range));
        GraphIdentity identity = NewIdentity(
            _dynamicAccesses.Count,
            GraphIdentityDomain.Access);
        _dynamicAccesses.Add(new FrameResourceAccess
        {
            Identity = identity,
            Pass = pass,
            TargetKind = GraphAccessTargetKind.QueryPool,
            Target = pool.Value,
            Mode = mode,
            Coverage = coverage,
            QueryRange = range,
            ResultContents = resultContents,
        });
    }

    internal void AddShaderTableAccess(
        in GraphIdentity pass,
        GraphRayTracingShaderTableId table,
        GraphAccessMode mode,
        WriteCoverage coverage,
        ResourceContentState? resultContents)
    {
        EnsureAuthoring();
        ValidatePassIdentity(pass);
        ValidateStatic(table.Value, StructureIndex.Structure.ShaderTables);
        GraphIdentity identity = NewIdentity(
            _dynamicAccesses.Count,
            GraphIdentityDomain.Access);
        _dynamicAccesses.Add(new FrameResourceAccess
        {
            Identity = identity,
            Pass = pass,
            TargetKind = GraphAccessTargetKind.RayTracingShaderTable,
            Target = table.Value,
            Mode = mode,
            Coverage = coverage,
            ResultContents = resultContents,
        });
    }

    internal FrameView ResolveView(in GraphIdentity identity)
    {
        if (identity.Owner == Graph.Identity)
            return new FrameView(StructureIndex.Structure.Views.Get(identity));
        if (IsDynamicIdentity(identity, GraphIdentityDomain.View, _dynamicViews.Count))
            return _dynamicViews[identity.Slot];
        throw new ArgumentException("The view identity is invalid or stale.");
    }

    internal GraphRayTracingShaderTable ResolveShaderTable(
        GraphRayTracingShaderTableId table)
    {
        ValidateStatic(table.Value, StructureIndex.Structure.ShaderTables);
        return StructureIndex.Structure.ShaderTables.Get(table.Value);
    }

    internal int AddRenderingRegion(
        in GraphIdentity pass,
        uint x,
        uint y,
        uint width,
        uint height)
    {
        _ = ResolveDynamicPass(pass);
        if (!DynamicRenderingRegions.TryGetValue(pass, out List<PassRenderingRegion>? regions))
        {
            regions = [];
            DynamicRenderingRegions.Add(pass, regions);
        }
        int region = regions.Count;
        regions.Add(new PassRenderingRegion(
            x, y, width, height));
        return region;
    }

    internal void AddColorAttachment(in GraphIdentity pass, in GraphColorAttachment attachment)
    {
        if (!DynamicColorAttachments.TryGetValue(pass, out List<GraphColorAttachment>? attachments))
        {
            attachments = [];
            DynamicColorAttachments.Add(pass, attachments);
        }
        attachments.Add(attachment);
    }

    internal void SetDepthStencilAttachment(in GraphIdentity pass, in GraphDepthStencilAttachment attachment)
    {
        if (!DynamicDepthAttachments.TryAdd(pass, attachment))
            throw new InvalidOperationException("The pass already has a depth-stencil attachment.");
    }

    internal readonly Dictionary<GraphIdentity, List<PassRenderingRegion>> DynamicRenderingRegions = [];
    internal readonly Dictionary<GraphIdentity, List<GraphColorAttachment>> DynamicColorAttachments = [];
    internal readonly Dictionary<GraphIdentity, GraphDepthStencilAttachment> DynamicDepthAttachments = [];

    internal int Execute(
        ulong identity,
        Span<QueueCompletion> destination)
    {
        EnsureLease(identity);
        if (_sealed) throw new InvalidOperationException("The render graph frame is sealed.");
        if (destination.Length < Graph.MaximumQueueCompletionCount)
            throw new ArgumentException("The completion destination is too small.", nameof(destination));
        destination[..Graph.MaximumQueueCompletionCount].Clear();
        _sealed = true;
        try
        {
            _executor.Reset();
            Slot.TransientResources!.FlushUploads();
            _executorActive = true;
            return _executor.Execute(destination);
        }
        catch (GraphicsException exception) when (exception.Error == GraphicsError.DeviceLost)
        {
            Graph.MarkDeviceLost();
            throw;
        }
        finally
        {
            _finished = true;
            _executorActive = false;
            Slot.SetCompletions(_executor.SubmittedCompletions);
            FinishFrame(this);
        }
    }

    internal void Cancel(ulong identity)
    {
        if (_finished || Identity != identity) return;
        _finished = true;
        _executorActive = false;
        FinishFrame(null);
    }

    private void FinishFrame(RenderGraphFrameState? frame)
    {
        try
        {
            NotifyExecutionObservers(frame);
        }
        finally
        {
            try
            {
                if (_frameRetirements.Count != 0)
                    Graph.RetireAfterSubmittedFrames(CollectionsMarshal.AsSpan(_frameRetirements));
            }
            finally
            {
                _frameRetirements.Clear();
                ClearFrameData();
                Graph.EndFrame(Identity);
            }
        }
    }

    private GraphPassId AddDynamicPass<TState, TCallback>(
        string label,
        GraphPassKind kind,
        in PassQueueSelection queue,
        in TState state,
        in PassOptions options,
        Pipeline? pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        FramePassCallbackStore callbackStore,
        TCallback callback,
        int extensionOrdinal)
        where TCallback : Delegate
    {
        EnsureAuthoring();
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(declaration);
        Graph.ValidateQueueSelection(queue, kind);
        if (pipeline is not null && !ReferenceEquals(pipeline.Device, Graph.Device))
            throw new ArgumentException("The Pipeline belongs to another Device.", nameof(pipeline));
        if (pipeline is null && parameterLayout != VariableLayoutReflection.Null)
            throw new ArgumentException("A parameter layout requires a Pipeline.", nameof(parameterLayout));
        GraphIdentity identity = NewIdentity(
            _dynamicPasses.Count,
            GraphIdentityDomain.Pass);
        int passIndex = _dynamicPasses.Count;
        int firstAccess = _dynamicAccesses.Count;
        _dynamicPasses.Add(new FramePass
        {
            Identity = identity,
            Label = label,
            Kind = kind,
            QueuePolicy = queue,
            Options = options,
            Pipeline = pipeline,
            ParameterLayout = parameterLayout,
            DeclarationOrdinal = passIndex,
            Enabled = true,
            FrameCallbacks = callbackStore,
            FrameCallbackIndex = -1,
            ExtensionPointIndex = extensionOrdinal,
        });
        TState mutable = state;
        try
        {
            var access = new PassDefinition(this, identity, kind);
            access.Bind(parameterBindings);
            declaration(ref access, ref mutable);
            int item = callbackStore switch
            {
                RasterFramePassCallbackStore<TState> typed when callback is RasterFrameCallback<TState> cb => typed.Add(mutable, cb),
                ComputeFramePassCallbackStore<TState> typed when callback is ComputeFrameCallback<TState> cb => typed.Add(mutable, cb),
                CopyFramePassCallbackStore<TState> typed when callback is CopyFrameCallback<TState> cb => typed.Add(mutable, cb),
                GeneralFramePassCallbackStore<TState> typed when callback is GeneralFrameCallback<TState> cb => typed.Add(mutable, cb),
                _ => throw new InvalidOperationException("The pass callback callbackStore does not match the pass kind."),
            };
            FramePass row = _dynamicPasses[passIndex];
            row.FrameCallbackIndex = item;
            row.FirstAccess = firstAccess;
            row.AccessCount = _dynamicAccesses.Count - firstAccess;
            _dynamicPasses[passIndex] = row;
            return new GraphPassId(identity);
        }
        catch
        {
            if (_dynamicAccesses.Count > firstAccess)
            {
                _dynamicAccesses.RemoveRange(firstAccess, _dynamicAccesses.Count - firstAccess);
            }
            _bufferRangeReads.RemoveAll(read => read.Pass == identity);
            DynamicRenderingRegions.Remove(identity);
            DynamicColorAttachments.Remove(identity);
            DynamicDepthAttachments.Remove(identity);
            _dynamicPasses.RemoveAt(_dynamicPasses.Count - 1);
            throw;
        }
    }

    private RasterFramePassCallbackStore<TState> GetRasterCallbackStore<TState>() =>
        GetCallbackStore(new FramePassCallbackStoreKey(GraphPassKind.Raster, typeof(TState)), static () => new RasterFramePassCallbackStore<TState>());
    private ComputeFramePassCallbackStore<TState> GetComputeCallbackStore<TState>() =>
        GetCallbackStore(new FramePassCallbackStoreKey(GraphPassKind.Compute, typeof(TState)), static () => new ComputeFramePassCallbackStore<TState>());
    private CopyFramePassCallbackStore<TState> GetCopyCallbackStore<TState>() =>
        GetCallbackStore(new FramePassCallbackStoreKey(GraphPassKind.Copy, typeof(TState)), static () => new CopyFramePassCallbackStore<TState>());
    private GeneralFramePassCallbackStore<TState> GetGeneralCallbackStore<TState>() =>
        GetCallbackStore(new FramePassCallbackStoreKey(GraphPassKind.General, typeof(TState)), static () => new GeneralFramePassCallbackStore<TState>());

    private T GetCallbackStore<T>(in FramePassCallbackStoreKey key, Func<T> create) where T : FramePassCallbackStore
    {
        if (_callbackStores.TryGetValue(key, out FramePassCallbackStore? callbackStore)) return (T)callbackStore;
        T value = create();
        _callbackStores.Add(key, value);
        return value;
    }

    private GraphIdentity NewIdentity(int slot, GraphIdentityDomain domain) =>
        new(Identity, slot, 1, domain);

    private int ResolveDynamicPass(in GraphIdentity pass)
    {
        if (!IsDynamicIdentity(pass, GraphIdentityDomain.Pass, _dynamicPasses.Count))
            throw new ArgumentException("The pass identity is invalid or not frame-local.");
        return pass.Slot;
    }

    private void ValidatePassIdentity(in GraphIdentity pass)
    {
        if (pass.Owner == Graph.Identity)
        {
            ValidateStatic(pass, StructureIndex.Structure.Passes);
            return;
        }
        _ = ResolveDynamicPass(pass);
    }

    private void ValidateParameterBinding(in GraphParameterResourceBinding binding)
    {
        if (binding.BeginsParameterBlock || binding.IsNull || binding.ParameterInput is not null)
            return;
        if (binding.Type == ResourceBindingType.Sampler)
        {
            Sampler sampler = binding.Sampler ??
                throw new ArgumentException("A sampler binding has no Sampler.", nameof(binding));
            if (!ReferenceEquals(sampler.Device, Graph.Device))
                throw new ArgumentException("The Sampler belongs to another Device.", nameof(binding));
            return;
        }

        if (binding.Type == ResourceBindingType.AccelerationStructure)
        {
            AccelerationStructureSrv accelerationStructureView = binding.AccelerationStructureSrv ??
                throw new ArgumentException(
                    "An acceleration-structure binding has no SRV.",
                    nameof(binding));
            if (!ReferenceEquals(accelerationStructureView.Device, Graph.Device))
                throw new ArgumentException(
                    "The AccelerationStructureSrv belongs to another Device.",
                    nameof(binding));
            Buffer storage = ResolveKnownBuffer(binding.Value);
            BufferRange range = GraphStructureIndex.ResolveRange(
                binding.BufferRange,
                storage.Info.Size);
            if (!ReferenceEquals(storage, accelerationStructureView.Resource.Info.Storage) ||
                range != accelerationStructureView.Resource.Info.StorageRange)
            {
                throw new ArgumentException(
                    "The acceleration-structure binding does not match its Graph Buffer range.",
                    nameof(binding));
            }
            return;
        }

        if (binding.Type == ResourceBindingType.TextureUav &&
            binding.SecondaryValue.IsValid)
        {
            SamplerFeedbackUav feedbackView = binding.SamplerFeedbackUav ??
                throw new ArgumentException(
                    "A sampler-feedback binding has no UAV.",
                    nameof(binding));
            if (!ReferenceEquals(feedbackView.Device, Graph.Device))
                throw new ArgumentException(
                    "The SamplerFeedbackUav belongs to another Device.",
                    nameof(binding));
            Texture feedback = ResolveKnownTexture(binding.Value);
            Texture sampled = ResolveKnownTexture(binding.SecondaryValue);
            _ = ResolveTextureRange(new GraphTextureId(binding.Value), binding.TextureRange);
            _ = ResolveTextureRange(
                new GraphTextureId(binding.SecondaryValue),
                binding.SecondaryTextureRange);
            if (!ReferenceEquals(feedback, feedbackView.Description.Texture) ||
                !ReferenceEquals(sampled, feedbackView.SampledTexture) ||
                binding.TextureRange != feedbackView.Description.Range)
            {
                throw new ArgumentException(
                    "The sampler-feedback binding does not match its Graph Textures or UAV range.",
                    nameof(binding));
            }
            return;
        }

        if (binding.IsBufferRange)
        {
            ValidateResourceIdentity(binding.BufferInput is null ? binding.Value : ResolveBufferInput(binding.BufferInput).Buffer.Value, true);
            return;
        }

        FrameView view = ResolveView(binding.Value);
        bool valid = binding.Type switch
        {
            ResourceBindingType.ConstantBuffer => view.Kind == GraphViewKind.BufferCbv,
            ResourceBindingType.BufferSrv => view.Kind == GraphViewKind.BufferSrv,
            ResourceBindingType.BufferUav => view.Kind == GraphViewKind.BufferUav,
            ResourceBindingType.TextureSrv => view.Kind == GraphViewKind.TextureSrv,
            ResourceBindingType.TextureUav => view.Kind == GraphViewKind.TextureUav,
            _ => false,
        };
        if (!valid)
            throw new ArgumentException(
                "The parameter binding type does not match its Graph View.",
                nameof(binding));
    }

    private Buffer ResolveKnownBuffer(in GraphIdentity identity)
    {
        ValidateResourceIdentity(identity, buffer: true);
        if (identity.Owner == Graph.Identity)
        {
            if (_bufferData.TryGetValue(identity, out var data)) return data.Buffer;
            GraphBuffer row = StructureIndex.Structure.Buffers.Get(identity);
            if (row.RegisteredResource is not null) return row.RegisteredResource;
            if (row.PersistentResource is not null) return row.PersistentResource;
            if (row.BoundResource is not null) return row.BoundResource;
        }
        else
        {
            Buffer? resource = _dynamicBuffers[identity.Slot].Resource;
            if (resource is not null) return resource;
        }
        throw new InvalidOperationException(
            "A specialized parameter binding requires a registered, bound, or imported Buffer.");
    }

    private Texture ResolveKnownTexture(in GraphIdentity identity)
    {
        ValidateResourceIdentity(identity, buffer: false);
        if (identity.Owner == Graph.Identity)
        {
            GraphTexture row = StructureIndex.Structure.Textures.Get(identity);
            if (row.RegisteredResource is not null) return row.RegisteredResource;
            if (row.PersistentResource is not null) return row.PersistentResource;
            if (row.BoundResource is not null) return row.BoundResource;
        }
        else
        {
            Texture? resource = _dynamicTextures[identity.Slot].Resource;
            if (resource is not null) return resource;
        }
        throw new InvalidOperationException(
            "A specialized parameter binding requires a registered, bound, or imported Texture.");
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void ValidateResourceIdentity(in GraphIdentity resource, bool buffer)
    {
        if (resource.Owner == Graph.Identity)
        {
            if (buffer) ValidateStatic(resource, StructureIndex.Structure.Buffers);
            else ValidateStatic(resource, StructureIndex.Structure.Textures);
            return;
        }
        if (!IsDynamicIdentity(resource, buffer ? GraphIdentityDomain.Buffer : GraphIdentityDomain.Texture,
            buffer ? _dynamicBuffers.Count : _dynamicTextures.Count))
        {
            throw new ArgumentException("The resource identity is invalid or stale.");
        }
    }

    private bool IsDynamicIdentity(in GraphIdentity identity, GraphIdentityDomain domain, int count) =>
        identity.Owner == Identity && identity.Domain == domain && identity.Generation == 1 &&
        (uint)identity.Slot < (uint)count;

    [System.Diagnostics.Conditional("DEBUG")]
    private static void ValidateStatic<T>(in GraphIdentity identity, GraphRows<T> values)
    {
        if (!values.Contains(identity))
            throw new ArgumentException("The graph identity is invalid or stale.");
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private void EnsureAuthoring()
    {
        EnsureLease(Identity);
        if (_sealed) throw new InvalidOperationException("The render graph frame is sealed.");
    }

    private void ClearFrameData()
    {
        foreach (GraphIdentity id in _bufferData.Keys) _executor.ChangeBuffer(id, refreshViews: false);
        _bufferData.Clear();
        foreach (FramePassCallbackStore callbackStore in _callbackStores.Values) callbackStore.Reset();
        _dynamicBuffers.Clear();
        _dynamicTextures.Clear();
        _dynamicViews.Clear();
        _bufferViews.Clear();
        _textureViews.Clear();
        _dynamicPasses.Clear();
        _dynamicAccesses.Clear();
        _bufferRangeReads.Clear();
        foreach (ExplicitPassOrder order in _dynamicOrders)
            _executor.ChangeDynamicOrder(order, false);
        _dynamicOrders.Clear();
        foreach (var image in _swapchainImages)
        {
            if (StructureIndex.Structure.Textures.Contains(image.Identity))
                StructureIndex.Structure.Textures.Get(image.Identity).BoundResource = null;
            Graph.PropertyChanged(image.Identity, GraphStoreKind.Textures);
        }
        _swapchainImages.Clear();
        _importedBuffers.Clear();
        _importedTextures.Clear();
        DynamicRenderingRegions.Clear();
        DynamicColorAttachments.Clear();
        DynamicDepthAttachments.Clear();
        _executionObservers.Clear();
    }

    internal ReadOnlySpan<QueueCompletion> SubmittedCompletions => _executor.SubmittedCompletions;

    internal ReadOnlySpan<BufferBoundaryState> GetBufferStates(GraphBufferId buffer) =>
        _executor.GetBufferStates(buffer);

    internal ReadOnlySpan<TextureBoundaryState> GetTextureStates(GraphTextureId texture) =>
        _executor.GetTextureStates(texture);

    internal ReadOnlySpan<BufferBoundaryState> GetBufferStates(Buffer buffer)
    {
        if (_importedBuffers.TryGetValue(buffer, out GraphBufferId id)) return GetBufferStates(id);
        return StructureIndex.ResourceIdentities.TryGetValue(buffer, out GraphIdentity identity)
            ? GetBufferStates(new GraphBufferId(identity)) : [];
    }

    internal ReadOnlySpan<TextureBoundaryState> GetTextureStates(Texture texture)
    {
        if (_importedTextures.TryGetValue(texture, out GraphTextureId id)) return GetTextureStates(id);
        return StructureIndex.ResourceIdentities.TryGetValue(texture, out GraphIdentity identity)
            ? GetTextureStates(new GraphTextureId(identity)) : [];
    }

    internal bool TryGetPassCompletion(GraphPassId pass, out QueueCompletion completion) =>
        _executor.TryGetPassCompletion(pass, out completion);

    private void NotifyExecutionObservers(RenderGraphFrameState? frame)
    {
        IRenderGraphFrameExecutionObserver[] observers = [.. _executionObservers];
        _executionObservers.Clear();
        List<Exception>? errors = null;
        foreach (IRenderGraphFrameExecutionObserver observer in observers)
        {
            try
            {
                observer.OnRenderGraphFrameFinished(new RenderGraphFrameResult(frame));
            }
            catch (Exception exception)
            {
                (errors ??= []).Add(exception);
            }
        }
        if (errors is not null)
            throw new AggregateException("Render graph completion callbacks failed.", errors);
    }

    internal void NotifySubmitted()
    {
        List<Exception>? errors = null;
        foreach (IRenderGraphFrameExecutionObserver observer in _executionObservers)
        {
            try { observer.OnRenderGraphSubmitted(new RenderGraphFrameResult(this)); }
            catch (Exception exception) { (errors ??= []).Add(exception); }
        }
        if (errors is not null)
            throw new AggregateException("Render graph submission callbacks failed.", errors);
    }

    private sealed class InputData<T>
    {
        internal T Value = default!;
        internal ulong Frame;
    }

    // Input keys can disappear after graph edits. Weak keys avoid keeping removed inputs
    // and their resources alive while preserving values between uses of this frame.
    private readonly ConditionalWeakTable<GraphBufferInput, InputData<GraphBufferSlice>> _bufferInputs = new();

    internal void SetBufferInput(GraphBufferInput input, GraphBufferSlice value)
    {
        EnsureAuthoring();
        DevelopmentCheck.Require(input is not null && input.Graph == Graph.Identity,
            "The buffer input must belong to this graph.");
        ValidateResourceIdentity(value.Buffer.Value, buffer: true);
        BufferDesc description = value.Buffer.Value.Owner == Graph.Identity
            ? StructureIndex.Structure.Buffers.Get(value.Buffer.Value).Description
            : _dynamicBuffers[value.Buffer.Value.Slot].Description;
        DevelopmentCheck.Require((description.Usages & input.Usages) == input.Usages,
            "The buffer does not provide the input's declared usages.");
        BufferRange range = GraphStructureIndex.ResolveRange(value.Range, description.Size);
        InputData<GraphBufferSlice> data = _bufferInputs.GetValue(input, static _ => new());
        GraphBufferSlice next = new(value.Buffer, range);
        bool changed = data.Frame == 0 || !data.Value.Equals(next);
        data.Value = next;
        data.Frame = Identity;
        if (changed) _executor.ChangeBufferInput(input);
    }

    internal GraphBufferSlice ResolveBufferInput(GraphBufferInput input)
    {
        bool found = _bufferInputs.TryGetValue(input, out InputData<GraphBufferSlice>? data) && data.Frame == Identity;
        DevelopmentCheck.Require(found, "A buffer input has no value for this frame.");
        return found ? data!.Value : default;
    }

    private readonly ConditionalWeakTable<GraphTextureInput, InputData<GraphTextureId>> _textureInputs = new();

    internal void SetTextureInput(GraphTextureInput input, GraphTextureId value)
    {
        EnsureAuthoring();
        DevelopmentCheck.Require(input is not null && input.Graph == Graph.Identity,
            "The texture input must belong to this graph.");
        ValidateResourceIdentity(value.Value, buffer: false);
        InputData<GraphTextureId> data = _textureInputs.GetValue(input, static _ => new());
        bool changed = data.Frame == 0 || data.Value != value;
        data.Value = value;
        data.Frame = Identity;
        if (changed) _executor.ChangeTextureInput(input);
    }

    internal GraphTextureId ResolveTextureInput(GraphTextureInput input)
    {
        bool found = _textureInputs.TryGetValue(input, out InputData<GraphTextureId>? data) && data.Frame == Identity;
        DevelopmentCheck.Require(found, "A texture input has no value for this frame.");
        return found ? data!.Value : default;
    }

    internal Texture GetTexture(int pass, GraphTextureInput input) => GetTexture(pass, ResolveTextureInput(input));

    internal BufferRegion GetBufferRegion(int pass, GraphBufferInput input)
    {
        GraphBufferSlice value = ResolveBufferInput(input);
        return new BufferRegion(GetBuffer(pass, value.Buffer), value.Range);
    }

    private readonly ConditionalWeakTable<GraphParameterInput, InputData<ParameterInputValue>> _parameterInputs = new();

    internal void SetParameterInput(GraphParameterInput input, ResourceBinding binding)
    {
        EnsureAuthoring();
        DevelopmentCheck.Require(input is not null && input.Graph == Graph.Identity && input.Type == binding.Type,
            "The binding must match this graph's parameter input.");
        DevelopmentCheck.Require(binding.Value is DeviceResource resource &&
            ReferenceEquals(resource.Device, Graph.Device) && !resource.IsDisposed,
            "A parameter input requires a live resource from this graph's device.");
        GraphIdentity primary = default;
        GraphIdentity secondary = default;
        BufferRange range = default;
        BufferRange secondaryBufferRange = default;
        TextureSubresourceRange textureRange = default;
        TextureSubresourceRange secondaryTextureRange = default;
        switch (binding.Value)
        {
            case BufferCbv view:
                primary = GetImported(view.Description.Buffer).Value;
                range = view.Description.Range;
                break;
            case BufferSrv view:
                primary = GetImported(view.Description.Buffer).Value;
                range = view.Description.Range;
                break;
            case BufferUav view:
                primary = GetImported(view.Description.Buffer).Value;
                range = view.Description.Range;
                if (view.Description.CounterBuffer is { } counterBuffer)
                {
                    secondary = GetImported(counterBuffer).Value;
                    secondaryBufferRange = new BufferRange(view.Description.CounterOffset, sizeof(uint));
                }
                break;
            case Buffer buffer when binding.Type == ResourceBindingType.ConstantBuffer:
                primary = GetImported(buffer).Value;
                range = binding.BufferRange;
                break;
            case TextureSrv view:
                primary = GetImported(view.Description.Texture).Value;
                textureRange = view.Description.Range;
                break;
            case SamplerFeedbackUav view:
                primary = GetImported(view.Description.Texture).Value;
                textureRange = view.Description.Range;
                secondary = GetImported(view.SampledTexture).Value;
                TextureInfo description = view.SampledTexture.Info;
                secondaryTextureRange = new TextureSubresourceRange(0, description.MipLevelCount,
                    0, description.ArrayLayerCount, TextureFormatRules.Aspects(description.Format));
                break;
            case TextureUav view:
                primary = GetImported(view.Description.Texture).Value;
                textureRange = view.Description.Range;
                break;
            case AccelerationStructureSrv view:
                primary = GetImported(view.Resource.Info.Storage).Value;
                range = view.Resource.Info.StorageRange;
                break;
            default:
                DevelopmentCheck.Require(binding.Value is Sampler,
                    "The RHI resource is not supported by a parameter input.");
                break;
        }
        var next = new ParameterInputValue(binding, primary, range, textureRange,
            secondary, secondaryBufferRange, secondaryTextureRange);
        InputData<ParameterInputValue> data = _parameterInputs.GetValue(input, static _ => new());
        bool changed = data.Frame == 0 || data.Value != next;
        data.Value = next;
        data.Frame = Identity;
        if (!changed) return;
        foreach (GraphIdentity access in StructureIndex.ParameterInputAccesses.GetValueOrDefault(input, []))
            _executor.ChangeAccess(access);
        _executor.ChangeParameterInput(input);
    }

    internal ParameterInputValue ResolveParameterInput(GraphParameterInput input)
    {
        bool found = _parameterInputs.TryGetValue(input, out InputData<ParameterInputValue>? data) && data.Frame == Identity;
        DevelopmentCheck.Require(found, "A shader parameter input has no value for this frame.");
        return found ? data!.Value : default;
    }
}

