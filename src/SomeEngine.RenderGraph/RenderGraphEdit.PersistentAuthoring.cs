namespace SomeEngine.RenderGraph;

public ref partial struct RenderGraphEdit
{
    public GraphBufferId CreateBuffer(
        in BufferDesc description,
        MemoryType memoryType = MemoryType.DeviceLocal) =>
        CreateTransientBuffer(description, memoryType);

    public GraphTextureId CreateTexture(in TextureDesc description) =>
        CreateTransientTexture(description);

    public void Remove(GraphBufferCbvId view) => RemoveView(view.Value);
    public void Remove(GraphBufferSrvId view) => RemoveView(view.Value);
    public void Remove(GraphBufferUavId view) => RemoveView(view.Value);
    public void Remove(GraphTextureSrvId view) => RemoveView(view.Value);
    public void Remove(GraphTextureUavId view) => RemoveView(view.Value);
    public void Remove(GraphColorAttachmentViewId view) => RemoveView(view.Value);
    public void Remove(GraphDepthStencilViewId view) => RemoveView(view.Value);

    public GraphPassId AddRasterFramePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState declarationState,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback) =>
        AddPersistentFramePass(
            label,
            GraphPassKind.Raster,
            queue,
            declarationState,
            options,
            null,
            VariableLayoutReflection.Null,
            default,
            declaration,
            new RasterFrameOnlyPassCallbackStorage<TState>(
                declarationState,
                callback,
                Graph.FrameSlotCount));

    public GraphPassId AddRasterFramePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState declarationState,
        in PassOptions options,
        Pipeline pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        RasterFrameCallback<TState> callback) =>
        AddPersistentFramePass(
            label,
            GraphPassKind.Raster,
            queue,
            declarationState,
            options,
            pipeline,
            parameterLayout,
            parameterBindings,
            declaration,
            new RasterFrameOnlyPassCallbackStorage<TState>(
                declarationState,
                callback,
                Graph.FrameSlotCount));

    public GraphPassId AddComputeFramePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState declarationState,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback) =>
        AddPersistentFramePass(
            label,
            GraphPassKind.Compute,
            queue,
            declarationState,
            options,
            null,
            VariableLayoutReflection.Null,
            default,
            declaration,
            new ComputeFrameOnlyPassCallbackStorage<TState>(
                declarationState,
                callback,
                Graph.FrameSlotCount));

    public GraphPassId AddComputeFramePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState declarationState,
        in PassOptions options,
        Pipeline pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        ComputeFrameCallback<TState> callback) =>
        AddPersistentFramePass(
            label,
            GraphPassKind.Compute,
            queue,
            declarationState,
            options,
            pipeline,
            parameterLayout,
            parameterBindings,
            declaration,
            new ComputeFrameOnlyPassCallbackStorage<TState>(
                declarationState,
                callback,
                Graph.FrameSlotCount));

    public GraphPassId AddCopyFramePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState declarationState,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        CopyFrameCallback<TState> callback) =>
        AddPersistentFramePass(
            label,
            GraphPassKind.Copy,
            queue,
            declarationState,
            options,
            null,
            VariableLayoutReflection.Null,
            default,
            declaration,
            new CopyFrameOnlyPassCallbackStorage<TState>(
                declarationState,
                callback,
                Graph.FrameSlotCount));

    public GraphPassId AddGeneralFramePass<TState>(
        string label,
        in PassQueueSelection queue,
        in TState declarationState,
        in PassOptions options,
        PassDeclaration<TState> declaration,
        GeneralFrameCallback<TState> callback) =>
        AddPersistentFramePass(
            label,
            GraphPassKind.General,
            queue,
            declarationState,
            options,
            null,
            VariableLayoutReflection.Null,
            default,
            declaration,
            new GeneralFrameOnlyPassCallbackStorage<TState>(
                declarationState,
                callback,
                Graph.FrameSlotCount));

    public GraphBufferCbvId CreateBufferCbv(
        GraphBufferId buffer,
        BufferRange? range = null,
        string? label = null) =>
        new(AddBufferView(
            GraphViewKind.BufferCbv,
            buffer,
            range ?? BufferRange.Whole,
            null,
            0,
            default,
            0,
            label));

    public GraphBufferSrvId CreateBufferSrv(
        GraphBufferId buffer,
        BufferRange? range = null,
        Format? format = null,
        uint structureStride = 0,
        string? label = null) =>
        new(AddBufferView(
            GraphViewKind.BufferSrv,
            buffer,
            range ?? BufferRange.Whole,
            format,
            structureStride,
            default,
            0,
            label));

    public GraphBufferUavId CreateBufferUav(
        GraphBufferId buffer,
        BufferRange? range = null,
        Format? format = null,
        uint structureStride = 0,
        GraphBufferId counterBuffer = default,
        ulong counterOffset = 0,
        string? label = null) =>
        new(AddBufferView(
            GraphViewKind.BufferUav,
            buffer,
            range ?? BufferRange.Whole,
            format,
            structureStride,
            counterBuffer.Value,
            counterOffset,
            label));

    public GraphTextureSrvId CreateTextureSrv(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null) =>
        new(AddTextureView(
            GraphViewKind.TextureSrv,
            texture,
            ResolveTextureRange(texture, range),
            ResolveTextureFormat(texture, format),
            ResolveTextureViewDimension(texture, dimension),
            false,
            false,
            label));

    public GraphTextureUavId CreateTextureUav(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null) =>
        new(AddTextureView(
            GraphViewKind.TextureUav,
            texture,
            ResolveTextureRange(texture, range),
            ResolveTextureFormat(texture, format),
            ResolveTextureViewDimension(texture, dimension),
            false,
            false,
            label));

    public GraphColorAttachmentViewId CreateColorAttachmentView(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        string? label = null) =>
        new(AddTextureView(
            GraphViewKind.ColorAttachment,
            texture,
            ResolveTextureRange(texture, range),
            ResolveTextureFormat(texture, format),
            ResolveTextureViewDimension(texture, dimension),
            false,
            false,
            label));

    public GraphDepthStencilViewId CreateDepthStencilView(
        GraphTextureId texture,
        TextureSubresourceRange? range = null,
        Format? format = null,
        TextureViewDimension? dimension = null,
        bool readOnlyDepth = false,
        bool readOnlyStencil = false,
        string? label = null) =>
        new(AddTextureView(
            GraphViewKind.DepthStencil,
            texture,
            ResolveTextureRange(texture, range),
            ResolveTextureFormat(texture, format),
            ResolveTextureViewDimension(texture, dimension),
            readOnlyDepth,
            readOnlyStencil,
            label));

    private GraphPassId AddPersistentFramePass<TState>(
        string label,
        GraphPassKind kind,
        in PassQueueSelection queue,
        in TState declarationState,
        in PassOptions options,
        Pipeline? pipeline,
        VariableLayoutReflection parameterLayout,
        ReadOnlySpan<GraphParameterResourceBinding> parameterBindings,
        PassDeclaration<TState> declaration,
        PassCallbackStorage callbackStorage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(declaration);
        if (pipeline is not null && !ReferenceEquals(pipeline.Device, Graph.Device))
            throw new ArgumentException("The Pipeline belongs to another Device.", nameof(pipeline));
        if (pipeline is null && parameterLayout != VariableLayoutReflection.Null)
            throw new ArgumentException("A parameter layout requires a Pipeline.", nameof(parameterLayout));

        var pass = new GraphPass
        {
            Label = label,
            Kind = kind,
            Queue = queue,
            Options = options,
            CallbackStorage = callbackStorage,
            Pipeline = pipeline,
            ParameterLayout = parameterLayout,
            DeclarationOrdinal = Staging.Passes.Count,
        };
        GraphIdentity identity = Staging.Passes.Add(Graph.Identity, pass);
        TState mutable = declarationState;
        try
        {
            var definition = new PassDefinition(this, identity, kind);
            definition.Bind(parameterBindings);
            declaration(ref definition, ref mutable);
            ((IStaticPassDataStorage<TState>)callbackStorage).SetStaticData(mutable);
            return new GraphPassId(identity);
        }
        catch
        {
            foreach (GraphIdentity access in pass.Accesses)
            {
                if (Staging.Accesses.Contains(access))
                    _ = Staging.Accesses.Remove(access);
            }
            _ = Staging.Passes.Remove(identity);
            throw;
        }
    }

    private void RemoveView(in GraphIdentity view)
    {
        ValidateOwner(view);
        _ = Staging.Views.Remove(view);
    }

    private TextureSubresourceRange ResolveTextureRange(
        GraphTextureId texture,
        TextureSubresourceRange? requested)
    {
        GraphTexture row = ResolveTexture(texture);
        TextureAspects allowedAspects = TextureFormatRules.Aspects(row.Format);
        TextureSubresourceRange range = requested ?? new TextureSubresourceRange(
            0,
            row.MipLevelCount,
            0,
            row.ArrayLayerCount,
            allowedAspects);
        if (range.MipLevelCount == 0 ||
            range.ArrayLayerCount == 0 ||
            range.Aspects == TextureAspects.None ||
            (range.Aspects & ~allowedAspects) != TextureAspects.None ||
            range.FirstMipLevel >= row.MipLevelCount ||
            range.MipLevelCount > row.MipLevelCount - range.FirstMipLevel ||
            range.FirstArrayLayer >= row.ArrayLayerCount ||
            range.ArrayLayerCount > row.ArrayLayerCount - range.FirstArrayLayer)
        {
            throw new ArgumentOutOfRangeException(nameof(requested));
        }
        return range;
    }

    private Format ResolveTextureFormat(GraphTextureId texture, Format? requested) =>
        requested ?? ResolveTexture(texture).Format;

    private TextureViewDimension ResolveTextureViewDimension(
        GraphTextureId texture,
        TextureViewDimension? requested)
    {
        if (requested.HasValue)
            return requested.Value;
        GraphTexture row = ResolveTexture(texture);
        return row.Dimension switch
        {
            TextureDimension.Texture1D when row.ArrayLayerCount > 1 =>
                TextureViewDimension.Texture1DArray,
            TextureDimension.Texture1D => TextureViewDimension.Texture1D,
            TextureDimension.Texture2D when row.SampleCount > 1 && row.ArrayLayerCount > 1 =>
                TextureViewDimension.Texture2DMultisampledArray,
            TextureDimension.Texture2D when row.SampleCount > 1 =>
                TextureViewDimension.Texture2DMultisampled,
            TextureDimension.Texture2D when row.ArrayLayerCount > 1 =>
                TextureViewDimension.Texture2DArray,
            TextureDimension.Texture2D => TextureViewDimension.Texture2D,
            TextureDimension.Texture3D => TextureViewDimension.Texture3D,
            _ => throw new ArgumentOutOfRangeException(nameof(texture)),
        };
    }

    private GraphTexture ResolveTexture(GraphTextureId texture)
    {
        ValidateTexture(texture);
        return Staging.Textures.Get(texture.Value);
    }
}
