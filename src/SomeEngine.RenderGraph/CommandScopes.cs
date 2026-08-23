namespace SomeEngine.RenderGraph;

public ref struct RasterPassCommandScope
{
    private readonly RenderGraphFrameState _frame;
    private readonly int _passIndex;
    private readonly CommandContext _context;
    private int _eventDepth;

    internal RasterPassCommandScope(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        _frame = frame;
        _passIndex = passIndex;
        _context = context;
    }

    public Buffer GetBuffer(GraphBufferId id) => _frame.GetBuffer(_passIndex, id);
    public Texture GetTexture(GraphTextureId id) => _frame.GetTexture(_passIndex, id);
    public BufferCbv GetBufferCbv(GraphBufferCbvId id) => _frame.GetBufferCbv(_passIndex, id);
    public BufferSrv GetBufferSrv(GraphBufferSrvId id) => _frame.GetBufferSrv(_passIndex, id);
    public BufferUav GetBufferUav(GraphBufferUavId id) => _frame.GetBufferUav(_passIndex, id);
    public TextureSrv GetTextureSrv(GraphTextureSrvId id) => _frame.GetTextureSrv(_passIndex, id);
    public TextureUav GetTextureUav(GraphTextureUavId id) => _frame.GetTextureUav(_passIndex, id);

    public void SetPipeline(Pipeline pipeline) => _frame.Backend.SetPipeline(_context, pipeline);
    public void SetPersistentParameterBindings(GraphPersistentParameterBindingsId bindings) =>
        _frame.Backend.SetPersistentParameterBindings(
            _context,
            _frame.Executor.GetPersistentParameterBindings(_passIndex, bindings));
    public void SetTransientParameterBindings(in ParameterBlockBindings bindings)
    {
        _frame.Executor.ValidateBindings(_passIndex, bindings.Resources);
        _frame.Backend.SetTransientParameterBindings(_context, bindings);
    }
    public void SetVertexBuffers(uint firstSlot, ReadOnlySpan<VertexBufferBinding> bindings)
    {
        foreach (ref readonly VertexBufferBinding binding in bindings)
            _frame.Executor.ValidateBuffer(_passIndex, binding.Buffer,
                new BufferRange(binding.Offset, binding.Size), write: false);
        _frame.Backend.SetVertexBuffers(_context, firstSlot, bindings);
    }
    public void SetIndexBuffer(in IndexBufferBinding binding)
    {
        _frame.Executor.ValidateBuffer(_passIndex, binding.Buffer,
            new BufferRange(binding.Offset, binding.Size), write: false);
        _frame.Backend.SetIndexBuffer(_context, binding);
    }
    public void SetStreamOutputBuffers(uint firstSlot, ReadOnlySpan<StreamOutputBufferBinding> bindings)
    {
        foreach (ref readonly StreamOutputBufferBinding binding in bindings)
        {
            _frame.Executor.ValidateBuffer(_passIndex, binding.Buffer,
                new BufferRange(binding.Offset, binding.Size), write: true);
            if (binding.FilledSizeBuffer is not null)
                _frame.Executor.ValidateBuffer(_passIndex, binding.FilledSizeBuffer,
                    new BufferRange(binding.FilledSizeOffset, sizeof(uint)), write: true);
        }
        _frame.Backend.SetStreamOutputBuffers(_context, firstSlot, bindings);
    }
    public void SetViewports(scoped ReadOnlySpan<Viewport> viewports) =>
        _frame.Backend.SetViewports(_context, viewports);
    public void SetScissors(scoped ReadOnlySpan<ScissorRect> scissors) =>
        _frame.Backend.SetScissors(_context, scissors);
    public void SetBlendConstants(in Vector4 value) => _frame.Backend.SetBlendConstants(_context, value);
    public void SetStencilReference(uint value) => _frame.Backend.SetStencilReference(_context, value);
    public void SetDepthBounds(float minimum, float maximum) =>
        _frame.Backend.SetDepthBounds(_context, minimum, maximum);
    public void SetDepthBias(int bias, float clamp, float slopeScaledBias) =>
        _frame.Backend.SetDepthBias(_context, bias, clamp, slopeScaledBias);
    public void SetPrimitiveTopology(PrimitiveTopology topology) =>
        _frame.Backend.SetPrimitiveTopology(_context, topology);
    public void SetStripCut(StripCut stripCut) => _frame.Backend.SetStripCut(_context, stripCut);
    public void SetPredication(Buffer? buffer, ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero)
    {
        if (buffer is not null)
            _frame.Executor.ValidateBuffer(_passIndex, buffer, new BufferRange(offset, sizeof(ulong)), false);
        _frame.Backend.SetPredication(_context, buffer, offset, operation);
    }
    public void Draw(in DrawArguments arguments) => _frame.Backend.Draw(_context, arguments);
    public void DrawIndexed(in DrawIndexedArguments arguments) =>
        _frame.Backend.DrawIndexed(_context, arguments);
    public void ExecuteIndirect(IndirectCommandLayout layout, in BufferRegion arguments,
        uint maximumCommandCount, BufferRegion? count = null)
    {
        _frame.Executor.ValidateBuffer(_passIndex, arguments.Buffer, arguments.Range, false);
        if (count.HasValue)
            _frame.Executor.ValidateBuffer(_passIndex, count.Value.Buffer, count.Value.Range, false);
        _frame.Backend.ExecuteIndirect(_context, layout, arguments, maximumCommandCount, count);
    }
    public void DispatchMesh(in DispatchArguments arguments) =>
        _frame.Backend.DispatchMesh(_context, arguments);
    public void DispatchMeshIndirect(in BufferRegion arguments)
    {
        _frame.Executor.ValidateBuffer(_passIndex, arguments.Buffer, arguments.Range, false);
        _frame.Backend.DispatchMeshIndirect(_context, arguments);
    }
    public void SetShadingRate(ShadingRate rate, ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner) =>
        _frame.Backend.SetShadingRate(_context, rate, primitiveCombiner, imageCombiner);
    public void SetShadingRateImage(Texture? texture)
    {
        if (texture is not null) _frame.Executor.ValidateTexture(_passIndex, texture, null, false);
        _frame.Backend.SetShadingRateImage(_context, texture);
    }
    public void BeginEvent(ReadOnlySpan<byte> utf8Label)
    {
        _frame.Backend.BeginEvent(_context, utf8Label);
        _eventDepth++;
    }
    public void EndEvent()
    {
        if (_eventDepth == 0) throw new InvalidOperationException("No event is active.");
        _frame.Backend.EndEvent(_context);
        _eventDepth--;
    }
    public void SetMarker(ReadOnlySpan<byte> utf8Label) => _frame.Backend.SetMarker(_context, utf8Label);
    internal void Finish()
    {
        if (_eventDepth != 0) throw new InvalidOperationException("Pass events are not balanced.");
    }
}

public ref struct ComputePassCommandScope
{
    private readonly RenderGraphFrameState _frame;
    private readonly int _passIndex;
    private readonly CommandContext _context;
    private int _eventDepth;

    internal ComputePassCommandScope(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        _frame = frame;
        _passIndex = passIndex;
        _context = context;
    }

    public Buffer GetBuffer(GraphBufferId id) => _frame.GetBuffer(_passIndex, id);
    public Texture GetTexture(GraphTextureId id) => _frame.GetTexture(_passIndex, id);
    public BufferCbv GetBufferCbv(GraphBufferCbvId id) => _frame.GetBufferCbv(_passIndex, id);
    public BufferSrv GetBufferSrv(GraphBufferSrvId id) => _frame.GetBufferSrv(_passIndex, id);
    public BufferUav GetBufferUav(GraphBufferUavId id) => _frame.GetBufferUav(_passIndex, id);
    public TextureSrv GetTextureSrv(GraphTextureSrvId id) => _frame.GetTextureSrv(_passIndex, id);
    public TextureUav GetTextureUav(GraphTextureUavId id) => _frame.GetTextureUav(_passIndex, id);

    public void SetPipeline(Pipeline pipeline) => _frame.Backend.SetPipeline(_context, pipeline);
    public void SetPersistentParameterBindings(GraphPersistentParameterBindingsId bindings) =>
        _frame.Backend.SetPersistentParameterBindings(
            _context,
            _frame.Executor.GetPersistentParameterBindings(_passIndex, bindings));
    public void SetTransientParameterBindings(in ParameterBlockBindings bindings)
    {
        _frame.Executor.ValidateBindings(_passIndex, bindings.Resources);
        _frame.Backend.SetTransientParameterBindings(_context, bindings);
    }
    public void SetPredication(Buffer? buffer, ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero)
    {
        if (buffer is not null)
            _frame.Executor.ValidateBuffer(_passIndex, buffer, new BufferRange(offset, sizeof(ulong)), false);
        _frame.Backend.SetPredication(_context, buffer, offset, operation);
    }
    public void Dispatch(in DispatchArguments arguments) => _frame.Backend.Dispatch(_context, arguments);
    public void ExecuteIndirect(IndirectCommandLayout layout, in BufferRegion arguments,
        uint maximumCommandCount, BufferRegion? count = null)
    {
        _frame.Executor.ValidateBuffer(_passIndex, arguments.Buffer, arguments.Range, false);
        if (count.HasValue)
            _frame.Executor.ValidateBuffer(_passIndex, count.Value.Buffer, count.Value.Range, false);
        _frame.Backend.ExecuteIndirect(_context, layout, arguments, maximumCommandCount, count);
    }
    public void CopyBuffer(in BufferCopy copy)
    {
        _frame.Executor.ValidateBuffer(_passIndex, copy.Source,
            new BufferRange(copy.SourceOffset, copy.Size), false);
        _frame.Executor.ValidateBuffer(_passIndex, copy.Destination,
            new BufferRange(copy.DestinationOffset, copy.Size), true);
        _frame.Backend.CopyBuffer(_context, copy);
    }
    public void ClearBuffer(Buffer buffer, in BufferRange range, uint value = 0)
    {
        _frame.Executor.ValidateBuffer(_passIndex, buffer, range, true);
        _frame.Backend.ClearBuffer(_context, buffer, range, value);
    }
    public void BuildAccelerationStructure(in AccelerationStructureBuildDesc description)
    {
        _frame.Executor.ValidateAccelerationStructureBuild(_passIndex, description);
        _frame.Backend.BuildAccelerationStructure(_context, description);
    }
    public void CopyAccelerationStructure(AccelerationStructure destination,
        AccelerationStructure source, AccelerationStructureCopyType type)
    {
        _frame.Executor.ValidateAccelerationStructure(_passIndex, source, write: false);
        _frame.Executor.ValidateAccelerationStructure(_passIndex, destination, write: true);
        _frame.Backend.CopyAccelerationStructure(_context, destination, source, type);
    }
    public void SerializeAccelerationStructure(in BufferRegion destination, AccelerationStructure source)
    {
        _frame.Executor.ValidateAccelerationStructure(_passIndex, source, write: false);
        _frame.Executor.ValidateBuffer(_passIndex, destination.Buffer, destination.Range, true);
        _frame.Backend.SerializeAccelerationStructure(_context, destination, source);
    }
    public void DeserializeAccelerationStructure(AccelerationStructure destination, in BufferRegion source)
    {
        _frame.Executor.ValidateBuffer(_passIndex, source.Buffer, source.Range, false);
        _frame.Executor.ValidateAccelerationStructure(_passIndex, destination, write: true);
        _frame.Backend.DeserializeAccelerationStructure(_context, destination, source);
    }
    public void EmitAccelerationStructurePostBuildInfo(AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type, Buffer destination, ulong destinationOffset)
    {
        _frame.Executor.ValidateAccelerationStructure(_passIndex, source, write: false);
        _frame.Executor.ValidateBuffer(_passIndex, destination,
            new BufferRange(destinationOffset, sizeof(ulong)), true);
        _frame.Backend.EmitAccelerationStructurePostBuildInfo(
            _context, source, type, destination, destinationOffset);
    }
    public void UpdateRayTracingShaderTable(GraphRayTracingShaderTableId table,
        in RayTracingShaderTableUpdate update)
    {
        RayTracingShaderTable resource =
            _frame.Executor.GetRayTracingShaderTable(_passIndex, table, write: true);
        _frame.Executor.ValidateBindings(_passIndex, update.Resources);
        _frame.Backend.UpdateRayTracingShaderTable(_context, resource, update);
    }
    public void DispatchRays(
        GraphRayTracingShaderTableId table,
        uint width,
        uint height = 1,
        uint depth = 1)
    {
        RayTracingShaderTable resource =
            _frame.Executor.GetRayTracingShaderTable(_passIndex, table, write: false);
        _frame.Backend.DispatchRays(
            _context,
            new DispatchRaysDesc(resource, width, height, depth));
    }
    public void BindWorkGraph(Pipeline pipeline, in BufferRegion? backingMemory,
        WorkGraphInitialization initialization)
    {
        if (backingMemory.HasValue)
            _frame.Executor.ValidateBuffer(_passIndex, backingMemory.Value.Buffer,
                backingMemory.Value.Range, true);
        _frame.Backend.BindWorkGraph(_context, pipeline, backingMemory, initialization);
    }
    public void DispatchWorkGraph(in WorkGraphDispatchDesc description) =>
        _frame.Backend.DispatchWorkGraph(_context, description);
    public void ClearSamplerFeedback(SamplerFeedbackUav feedback)
    {
        _frame.Executor.ValidateSamplerFeedback(_passIndex, feedback, write: true);
        _frame.Backend.ClearSamplerFeedback(_context, feedback);
    }
    public void ResolveSamplerFeedback(
        SamplerFeedbackTexture source,
        Buffer destination,
        in BufferRange destinationRange)
    {
        _frame.Executor.ValidateSamplerFeedbackSource(_passIndex, source);
        _frame.Executor.ValidateBuffer(_passIndex, destination, destinationRange, true);
        _frame.Backend.ResolveSamplerFeedback(_context, source, destination, destinationRange);
    }
    public void ResolveSamplerFeedback(
        SamplerFeedbackTexture source,
        Texture destination,
        in TextureSubresourceRange destinationRange)
    {
        _frame.Executor.ValidateSamplerFeedbackSource(_passIndex, source);
        _frame.Executor.ValidateTexture(_passIndex, destination, destinationRange, true);
        _frame.Backend.ResolveSamplerFeedback(_context, source, destination, destinationRange);
    }
    public void BeginQuery(GraphQueryPoolId pool, uint queryIndex)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex, pool, new QueryRange(queryIndex, 1), write: true);
        _frame.Backend.BeginQuery(_context, resource, queryIndex);
    }
    public void EndQuery(GraphQueryPoolId pool, uint queryIndex)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex, pool, new QueryRange(queryIndex, 1), write: true);
        _frame.Backend.EndQuery(_context, resource, queryIndex);
    }
    public void WriteTimestamp(GraphQueryPoolId pool, uint queryIndex)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex, pool, new QueryRange(queryIndex, 1), write: true);
        _frame.Backend.WriteTimestamp(_context, resource, queryIndex);
    }
    public void ResolveQueries(GraphQueryPoolId pool, uint firstQuery, uint queryCount,
        Buffer destination, in BufferRange destinationRange)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex,
            pool,
            new QueryRange(firstQuery, queryCount),
            write: false);
        _frame.Executor.ValidateBuffer(_passIndex, destination, destinationRange, true);
        _frame.Backend.ResolveQueries(
            _context, resource, firstQuery, queryCount, destination, destinationRange);
    }
    public void BeginEvent(ReadOnlySpan<byte> utf8Label)
    {
        _frame.Backend.BeginEvent(_context, utf8Label);
        _eventDepth++;
    }
    public void EndEvent()
    {
        if (_eventDepth == 0) throw new InvalidOperationException("No event is active.");
        _frame.Backend.EndEvent(_context);
        _eventDepth--;
    }
    public void SetMarker(ReadOnlySpan<byte> utf8Label) => _frame.Backend.SetMarker(_context, utf8Label);
    internal void Finish()
    {
        if (_eventDepth != 0) throw new InvalidOperationException("Pass events are not balanced.");
    }
}

public ref struct CopyPassCommandScope
{
    private readonly RenderGraphFrameState _frame;
    private readonly int _passIndex;
    private readonly CommandContext _context;
    private int _eventDepth;

    internal CopyPassCommandScope(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        _frame = frame;
        _passIndex = passIndex;
        _context = context;
    }

    public Buffer GetBuffer(GraphBufferId id) => _frame.GetBuffer(_passIndex, id);
    public Texture GetTexture(GraphTextureId id) => _frame.GetTexture(_passIndex, id);
    public void CopyBuffer(in BufferCopy copy)
    {
        _frame.Executor.ValidateBuffer(_passIndex, copy.Source,
            new BufferRange(copy.SourceOffset, copy.Size), false);
        _frame.Executor.ValidateBuffer(_passIndex, copy.Destination,
            new BufferRange(copy.DestinationOffset, copy.Size), true);
        _frame.Backend.CopyBuffer(_context, copy);
    }
    public void CopyBufferToTexture(in BufferTextureCopy copy)
    {
        _frame.Executor.ValidateBuffer(_passIndex, copy.Buffer,
            new BufferRange(copy.BufferOffset, EstimateBufferTextureBytes(copy)), false);
        _frame.Executor.ValidateTexture(_passIndex, copy.Texture,
            new TextureSubresourceRange(copy.MipLevel, 1, copy.ArrayLayer, 1, copy.Aspect), true);
        _frame.Backend.CopyBufferToTexture(_context, copy);
    }
    public void CopyTextureToBuffer(in BufferTextureCopy copy)
    {
        _frame.Executor.ValidateTexture(_passIndex, copy.Texture,
            new TextureSubresourceRange(copy.MipLevel, 1, copy.ArrayLayer, 1, copy.Aspect), false);
        _frame.Executor.ValidateBuffer(_passIndex, copy.Buffer,
            new BufferRange(copy.BufferOffset, EstimateBufferTextureBytes(copy)), true);
        _frame.Backend.CopyTextureToBuffer(_context, copy);
    }
    public void CopyTexture(in TextureCopy copy)
    {
        _frame.Executor.ValidateTexture(_passIndex, copy.Source,
            new TextureSubresourceRange(copy.SourceMipLevel, 1, copy.SourceArrayLayer, 1, copy.SourceAspect), false);
        _frame.Executor.ValidateTexture(_passIndex, copy.Destination,
            new TextureSubresourceRange(copy.DestinationMipLevel, 1, copy.DestinationArrayLayer, 1, copy.DestinationAspect), true);
        _frame.Backend.CopyTexture(_context, copy);
    }
    public void ResolveTexture(in TextureResolve resolve)
    {
        _frame.Executor.ValidateTexture(_passIndex, resolve.Source,
            new TextureSubresourceRange(resolve.SourceMipLevel, 1, resolve.SourceArrayLayer, 1,
                TextureFormatRules.Aspects(resolve.Format)), false);
        _frame.Executor.ValidateTexture(_passIndex, resolve.Destination,
            new TextureSubresourceRange(resolve.DestinationMipLevel, 1, resolve.DestinationArrayLayer, 1,
                TextureFormatRules.Aspects(resolve.Format)), true);
        _frame.Backend.ResolveTexture(_context, resolve);
    }
    public void ClearBuffer(Buffer buffer, in BufferRange range, uint value = 0)
    {
        _frame.Executor.ValidateBuffer(_passIndex, buffer, range, true);
        _frame.Backend.ClearBuffer(_context, buffer, range, value);
    }
    public void ClearTexture(Texture texture, in TextureSubresourceRange range, in Vector4 color)
    {
        _frame.Executor.ValidateTexture(_passIndex, texture, range, true);
        _frame.Backend.ClearTexture(_context, texture, range, color);
    }
    public void ClearDepthStencil(Texture texture, in TextureSubresourceRange range,
        float depth = 1, byte stencil = 0)
    {
        _frame.Executor.ValidateTexture(_passIndex, texture, range, true);
        _frame.Backend.ClearDepthStencil(_context, texture, range, depth, stencil);
    }
    public void ResolveQueries(GraphQueryPoolId pool, uint firstQuery, uint queryCount,
        Buffer destination, in BufferRange destinationRange)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex,
            pool,
            new QueryRange(firstQuery, queryCount),
            write: false);
        _frame.Executor.ValidateBuffer(_passIndex, destination, destinationRange, true);
        _frame.Backend.ResolveQueries(
            _context, resource, firstQuery, queryCount, destination, destinationRange);
    }
    public void WriteTimestamp(GraphQueryPoolId pool, uint queryIndex)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex, pool, new QueryRange(queryIndex, 1), write: true);
        _frame.Backend.WriteTimestamp(_context, resource, queryIndex);
    }
    public void BeginEvent(ReadOnlySpan<byte> utf8Label)
    {
        _frame.Backend.BeginEvent(_context, utf8Label);
        _eventDepth++;
    }
    public void EndEvent()
    {
        if (_eventDepth == 0) throw new InvalidOperationException("No event is active.");
        _frame.Backend.EndEvent(_context);
        _eventDepth--;
    }
    public void SetMarker(ReadOnlySpan<byte> utf8Label) => _frame.Backend.SetMarker(_context, utf8Label);
    internal void Finish()
    {
        if (_eventDepth != 0) throw new InvalidOperationException("Pass events are not balanced.");
    }

    private static ulong EstimateBufferTextureBytes(in BufferTextureCopy copy)
    {
        uint rows = Math.Max(copy.Height, 1);
        uint depth = Math.Max(copy.Depth, 1);
        return checked((ulong)Math.Max(copy.BufferRowPitch, 1) * rows * depth);
    }
}

public ref struct GeneralPassCommandScope
{
    private readonly RenderGraphFrameState _frame;
    private readonly int _passIndex;
    private readonly CommandContext _context;
    private int _eventDepth;
    private bool _rendering;
    private int _nextRegion;

    internal GeneralPassCommandScope(RenderGraphFrameState frame, int passIndex, CommandContext context)
    {
        _frame = frame;
        _passIndex = passIndex;
        _context = context;
    }

    public Buffer GetBuffer(GraphBufferId id) => _frame.GetBuffer(_passIndex, id);
    public Texture GetTexture(GraphTextureId id) => _frame.GetTexture(_passIndex, id);
    public BufferCbv GetBufferCbv(GraphBufferCbvId id) => _frame.GetBufferCbv(_passIndex, id);
    public BufferSrv GetBufferSrv(GraphBufferSrvId id) => _frame.GetBufferSrv(_passIndex, id);
    public BufferUav GetBufferUav(GraphBufferUavId id) => _frame.GetBufferUav(_passIndex, id);
    public TextureSrv GetTextureSrv(GraphTextureSrvId id) => _frame.GetTextureSrv(_passIndex, id);
    public TextureUav GetTextureUav(GraphTextureUavId id) => _frame.GetTextureUav(_passIndex, id);
    public ColorAttachmentView GetColorAttachmentView(GraphColorAttachmentViewId id) =>
        _frame.GetColorAttachmentView(_passIndex, id);
    public DepthStencilView GetDepthStencilView(GraphDepthStencilViewId id) =>
        _frame.GetDepthStencilView(_passIndex, id);

    public void BeginRendering(PassRenderingRegionId region)
    {
        if (_rendering) throw new InvalidOperationException("A rendering region is already active.");
        _frame.Executor.BeginRawRendering(_passIndex, region, _context, _nextRegion++);
        _rendering = true;
    }
    public void EndRendering()
    {
        if (!_rendering) throw new InvalidOperationException("No rendering region is active.");
        _frame.Backend.EndRendering(_context);
        _rendering = false;
    }
    public void SetPipeline(Pipeline pipeline) => _frame.Backend.SetPipeline(_context, pipeline);
    public void SetPersistentParameterBindings(GraphPersistentParameterBindingsId bindings) =>
        _frame.Backend.SetPersistentParameterBindings(
            _context,
            _frame.Executor.GetPersistentParameterBindings(_passIndex, bindings));
    public void SetTransientParameterBindings(in ParameterBlockBindings bindings)
    {
        _frame.Executor.ValidateBindings(_passIndex, bindings.Resources);
        _frame.Backend.SetTransientParameterBindings(_context, bindings);
    }
    public void SetVertexBuffers(uint firstSlot, ReadOnlySpan<VertexBufferBinding> bindings)
    {
        foreach (ref readonly VertexBufferBinding binding in bindings)
            _frame.Executor.ValidateBuffer(_passIndex, binding.Buffer,
                new BufferRange(binding.Offset, binding.Size), write: false);
        _frame.Backend.SetVertexBuffers(_context, firstSlot, bindings);
    }
    public void SetIndexBuffer(in IndexBufferBinding binding)
    {
        _frame.Executor.ValidateBuffer(_passIndex, binding.Buffer,
            new BufferRange(binding.Offset, binding.Size), write: false);
        _frame.Backend.SetIndexBuffer(_context, binding);
    }
    public void SetStreamOutputBuffers(uint firstSlot, ReadOnlySpan<StreamOutputBufferBinding> bindings)
    {
        foreach (ref readonly StreamOutputBufferBinding binding in bindings)
        {
            _frame.Executor.ValidateBuffer(_passIndex, binding.Buffer,
                new BufferRange(binding.Offset, binding.Size), write: true);
            if (binding.FilledSizeBuffer is not null)
                _frame.Executor.ValidateBuffer(_passIndex, binding.FilledSizeBuffer,
                    new BufferRange(binding.FilledSizeOffset, sizeof(uint)), write: true);
        }
        _frame.Backend.SetStreamOutputBuffers(_context, firstSlot, bindings);
    }
    public void SetViewports(scoped ReadOnlySpan<Viewport> viewports) => _frame.Backend.SetViewports(_context, viewports);
    public void SetScissors(scoped ReadOnlySpan<ScissorRect> scissors) => _frame.Backend.SetScissors(_context, scissors);
    public void SetBlendConstants(in Vector4 value) => _frame.Backend.SetBlendConstants(_context, value);
    public void SetStencilReference(uint value) => _frame.Backend.SetStencilReference(_context, value);
    public void SetDepthBounds(float minimum, float maximum) =>
        _frame.Backend.SetDepthBounds(_context, minimum, maximum);
    public void SetDepthBias(int bias, float clamp, float slopeScaledBias) =>
        _frame.Backend.SetDepthBias(_context, bias, clamp, slopeScaledBias);
    public void SetPrimitiveTopology(PrimitiveTopology topology) =>
        _frame.Backend.SetPrimitiveTopology(_context, topology);
    public void SetStripCut(StripCut stripCut) => _frame.Backend.SetStripCut(_context, stripCut);
    public void SetPredication(Buffer? buffer, ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero)
    {
        if (buffer is not null)
            _frame.Executor.ValidateBuffer(_passIndex, buffer,
                new BufferRange(offset, sizeof(ulong)), write: false);
        _frame.Backend.SetPredication(_context, buffer, offset, operation);
    }
    public void Draw(in DrawArguments arguments) => _frame.Backend.Draw(_context, arguments);
    public void DrawIndexed(in DrawIndexedArguments arguments) => _frame.Backend.DrawIndexed(_context, arguments);
    public void Dispatch(in DispatchArguments arguments) => _frame.Backend.Dispatch(_context, arguments);
    public void CopyBuffer(in BufferCopy copy)
    {
        _frame.Executor.ValidateBuffer(_passIndex, copy.Source,
            new BufferRange(copy.SourceOffset, copy.Size), write: false);
        _frame.Executor.ValidateBuffer(_passIndex, copy.Destination,
            new BufferRange(copy.DestinationOffset, copy.Size), write: true);
        _frame.Backend.CopyBuffer(_context, copy);
    }
    public void CopyBufferToTexture(in BufferTextureCopy copy)
    {
        _frame.Executor.ValidateBuffer(_passIndex, copy.Buffer,
            new BufferRange(copy.BufferOffset, EstimateBufferTextureBytes(copy)), write: false);
        _frame.Executor.ValidateTexture(_passIndex, copy.Texture,
            new TextureSubresourceRange(copy.MipLevel, 1, copy.ArrayLayer, 1, copy.Aspect), write: true);
        _frame.Backend.CopyBufferToTexture(_context, copy);
    }
    public void CopyTextureToBuffer(in BufferTextureCopy copy)
    {
        _frame.Executor.ValidateTexture(_passIndex, copy.Texture,
            new TextureSubresourceRange(copy.MipLevel, 1, copy.ArrayLayer, 1, copy.Aspect), write: false);
        _frame.Executor.ValidateBuffer(_passIndex, copy.Buffer,
            new BufferRange(copy.BufferOffset, EstimateBufferTextureBytes(copy)), write: true);
        _frame.Backend.CopyTextureToBuffer(_context, copy);
    }
    public void CopyTexture(in TextureCopy copy)
    {
        _frame.Executor.ValidateTexture(_passIndex, copy.Source,
            new TextureSubresourceRange(copy.SourceMipLevel, 1, copy.SourceArrayLayer, 1, copy.SourceAspect), write: false);
        _frame.Executor.ValidateTexture(_passIndex, copy.Destination,
            new TextureSubresourceRange(copy.DestinationMipLevel, 1, copy.DestinationArrayLayer, 1, copy.DestinationAspect), write: true);
        _frame.Backend.CopyTexture(_context, copy);
    }
    public void ResolveTexture(in TextureResolve resolve)
    {
        TextureAspects aspects = TextureFormatRules.Aspects(resolve.Format);
        _frame.Executor.ValidateTexture(_passIndex, resolve.Source,
            new TextureSubresourceRange(resolve.SourceMipLevel, 1, resolve.SourceArrayLayer, 1, aspects), write: false);
        _frame.Executor.ValidateTexture(_passIndex, resolve.Destination,
            new TextureSubresourceRange(resolve.DestinationMipLevel, 1, resolve.DestinationArrayLayer, 1, aspects), write: true);
        _frame.Backend.ResolveTexture(_context, resolve);
    }
    public void ClearBuffer(Buffer buffer, in BufferRange range, uint value = 0)
    {
        _frame.Executor.ValidateBuffer(_passIndex, buffer, range, write: true);
        _frame.Backend.ClearBuffer(_context, buffer, range, value);
    }
    public void ClearTexture(Texture texture, in TextureSubresourceRange range, in Vector4 color)
    {
        _frame.Executor.ValidateTexture(_passIndex, texture, range, write: true);
        _frame.Backend.ClearTexture(_context, texture, range, color);
    }
    public void ClearDepthStencil(Texture texture, in TextureSubresourceRange range,
        float depth = 1, byte stencil = 0)
    {
        _frame.Executor.ValidateTexture(_passIndex, texture, range, write: true);
        _frame.Backend.ClearDepthStencil(_context, texture, range, depth, stencil);
    }
    public void ExecuteIndirect(IndirectCommandLayout layout, in BufferRegion arguments,
        uint maximumCommandCount, BufferRegion? count = null)
    {
        _frame.Executor.ValidateBuffer(_passIndex, arguments.Buffer, arguments.Range, write: false);
        if (count.HasValue)
            _frame.Executor.ValidateBuffer(_passIndex, count.Value.Buffer, count.Value.Range, write: false);
        _frame.Backend.ExecuteIndirect(_context, layout, arguments, maximumCommandCount, count);
    }
    public void DispatchMesh(in DispatchArguments arguments) => _frame.Backend.DispatchMesh(_context, arguments);
    public void DispatchMeshIndirect(in BufferRegion arguments)
    {
        _frame.Executor.ValidateBuffer(_passIndex, arguments.Buffer, arguments.Range, write: false);
        _frame.Backend.DispatchMeshIndirect(_context, arguments);
    }
    public void SetShadingRate(ShadingRate rate, ShadingRateCombiner primitiveCombiner,
        ShadingRateCombiner imageCombiner) =>
        _frame.Backend.SetShadingRate(_context, rate, primitiveCombiner, imageCombiner);
    public void SetShadingRateImage(Texture? texture)
    {
        if (texture is not null)
            _frame.Executor.ValidateTexture(_passIndex, texture, null, write: false);
        _frame.Backend.SetShadingRateImage(_context, texture);
    }
    public void BuildAccelerationStructure(in AccelerationStructureBuildDesc description)
    {
        _frame.Executor.ValidateAccelerationStructureBuild(_passIndex, description);
        _frame.Backend.BuildAccelerationStructure(_context, description);
    }
    public void CopyAccelerationStructure(AccelerationStructure destination,
        AccelerationStructure source, AccelerationStructureCopyType type)
    {
        _frame.Executor.ValidateAccelerationStructure(_passIndex, source, write: false);
        _frame.Executor.ValidateAccelerationStructure(_passIndex, destination, write: true);
        _frame.Backend.CopyAccelerationStructure(_context, destination, source, type);
    }
    public void SerializeAccelerationStructure(in BufferRegion destination, AccelerationStructure source)
    {
        _frame.Executor.ValidateAccelerationStructure(_passIndex, source, write: false);
        _frame.Executor.ValidateBuffer(_passIndex, destination.Buffer, destination.Range, write: true);
        _frame.Backend.SerializeAccelerationStructure(_context, destination, source);
    }
    public void DeserializeAccelerationStructure(AccelerationStructure destination, in BufferRegion source)
    {
        _frame.Executor.ValidateBuffer(_passIndex, source.Buffer, source.Range, write: false);
        _frame.Executor.ValidateAccelerationStructure(_passIndex, destination, write: true);
        _frame.Backend.DeserializeAccelerationStructure(_context, destination, source);
    }
    public void EmitAccelerationStructurePostBuildInfo(AccelerationStructure source,
        AccelerationStructurePostBuildInfoType type, Buffer destination, ulong destinationOffset)
    {
        _frame.Executor.ValidateAccelerationStructure(_passIndex, source, write: false);
        _frame.Executor.ValidateBuffer(_passIndex, destination,
            new BufferRange(destinationOffset, sizeof(ulong)), write: true);
        _frame.Backend.EmitAccelerationStructurePostBuildInfo(
            _context, source, type, destination, destinationOffset);
    }
    public void UpdateRayTracingShaderTable(GraphRayTracingShaderTableId table,
        in RayTracingShaderTableUpdate update)
    {
        RayTracingShaderTable resource =
            _frame.Executor.GetRayTracingShaderTable(_passIndex, table, write: true);
        _frame.Executor.ValidateBindings(_passIndex, update.Resources);
        _frame.Backend.UpdateRayTracingShaderTable(_context, resource, update);
    }
    public void DispatchRays(
        GraphRayTracingShaderTableId table,
        uint width,
        uint height = 1,
        uint depth = 1)
    {
        RayTracingShaderTable resource =
            _frame.Executor.GetRayTracingShaderTable(_passIndex, table, write: false);
        _frame.Backend.DispatchRays(
            _context,
            new DispatchRaysDesc(resource, width, height, depth));
    }
    public void BindWorkGraph(Pipeline pipeline, in BufferRegion? backingMemory,
        WorkGraphInitialization initialization)
    {
        if (backingMemory.HasValue)
            _frame.Executor.ValidateBuffer(_passIndex, backingMemory.Value.Buffer,
                backingMemory.Value.Range, write: true);
        _frame.Backend.BindWorkGraph(_context, pipeline, backingMemory, initialization);
    }
    public void DispatchWorkGraph(in WorkGraphDispatchDesc description) =>
        _frame.Backend.DispatchWorkGraph(_context, description);
    public void ClearSamplerFeedback(SamplerFeedbackUav feedback)
    {
        _frame.Executor.ValidateSamplerFeedback(_passIndex, feedback, write: true);
        _frame.Backend.ClearSamplerFeedback(_context, feedback);
    }
    public void ResolveSamplerFeedback(
        SamplerFeedbackTexture source,
        Buffer destination,
        in BufferRange destinationRange)
    {
        _frame.Executor.ValidateBuffer(_passIndex, destination, destinationRange, write: true);
        _frame.Backend.ResolveSamplerFeedback(_context, source, destination, destinationRange);
    }
    public void ResolveSamplerFeedback(
        SamplerFeedbackTexture source,
        Texture destination,
        in TextureSubresourceRange destinationRange)
    {
        _frame.Executor.ValidateTexture(_passIndex, destination, destinationRange, write: true);
        _frame.Backend.ResolveSamplerFeedback(_context, source, destination, destinationRange);
    }
    public void BeginQuery(GraphQueryPoolId pool, uint queryIndex)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex, pool, new QueryRange(queryIndex, 1), write: true);
        _frame.Backend.BeginQuery(_context, resource, queryIndex);
    }
    public void EndQuery(GraphQueryPoolId pool, uint queryIndex)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex, pool, new QueryRange(queryIndex, 1), write: true);
        _frame.Backend.EndQuery(_context, resource, queryIndex);
    }
    public void WriteTimestamp(GraphQueryPoolId pool, uint queryIndex)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex, pool, new QueryRange(queryIndex, 1), write: true);
        _frame.Backend.WriteTimestamp(_context, resource, queryIndex);
    }
    public void ResolveQueries(GraphQueryPoolId pool, uint firstQuery, uint queryCount,
        Buffer destination, in BufferRange destinationRange)
    {
        QueryPool resource = _frame.Executor.GetQueryPool(
            _passIndex,
            pool,
            new QueryRange(firstQuery, queryCount),
            write: false);
        _frame.Executor.ValidateBuffer(_passIndex, destination, destinationRange, write: true);
        _frame.Backend.ResolveQueries(
            _context, resource, firstQuery, queryCount, destination, destinationRange);
    }
    public void BeginEvent(ReadOnlySpan<byte> utf8Label)
    {
        _frame.Backend.BeginEvent(_context, utf8Label);
        _eventDepth++;
    }
    public void EndEvent()
    {
        if (_eventDepth == 0) throw new InvalidOperationException("No event is active.");
        _frame.Backend.EndEvent(_context);
        _eventDepth--;
    }
    public void SetMarker(ReadOnlySpan<byte> utf8Label) => _frame.Backend.SetMarker(_context, utf8Label);
    internal void Finish()
    {
        if (_rendering) throw new InvalidOperationException("The Raw pass ended inside a rendering region.");
        if (_eventDepth != 0) throw new InvalidOperationException("Pass events are not balanced.");
        _frame.Executor.ValidateRawRegionsCompleted(_passIndex, _nextRegion);
    }

    private static ulong EstimateBufferTextureBytes(in BufferTextureCopy copy)
    {
        uint rows = Math.Max(copy.Height, 1);
        uint depth = Math.Max(copy.Depth, 1);
        return checked((ulong)Math.Max(copy.BufferRowPitch, 1) * rows * depth);
    }
}

