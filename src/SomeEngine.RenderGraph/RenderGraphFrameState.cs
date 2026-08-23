namespace SomeEngine.RenderGraph;

internal sealed class RenderGraphFrameState
{
    private RenderGraphFrameOptions _options;
    private readonly List<FrameBuffer> _dynamicBuffers = [];
    private readonly List<FrameTexture> _dynamicTextures = [];
    private readonly List<FrameView> _dynamicViews = [];
    private readonly List<FramePass> _dynamicPasses = [];
    private readonly List<FrameResourceAccess> _dynamicAccesses = [];
    private readonly List<ExplicitPassOrder> _dynamicOrders = [];
    private readonly Dictionary<FramePassCallbackStoreKey, FramePassCallbackStore> _callbackStores = [];
    private readonly List<PassCallbackStorage> _populatedPersistentCallbacks = [];
    private readonly Dictionary<GraphIdentity, bool> _enabled = [];
    private readonly Dictionary<GraphIdentity, BufferRange> _bufferRangeOverrides = [];
    private readonly Dictionary<GraphIdentity, TextureSubresourceRange> _textureRangeOverrides = [];
    private readonly Dictionary<GraphIdentity, (Buffer Resource, BufferBoundaryState[] BoundaryStates)> _bufferBindings = [];
    private readonly Dictionary<GraphIdentity, (Texture Resource, TextureBoundaryState[] BoundaryStates)> _textureBindings = [];
    private readonly List<(GraphIdentity Identity, SwapchainImage Image, Queue PresentQueue)> _swapchainImages = [];
    private readonly Dictionary<GraphIdentity, int> _dynamicBufferIndices = [];
    private readonly Dictionary<GraphIdentity, int> _dynamicTextureIndices = [];
    private readonly Dictionary<GraphIdentity, int> _dynamicViewIndices = [];
    private readonly Dictionary<GraphIdentity, int> _dynamicPassIndices = [];
    private readonly Dictionary<GraphIdentity, int> _dynamicAccessIndices = [];
    private readonly Dictionary<Buffer, GraphBufferId> _importedBuffers =
        new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<Texture, GraphTextureId> _importedTextures =
        new(ReferenceEqualityComparer.Instance);
    private readonly FrameExecutor _executor;
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

    internal void Begin(ulong identity, in RenderGraphFrameOptions options)
    {
        if (!_finished)
            throw new InvalidOperationException("The FrameSlot execution is already active.");
        Identity = identity;
        _options = options;
        _sealed = false;
        _finished = false;
        _executorActive = false;
    }

    internal RenderGraph Graph { get; }
    internal FrameSlot Slot { get; }
    internal ulong Identity { get; private set; }
    internal int FrameSlot => Slot.Index;
    internal RenderGraphFrameOptions Options => _options;
    internal IReadOnlyList<FrameBuffer> DynamicBuffers => _dynamicBuffers;
    internal IReadOnlyList<FrameTexture> DynamicTextures => _dynamicTextures;
    internal IReadOnlyList<FrameView> DynamicViews => _dynamicViews;
    internal IReadOnlyList<FramePass> DynamicPasses => _dynamicPasses;
    internal IReadOnlyList<FrameResourceAccess> DynamicAccesses => _dynamicAccesses;
    internal IReadOnlyList<ExplicitPassOrder> DynamicOrders => _dynamicOrders;
    internal IReadOnlyDictionary<GraphIdentity, bool> EnabledOverrides => _enabled;
    internal IReadOnlyDictionary<GraphIdentity, BufferRange> BufferRangeOverrides => _bufferRangeOverrides;
    internal IReadOnlyDictionary<GraphIdentity, TextureSubresourceRange> TextureRangeOverrides => _textureRangeOverrides;
    internal IReadOnlyDictionary<GraphIdentity, (Buffer Resource, BufferBoundaryState[] BoundaryStates)> BufferBindings => _bufferBindings;
    internal IReadOnlyDictionary<GraphIdentity, (Texture Resource, TextureBoundaryState[] BoundaryStates)> TextureBindings => _textureBindings;
    internal IReadOnlyList<(GraphIdentity Identity, SwapchainImage Image, Queue PresentQueue)> SwapchainImages => _swapchainImages;
    internal FrameExecutor Executor => _executorActive
        ? _executor
        : throw new InvalidOperationException("The render graph frame has not been prepared for execution.");
    internal IGraphicsBackend Backend => Graph.Backend;

    internal void EnsureLease(ulong identity)
    {
        if (_finished || identity == 0 || Identity != identity)
            throw new InvalidOperationException("The render graph frame lease is no longer active.");
        Graph.EnsureFrame(identity);
    }

    internal Buffer GetBuffer(int passIndex, GraphBufferId buffer) =>
        Executor.GetBuffer(passIndex, buffer);
    internal Texture GetTexture(int passIndex, GraphTextureId texture) =>
        Executor.GetTexture(passIndex, texture);
    internal BufferCbv GetBufferCbv(int passIndex, GraphBufferCbvId view) =>
        Executor.GetBufferCbv(passIndex, view);
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
        ValidateStatic(pass.Value, Graph.StructureIndex.Structure.Passes);
        _enabled[pass.Value] = enabled;
    }

    internal void SetPassData<T>(GraphPassId pass, in T value)
    {
        EnsureAuthoring();
        ValidateStatic(pass.Value, Graph.StructureIndex.Structure.Passes);
        GraphPass graphPass = Graph.StructureIndex.Structure.Passes.Get(pass.Value);
        if (graphPass.CallbackStorage is not IFramePassDataStorage<T> slot)
        {
            throw new ArgumentException(
                $"The pass expects frame data of type {graphPass.CallbackStorage.FrameDataType.FullName}.",
                nameof(value));
        }
        slot.SetFrameData(FrameSlot, value);
        if (!_populatedPersistentCallbacks.Contains(graphPass.CallbackStorage))
            _populatedPersistentCallbacks.Add(graphPass.CallbackStorage);
    }

    internal void SetBufferRange(GraphBufferAccessId access, in BufferRange range)
    {
        EnsureAuthoring();
        ValidateStatic(access.Value, Graph.StructureIndex.Structure.Accesses);
        PassResourceAccess row = Graph.StructureIndex.Structure.Accesses.Get(access.Value);
        if (row.TargetKind != GraphAccessTargetKind.Buffer || !row.DynamicRange)
            throw new InvalidOperationException("The Buffer access range is not dynamic.");
        _bufferRangeOverrides[access.Value] = range;
    }

    internal void SetTextureRange(GraphTextureAccessId access, in TextureSubresourceRange range)
    {
        EnsureAuthoring();
        ValidateStatic(access.Value, Graph.StructureIndex.Structure.Accesses);
        PassResourceAccess row = Graph.StructureIndex.Structure.Accesses.Get(access.Value);
        if (row.TargetKind != GraphAccessTargetKind.Texture || !row.DynamicRange)
            throw new InvalidOperationException("The Texture access range is not dynamic.");
        _textureRangeOverrides[access.Value] = range;
    }

    internal void BindExternalBuffer(
        GraphBufferId slot,
        Buffer buffer,
        scoped ReadOnlySpan<BufferBoundaryState> boundaryStates)
    {
        EnsureAuthoring();
        ArgumentNullException.ThrowIfNull(buffer);
        ValidateStatic(slot.Value, Graph.StructureIndex.Structure.Buffers);
        GraphBuffer graphBuffer = Graph.StructureIndex.Structure.Buffers.Get(slot.Value);
        if (graphBuffer.Ownership != RenderGraphResourceOwnership.CallerOwned ||
            graphBuffer.Lifetime != RenderGraphResourceLifetime.PerFrame ||
            graphBuffer.RegisteredResource is not null)
            throw new InvalidOperationException("The GraphBufferId is not an unbound external slot.");
        if (!ReferenceEquals(buffer.Device, Graph.Device))
            throw new ArgumentException("The Buffer belongs to another Device.", nameof(buffer));
        _bufferBindings[slot.Value] = (buffer, boundaryStates.ToArray());
    }

    internal void BindExternalTexture(
        GraphTextureId slot,
        Texture texture,
        scoped ReadOnlySpan<TextureBoundaryState> boundaryStates)
    {
        EnsureAuthoring();
        ArgumentNullException.ThrowIfNull(texture);
        ValidateStatic(slot.Value, Graph.StructureIndex.Structure.Textures);
        GraphTexture graphTexture = Graph.StructureIndex.Structure.Textures.Get(slot.Value);
        if (graphTexture.Ownership != RenderGraphResourceOwnership.CallerOwned ||
            graphTexture.Lifetime != RenderGraphResourceLifetime.PerFrame ||
            graphTexture.RegisteredResource is not null)
            throw new InvalidOperationException("The GraphTextureId is not an unbound external slot.");
        if (!ReferenceEquals(texture.Device, Graph.Device))
            throw new ArgumentException("The Texture belongs to another Device.", nameof(texture));
        _textureBindings[slot.Value] = (texture, boundaryStates.ToArray());
    }

    internal GraphBufferId AddBuffer(
        in BufferDesc description,
        MemoryType memoryType,
        RenderGraphResourceOwnership ownership,
        RenderGraphResourceLifetime lifetime)
    {
        EnsureAuthoring();
        GraphIdentity identity = NewIdentity(_dynamicBuffers.Count);
        var buffer = new FrameBuffer
        {
            Identity = identity,
            Description = description,
            MemoryType = memoryType,
            Ownership = ownership,
            Lifetime = lifetime,
            FirstUse = int.MaxValue,
            LastUse = -1,
        };
        _dynamicBufferIndices.Add(identity, _dynamicBuffers.Count);
        _dynamicBuffers.Add(buffer);
        return new GraphBufferId(identity);
    }

    internal GraphBufferId Upload(
        scoped ReadOnlySpan<byte> data,
        BufferUsages usages,
        string? label)
    {
        EnsureAuthoring();
        if (data.IsEmpty)
            throw new ArgumentException("Upload data cannot be empty.", nameof(data));
        GraphBufferId id = AddBuffer(
            new BufferDesc(checked((ulong)data.Length), usages, label),
            MemoryType.Upload,
            RenderGraphResourceOwnership.GraphOwned,
            RenderGraphResourceLifetime.PerFrame);
        int index = _dynamicBufferIndices[id.Value];
        FrameBuffer buffer = _dynamicBuffers[index];
        buffer.InitialData = data.ToArray();
        buffer.EntryBoundaryStates =
        [
            new BufferBoundaryState(
                new BufferRange(0, checked((ulong)data.Length)),
                PipelineSync.None,
                ResourceAccess.NoAccess,
                ResourceContentState.Defined),
        ];
        _dynamicBuffers[index] = buffer;
        return id;
    }

    internal GraphTextureId AddTexture(
        in TextureDesc description,
        RenderGraphResourceOwnership ownership,
        RenderGraphResourceLifetime lifetime)
    {
        EnsureAuthoring();
        GraphIdentity identity = NewIdentity(_dynamicTextures.Count);
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
            FirstUse = int.MaxValue,
            LastUse = -1,
        };
        _dynamicTextureIndices.Add(identity, _dynamicTextures.Count);
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
        if (range.MipLevelCount == 0 || range.ArrayLayerCount == 0 ||
            range.Aspects == TextureAspects.None ||
            (range.Aspects & ~allowedAspects) != TextureAspects.None ||
            range.FirstMipLevel >= mipCount ||
            range.MipLevelCount > mipCount - range.FirstMipLevel ||
            range.FirstArrayLayer >= layerCount ||
            range.ArrayLayerCount > layerCount - range.FirstArrayLayer)
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }
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
            GraphTexture row = Graph.StructureIndex.Structure.Textures.Get(texture.Value);
            dimension = row.Dimension;
            mipCount = row.MipLevelCount;
            layerCount = row.ArrayLayerCount;
            sampleCount = row.SampleCount;
            format = row.Format;
            return;
        }
        FrameTexture dynamic = _dynamicTextures[_dynamicTextureIndices[texture.Value]];
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
        if (!ReferenceEquals(buffer.Device, Graph.Device))
            throw new ArgumentException("The Buffer belongs to another Device.", nameof(buffer));
        GraphBufferId id = AddBuffer(
            new BufferDesc(buffer.Info.Size, buffer.Info.Usages, buffer.Label,
                new ResourceNodePlacement(buffer.Info.CreationNodeMask, buffer.Info.VisibleNodeMask)),
            buffer.Info.MemoryType,
            RenderGraphResourceOwnership.CallerOwned,
            RenderGraphResourceLifetime.PerFrame);
        int index = _dynamicBufferIndices[id.Value];
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
        int index = _dynamicTextureIndices[id.Value];
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
        ArgumentNullException.ThrowIfNull(buffer);
        return _importedBuffers.TryGetValue(buffer, out GraphBufferId id)
            ? id
            : throw new ArgumentException("The Buffer has not been imported by this frame.", nameof(buffer));
    }

    internal GraphTextureId GetImported(Texture texture)
    {
        EnsureAuthoring();
        ArgumentNullException.ThrowIfNull(texture);
        return _importedTextures.TryGetValue(texture, out GraphTextureId id)
            ? id
            : throw new ArgumentException("The Texture has not been imported by this frame.", nameof(texture));
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
        GraphIdentity identity = NewIdentity(0x10000000 + _dynamicViews.Count);
        _dynamicViewIndices.Add(identity, _dynamicViews.Count);
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
        GraphIdentity identity = NewIdentity(0x10000000 + _dynamicViews.Count);
        _dynamicViewIndices.Add(identity, _dynamicViews.Count);
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
        if (!ReferenceEquals(pipeline.Device, Graph.Device))
            throw new ArgumentException("The Pipeline belongs to another Device.", nameof(pipeline));
        int index = ResolveDynamicPass(pass);
        FramePass row = _dynamicPasses[index];
        if (row.Pipeline is not null)
            throw new InvalidOperationException("The Pass pipeline is already assigned.");
        row.Pipeline = pipeline;
        _dynamicPasses[index] = row;
    }

    internal void SetPassParameterBlock(
        in GraphIdentity pass,
        VariableLayoutReflection layout,
        ReadOnlySpan<byte> ordinaryData)
    {
        EnsureAuthoring();
        int index = ResolveDynamicPass(pass);
        FramePass row = _dynamicPasses[index];
        if (row.ParameterLayout != VariableLayoutReflection.Null)
            throw new InvalidOperationException("The Pass parameter block is already assigned.");
        row.ParameterLayout = layout;
        row.ParameterOrdinaryData = ordinaryData.ToArray();
        _dynamicPasses[index] = row;
    }

    internal void AddPassParameterBinding(
        in GraphIdentity pass,
        in GraphParameterResourceBinding binding)
    {
        EnsureAuthoring();
        int index = ResolveDynamicPass(pass);
        ValidateParameterBinding(binding);
        FramePass row = _dynamicPasses[index];
        (row.ParameterBindings ??= []).Add(binding);
        _dynamicPasses[index] = row;
    }

    internal ReadOnlySpan<GraphParameterResourceBinding> UsePersistentParameterBindings(
        in GraphIdentity pass,
        GraphPersistentParameterBindingsId bindings)
    {
        EnsureAuthoring();
        ValidateStatic(bindings.Value, Graph.StructureIndex.Structure.PersistentBindings);
        int index = ResolveDynamicPass(pass);
        FramePass row = _dynamicPasses[index];
        (row.PersistentBindings ??= []).Add(bindings.Value);
        _dynamicPasses[index] = row;
        return Graph.StructureIndex.Structure.PersistentBindings.Get(bindings.Value).Inventory;
    }

    internal void OrderAfter(GraphPassId pass, GraphPassId predecessor)
    {
        EnsureAuthoring();
        ValidatePassIdentity(pass.Value);
        ValidatePassIdentity(predecessor.Value);
        _dynamicOrders.Add(new ExplicitPassOrder(predecessor.Value, pass.Value));
    }

    internal int ResolveExtensionPoint(GraphExtensionPointId point)
    {
        EnsureAuthoring();
        ValidateStatic(point.Value, Graph.StructureIndex.Structure.ExtensionPoints);
        return Graph.StructureIndex.Structure.ExtensionPoints.DenseIndex(point.Value);
    }

    internal GraphBufferAccessId AddBufferAccess(
        in GraphIdentity pass,
        GraphBufferId buffer,
        GraphAccessMode mode,
        WriteCoverage coverage,
        PipelineSync sync,
        ResourceAccess access,
        in BufferRange range,
        ResourceContentState? resultContents)
    {
        ValidateResourceIdentity(buffer.Value, true);
        GraphIdentity identity = NewIdentity(0x20000000 + _dynamicAccesses.Count);
        _dynamicAccessIndices.Add(identity, _dynamicAccesses.Count);
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
        ResourceContentState? resultContents)
    {
        ValidateResourceIdentity(texture.Value, false);
        GraphIdentity identity = NewIdentity(0x20000000 + _dynamicAccesses.Count);
        _dynamicAccessIndices.Add(identity, _dynamicAccesses.Count);
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
        ValidateStatic(pool.Value, Graph.StructureIndex.Structure.QueryPools);
        QueryPool resource = Graph.StructureIndex.Structure.QueryPools.Get(pool.Value).Resource;
        if (range.QueryCount == 0 || range.FirstQuery >= resource.Description.Count ||
            range.QueryCount > resource.Description.Count - range.FirstQuery)
            throw new ArgumentOutOfRangeException(nameof(range));
        GraphIdentity identity = NewIdentity(0x20000000 + _dynamicAccesses.Count);
        _dynamicAccessIndices.Add(identity, _dynamicAccesses.Count);
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
        ValidateStatic(table.Value, Graph.StructureIndex.Structure.ShaderTables);
        GraphIdentity identity = NewIdentity(0x20000000 + _dynamicAccesses.Count);
        _dynamicAccessIndices.Add(identity, _dynamicAccesses.Count);
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

    internal GraphView ResolveView(in GraphIdentity identity)
    {
        if (identity.Owner == Graph.Identity)
            return Graph.StructureIndex.Structure.Views.Get(identity);
        if (identity.Owner == Identity && _dynamicViewIndices.TryGetValue(identity, out int index))
        {
            FrameView row = _dynamicViews[index];
            return new GraphView
            {
                Kind = row.Kind,
                Buffer = row.Buffer,
                Texture = row.Texture,
                AdditionalBuffer = row.AdditionalBuffer,
                BufferRange = row.BufferRange,
                TextureRange = row.TextureRange,
                BufferFormat = row.BufferFormat,
                TextureFormat = row.TextureFormat,
                StructureStride = row.StructureStride,
                CounterOffset = row.CounterOffset,
                Dimension = row.Dimension,
                ReadOnlyDepth = row.ReadOnlyDepth,
                ReadOnlyStencil = row.ReadOnlyStencil,
                Label = row.Label,
            };
        }
        throw new ArgumentException("The view identity is invalid or stale.");
    }

    internal GraphRayTracingShaderTable ResolveShaderTable(
        GraphRayTracingShaderTableId table)
    {
        ValidateStatic(table.Value, Graph.StructureIndex.Structure.ShaderTables);
        return Graph.StructureIndex.Structure.ShaderTables.Get(table.Value);
    }

    internal int AddRenderingRegion(
        in GraphIdentity pass,
        uint x,
        uint y,
        uint width,
        uint height,
        uint firstArrayLayer,
        uint arrayLayerCount)
    {
        _ = ResolveDynamicPass(pass);
        if (!DynamicRenderingRegions.TryGetValue(pass, out List<PassRenderingRegion>? regions))
        {
            regions = [];
            DynamicRenderingRegions.Add(pass, regions);
        }
        int region = regions.Count;
        regions.Add(new PassRenderingRegion(
            x, y, width, height, firstArrayLayer, arrayLayerCount));
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
            _executorActive = true;
            int result = _executor.Execute(destination);
            _finished = true;
            _executorActive = false;
            Slot.SetCompletions(_executor.SubmittedCompletions);
            ClearFrameData();
            Graph.EndFrame(Identity);
            return result;
        }
        catch (GraphicsException exception) when (exception.Error == GraphicsError.DeviceLost)
        {
            Slot.SetCompletions(_executor.SubmittedCompletions);
            Graph.MarkDeviceLost();
            _finished = true;
            _executorActive = false;
            ClearFrameData();
            Graph.EndFrame(Identity);
            throw;
        }
        catch
        {
            Slot.SetCompletions(_executor.SubmittedCompletions);
            _finished = true;
            _executorActive = false;
            ClearFrameData();
            Graph.EndFrame(Identity);
            throw;
        }
    }

    internal void Cancel(ulong identity)
    {
        if (_finished || Identity != identity) return;
        _finished = true;
        _executorActive = false;
        ClearFrameData();
        Graph.EndFrame(Identity);
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
        GraphIdentity identity = NewIdentity(0x30000000 + _dynamicPasses.Count);
        int passIndex = _dynamicPasses.Count;
        int firstAccess = _dynamicAccesses.Count;
        _dynamicPassIndices.Add(identity, passIndex);
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
                for (int i = _dynamicAccesses.Count - 1; i >= firstAccess; i--)
                    _dynamicAccessIndices.Remove(_dynamicAccesses[i].Identity);
                _dynamicAccesses.RemoveRange(firstAccess, _dynamicAccesses.Count - firstAccess);
            }
            DynamicRenderingRegions.Remove(identity);
            DynamicColorAttachments.Remove(identity);
            DynamicDepthAttachments.Remove(identity);
            _dynamicPasses.RemoveAt(_dynamicPasses.Count - 1);
            _dynamicPassIndices.Remove(identity);
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

    private GraphIdentity NewIdentity(int slot) => new(Identity, slot, 1);

    private int ResolveDynamicPass(in GraphIdentity pass)
    {
        if (pass.Owner != Identity || !_dynamicPassIndices.TryGetValue(pass, out int index))
            throw new ArgumentException("The pass identity is invalid or not frame-local.");
        return index;
    }

    private void ValidatePassIdentity(in GraphIdentity pass)
    {
        if (pass.Owner == Graph.Identity)
        {
            ValidateStatic(pass, Graph.StructureIndex.Structure.Passes);
            return;
        }
        _ = ResolveDynamicPass(pass);
    }

    private void ValidateParameterBinding(in GraphParameterResourceBinding binding)
    {
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

        GraphView view = ResolveView(binding.Value);
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
            GraphBuffer row = Graph.StructureIndex.Structure.Buffers.Get(identity);
            if (row.RegisteredResource is not null) return row.RegisteredResource;
            if (row.PersistentResource is not null) return row.PersistentResource;
            if (_bufferBindings.TryGetValue(identity, out var binding)) return binding.Resource;
        }
        else
        {
            Buffer? resource = _dynamicBuffers[_dynamicBufferIndices[identity]].Resource;
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
            GraphTexture row = Graph.StructureIndex.Structure.Textures.Get(identity);
            if (row.RegisteredResource is not null) return row.RegisteredResource;
            if (row.PersistentResource is not null) return row.PersistentResource;
            if (_textureBindings.TryGetValue(identity, out var binding)) return binding.Resource;
        }
        else
        {
            Texture? resource = _dynamicTextures[_dynamicTextureIndices[identity]].Resource;
            if (resource is not null) return resource;
        }
        throw new InvalidOperationException(
            "A specialized parameter binding requires a registered, bound, or imported Texture.");
    }

    private void ValidateResourceIdentity(in GraphIdentity resource, bool buffer)
    {
        if (resource.Owner == Graph.Identity)
        {
            if (buffer) ValidateStatic(resource, Graph.StructureIndex.Structure.Buffers);
            else ValidateStatic(resource, Graph.StructureIndex.Structure.Textures);
            return;
        }
        if (resource.Owner != Identity ||
            !(buffer ? _dynamicBufferIndices.ContainsKey(resource) : _dynamicTextureIndices.ContainsKey(resource)))
        {
            throw new ArgumentException("The resource identity is invalid or stale.");
        }
    }

    private static void ValidateStatic<T>(in GraphIdentity identity, SlotMap<T> values)
    {
        if (!values.Contains(identity))
            throw new ArgumentException("The graph identity is invalid or stale.");
    }

    private void EnsureAuthoring()
    {
        EnsureLease(Identity);
        if (_sealed) throw new InvalidOperationException("The render graph frame is sealed.");
    }

    private void ClearFrameData()
    {
        foreach (FramePassCallbackStore callbackStore in _callbackStores.Values) callbackStore.Reset();
        foreach (PassCallbackStorage storage in _populatedPersistentCallbacks)
            storage.ClearFrameData(FrameSlot);
        _populatedPersistentCallbacks.Clear();
        _bufferBindings.Clear();
        _textureBindings.Clear();
        _bufferRangeOverrides.Clear();
        _textureRangeOverrides.Clear();
        _enabled.Clear();
        _dynamicBuffers.Clear();
        _dynamicTextures.Clear();
        _dynamicViews.Clear();
        _dynamicPasses.Clear();
        _dynamicAccesses.Clear();
        _dynamicOrders.Clear();
        _swapchainImages.Clear();
        _dynamicBufferIndices.Clear();
        _dynamicTextureIndices.Clear();
        _dynamicViewIndices.Clear();
        _dynamicPassIndices.Clear();
        _dynamicAccessIndices.Clear();
        _importedBuffers.Clear();
        _importedTextures.Clear();
        DynamicRenderingRegions.Clear();
        DynamicColorAttachments.Clear();
        DynamicDepthAttachments.Clear();
    }
}

