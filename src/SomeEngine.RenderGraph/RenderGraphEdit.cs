namespace SomeEngine.RenderGraph;

public ref partial struct RenderGraphEdit
{
    private RenderGraph? _graph;
    private readonly long _token;
    private GraphStructure? _staging;
    private bool _committed;

    internal RenderGraphEdit(RenderGraph graph, long token, GraphStructure staging)
    {
        _graph = graph;
        _token = token;
        _staging = staging;
    }

    private RenderGraph Graph => _graph ?? throw new InvalidOperationException("The edit is no longer active.");
    private GraphStructure Staging => _staging ?? throw new InvalidOperationException("The edit is no longer active.");

    public GraphBufferId CreatePersistentBuffer(in BufferDesc description, MemoryType memoryType = MemoryType.DeviceLocal) =>
        AddBuffer(description, memoryType, RenderGraphResourceOwnership.GraphOwned,
            RenderGraphResourceLifetime.Persistent);

    public GraphBufferId CreateTransientBuffer(in BufferDesc description, MemoryType memoryType = MemoryType.DeviceLocal) =>
        AddBuffer(description, memoryType, RenderGraphResourceOwnership.GraphOwned,
            RenderGraphResourceLifetime.PerFrame);

    public GraphBufferId DeclareExternalBuffer(
        in BufferDesc description,
        MemoryType memoryType = MemoryType.DeviceLocal) =>
        AddBuffer(description, memoryType, RenderGraphResourceOwnership.CallerOwned,
            RenderGraphResourceLifetime.PerFrame);

    public GraphBufferId RegisterExternalBuffer(
        Buffer buffer,
        scoped ReadOnlySpan<BufferBoundaryState> boundaryStates)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!ReferenceEquals(buffer.Device, Graph.Device))
            throw new ArgumentException("The Buffer belongs to another Device.", nameof(buffer));
        GraphIdentity identity = Staging.Buffers.Add(Graph.Identity, new GraphBuffer
        {
            Description = new BufferDesc(buffer.Info.Size, buffer.Info.Usages, buffer.Label,
                new ResourceNodePlacement(buffer.Info.CreationNodeMask, buffer.Info.VisibleNodeMask)),
            MemoryType = buffer.Info.MemoryType,
            Ownership = RenderGraphResourceOwnership.CallerOwned,
            Lifetime = RenderGraphResourceLifetime.Persistent,
            RegisteredResource = buffer,
            BoundaryStates = boundaryStates.ToArray(),
        });
        return new GraphBufferId(identity);
    }

    public GraphTextureId CreatePersistentTexture(in TextureDesc description) =>
        AddTexture(description, RenderGraphResourceOwnership.GraphOwned,
            RenderGraphResourceLifetime.Persistent);

    public GraphTextureId CreateTransientTexture(in TextureDesc description) =>
        AddTexture(description, RenderGraphResourceOwnership.GraphOwned,
            RenderGraphResourceLifetime.PerFrame);

    public GraphTextureId DeclareExternalTexture(in TextureDesc description) =>
        AddTexture(description, RenderGraphResourceOwnership.CallerOwned,
            RenderGraphResourceLifetime.PerFrame);

    public GraphTextureId RegisterExternalTexture(
        Texture texture,
        scoped ReadOnlySpan<TextureBoundaryState> boundaryStates)
    {
        ArgumentNullException.ThrowIfNull(texture);
        if (!ReferenceEquals(texture.Device, Graph.Device))
            throw new ArgumentException("The Texture belongs to another Device.", nameof(texture));
        TextureInfo info = texture.Info;
        GraphIdentity identity = Staging.Textures.Add(Graph.Identity, new GraphTexture
        {
            Dimension = info.Dimension,
            Width = info.Width,
            Height = info.Height,
            Depth = info.Depth,
            MipLevelCount = info.MipLevelCount,
            ArrayLayerCount = info.ArrayLayerCount,
            SampleCount = info.SampleCount,
            Format = info.Format,
            Usages = info.Usages,
            PermittedViewFormats = info.PermittedViewFormats.ToArray(),
            Label = texture.Label,
            NodePlacement = new ResourceNodePlacement(info.CreationNodeMask, info.VisibleNodeMask),
            Ownership = RenderGraphResourceOwnership.CallerOwned,
            Lifetime = RenderGraphResourceLifetime.Persistent,
            RegisteredResource = texture,
            BoundaryStates = boundaryStates.ToArray(),
        });
        return new GraphTextureId(identity);
    }

    public GraphQueryPoolId RegisterQueryPool(QueryPool pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        if (!ReferenceEquals(pool.Device, Graph.Device))
            throw new ArgumentException("The QueryPool belongs to another Device.", nameof(pool));
        GraphIdentity identity = Staging.QueryPools.Add(Graph.Identity, new GraphQueryPool
        {
            Resource = pool,
            BoundaryStates =
            [
                new QueryBoundaryState(
                    new QueryRange(0, pool.Description.Count),
                    ResourceContentState.Undefined,
                    null,
                    null),
            ],
        });
        return new GraphQueryPoolId(identity);
    }

    public GraphRayTracingShaderTableId RegisterRayTracingShaderTable(
        RayTracingShaderTable table,
        scoped ReadOnlySpan<GraphParameterResourceBinding> inventory,
        ResourceContentState contents = ResourceContentState.Undefined,
        QueueCompletion? readyAfter = null)
    {
        ArgumentNullException.ThrowIfNull(table);
        if (!ReferenceEquals(table.Device, Graph.Device))
            throw new ArgumentException("The RayTracingShaderTable belongs to another Device.", nameof(table));
        if (!Enum.IsDefined(contents)) throw new ArgumentOutOfRangeException(nameof(contents));
        if (readyAfter.HasValue && !ReferenceEquals(readyAfter.Value.Queue.Device, Graph.Device))
            throw new ArgumentException("The readiness completion belongs to another Device.", nameof(readyAfter));
        GraphParameterResourceBinding[] resources = inventory.ToArray();
        foreach (ref readonly GraphParameterResourceBinding binding in resources.AsSpan())
            ValidateParameterBinding(binding);
        GraphIdentity identity = Staging.ShaderTables.Add(
            Graph.Identity,
            new GraphRayTracingShaderTable
            {
                Resource = table,
                Inventory = resources,
                BoundaryStates =
                [
                    new RayTracingShaderTableBoundaryState(
                        contents,
                        readyAfter?.Queue,
                        readyAfter),
                ],
            });
        return new GraphRayTracingShaderTableId(identity);
    }

    public GraphPersistentParameterBindingsId RegisterPersistentParameterBindings(
        PersistentParameterBindings bindings,
        scoped ReadOnlySpan<GraphParameterResourceBinding> inventory)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        if (!ReferenceEquals(bindings.Device, Graph.Device))
            throw new ArgumentException(
                "The PersistentParameterBindings belong to another Device.",
                nameof(bindings));
        GraphParameterResourceBinding[] resources = inventory.ToArray();
        foreach (ref readonly GraphParameterResourceBinding binding in resources.AsSpan())
            ValidateParameterBinding(binding);
        GraphIdentity identity = Staging.PersistentBindings.Add(
            Graph.Identity,
            new GraphPersistentParameterBindings
            {
                Resource = bindings,
                Inventory = resources,
            });
        return new GraphPersistentParameterBindingsId(identity);
    }

    public void Remove(GraphBufferId buffer)
    {
        ValidateOwner(buffer.Value);
        _ = Staging.Buffers.Remove(buffer.Value);
    }

    public void Remove(GraphTextureId texture)
    {
        ValidateOwner(texture.Value);
        _ = Staging.Textures.Remove(texture.Value);
    }

    public void Remove(GraphPersistentParameterBindingsId bindings)
    {
        ValidateOwner(bindings.Value);
        _ = Staging.PersistentBindings.Remove(bindings.Value);
    }

    public void Remove(GraphQueryPoolId pool)
    {
        ValidateOwner(pool.Value);
        _ = Staging.QueryPools.Remove(pool.Value);
    }

    public void Remove(GraphRayTracingShaderTableId table)
    {
        ValidateOwner(table.Value);
        _ = Staging.ShaderTables.Remove(table.Value);
    }

    public void Remove(GraphPassId pass)
    {
        ValidateOwner(pass.Value);
        GraphPass removed = Staging.Passes.Remove(pass.Value);
        foreach (GraphIdentity access in removed.Accesses)
            if (Staging.Accesses.Contains(access)) _ = Staging.Accesses.Remove(access);
        Staging.Orders.RemoveAll(order => order.Predecessor.Equals(pass.Value) || order.Consumer.Equals(pass.Value));
    }

    public GraphExtensionPointId AddExtensionPoint(string label)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        GraphIdentity identity = Staging.ExtensionPoints.Add(
            Graph.Identity,
            new GraphExtensionPoint(label, Staging.Passes.Count));
        return new GraphExtensionPointId(identity);
    }

    public void OrderAfter(GraphPassId pass, GraphPassId predecessor)
    {
        ValidatePass(pass);
        ValidatePass(predecessor);
        if (pass == predecessor) throw new ArgumentException("A pass cannot depend on itself.");
        ExplicitPassOrder order = new(predecessor.Value, pass.Value);
        if (!Staging.Orders.Contains(order)) Staging.Orders.Add(order);
    }

    public GraphPassId AddRasterPass<TStatic, TFrame>(
        string label,
        in PassQueueSelection queue,
        in TStatic staticData,
        in PassOptions options,
        PassDeclaration<TStatic> declaration,
        RasterPassCallback<TStatic, TFrame> callback)
    {
        return AddPass(
            label,
            GraphPassKind.Raster,
            queue,
            staticData,
            options,
            declaration,
            new RasterPassCallbackStorage<TStatic, TFrame>(staticData, callback, Graph.FrameSlotCount));
    }

    public GraphPassId AddComputePass<TStatic, TFrame>(
        string label,
        in PassQueueSelection queue,
        in TStatic staticData,
        in PassOptions options,
        PassDeclaration<TStatic> declaration,
        ComputePassCallback<TStatic, TFrame> callback)
    {
        return AddPass(
            label,
            GraphPassKind.Compute,
            queue,
            staticData,
            options,
            declaration,
            new ComputePassCallbackStorage<TStatic, TFrame>(staticData, callback, Graph.FrameSlotCount));
    }

    public GraphPassId AddCopyPass<TStatic, TFrame>(
        string label,
        in PassQueueSelection queue,
        in TStatic staticData,
        in PassOptions options,
        PassDeclaration<TStatic> declaration,
        CopyPassCallback<TStatic, TFrame> callback)
    {
        return AddPass(
            label,
            GraphPassKind.Copy,
            queue,
            staticData,
            options,
            declaration,
            new CopyPassCallbackStorage<TStatic, TFrame>(staticData, callback, Graph.FrameSlotCount));
    }

    public GraphPassId AddGeneralPass<TStatic, TFrame>(
        string label,
        in PassQueueSelection queue,
        in TStatic staticData,
        in PassOptions options,
        PassDeclaration<TStatic> declaration,
        GeneralPassCallback<TStatic, TFrame> callback)
    {
        return AddPass(
            label,
            GraphPassKind.General,
            queue,
            staticData,
            options,
            declaration,
            new GeneralPassCallbackStorage<TStatic, TFrame>(staticData, callback, Graph.FrameSlotCount));
    }

    public GraphBufferCbvId CreateBufferCbv(GraphBufferId buffer, in BufferRange range, string? label = null) =>
        new(AddBufferView(GraphViewKind.BufferCbv, buffer, range, null, 0, default, 0, label));

    public GraphBufferSrvId CreateBufferSrv(
        GraphBufferId buffer,
        in BufferRange range,
        Format? format = null,
        uint structureStride = 0,
        string? label = null) =>
        new(AddBufferView(GraphViewKind.BufferSrv, buffer, range, format, structureStride, default, 0, label));

    public GraphBufferUavId CreateBufferUav(
        GraphBufferId buffer,
        in BufferRange range,
        Format? format = null,
        uint structureStride = 0,
        GraphBufferId counterBuffer = default,
        ulong counterOffset = 0,
        string? label = null) =>
        new(AddBufferView(GraphViewKind.BufferUav, buffer, range, format, structureStride,
            counterBuffer.Value, counterOffset, label));

    public GraphTextureSrvId CreateTextureSrv(
        GraphTextureId texture,
        in TextureSubresourceRange range,
        Format format,
        TextureViewDimension dimension,
        string? label = null) =>
        new(AddTextureView(GraphViewKind.TextureSrv, texture, range, format, dimension, false, false, label));

    public GraphTextureUavId CreateTextureUav(
        GraphTextureId texture,
        in TextureSubresourceRange range,
        Format format,
        TextureViewDimension dimension,
        string? label = null) =>
        new(AddTextureView(GraphViewKind.TextureUav, texture, range, format, dimension, false, false, label));

    public GraphColorAttachmentViewId CreateColorAttachmentView(
        GraphTextureId texture,
        in TextureSubresourceRange range,
        Format format,
        TextureViewDimension dimension,
        string? label = null) =>
        new(AddTextureView(GraphViewKind.ColorAttachment, texture, range, format, dimension, false, false, label));

    public GraphDepthStencilViewId CreateDepthStencilView(
        GraphTextureId texture,
        in TextureSubresourceRange range,
        Format format,
        TextureViewDimension dimension,
        bool readOnlyDepth = false,
        bool readOnlyStencil = false,
        string? label = null) =>
        new(AddTextureView(GraphViewKind.DepthStencil, texture, range, format, dimension,
            readOnlyDepth, readOnlyStencil, label));

    public void Commit()
    {
        RenderGraph graph = Graph;
        GraphStructure staging = Staging;
        graph.CommitEdit(_token, staging);
        _committed = true;
        _graph = null;
        _staging = null;
    }

    public void Dispose()
    {
        if (_graph is null) return;
        if (!_committed) _graph.AbandonEdit(_token);
        _graph = null;
        _staging = null;
    }

    private GraphBufferId AddBuffer(
        in BufferDesc description,
        MemoryType memoryType,
        RenderGraphResourceOwnership ownership,
        RenderGraphResourceLifetime lifetime)
    {
        GraphIdentity identity = Staging.Buffers.Add(Graph.Identity, new GraphBuffer
        {
            Description = description,
            MemoryType = memoryType,
            Ownership = ownership,
            Lifetime = lifetime,
        });
        return new GraphBufferId(identity);
    }

    private GraphTextureId AddTexture(
        in TextureDesc description,
        RenderGraphResourceOwnership ownership,
        RenderGraphResourceLifetime lifetime)
    {
        GraphIdentity identity = Staging.Textures.Add(Graph.Identity, new GraphTexture
        {
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
        });
        return new GraphTextureId(identity);
    }

    private GraphPassId AddPass<TStatic>(
        string label,
        GraphPassKind kind,
        in PassQueueSelection queue,
        in TStatic staticData,
        in PassOptions options,
        PassDeclaration<TStatic> declaration,
        PassCallbackStorage callbackStorage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(declaration);
        var pass = new GraphPass
        {
            Label = label,
            Kind = kind,
            Queue = queue,
            Options = options,
            CallbackStorage = callbackStorage,
            DeclarationOrdinal = Staging.Passes.Count,
        };
        GraphIdentity identity = Staging.Passes.Add(Graph.Identity, pass);
        TStatic mutable = staticData;
        try
        {
            var access = new PassDefinition(this, identity, kind);
            declaration(ref access, ref mutable);
            ((IStaticPassDataStorage<TStatic>)callbackStorage).SetStaticData(mutable);
            return new GraphPassId(identity);
        }
        catch
        {
            foreach (GraphIdentity access in pass.Accesses)
                if (Staging.Accesses.Contains(access)) _ = Staging.Accesses.Remove(access);
            _ = Staging.Passes.Remove(identity);
            throw;
        }
    }

    private GraphIdentity AddBufferView(
        GraphViewKind kind,
        GraphBufferId buffer,
        in BufferRange range,
        Format? format,
        uint stride,
        in GraphIdentity additionalBuffer,
        ulong counterOffset,
        string? label)
    {
        ValidateBuffer(buffer);
        if (additionalBuffer.IsValid) ValidateOwner(additionalBuffer);
        return Staging.Views.Add(Graph.Identity, new GraphView
        {
            Kind = kind,
            Buffer = buffer.Value,
            AdditionalBuffer = additionalBuffer,
            BufferRange = range,
            BufferFormat = format,
            StructureStride = stride,
            CounterOffset = counterOffset,
            Label = label,
        });
    }

    private GraphIdentity AddTextureView(
        GraphViewKind kind,
        GraphTextureId texture,
        in TextureSubresourceRange range,
        Format format,
        TextureViewDimension dimension,
        bool readOnlyDepth,
        bool readOnlyStencil,
        string? label)
    {
        ValidateTexture(texture);
        return Staging.Views.Add(Graph.Identity, new GraphView
        {
            Kind = kind,
            Texture = texture.Value,
            TextureRange = range,
            TextureFormat = format,
            Dimension = dimension,
            ReadOnlyDepth = readOnlyDepth,
            ReadOnlyStencil = readOnlyStencil,
            Label = label,
        });
    }

    internal GraphBufferAccessId AddBufferAccess(
        in GraphIdentity pass,
        GraphBufferId buffer,
        GraphAccessMode mode,
        WriteCoverage coverage,
        PipelineSync sync,
        ResourceAccess access,
        in BufferRange range,
        bool dynamicRange,
        ResourceContentState? resultContents)
    {
        ValidateBuffer(buffer);
        GraphIdentity identity = Staging.Accesses.Add(Graph.Identity, new PassResourceAccess
        {
            Pass = pass,
            TargetKind = GraphAccessTargetKind.Buffer,
            Target = buffer.Value,
            Mode = mode,
            Coverage = coverage,
            Sync = sync,
            Access = access,
            BufferRange = range,
            DynamicRange = dynamicRange,
            ResultContents = resultContents,
        });
        Staging.Passes.Get(pass).Accesses.Add(identity);
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
        bool dynamicRange,
        ResourceContentState? resultContents)
    {
        ValidateTexture(texture);
        GraphIdentity identity = Staging.Accesses.Add(Graph.Identity, new PassResourceAccess
        {
            Pass = pass,
            TargetKind = GraphAccessTargetKind.Texture,
            Target = texture.Value,
            Mode = mode,
            Coverage = coverage,
            Sync = sync,
            Access = access,
            TextureLayout = layout,
            TextureRange = range,
            DynamicRange = dynamicRange,
            ResultContents = resultContents,
        });
        Staging.Passes.Get(pass).Accesses.Add(identity);
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
        ValidateOwner(pool.Value);
        GraphQueryPool queryPool = Staging.QueryPools.Get(pool.Value);
        ValidateQueryRange(queryPool.Resource, range);
        GraphIdentity identity = Staging.Accesses.Add(Graph.Identity, new PassResourceAccess
        {
            Pass = pass,
            TargetKind = GraphAccessTargetKind.QueryPool,
            Target = pool.Value,
            Mode = mode,
            Coverage = coverage,
            Sync = PipelineSync.None,
            Access = ResourceAccess.NoAccess,
            QueryRange = range,
            ResultContents = resultContents,
        });
        Staging.Passes.Get(pass).Accesses.Add(identity);
    }

    internal void AddShaderTableAccess(
        in GraphIdentity pass,
        GraphRayTracingShaderTableId table,
        GraphAccessMode mode,
        WriteCoverage coverage,
        ResourceContentState? resultContents)
    {
        ValidateOwner(table.Value);
        _ = Staging.ShaderTables.Get(table.Value);
        GraphIdentity identity = Staging.Accesses.Add(Graph.Identity, new PassResourceAccess
        {
            Pass = pass,
            TargetKind = GraphAccessTargetKind.RayTracingShaderTable,
            Target = table.Value,
            Mode = mode,
            Coverage = coverage,
            Sync = PipelineSync.None,
            Access = ResourceAccess.NoAccess,
            ResultContents = resultContents,
        });
        Staging.Passes.Get(pass).Accesses.Add(identity);
    }

    internal PassRenderingRegionId AddRenderingRegion(
        in GraphIdentity pass,
        uint x,
        uint y,
        uint width,
        uint height,
        uint firstArrayLayer,
        uint arrayLayerCount)
    {
        if (width == 0 || height == 0 || arrayLayerCount == 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        GraphPass graphPass = Staging.Passes.Get(pass);
        graphPass.RenderingRegions.Add(new PassRenderingRegion(
            x, y, width, height, firstArrayLayer, arrayLayerCount));
        return new PassRenderingRegionId(new GraphIdentity(
            Graph.Identity,
            graphPass.RenderingRegions.Count - 1,
            checked((uint)Staging.Passes.DenseIndex(pass) + 1)));
    }

    internal void SetPassPipeline(in GraphIdentity pass, Pipeline pipeline)
    {
        if (!ReferenceEquals(pipeline.Device, Graph.Device))
            throw new ArgumentException("The Pipeline belongs to another Device.", nameof(pipeline));
        GraphPass row = Staging.Passes.Get(pass);
        if (row.Pipeline is not null)
            throw new InvalidOperationException("The Pass pipeline is already assigned.");
        row.Pipeline = pipeline;
    }

    internal void SetPassParameterBlock(
        in GraphIdentity pass,
        VariableLayoutReflection layout,
        ReadOnlySpan<byte> ordinaryData)
    {
        GraphPass row = Staging.Passes.Get(pass);
        if (row.ParameterLayout != VariableLayoutReflection.Null)
            throw new InvalidOperationException("The Pass parameter block is already assigned.");
        row.ParameterLayout = layout;
        row.ParameterOrdinaryData = ordinaryData.ToArray();
    }

    internal void AddPassParameterBinding(
        in GraphIdentity pass,
        in GraphParameterResourceBinding binding)
    {
        ValidateParameterBinding(binding);
        Staging.Passes.Get(pass).ParameterBindings.Add(binding);
    }

    internal ReadOnlySpan<GraphParameterResourceBinding> UsePersistentParameterBindings(
        in GraphIdentity pass,
        GraphPersistentParameterBindingsId bindings)
    {
        ValidateOwner(bindings.Value);
        GraphPersistentParameterBindings row = Staging.PersistentBindings.Get(bindings.Value);
        GraphPass graphPass = Staging.Passes.Get(pass);
        if (!graphPass.PersistentBindings.Contains(bindings.Value))
            graphPass.PersistentBindings.Add(bindings.Value);
        return row.Inventory;
    }

    internal void AddColorAttachment(in GraphIdentity pass, in GraphColorAttachment attachment) =>
        Staging.Passes.Get(pass).ColorAttachments.Add(attachment);

    internal void SetDepthStencilAttachment(
        in GraphIdentity pass,
        in GraphDepthStencilAttachment attachment)
    {
        GraphPass graphPass = Staging.Passes.Get(pass);
        if (graphPass.DepthStencilAttachment.HasValue)
            throw new InvalidOperationException("The pass already has a depth-stencil attachment.");
        graphPass.DepthStencilAttachment = attachment;
    }

    internal GraphView ResolveView(in GraphIdentity identity)
    {
        ValidateOwner(identity);
        return Staging.Views.Get(identity);
    }

    internal GraphRayTracingShaderTable ResolveShaderTable(
        GraphRayTracingShaderTableId table)
    {
        ValidateOwner(table.Value);
        return Staging.ShaderTables.Get(table.Value);
    }

    internal void ValidateBuffer(GraphBufferId buffer)
    {
        ValidateOwner(buffer.Value);
        _ = Staging.Buffers.Get(buffer.Value);
    }

    internal void ValidateTexture(GraphTextureId texture)
    {
        ValidateOwner(texture.Value);
        _ = Staging.Textures.Get(texture.Value);
    }

    private void ValidatePass(GraphPassId pass)
    {
        ValidateOwner(pass.Value);
        _ = Staging.Passes.Get(pass.Value);
    }

    private void ValidateOwner(in GraphIdentity identity)
    {
        if (!identity.IsValid || identity.Owner != Graph.Identity)
            throw new ArgumentException("The graph identity is default or belongs to another RenderGraph.");
    }

    private static void ValidateQueryRange(QueryPool pool, in QueryRange range)
    {
        if (range.QueryCount == 0 || range.FirstQuery >= pool.Description.Count ||
            range.QueryCount > pool.Description.Count - range.FirstQuery)
            throw new ArgumentOutOfRangeException(nameof(range));
    }

    private void ValidateParameterBinding(in GraphParameterResourceBinding binding)
        => binding.ValidateStatic(Staging, Graph.Device);
}

