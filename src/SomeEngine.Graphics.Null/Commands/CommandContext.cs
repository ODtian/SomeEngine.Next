namespace SomeEngine.Graphics.Null;

internal sealed class CommandContext : ICommandContext
{
    private readonly Device _device;
    private readonly string? _name;
    private readonly List<RecordedCommand> _commands = [];
    private readonly CommandReferences _references = new();
    private readonly Dictionary<uint, BindGroupLayoutHandle> _boundGroups = [];
    private int _ownerThreadId;
    private bool _rendering;
    private bool _finished;
    private bool _disposed;
    private int _debugDepth;
    private PipelineHandle _pipeline;
    private PipelineKind? _pipelineKind;

    public CommandContext(Device device, QueueType queue, string? name)
    {
        _device = device;
        Queue = queue;
        _name = name;
    }

    public QueueType Queue { get; }
    public bool IsFinished => Volatile.Read(ref _finished);

    public void Barriers(ReadOnlySpan<ResourceBarrier> barriers)
    {
        EnterRecording();
        RequireOutsideRendering(nameof(Barriers));
        if (barriers.IsEmpty) return;
        ResourceBarrier[] copy = barriers.ToArray();
        foreach (ResourceBarrier barrier in copy)
        {
            if (Queue != QueueType.Graphics && barrier.Kind == BarrierKind.Transition &&
                (barrier.Before is ResourceState.ResolveSource or ResourceState.ResolveDestination ||
                 barrier.After is ResourceState.ResolveSource or ResourceState.ResolveDestination))
            {
                throw _device.ValidationError("Resolve states require the graphics queue.");
            }
            _device.ValidateBarrierForRecording(barrier);
            AddResourceReference(barrier.Resource);
            if (barrier.AliasingBefore.IsValid) AddResourceReference(barrier.AliasingBefore);
        }
        _commands.Add(new BarrierCommand(copy));
    }

    public void CopyBuffer(BufferHandle source, ulong sourceOffset, BufferHandle destination, ulong destinationOffset, ulong size)
    {
        EnterRecording();
        RequireOutsideRendering(nameof(CopyBuffer));
        _device.ValidateBufferCopyForRecording(source, sourceOffset, destination, destinationOffset, size);
        _references.Buffers.Add(source);
        _references.Buffers.Add(destination);
        _commands.Add(new CopyBufferCommand(source, sourceOffset, destination, destinationOffset, size));
    }

    public void CopyBufferToTexture(in BufferTextureCopy copy)
    {
        EnterRecording();
        RequireOutsideRendering(nameof(CopyBufferToTexture));
        _device.ValidateBufferToTextureForRecording(in copy);
        _references.Buffers.Add(copy.Source);
        _references.Textures.Add(copy.Destination);
        _commands.Add(new CopyBufferToTextureCommand(copy));
    }

    public void CopyTextureToBuffer(in TextureBufferCopy copy)
    {
        EnterRecording();
        RequireOutsideRendering(nameof(CopyTextureToBuffer));
        _device.ValidateTextureToBufferForRecording(in copy);
        _references.Textures.Add(copy.Source);
        _references.Buffers.Add(copy.Destination);
        _commands.Add(new CopyTextureToBufferCommand(copy));
    }

    public void ResolveTexture(in TextureResolveRegion resolve)
    {
        EnterRecording();
        RequireOutsideRendering(nameof(ResolveTexture));
        if (Queue != QueueType.Graphics)
            throw _device.ValidationError("Texture resolves require the graphics queue.");
        _device.ValidateTextureResolveForRecording(in resolve);
        _references.Textures.Add(resolve.Source);
        _references.Textures.Add(resolve.Destination);
        _commands.Add(new ResolveTextureCommand(resolve));
    }

    public void BeginRendering(in RenderingInfo rendering)
    {
        EnterRecording();
        if (Queue != QueueType.Graphics) throw _device.ValidationError("Rendering requires the graphics queue.");
        if (_rendering) throw _device.ValidationError("A rendering scope is already open.");
        RenderingInfo frozen = _device.FreezeRenderingInfo(in rendering, _references);
        _rendering = true;
        _commands.Add(new BeginRenderingCommand(frozen));
    }

    public void EndRendering()
    {
        EnterRecording();
        if (!_rendering) throw _device.ValidationError("No rendering scope is open.");
        _rendering = false;
        _commands.Add(new EndRenderingCommand());
    }

    public void SetPipeline(PipelineHandle pipeline)
    {
        EnterRecording();
        PipelineRecord record = _device.GetPipelineForRecording(pipeline);
        if (Queue == QueueType.Copy) throw _device.ValidationError("The copy queue cannot bind pipelines.");
        if (Queue == QueueType.Compute && record.Kind != PipelineKind.Compute)
        {
            throw _device.ValidationError("The compute queue accepts only compute pipelines.");
        }
        if (_rendering && record.Kind != PipelineKind.Raster)
        {
            throw _device.ValidationError("A rendering scope accepts only raster pipelines.");
        }
        if (!_rendering && record.Kind == PipelineKind.Raster)
        {
            throw _device.ValidationError("A raster pipeline must be bound inside a rendering scope.");
        }

        _pipeline = pipeline;
        _pipelineKind = record.Kind;
        _references.Pipelines.Add(pipeline);
        _commands.Add(new SetPipelineCommand(pipeline));
    }

    public void SetBindGroup(uint groupIndex, BindGroupHandle group)
    {
        EnterRecording();
        if (Queue == QueueType.Copy) throw _device.ValidationError("The copy queue cannot bind descriptor groups.");
        BindGroupRecord record = _device.GetBindGroupForRecording(group);
        _boundGroups[groupIndex] = record.Layout;
        _references.BindGroups.Add(group);
        _commands.Add(new SetBindGroupCommand(groupIndex, group));
    }

    public void SetBindings(uint groupIndex, BindGroupLayoutHandle layout, ReadOnlySpan<BindingWrite> writes)
    {
        EnterRecording();
        if (Queue == QueueType.Copy) throw _device.ValidationError("The copy queue cannot bind descriptors.");
        BindingWrite[] frozen = _device.ValidateAndFreezeBindingWrites(layout, writes, _references);
        _boundGroups[groupIndex] = layout;
        _references.BindGroupLayouts.Add(layout);
        _commands.Add(new SetBindingsCommand(groupIndex, layout, frozen));
    }

    public void SetPushConstants(
        PipelineLayoutHandle layout,
        ShaderStage stages,
        uint byteOffset,
        ReadOnlySpan<byte> data)
    {
        EnterRecording();
        if (Queue == QueueType.Copy) throw _device.ValidationError("The copy queue cannot bind push constants.");
        _device.ValidatePushConstantsForRecording(_pipeline, layout, stages, byteOffset, data.Length);
        _references.PipelineLayouts.Add(layout);
        _commands.Add(new SetPushConstantsCommand(layout, stages, byteOffset, data.ToArray()));
    }

    public void SetViewport(in Viewport viewport)
    {
        EnterRecording();
        if (Queue != QueueType.Graphics) throw _device.ValidationError("Viewports require the graphics queue.");
        if (viewport.Width <= 0 || viewport.Height <= 0 || viewport.MinDepth < 0 || viewport.MaxDepth > 1 || viewport.MinDepth > viewport.MaxDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(viewport));
        }
        _commands.Add(new SetViewportCommand(viewport));
    }

    public void SetScissor(in Rect rect)
    {
        EnterRecording();
        if (Queue != QueueType.Graphics) throw _device.ValidationError("Scissors require the graphics queue.");
        if (rect.Width <= 0 || rect.Height <= 0) throw new ArgumentOutOfRangeException(nameof(rect));
        _commands.Add(new SetScissorCommand(rect));
    }

    public void SetVertexBuffer(uint slot, BufferHandle buffer, ulong offset, uint stride)
    {
        EnterRecording();
        if (Queue != QueueType.Graphics) throw _device.ValidationError("Vertex buffers require the graphics queue.");
        _device.ValidateVertexBufferForRecording(buffer, offset, stride);
        _references.Buffers.Add(buffer);
        _commands.Add(new SetVertexBufferCommand(slot, buffer, offset, stride));
    }

    public void SetIndexBuffer(BufferHandle buffer, ulong offset, IndexFormat format)
    {
        EnterRecording();
        if (Queue != QueueType.Graphics) throw _device.ValidationError("Index buffers require the graphics queue.");
        _device.ValidateIndexBufferForRecording(buffer, offset, format);
        _references.Buffers.Add(buffer);
        _commands.Add(new SetIndexBufferCommand(buffer, offset, format));
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        EnterRecording();
        if (!_rendering || _pipelineKind != PipelineKind.Raster)
        {
            throw _device.ValidationError("Draw requires an open rendering scope and a raster pipeline.");
        }
        if (vertexCount == 0 || instanceCount == 0) throw new ArgumentOutOfRangeException(nameof(vertexCount));
        _device.ValidatePipelineBindings(_pipeline, _boundGroups);
        _commands.Add(new DrawCommand(vertexCount, instanceCount, firstVertex, firstInstance));
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        EnterRecording();
        if (!_rendering || _pipelineKind != PipelineKind.Raster)
        {
            throw _device.ValidationError("DrawIndexed requires an open rendering scope and a raster pipeline.");
        }
        if (indexCount == 0 || instanceCount == 0) throw new ArgumentOutOfRangeException(nameof(indexCount));
        _device.ValidatePipelineBindings(_pipeline, _boundGroups);
        _commands.Add(new DrawIndexedCommand(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance));
    }

    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        EnterRecording();
        RequireOutsideRendering(nameof(Dispatch));
        if (Queue == QueueType.Copy || _pipelineKind != PipelineKind.Compute)
        {
            throw _device.ValidationError("Dispatch requires a compute pipeline on a graphics or compute queue.");
        }
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0) throw new ArgumentOutOfRangeException(nameof(groupCountX));
        _device.ValidatePipelineBindings(_pipeline, _boundGroups);
        _commands.Add(new DispatchCommand(groupCountX, groupCountY, groupCountZ));
    }

    public void PushDebugGroup(string name)
    {
        EnterRecording();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _debugDepth++;
        _commands.Add(new PushDebugGroupCommand(name));
    }

    public void PopDebugGroup()
    {
        EnterRecording();
        if (_debugDepth == 0) throw _device.ValidationError("No debug group is open.");
        _debugDepth--;
        _commands.Add(new PopDebugGroupCommand());
    }

    public CommandListHandle Finish()
    {
        EnterRecording();
        if (_rendering) throw _device.ValidationError("Finish rejected an unclosed rendering scope.");
        if (_debugDepth != 0) throw _device.ValidationError("Finish rejected unclosed debug groups.");
        if (_finished) throw _device.ValidationError("A command context can be finished only once.");
        _finished = true;
        return _device.PublishCommandList(Queue, _commands.ToArray(), _references, _name);
    }

    public void Dispose()
    {
        if (_disposed) return;
        BindOrValidateThread();
        _disposed = true;
        if (!_finished) _commands.Clear();
    }

    private void EnterRecording()
    {
        _device.EnsureAvailableForContext();
        BindOrValidateThread();
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw _device.ValidationError("The command context has already been finished.");
    }

    private void BindOrValidateThread()
    {
        int current = Environment.CurrentManagedThreadId;
        int owner = Volatile.Read(ref _ownerThreadId);
        if (owner == 0)
        {
            owner = Interlocked.CompareExchange(ref _ownerThreadId, current, 0);
            if (owner == 0) owner = current;
        }
        if (owner != current)
        {
            throw _device.ValidationError($"Command context is owned by managed thread {owner}, not {current}.");
        }
    }

    private void RequireOutsideRendering(string operation)
    {
        if (_rendering) throw _device.ValidationError($"{operation} is not permitted inside a rendering scope.");
    }

    private void AddResourceReference(ResourceHandle resource)
    {
        switch (resource.Kind)
        {
            case ResourceKind.Buffer:
                _references.Buffers.Add(new BufferHandle(resource.Domain, resource.Slot, resource.Generation));
                break;
            case ResourceKind.Texture:
                _references.Textures.Add(new TextureHandle(resource.Domain, resource.Slot, resource.Generation));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(resource));
        }
    }
}
