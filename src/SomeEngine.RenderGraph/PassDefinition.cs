namespace SomeEngine.RenderGraph;

public ref partial struct PassDefinition
{
    private RenderGraphEdit _edit;
    private RenderGraphFrameState? _frame;
    private readonly ulong _frameIdentity;
    private readonly GraphIdentity _pass;
    private readonly GraphPassKind _kind;

    internal PassDefinition(RenderGraphEdit edit, GraphIdentity pass, GraphPassKind kind)
    {
        _edit = edit;
        _frame = null;
        _pass = pass;
        _kind = kind;
    }

    internal PassDefinition(RenderGraphFrameState frame, GraphIdentity pass, GraphPassKind kind)
    {
        _edit = default;
        _frame = frame;
        _frameIdentity = frame.Identity;
        _pass = pass;
        _kind = kind;
    }

    private RenderGraphFrameState Frame
    {
        get
        {
            _frame!.EnsureLease(_frameIdentity);
            return _frame;
        }
    }

    /// <summary>Returns the stable graph identity of this pass.</summary>
    public GraphPassId Id => new(_pass);

    public void SetPipeline(Pipeline pipeline)
    {
        DevelopmentCheck.Require(pipeline is not null, "The pipeline cannot be null.");
        if (_frame is null) _edit.SetPassPipeline(_pass, pipeline);
        else Frame.SetPassPipeline(_pass, pipeline);
    }

    public void SetParameterBlock(
        ShaderVariableLayout layout,
        ReadOnlySpan<byte> ordinaryData = default)
    {
        DevelopmentCheck.Require(layout != ShaderVariableLayout.Null, "The parameter layout cannot be null.");
        if (_frame is null) _edit.SetPassParameterBlock(_pass, layout, ordinaryData);
        else Frame.SetPassParameterBlock(_pass, layout, ordinaryData);
    }

    public GraphBufferAccessId Bind(GraphBufferInput buffer, PipelineSync sync)
    {
        GraphBufferAccessId access = Read(buffer, BufferRange.Whole, sync, ResourceAccess.ConstantBuffer);
        AddParameterBinding(GraphParameterResourceBinding.ConstantBuffer(buffer, sync));
        return access;
    }

    public GraphBufferAccessId Read(GraphBufferInput buffer, in BufferRange range,
        PipelineSync sync, ResourceAccess access, BufferContentScope contentScope = BufferContentScope.FullRange)
    {
        if (_frame is null) return _edit.AddBufferInputAccess(_pass, buffer, sync, access, range, contentScope);
        GraphBufferSlice value = Frame.ResolveBufferInput(buffer);
        return Read(value.Buffer, value.ResolveRange(range), sync, access, contentScope: contentScope);
    }

    public GraphBufferAccessId Write(GraphBufferInput buffer, in BufferRange range,
        PipelineSync sync, ResourceAccess access, WriteCoverage coverage,
        ResourceContentState? resultContents = null, BufferContentScope contentScope = BufferContentScope.FullRange) =>
        AddBufferInputAccess(buffer, range, sync, access, GraphAccessMode.Write, coverage, resultContents, contentScope);

    public GraphBufferAccessId ReadWrite(GraphBufferInput buffer, in BufferRange range,
        PipelineSync sync, ResourceAccess access, ResourceContentState? resultContents = null,
        BufferContentScope contentScope = BufferContentScope.FullRange,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered) =>
        AddBufferInputAccess(buffer, range, sync, access, GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            resultContents, contentScope, unorderedAccessHazards);

    private GraphBufferAccessId AddBufferInputAccess(GraphBufferInput buffer, BufferRange range,
        PipelineSync sync, ResourceAccess access, GraphAccessMode mode, WriteCoverage coverage,
        ResourceContentState? resultContents, BufferContentScope contentScope,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        UnorderedAccessHazardRules.Validate(unorderedAccessHazards, mode, coverage, access,
            TextureLayout.Undefined, resultContents);
        if (_frame is null)
            return _edit.AddBufferInputAccess(_pass, buffer, sync, access, range, contentScope,
                mode, coverage, resultContents, unorderedAccessHazards);
        GraphBufferSlice value = Frame.ResolveBufferInput(buffer);
        return AddBufferAccess(value.Buffer, mode, coverage, sync, access, value.ResolveRange(range),
            false, resultContents, contentScope, unorderedAccessHazards);
    }

    public GraphBufferAccessId Bind(GraphBufferSlice buffer, PipelineSync sync)
    {
        GraphBufferAccessId access = Read(buffer.Buffer, buffer.Range, sync, ResourceAccess.ConstantBuffer);
        AddParameterBinding(GraphParameterResourceBinding.ConstantBuffer(buffer, sync));
        return access;
    }

    public GraphBufferAccessId Bind(GraphBufferCbvId view, PipelineSync sync)
    {
        GraphBufferAccessId access = Read(view, sync);
        AddParameterBinding(GraphParameterResourceBinding.ConstantBuffer(view, sync));
        return access;
    }

    public GraphBufferAccessId Bind(
        GraphBufferSrvId view,
        PipelineSync sync,
        BufferContentScope contentScope = BufferContentScope.FullRange)
    {
        GraphBufferAccessId access = Read(view, sync, contentScope);
        AddParameterBinding(GraphParameterResourceBinding.ReadOnlyBuffer(view, sync, contentScope));
        return access;
    }

    /// <summary>Starts the next reflected parameter block in this pass's binding stream.</summary>
    public void BindParameterBlock(ShaderVariableLayout layout, ReadOnlySpan<byte> ordinaryData = default) =>
        Bind(GraphParameterResourceBinding.BeginBlock(layout, ordinaryData));

    public GraphBufferAccessId Bind(
        GraphBufferUavId view,
        PipelineSync sync,
        GraphAccessMode mode = GraphAccessMode.ReadWrite,
        WriteCoverage coverage = WriteCoverage.Partial,
        ResourceContentState? resultContents = null,
        BufferContentScope contentScope = BufferContentScope.FullRange,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        GraphBufferAccessId access = mode switch
        {
            GraphAccessMode.Read => Read(view, sync, contentScope),
            GraphAccessMode.Write => Write(view, sync, coverage, resultContents, contentScope),
            GraphAccessMode.ReadWrite => ReadWrite(
                view, sync, resultContents, contentScope, unorderedAccessHazards),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        AddParameterBinding(GraphParameterResourceBinding.WritableBuffer(
            view, sync, mode, coverage, resultContents, contentScope, unorderedAccessHazards));
        return access;
    }

    public GraphTextureAccessId Bind(
        GraphTextureSrvId view,
        PipelineSync sync,
        TextureLayout layout = TextureLayout.ShaderResource)
    {
        GraphTextureAccessId access = Read(view, sync, layout);
        AddParameterBinding(GraphParameterResourceBinding.SampledTexture(view, sync, layout));
        return access;
    }

    public GraphTextureAccessId Bind(
        GraphTextureUavId view,
        PipelineSync sync,
        GraphAccessMode mode = GraphAccessMode.ReadWrite,
        WriteCoverage coverage = WriteCoverage.Partial,
        ResourceContentState? resultContents = null,
        TextureLayout layout = TextureLayout.UnorderedAccess,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        GraphTextureAccessId access = mode switch
        {
            GraphAccessMode.Read => Read(view, sync, layout),
            GraphAccessMode.Write => Write(view, sync, coverage, resultContents, layout),
            GraphAccessMode.ReadWrite => ReadWrite(
                view, sync, resultContents, layout, unorderedAccessHazards),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        AddParameterBinding(GraphParameterResourceBinding.StorageTexture(
            view, sync, mode, coverage, resultContents, layout, unorderedAccessHazards));
        return access;
    }

    public void Bind(Sampler sampler)
    {
        ArgumentNullException.ThrowIfNull(sampler);
        Bind(GraphParameterResourceBinding.SampledWith(sampler));
    }

    public void Bind(GraphPersistentParameterBindingsId bindings)
    {
        ReadOnlySpan<GraphParameterResourceBinding> inventory = _frame is null
            ? _edit.UsePersistentParameterBindings(_pass, bindings)
            : Frame.UsePersistentParameterBindings(_pass, bindings);
        foreach (ref readonly GraphParameterResourceBinding binding in inventory)
            DeclareParameterBinding(binding);
    }

    public void Bind(in GraphParameterResourceBinding binding)
    {
        DeclareParameterBinding(binding);
        AddParameterBinding(binding);
    }

    private void DeclareParameterBinding(in GraphParameterResourceBinding binding)
    {
        if (binding.RequiresExplicitAccess)
            return;
        switch (binding.Type)
        {
            case ResourceBindingType.ConstantBuffer:
                if (binding.BufferInput is not null)
                    _ = Read(binding.BufferInput, binding.BufferRange, binding.Sync, ResourceAccess.ConstantBuffer);
                else if (binding.IsBufferRange)
                    _ = Read(new GraphBufferId(binding.Value), binding.BufferRange, binding.Sync, ResourceAccess.ConstantBuffer);
                else _ = Read(new GraphBufferCbvId(binding.Value), binding.Sync);
                break;
            case ResourceBindingType.BufferSrv:
                _ = Read(
                    new GraphBufferSrvId(binding.Value),
                    binding.Sync,
                    binding.BufferContentScope);
                break;
            case ResourceBindingType.BufferUav:
                if (binding.Mode == GraphAccessMode.Read)
                {
                    _ = Read(
                        new GraphBufferUavId(binding.Value),
                        binding.Sync,
                        binding.BufferContentScope);
                }
                else if (binding.Mode == GraphAccessMode.Write)
                {
                    _ = Write(new GraphBufferUavId(binding.Value), binding.Sync,
                        binding.Coverage, binding.ResultContents, binding.BufferContentScope);
                }
                else
                {
                    _ = ReadWrite(new GraphBufferUavId(binding.Value), binding.Sync,
                        binding.ResultContents, binding.BufferContentScope,
                        binding.UnorderedAccessHazards);
                }
                break;
            case ResourceBindingType.TextureSrv:
                _ = Read(new GraphTextureSrvId(binding.Value), binding.Sync, binding.Layout);
                break;
            case ResourceBindingType.TextureUav:
                if (binding.SecondaryValue.IsValid)
                {
                    _ = Write(
                        new GraphTextureId(binding.Value),
                        binding.TextureRange,
                        binding.Sync,
                        ResourceAccess.UnorderedAccess,
                        TextureLayout.UnorderedAccess,
                        binding.Coverage,
                        binding.ResultContents);
                    _ = Read(
                        new GraphTextureId(binding.SecondaryValue),
                        binding.SecondaryTextureRange,
                        binding.Sync,
                        ResourceAccess.ShaderResource,
                        TextureLayout.ShaderResource);
                    break;
                }
                if (binding.Mode == GraphAccessMode.Read)
                {
                    _ = Read(new GraphTextureUavId(binding.Value), binding.Sync, binding.Layout);
                }
                else if (binding.Mode == GraphAccessMode.Write)
                {
                    _ = Write(new GraphTextureUavId(binding.Value), binding.Sync,
                        binding.Coverage, binding.ResultContents, binding.Layout);
                }
                else
                {
                    _ = ReadWrite(new GraphTextureUavId(binding.Value), binding.Sync,
                        binding.ResultContents, binding.Layout,
                        binding.UnorderedAccessHazards);
                }
                break;
            case ResourceBindingType.AccelerationStructure:
                _ = Read(
                    new GraphBufferId(binding.Value),
                    binding.BufferRange,
                    binding.Sync,
                    ResourceAccess.RayTracingAccelerationStructureRead);
                break;
            case ResourceBindingType.Sampler:
                break;
            default:
                throw new ArgumentException("The graph parameter binding type is unsupported.", nameof(binding));
        }
    }

    public void Bind(ReadOnlySpan<GraphParameterResourceBinding> bindings)
    {
        foreach (ref readonly GraphParameterResourceBinding binding in bindings)
            Bind(binding);
    }

    public GraphBufferAccessId Read(
        GraphBufferId buffer,
        in BufferRange range,
        PipelineSync sync,
        ResourceAccess access,
        bool dynamicRange = false,
        BufferContentScope contentScope = BufferContentScope.FullRange) =>
        AddBufferAccess(buffer, GraphAccessMode.Read, WriteCoverage.Partial,
            sync, access, range, dynamicRange, contentScope: contentScope);

    public GraphBufferAccessId Write(
        GraphBufferId buffer,
        in BufferRange range,
        PipelineSync sync,
        ResourceAccess access,
        WriteCoverage coverage,
        ResourceContentState? resultContents = null,
        bool dynamicRange = false,
        BufferContentScope contentScope = BufferContentScope.FullRange) =>
        AddBufferAccess(buffer, GraphAccessMode.Write, coverage,
            sync, access, range, dynamicRange, resultContents, contentScope);

    /// <summary>Declares shared sparse reads independently of descriptor reachability.</summary>
    public void ReadRanges(
        GraphBufferId buffer,
        BufferRangeSet ranges,
        PipelineSync sync,
        ResourceAccess access)
    {
        ArgumentNullException.ThrowIfNull(ranges);
        if (_frame is not null)
            Frame.AddBufferRangeRead(_pass, buffer, ranges, sync, access);
        else
            foreach (ref readonly BufferRange range in ranges.Ranges)
                _ = Read(buffer, range, sync, access);
    }

    public GraphBufferAccessId ReadWrite(
        GraphBufferId buffer,
        in BufferRange range,
        PipelineSync sync,
        ResourceAccess access,
        ResourceContentState? resultContents = null,
        bool dynamicRange = false,
        BufferContentScope contentScope = BufferContentScope.FullRange,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered) =>
        AddBufferAccess(buffer, GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            sync, access, range, dynamicRange, resultContents, contentScope,
            unorderedAccessHazards);

    public GraphTextureAccessId Read(
        GraphTextureId texture,
        in TextureSubresourceRange range,
        PipelineSync sync,
        ResourceAccess access,
        TextureLayout layout,
        bool dynamicRange = false) =>
        AddTextureAccess(texture, GraphAccessMode.Read, WriteCoverage.Partial,
            sync, access, layout, range, dynamicRange);

    public GraphTextureAccessId Write(
        GraphTextureId texture,
        in TextureSubresourceRange range,
        PipelineSync sync,
        ResourceAccess access,
        TextureLayout layout,
        WriteCoverage coverage,
        ResourceContentState? resultContents = null,
        bool dynamicRange = false) =>
        AddTextureAccess(texture, GraphAccessMode.Write, coverage,
            sync, access, layout, range, dynamicRange, resultContents);

    public GraphTextureAccessId ReadWrite(
        GraphTextureId texture,
        in TextureSubresourceRange range,
        PipelineSync sync,
        ResourceAccess access,
        TextureLayout layout,
        ResourceContentState? resultContents = null,
        bool dynamicRange = false,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered) =>
        AddTextureAccess(texture, GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            sync, access, layout, range, dynamicRange, resultContents,
            unorderedAccessHazards);

    public GraphBufferAccessId Read(GraphBufferCbvId view, PipelineSync sync) =>
        ReadBufferView(view.Value, sync, ResourceAccess.ConstantBuffer);

    public GraphBufferAccessId Read(
        GraphBufferSrvId view,
        PipelineSync sync,
        BufferContentScope contentScope = BufferContentScope.FullRange) =>
        ReadBufferView(view.Value, sync, ResourceAccess.ShaderResource, contentScope);

    public GraphBufferAccessId Write(
        GraphBufferUavId view,
        PipelineSync sync,
        WriteCoverage coverage,
        ResourceContentState? resultContents = null,
        BufferContentScope contentScope = BufferContentScope.FullRange) =>
        WriteBufferView(view.Value, sync, ResourceAccess.UnorderedAccess,
            coverage, false, resultContents, contentScope);

    public GraphBufferAccessId Read(
        GraphBufferUavId view,
        PipelineSync sync,
        BufferContentScope contentScope = BufferContentScope.FullRange) =>
        ReadBufferUav(view.Value, sync, contentScope);

    public GraphBufferAccessId ReadWrite(
        GraphBufferUavId view,
        PipelineSync sync,
        ResourceContentState? resultContents = null,
        BufferContentScope contentScope = BufferContentScope.FullRange,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered) =>
        WriteBufferView(view.Value, sync, ResourceAccess.UnorderedAccess,
            WriteCoverage.Partial, true, resultContents, contentScope,
            unorderedAccessHazards);

    public GraphTextureAccessId Read(
        GraphTextureSrvId view,
        PipelineSync sync,
        TextureLayout layout = TextureLayout.ShaderResource) =>
        ReadTextureView(view.Value, sync, ResourceAccess.ShaderResource, layout);

    public GraphTextureAccessId Write(
        GraphTextureUavId view,
        PipelineSync sync,
        WriteCoverage coverage,
        ResourceContentState? resultContents = null,
        TextureLayout layout = TextureLayout.UnorderedAccess) =>
        WriteTextureView(view.Value, sync, ResourceAccess.UnorderedAccess,
            layout, coverage, false, resultContents);

    public GraphTextureAccessId Read(
        GraphTextureUavId view,
        PipelineSync sync,
        TextureLayout layout = TextureLayout.UnorderedAccess) =>
        ReadTextureUav(view.Value, sync, layout);

    public GraphTextureAccessId ReadWrite(
        GraphTextureUavId view,
        PipelineSync sync,
        ResourceContentState? resultContents = null,
        TextureLayout layout = TextureLayout.UnorderedAccess,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered) =>
        WriteTextureView(view.Value, sync, ResourceAccess.UnorderedAccess,
            layout, WriteCoverage.Partial, true, resultContents,
            unorderedAccessHazards);

    public void Read(
        GraphQueryPoolId pool,
        in QueryRange range) =>
        AddQueryAccess(pool, GraphAccessMode.Read, WriteCoverage.Partial,
            range, null);

    public void Write(
        GraphQueryPoolId pool,
        in QueryRange range,
        WriteCoverage coverage = WriteCoverage.Complete,
        ResourceContentState? resultContents = ResourceContentState.Defined) =>
        AddQueryAccess(pool, GraphAccessMode.Write, coverage, range, resultContents);

    public void Read(GraphRayTracingShaderTableId table)
    {
        DeclareShaderTableInventory(table);
        AddShaderTableAccess(
            table,
            GraphAccessMode.Read,
            WriteCoverage.Partial,
            null);
    }

    public void Write(
        GraphRayTracingShaderTableId table,
        ResourceContentState? resultContents = ResourceContentState.Defined)
    {
        DeclareShaderTableInventory(table);
        AddShaderTableAccess(
            table,
            GraphAccessMode.Write,
            WriteCoverage.Complete,
            resultContents);
    }

    public PassRenderingRegionId DefineRenderingRegion(
        uint x,
        uint y,
        uint width,
        uint height,
        uint firstArrayLayer = 0,
        uint arrayLayerCount = 1)
    {
        EnsureKind(GraphPassKind.General);
        return AddRenderingRegion(x, y, width, height, firstArrayLayer, arrayLayerCount);
    }

    public void DefineRasterExtent(
        uint width,
        uint height,
        uint firstArrayLayer = 0,
        uint arrayLayerCount = 1)
    {
        EnsureKind(GraphPassKind.Raster);
        _ = AddRenderingRegion(0, 0, width, height, firstArrayLayer, arrayLayerCount);
    }

    public void ColorAttachment(
        uint slot,
        GraphColorAttachmentViewId view,
        LoadType load,
        StoreType store,
        WriteCoverage coverage,
        in Vector4 clearValue,
        GraphColorAttachmentViewId resolveView = default,
        ResolveType resolveType = ResolveType.Average) =>
        AddColorAttachment(-1, slot, view, load, store, coverage,
            clearValue, resolveView, resolveType);

    public void ColorAttachment(
        PassRenderingRegionId region,
        uint slot,
        GraphColorAttachmentViewId view,
        LoadType load,
        StoreType store,
        WriteCoverage coverage,
        in Vector4 clearValue,
        GraphColorAttachmentViewId resolveView = default,
        ResolveType resolveType = ResolveType.Average) =>
        AddColorAttachment(region.Value.Slot, slot, view, load, store, coverage,
            clearValue, resolveView, resolveType);

    public void DepthStencilAttachment(
        GraphDepthStencilViewId view,
        LoadType depthLoad,
        StoreType depthStore,
        WriteCoverage depthCoverage,
        float clearDepth,
        LoadType stencilLoad,
        StoreType stencilStore,
        WriteCoverage stencilCoverage,
        byte clearStencil) =>
        AddDepthStencilAttachment(-1, view, depthLoad, depthStore, depthCoverage,
            clearDepth, stencilLoad, stencilStore, stencilCoverage, clearStencil);

    public void DepthStencilAttachment(
        PassRenderingRegionId region,
        GraphDepthStencilViewId view,
        LoadType depthLoad,
        StoreType depthStore,
        WriteCoverage depthCoverage,
        float clearDepth,
        LoadType stencilLoad,
        StoreType stencilStore,
        WriteCoverage stencilCoverage,
        byte clearStencil) =>
        AddDepthStencilAttachment(region.Value.Slot, view, depthLoad, depthStore,
            depthCoverage, clearDepth, stencilLoad, stencilStore, stencilCoverage,
            clearStencil);

    private GraphBufferAccessId ReadBufferView(
        in GraphIdentity viewId,
        PipelineSync sync,
        ResourceAccess access,
        BufferContentScope contentScope = BufferContentScope.FullRange)
    {
        FrameView view = ResolveView(viewId);
        if (view.Kind is not (GraphViewKind.BufferCbv or GraphViewKind.BufferSrv))
            throw new ArgumentException("The view is not a readable Buffer view.");
        if (view.BufferInput is not null)
            return Read(view.BufferInput, view.BufferRange, sync, access, contentScope);
        return AddBufferAccess(new GraphBufferId(view.Buffer), GraphAccessMode.Read,
            WriteCoverage.Partial, sync, access, view.BufferRange, false,
            contentScope: contentScope);
    }

    private GraphBufferAccessId WriteBufferView(
        in GraphIdentity viewId,
        PipelineSync sync,
        ResourceAccess access,
        WriteCoverage coverage,
        bool readWrite,
        ResourceContentState? resultContents,
        BufferContentScope contentScope,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        FrameView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.BufferUav)
            throw new ArgumentException("The view is not a writable Buffer view.");
        if (view.BufferInput is not null)
            return AddBufferInputAccess(view.BufferInput, view.BufferRange, sync, access,
                readWrite ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
                coverage, resultContents, contentScope, unorderedAccessHazards);
        GraphBufferAccessId result = AddBufferAccess(
            new GraphBufferId(view.Buffer),
            readWrite ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
            coverage,
            sync,
            access,
            view.BufferRange,
            false,
            resultContents,
            contentScope,
            unorderedAccessHazards);
        if (view.AdditionalBuffer.IsValid)
        {
            _ = AddBufferAccess(
                new GraphBufferId(view.AdditionalBuffer),
                GraphAccessMode.ReadWrite,
                WriteCoverage.Partial,
                sync,
                ResourceAccess.UnorderedAccess,
                new BufferRange(view.CounterOffset, sizeof(uint)),
                false);
        }
        return result;
    }

    private GraphBufferAccessId ReadBufferUav(
        in GraphIdentity viewId,
        PipelineSync sync,
        BufferContentScope contentScope)
    {
        FrameView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.BufferUav)
            throw new ArgumentException("The view is not a Buffer UAV.");
        if (view.BufferInput is not null)
            return Read(view.BufferInput, view.BufferRange, sync, ResourceAccess.UnorderedAccess, contentScope);
        return AddBufferAccess(new GraphBufferId(view.Buffer), GraphAccessMode.Read,
            WriteCoverage.Partial, sync, ResourceAccess.UnorderedAccess,
            view.BufferRange, false, contentScope: contentScope);
    }

    private GraphTextureAccessId AddTextureInputAccess(GraphTextureInput input, GraphAccessMode mode,
        WriteCoverage coverage, PipelineSync sync, ResourceAccess access, TextureLayout layout,
        TextureSubresourceRange range, ResourceContentState? resultContents = null,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        UnorderedAccessHazardRules.Validate(unorderedAccessHazards, mode, coverage, access, layout, resultContents);
        if (_frame is null)
            return _edit.AddTextureInputAccess(_pass, input, mode, coverage, sync, access, layout,
                range, resultContents, unorderedAccessHazards);
        GraphTextureId texture = Frame.ResolveTextureInput(input);
        return AddTextureAccess(texture, mode, coverage, sync, access, layout,
            Frame.ResolveTextureRange(texture, range.MipLevelCount == 0 ? null : range), false, resultContents, unorderedAccessHazards);
    }

    private GraphTextureAccessId ReadTextureView(
        in GraphIdentity viewId,
        PipelineSync sync,
        ResourceAccess access,
        TextureLayout layout)
    {
        FrameView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.TextureSrv)
            throw new ArgumentException("The view is not a readable Texture view.");
        if (view.TextureInput is not null)
            return AddTextureInputAccess(view.TextureInput, GraphAccessMode.Read, WriteCoverage.Partial,
                sync, access, layout, view.TextureRange);
        return AddTextureAccess(new GraphTextureId(view.Texture), GraphAccessMode.Read,
            WriteCoverage.Partial, sync, access, layout, view.TextureRange, false);
    }

    private GraphTextureAccessId WriteTextureView(
        in GraphIdentity viewId,
        PipelineSync sync,
        ResourceAccess access,
        TextureLayout layout,
        WriteCoverage coverage,
        bool readWrite,
        ResourceContentState? resultContents,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        FrameView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.TextureUav)
            throw new ArgumentException("The view is not a writable Texture view.");
        if (view.TextureInput is not null)
            return AddTextureInputAccess(view.TextureInput, readWrite ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
                coverage, sync, access, layout, view.TextureRange, resultContents, unorderedAccessHazards);
        return AddTextureAccess(
            new GraphTextureId(view.Texture),
            readWrite ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
            coverage,
            sync,
            access,
            layout,
            view.TextureRange,
            false,
            resultContents,
            unorderedAccessHazards);
    }

    private GraphTextureAccessId ReadTextureUav(
        in GraphIdentity viewId,
        PipelineSync sync,
        TextureLayout layout)
    {
        FrameView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.TextureUav)
            throw new ArgumentException("The view is not a Texture UAV.");
        if (view.TextureInput is not null)
            return AddTextureInputAccess(view.TextureInput, GraphAccessMode.Read, WriteCoverage.Partial,
                sync, ResourceAccess.UnorderedAccess, layout, view.TextureRange);
        return AddTextureAccess(new GraphTextureId(view.Texture), GraphAccessMode.Read,
            WriteCoverage.Partial, sync, ResourceAccess.UnorderedAccess,
            layout, view.TextureRange, false);
    }

    private void AddColorAttachment(
        int region,
        uint slot,
        GraphColorAttachmentViewId viewId,
        LoadType load,
        StoreType store,
        WriteCoverage coverage,
        in Vector4 clearValue,
        GraphColorAttachmentViewId resolveView,
        ResolveType resolveType)
    {
        if (_kind is not (GraphPassKind.Raster or GraphPassKind.General))
            throw new InvalidOperationException("Only Raster and Raw passes can declare attachments.");
        FrameView view = ResolveView(viewId.Value);
        if (view.Kind != GraphViewKind.ColorAttachment)
            throw new ArgumentException("The view is not a color attachment view.", nameof(viewId));
        if ((_kind == GraphPassKind.Raster && region >= 0) ||
            (_kind == GraphPassKind.General && region < 0))
        {
            throw new InvalidOperationException("Raster and Raw attachment declarations use different region forms.");
        }

        PublishColorAttachment(new GraphColorAttachment(
            slot, viewId.Value, load, store, coverage, clearValue,
            resolveView.Value, resolveType, region));

        GraphAccessMode mode = load == LoadType.Load
            ? GraphAccessMode.ReadWrite
            : GraphAccessMode.Write;
        _ = AddTextureAccess(
            new GraphTextureId(view.Texture),
            mode,
            coverage,
            PipelineSync.RenderTarget,
            ResourceAccess.RenderTarget,
            TextureLayout.RenderTarget,
            view.TextureRange,
            false,
            store == StoreType.Discard ? ResourceContentState.Undefined : null);

        if (resolveView.Value.IsValid)
        {
            FrameView resolve = ResolveView(resolveView.Value);
            _ = AddTextureAccess(
                new GraphTextureId(resolve.Texture),
                GraphAccessMode.Write,
                WriteCoverage.Complete,
                PipelineSync.Resolve,
                ResourceAccess.ResolveDestination,
                TextureLayout.ResolveDestination,
                resolve.TextureRange,
                false);
        }
    }

    private void AddDepthStencilAttachment(
        int region,
        GraphDepthStencilViewId viewId,
        LoadType depthLoad,
        StoreType depthStore,
        WriteCoverage depthCoverage,
        float clearDepth,
        LoadType stencilLoad,
        StoreType stencilStore,
        WriteCoverage stencilCoverage,
        byte clearStencil)
    {
        if (_kind is not (GraphPassKind.Raster or GraphPassKind.General))
            throw new InvalidOperationException("Only Raster and Raw passes can declare attachments.");
        FrameView view = ResolveView(viewId.Value);
        if (view.Kind != GraphViewKind.DepthStencil)
            throw new ArgumentException("The view is not a depth-stencil view.", nameof(viewId));
        if ((_kind == GraphPassKind.Raster && region >= 0) ||
            (_kind == GraphPassKind.General && region < 0))
        {
            throw new InvalidOperationException("Raster and Raw attachment declarations use different region forms.");
        }

        PublishDepthStencilAttachment(new GraphDepthStencilAttachment(
            viewId.Value, depthLoad, depthStore, depthCoverage, clearDepth,
            stencilLoad, stencilStore, stencilCoverage, clearStencil, region));

        GraphTextureId texture = new(view.Texture);
        if ((view.TextureRange.Aspects & TextureAspects.Depth) != 0)
        {
            TextureSubresourceRange range = new(
                view.TextureRange.FirstMipLevel,
                view.TextureRange.MipLevelCount,
                view.TextureRange.FirstArrayLayer,
                view.TextureRange.ArrayLayerCount,
                TextureAspects.Depth);
            bool readOnly = view.ReadOnlyDepth;
            _ = AddTextureAccess(
                texture,
                readOnly ? GraphAccessMode.Read :
                    depthLoad == LoadType.Load ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
                readOnly ? WriteCoverage.Partial : depthCoverage,
                PipelineSync.DepthStencil,
                readOnly ? ResourceAccess.DepthStencilRead : ResourceAccess.DepthStencilWrite,
                readOnly ? TextureLayout.DepthStencilRead : TextureLayout.DepthStencilWrite,
                range,
                false,
                depthStore == StoreType.Discard ? ResourceContentState.Undefined : null);
        }
        if ((view.TextureRange.Aspects & TextureAspects.Stencil) != 0)
        {
            TextureSubresourceRange range = new(
                view.TextureRange.FirstMipLevel,
                view.TextureRange.MipLevelCount,
                view.TextureRange.FirstArrayLayer,
                view.TextureRange.ArrayLayerCount,
                TextureAspects.Stencil);
            bool readOnly = view.ReadOnlyStencil;
            _ = AddTextureAccess(
                texture,
                readOnly ? GraphAccessMode.Read :
                    stencilLoad == LoadType.Load ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
                readOnly ? WriteCoverage.Partial : stencilCoverage,
                PipelineSync.DepthStencil,
                readOnly ? ResourceAccess.DepthStencilRead : ResourceAccess.DepthStencilWrite,
                readOnly ? TextureLayout.DepthStencilRead : TextureLayout.DepthStencilWrite,
                range,
                false,
                stencilStore == StoreType.Discard ? ResourceContentState.Undefined : null);
        }
    }

    private GraphBufferAccessId AddBufferAccess(
        GraphBufferId buffer,
        GraphAccessMode mode,
        WriteCoverage coverage,
        PipelineSync sync,
        ResourceAccess access,
        in BufferRange range,
        bool dynamicRange,
        ResourceContentState? resultContents = null,
        BufferContentScope contentScope = BufferContentScope.FullRange,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        UnorderedAccessHazardRules.Validate(
            unorderedAccessHazards,
            mode,
            coverage,
            access,
            TextureLayout.Undefined,
            resultContents);
        return _frame is null
            ? _edit.AddBufferAccess(_pass, buffer, mode, coverage, sync, access, range,
                dynamicRange, resultContents, contentScope, unorderedAccessHazards)
            : Frame.AddBufferAccess(_pass, buffer, mode, coverage, sync, access, range,
                resultContents, contentScope, unorderedAccessHazards);
    }

    private GraphTextureAccessId AddTextureAccess(
        GraphTextureId texture,
        GraphAccessMode mode,
        WriteCoverage coverage,
        PipelineSync sync,
        ResourceAccess access,
        TextureLayout layout,
        in TextureSubresourceRange range,
        bool dynamicRange,
        ResourceContentState? resultContents = null,
        UnorderedAccessHazardMode unorderedAccessHazards = UnorderedAccessHazardMode.Ordered)
    {
        UnorderedAccessHazardRules.Validate(
            unorderedAccessHazards,
            mode,
            coverage,
            access,
            layout,
            resultContents);
        return _frame is null
            ? _edit.AddTextureAccess(_pass, texture, mode, coverage, sync, access, layout,
                range, dynamicRange, resultContents, unorderedAccessHazards)
            : Frame.AddTextureAccess(_pass, texture, mode, coverage, sync, access, layout,
                range, resultContents, unorderedAccessHazards);
    }

    private FrameView ResolveView(in GraphIdentity identity) =>
        _frame is null ? new FrameView(_edit.UseView(_pass, identity)) : Frame.ResolveView(identity);

    private void DeclareShaderTableInventory(GraphRayTracingShaderTableId table)
    {
        GraphRayTracingShaderTable row = _frame is null
            ? _edit.ResolveShaderTable(table)
            : Frame.ResolveShaderTable(table);
        foreach (ref readonly GraphParameterResourceBinding binding in row.Inventory.AsSpan())
            DeclareParameterBinding(binding);
    }

    private void AddQueryAccess(
        GraphQueryPoolId pool,
        GraphAccessMode mode,
        WriteCoverage coverage,
        in QueryRange range,
        ResourceContentState? resultContents)
    {
        if (_frame is null)
            _edit.AddQueryAccess(_pass, pool, mode, coverage, range, resultContents);
        else
            Frame.AddQueryAccess(_pass, pool, mode, coverage, range, resultContents);
    }

    private void AddShaderTableAccess(
        GraphRayTracingShaderTableId table,
        GraphAccessMode mode,
        WriteCoverage coverage,
        ResourceContentState? resultContents)
    {
        if (_frame is null)
            _edit.AddShaderTableAccess(_pass, table, mode, coverage, resultContents);
        else
            Frame.AddShaderTableAccess(_pass, table, mode, coverage, resultContents);
    }

    private PassRenderingRegionId AddRenderingRegion(
        uint x,
        uint y,
        uint width,
        uint height,
        uint firstArrayLayer,
        uint arrayLayerCount)
    {
        if (_frame is null)
        {
            return _edit.AddRenderingRegion(
                _pass, x, y, width, height, firstArrayLayer, arrayLayerCount);
        }

        int region = Frame.AddRenderingRegion(
            _pass, x, y, width, height, firstArrayLayer, arrayLayerCount);
        return new PassRenderingRegionId(new GraphIdentity(
            Frame.Identity,
            region,
            checked((uint)_pass.Slot + 1)));
    }

    private void PublishColorAttachment(in GraphColorAttachment attachment)
    {
        if (_frame is null) _edit.AddColorAttachment(_pass, attachment);
        else Frame.AddColorAttachment(_pass, attachment);
    }

    private void PublishDepthStencilAttachment(in GraphDepthStencilAttachment attachment)
    {
        if (_frame is null) _edit.SetDepthStencilAttachment(_pass, attachment);
        else Frame.SetDepthStencilAttachment(_pass, attachment);
    }

    private void AddParameterBinding(in GraphParameterResourceBinding binding)
    {
        if (_frame is null) _edit.AddPassParameterBinding(_pass, binding);
        else Frame.AddPassParameterBinding(_pass, binding);
    }

    private void EnsureKind(GraphPassKind kind)
    {
        if (_kind != kind)
            throw new InvalidOperationException($"The operation requires a {kind} pass.");
    }

    public GraphTextureAccessId Read(GraphTextureInput texture, in TextureSubresourceRange range,
        PipelineSync sync, ResourceAccess access, TextureLayout layout) =>
        AddTextureInputAccess(texture, GraphAccessMode.Read, WriteCoverage.Partial, sync, access,
            layout, range, null, UnorderedAccessHazardMode.Ordered);

    public GraphTextureAccessId Write(GraphTextureInput texture, in TextureSubresourceRange range,
        PipelineSync sync, ResourceAccess access, TextureLayout layout, WriteCoverage coverage,
        ResourceContentState? resultContents = null) =>
        AddTextureInputAccess(texture, GraphAccessMode.Write, coverage, sync, access,
            layout, range, resultContents, UnorderedAccessHazardMode.Ordered);

    public void Bind(GraphParameterInput input, PipelineSync sync)
    {
        DevelopmentCheck.Require(_frame is null,
            "Parameter input connections must be declared during a graph edit.");
        _edit.AddParameterInput(_pass, input, sync);
        AddParameterBinding(GraphParameterResourceBinding.FromInput(input));
    }
}

