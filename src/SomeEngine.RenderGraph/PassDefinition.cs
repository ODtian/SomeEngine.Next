namespace SomeEngine.RenderGraph;

public ref struct PassDefinition
{
    private RenderGraphEdit _edit;
    private RenderGraphFrameState? _frame;
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
        _pass = pass;
        _kind = kind;
    }

    /// <summary>Returns the stable graph identity of this pass.</summary>
    public GraphPassId Id => new(_pass);

    public void SetPipeline(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        if (_frame is null) _edit.SetPassPipeline(_pass, pipeline);
        else _frame.SetPassPipeline(_pass, pipeline);
    }

    public void SetParameterBlock(
        VariableLayoutReflection layout,
        ReadOnlySpan<byte> ordinaryData = default)
    {
        if (layout == VariableLayoutReflection.Null)
            throw new ArgumentException("The parameter layout cannot be null.", nameof(layout));
        if (_frame is null) _edit.SetPassParameterBlock(_pass, layout, ordinaryData);
        else _frame.SetPassParameterBlock(_pass, layout, ordinaryData);
    }

    public GraphBufferAccessId Bind(GraphBufferCbvId view, PipelineSync sync)
    {
        GraphBufferAccessId access = Read(view, sync);
        AddParameterBinding(GraphParameterResourceBinding.ConstantBuffer(view, sync));
        return access;
    }

    public GraphBufferAccessId Bind(GraphBufferSrvId view, PipelineSync sync)
    {
        GraphBufferAccessId access = Read(view, sync);
        AddParameterBinding(GraphParameterResourceBinding.ReadOnlyBuffer(view, sync));
        return access;
    }

    public GraphBufferAccessId Bind(
        GraphBufferUavId view,
        PipelineSync sync,
        GraphAccessMode mode = GraphAccessMode.ReadWrite,
        WriteCoverage coverage = WriteCoverage.Partial,
        ResourceContentState? resultContents = null)
    {
        GraphBufferAccessId access = mode switch
        {
            GraphAccessMode.Read => Read(view, sync),
            GraphAccessMode.Write => Write(view, sync, coverage, resultContents),
            GraphAccessMode.ReadWrite => ReadWrite(view, sync, resultContents),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        AddParameterBinding(GraphParameterResourceBinding.WritableBuffer(
            view, sync, mode, coverage, resultContents));
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
        TextureLayout layout = TextureLayout.UnorderedAccess)
    {
        GraphTextureAccessId access = mode switch
        {
            GraphAccessMode.Read => Read(view, sync, layout),
            GraphAccessMode.Write => Write(view, sync, coverage, resultContents, layout),
            GraphAccessMode.ReadWrite => ReadWrite(view, sync, resultContents, layout),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        AddParameterBinding(GraphParameterResourceBinding.StorageTexture(
            view, sync, mode, coverage, resultContents, layout));
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
            : _frame.UsePersistentParameterBindings(_pass, bindings);
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
        switch (binding.Type)
        {
            case ResourceBindingType.ConstantBuffer:
                _ = Read(new GraphBufferCbvId(binding.Value), binding.Sync);
                break;
            case ResourceBindingType.BufferSrv:
                _ = Read(new GraphBufferSrvId(binding.Value), binding.Sync);
                break;
            case ResourceBindingType.BufferUav:
                if (binding.Mode == GraphAccessMode.Read)
                {
                    _ = Read(new GraphBufferUavId(binding.Value), binding.Sync);
                }
                else if (binding.Mode == GraphAccessMode.Write)
                {
                    _ = Write(new GraphBufferUavId(binding.Value), binding.Sync,
                        binding.Coverage, binding.ResultContents);
                }
                else
                {
                    _ = ReadWrite(new GraphBufferUavId(binding.Value), binding.Sync,
                        binding.ResultContents);
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
                        binding.ResultContents, binding.Layout);
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
        bool dynamicRange = false) =>
        AddBufferAccess(buffer, GraphAccessMode.Read, WriteCoverage.Partial,
            sync, access, range, dynamicRange);

    public GraphBufferAccessId Write(
        GraphBufferId buffer,
        in BufferRange range,
        PipelineSync sync,
        ResourceAccess access,
        WriteCoverage coverage,
        ResourceContentState? resultContents = null,
        bool dynamicRange = false) =>
        AddBufferAccess(buffer, GraphAccessMode.Write, coverage,
            sync, access, range, dynamicRange, resultContents);

    public GraphBufferAccessId ReadWrite(
        GraphBufferId buffer,
        in BufferRange range,
        PipelineSync sync,
        ResourceAccess access,
        ResourceContentState? resultContents = null,
        bool dynamicRange = false) =>
        AddBufferAccess(buffer, GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            sync, access, range, dynamicRange, resultContents);

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
        bool dynamicRange = false) =>
        AddTextureAccess(texture, GraphAccessMode.ReadWrite, WriteCoverage.Partial,
            sync, access, layout, range, dynamicRange, resultContents);

    public GraphBufferAccessId Read(GraphBufferCbvId view, PipelineSync sync) =>
        ReadBufferView(view.Value, sync, ResourceAccess.ConstantBuffer);

    public GraphBufferAccessId Read(GraphBufferSrvId view, PipelineSync sync) =>
        ReadBufferView(view.Value, sync, ResourceAccess.ShaderResource);

    public GraphBufferAccessId Write(
        GraphBufferUavId view,
        PipelineSync sync,
        WriteCoverage coverage,
        ResourceContentState? resultContents = null) =>
        WriteBufferView(view.Value, sync, ResourceAccess.UnorderedAccess,
            coverage, false, resultContents);

    public GraphBufferAccessId Read(GraphBufferUavId view, PipelineSync sync) =>
        ReadBufferUav(view.Value, sync);

    public GraphBufferAccessId ReadWrite(
        GraphBufferUavId view,
        PipelineSync sync,
        ResourceContentState? resultContents = null) =>
        WriteBufferView(view.Value, sync, ResourceAccess.UnorderedAccess,
            WriteCoverage.Partial, true, resultContents);

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
        TextureLayout layout = TextureLayout.UnorderedAccess) =>
        WriteTextureView(view.Value, sync, ResourceAccess.UnorderedAccess,
            layout, WriteCoverage.Partial, true, resultContents);

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
        ResourceAccess access)
    {
        GraphView view = ResolveView(viewId);
        if (view.Kind is not (GraphViewKind.BufferCbv or GraphViewKind.BufferSrv))
            throw new ArgumentException("The view is not a readable Buffer view.");
        return AddBufferAccess(new GraphBufferId(view.Buffer), GraphAccessMode.Read,
            WriteCoverage.Partial, sync, access, view.BufferRange, false);
    }

    private GraphBufferAccessId WriteBufferView(
        in GraphIdentity viewId,
        PipelineSync sync,
        ResourceAccess access,
        WriteCoverage coverage,
        bool readWrite,
        ResourceContentState? resultContents)
    {
        GraphView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.BufferUav)
            throw new ArgumentException("The view is not a writable Buffer view.");
        GraphBufferAccessId result = AddBufferAccess(
            new GraphBufferId(view.Buffer),
            readWrite ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
            coverage,
            sync,
            access,
            view.BufferRange,
            false,
            resultContents);
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

    private GraphBufferAccessId ReadBufferUav(in GraphIdentity viewId, PipelineSync sync)
    {
        GraphView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.BufferUav)
            throw new ArgumentException("The view is not a Buffer UAV.");
        return AddBufferAccess(new GraphBufferId(view.Buffer), GraphAccessMode.Read,
            WriteCoverage.Partial, sync, ResourceAccess.UnorderedAccess,
            view.BufferRange, false);
    }

    private GraphTextureAccessId ReadTextureView(
        in GraphIdentity viewId,
        PipelineSync sync,
        ResourceAccess access,
        TextureLayout layout)
    {
        GraphView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.TextureSrv)
            throw new ArgumentException("The view is not a readable Texture view.");
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
        ResourceContentState? resultContents)
    {
        GraphView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.TextureUav)
            throw new ArgumentException("The view is not a writable Texture view.");
        return AddTextureAccess(
            new GraphTextureId(view.Texture),
            readWrite ? GraphAccessMode.ReadWrite : GraphAccessMode.Write,
            coverage,
            sync,
            access,
            layout,
            view.TextureRange,
            false,
            resultContents);
    }

    private GraphTextureAccessId ReadTextureUav(
        in GraphIdentity viewId,
        PipelineSync sync,
        TextureLayout layout)
    {
        GraphView view = ResolveView(viewId);
        if (view.Kind != GraphViewKind.TextureUav)
            throw new ArgumentException("The view is not a Texture UAV.");
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
        GraphView view = ResolveView(viewId.Value);
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
            GraphView resolve = ResolveView(resolveView.Value);
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
        GraphView view = ResolveView(viewId.Value);
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
        ResourceContentState? resultContents = null)
    {
        return _frame is null
            ? _edit.AddBufferAccess(_pass, buffer, mode, coverage, sync, access, range,
                dynamicRange, resultContents)
            : _frame.AddBufferAccess(_pass, buffer, mode, coverage, sync, access, range,
                resultContents);
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
        ResourceContentState? resultContents = null)
    {
        return _frame is null
            ? _edit.AddTextureAccess(_pass, texture, mode, coverage, sync, access, layout,
                range, dynamicRange, resultContents)
            : _frame.AddTextureAccess(_pass, texture, mode, coverage, sync, access, layout,
                range, resultContents);
    }

    private GraphView ResolveView(in GraphIdentity identity) =>
        _frame is null ? _edit.ResolveView(identity) : _frame.ResolveView(identity);

    private void DeclareShaderTableInventory(GraphRayTracingShaderTableId table)
    {
        GraphRayTracingShaderTable row = _frame is null
            ? _edit.ResolveShaderTable(table)
            : _frame.ResolveShaderTable(table);
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
            _frame.AddQueryAccess(_pass, pool, mode, coverage, range, resultContents);
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
            _frame.AddShaderTableAccess(_pass, table, mode, coverage, resultContents);
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

        int region = _frame.AddRenderingRegion(
            _pass, x, y, width, height, firstArrayLayer, arrayLayerCount);
        return new PassRenderingRegionId(new GraphIdentity(
            _frame.Identity,
            region,
            checked((uint)_pass.Slot + 1)));
    }

    private void PublishColorAttachment(in GraphColorAttachment attachment)
    {
        if (_frame is null) _edit.AddColorAttachment(_pass, attachment);
        else _frame.AddColorAttachment(_pass, attachment);
    }

    private void PublishDepthStencilAttachment(in GraphDepthStencilAttachment attachment)
    {
        if (_frame is null) _edit.SetDepthStencilAttachment(_pass, attachment);
        else _frame.SetDepthStencilAttachment(_pass, attachment);
    }

    private void AddParameterBinding(in GraphParameterResourceBinding binding)
    {
        if (_frame is null) _edit.AddPassParameterBinding(_pass, binding);
        else _frame.AddPassParameterBinding(_pass, binding);
    }

    private void EnsureKind(GraphPassKind kind)
    {
        if (_kind != kind)
            throw new InvalidOperationException($"The operation requires a {kind} pass.");
    }
}

