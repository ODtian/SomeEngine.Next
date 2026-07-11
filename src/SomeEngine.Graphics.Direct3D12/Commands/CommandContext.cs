using SomeEngine.Graphics;
using D3D12Barrier = Vortice.Direct3D12.ResourceBarrier;
using D3D12Viewport = Vortice.Mathematics.Viewport;
using Vortice.Direct3D12;
using Vortice.Mathematics;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed class CommandContext : ICommandContext
{
    private readonly Device _device;
    private readonly CommandAllocation _allocation;
    private readonly HashSet<NativeLifetime> _usage = new();
    private readonly Dictionary<uint, BoundDescriptorGroup> _boundGroups = [];
    private NativePipeline? _pipeline;
    private BoundColorAttachment[]? _renderingColors;
    private BoundDepthStencilAttachment? _renderingDepthStencil;
    private bool _descriptorHeapsSet;
    private int _ownerThread;
    private bool _finished;
    private bool _disposed;

    public CommandContext(Device device, CommandAllocation allocation)
    {
        _device = device;
        _allocation = allocation;
    }

    public QueueType Queue => _allocation.Queue;
    public bool IsFinished => _finished;

    public void Barriers(ReadOnlySpan<ResourceBarrier> barriers)
    {
        EnsureRecording();
        EnsureOutsideRendering(nameof(Barriers));
        List<D3D12Barrier> native = new();
        for (int index = 0; index < barriers.Length; index++)
        {
            ResourceBarrier barrier = barriers[index];
            switch (barrier.Kind)
            {
                case BarrierKind.Transition:
                {
                    NativeLifetime lifetime = Resolve(barrier.Resource, out Vortice.Direct3D12.ID3D12Resource resource, out TextureDesc? texture);
                    Track(lifetime);
                    if (lifetime is NativeBuffer buffer && BufferStateValidation.HasFixedState(buffer.MemoryType))
                    {
                        if (!BufferStateValidation.IsFixedState(buffer.MemoryType, barrier.Before) ||
                            !BufferStateValidation.IsFixedState(buffer.MemoryType, barrier.After))
                        {
                            throw new InvalidOperationException(
                                $"{buffer.MemoryType} buffers have fixed logical state {BufferStateValidation.DescribeFixedState(buffer.MemoryType)}.");
                        }

                        // Upload/readback heaps cannot transition. Every upload state above maps to
                        // GENERIC_READ, while readback remains COPY_DEST, so a valid logical barrier
                        // intentionally emits no native D3D12 barrier.
                        break;
                    }
                    if (texture is null || barrier.TextureRange == default)
                    {
                        native.Add(D3D12Barrier.BarrierTransition(resource, ResourceStateForQueue(barrier.Before), ResourceStateForQueue(barrier.After)));
                    }
                    else
                    {
                        foreach (uint subresource in EnumerateSubresources(texture.Value, barrier.TextureRange))
                        {
                            native.Add(D3D12Barrier.BarrierTransition(resource, ResourceStateForQueue(barrier.Before), ResourceStateForQueue(barrier.After), subresource));
                        }
                    }
                    break;
                }
                case BarrierKind.UnorderedAccess:
                {
                    NativeLifetime lifetime = Resolve(barrier.Resource, out Vortice.Direct3D12.ID3D12Resource resource, out _);
                    Track(lifetime);
                    native.Add(D3D12Barrier.BarrierUnorderedAccessView(resource));
                    break;
                }
                case BarrierKind.Aliasing:
                {
                    NativeLifetime beforeLifetime = Resolve(barrier.AliasingBefore, out Vortice.Direct3D12.ID3D12Resource before, out _);
                    NativeLifetime afterLifetime = Resolve(barrier.Resource, out Vortice.Direct3D12.ID3D12Resource after, out _);
                    Track(beforeLifetime);
                    Track(afterLifetime);
                    native.Add(D3D12Barrier.BarrierAliasing(before, after));
                    break;
                }
                default:
                    throw new ArgumentOutOfRangeException(nameof(barriers));
            }
        }

        if (native.Count != 0) _allocation.List.ResourceBarrier(native.ToArray());
    }

    public void CopyBuffer(BufferHandle source, ulong sourceOffset, BufferHandle destination, ulong destinationOffset, ulong size)
    {
        EnsureRecording();
        EnsureOutsideRendering(nameof(CopyBuffer));
        NativeBuffer sourceBuffer = _device.GetBuffer(source);
        NativeBuffer destinationBuffer = _device.GetBuffer(destination);
        Device.ValidateRange(sourceBuffer.Desc.Size, sourceOffset, size);
        Device.ValidateRange(destinationBuffer.Desc.Size, destinationOffset, size);
        if ((sourceBuffer.Desc.Usage & BufferUsage.CopySource) == 0) throw new ArgumentException("Source buffer is missing CopySource usage.", nameof(source));
        if ((destinationBuffer.Desc.Usage & BufferUsage.CopyDestination) == 0) throw new ArgumentException("Destination buffer is missing CopyDestination usage.", nameof(destination));
        Track(sourceBuffer);
        Track(destinationBuffer);
        _allocation.List.CopyBufferRegion(destinationBuffer.Resource, destinationOffset, sourceBuffer.Resource, sourceOffset, size);
    }

    public void CopyBufferToTexture(in BufferTextureCopy copy)
    {
        EnsureRecording();
        EnsureOutsideRendering(nameof(CopyBufferToTexture));
        NativeBuffer source = _device.GetBuffer(copy.Source);
        NativeTexture destination = _device.GetTexture(copy.Destination);
        if ((source.Desc.Usage & BufferUsage.CopySource) == 0)
            throw new ArgumentException("Source buffer is missing CopySource usage.", nameof(copy));
        if ((destination.Desc.Usage & TextureUsage.CopyDestination) == 0)
            throw new ArgumentException("Destination texture is missing CopyDestination usage.", nameof(copy));
        ValidateTextureBufferLayout(destination.Desc, copy.DestinationRegion, source.Desc.Size, copy.SourceLayout);
        Track(source);
        Track(destination);
        CopyBufferTexture(source, copy.SourceLayout, destination, copy.DestinationRegion, bufferToTexture: true);
    }

    public void CopyTextureToBuffer(in TextureBufferCopy copy)
    {
        EnsureRecording();
        EnsureOutsideRendering(nameof(CopyTextureToBuffer));
        NativeTexture source = _device.GetTexture(copy.Source);
        NativeBuffer destination = _device.GetBuffer(copy.Destination);
        if ((source.Desc.Usage & TextureUsage.CopySource) == 0) throw new ArgumentException("Source texture is missing CopySource usage.", nameof(copy));
        if ((destination.Desc.Usage & BufferUsage.CopyDestination) == 0) throw new ArgumentException("Destination buffer is missing CopyDestination usage.", nameof(copy));
        ValidateTextureBufferLayout(source.Desc, copy.SourceRegion, destination.Desc.Size, copy.DestinationLayout);
        Track(source);
        Track(destination);
        CopyBufferTexture(destination, copy.DestinationLayout, source, copy.SourceRegion, bufferToTexture: false);
    }

    public void ResolveTexture(in TextureResolveRegion resolve)
    {
        EnsureGraphics(nameof(ResolveTexture));
        EnsureOutsideRendering(nameof(ResolveTexture));
        NativeTexture source = _device.GetTexture(resolve.Source);
        NativeTexture destination = _device.GetTexture(resolve.Destination);
        if (resolve.Source == resolve.Destination)
            throw new ArgumentException("Resolve source and destination must be different textures.", nameof(resolve));
        TextureResolveValidation.Validate(resolve, source.Desc, destination.Desc);
        if (!_device.SupportsAverageTextureResolve(source.Desc.Format))
        {
            throw new NotSupportedException(
                $"D3D12 device does not support multisample resolve for format {source.Desc.Format}.");
        }
        Track(source);
        Track(destination);
        uint sourceSubresource = Device.NativeSubresource(
            source.Desc,
            resolve.SourceMipLevel,
            resolve.SourceArrayLayer,
            resolve.Aspect);
        uint destinationSubresource = Device.NativeSubresource(
            destination.Desc,
            resolve.DestinationMipLevel,
            resolve.DestinationArrayLayer,
            resolve.Aspect);
        _allocation.List.ResolveSubresource(
            destination.Resource,
            destinationSubresource,
            source.Resource,
            sourceSubresource,
            Mappings.Format(source.Desc.Format));
    }

    public void BeginRendering(in RenderingInfo rendering)
    {
        EnsureGraphics(nameof(BeginRendering));
        if (_renderingColors is not null) throw new InvalidOperationException("A rendering scope is already active.");
        if (rendering.Width <= 0 || rendering.Height <= 0) throw new ArgumentOutOfRangeException(nameof(rendering));

        ReadOnlySpan<ColorAttachment> colors = rendering.Colors.Span;
        if (colors.IsEmpty && !rendering.DepthStencil.HasValue)
            throw new ArgumentException("A rendering scope requires at least one color or depth-stencil attachment.", nameof(rendering));
        if (colors.Length > 8) throw new ArgumentOutOfRangeException(nameof(rendering), "D3D12 supports at most eight simultaneous color attachments.");

        BoundColorAttachment[] bound = new BoundColorAttachment[colors.Length];
        CpuDescriptorHandle[] descriptors = new CpuDescriptorHandle[colors.Length];
        int sampleCount = 0;
        for (int index = 0; index < colors.Length; index++)
        {
            ColorAttachment attachment = colors[index];
            NativeTextureView view = _device.GetTextureView(attachment.View);
            if ((view.Usage & TextureViewUsage.ColorAttachment) == 0)
                throw new ArgumentException($"Color attachment {index} lacks ColorAttachment view usage.", nameof(rendering));
            if (rendering.Width > view.Width || rendering.Height > view.Height)
            {
                throw new ArgumentException($"Rendering extent exceeds color attachment {index}.", nameof(rendering));
            }
            if (index == 0) sampleCount = view.SampleCount;
            else if (sampleCount != view.SampleCount) throw new ArgumentException("All color attachments must have the same sample count.", nameof(rendering));
            Track(view);
            descriptors[index] = view.Descriptor;
            bound[index] = new BoundColorAttachment(view, attachment.Store);
        }

        BoundDepthStencilAttachment? depthStencil = rendering.DepthStencil.HasValue
            ? BindDepthStencil(rendering.DepthStencil.Value, rendering.Width, rendering.Height)
            : null;
        if (depthStencil.HasValue)
        {
            if (sampleCount == 0) sampleCount = depthStencil.Value.View.SampleCount;
            else if (sampleCount != depthStencil.Value.View.SampleCount)
                throw new ArgumentException("Color and depth-stencil attachments must have the same sample count.", nameof(rendering));
        }

        ValidatePipelineCompatibility(_pipeline as NativeRasterPipeline, bound, depthStencil);
        _allocation.List.OMSetRenderTargets(descriptors, depthStencil?.Descriptor);
        for (int index = 0; index < colors.Length; index++)
        {
            ColorAttachment attachment = colors[index];
            NativeTextureView view = bound[index].View;
            switch (attachment.Load)
            {
                case LoadAction.Load:
                    break;
                case LoadAction.Clear:
                    _allocation.List.ClearRenderTargetView(
                        view.Descriptor,
                        new Color4(attachment.ClearColor.X, attachment.ClearColor.Y, attachment.ClearColor.Z, attachment.ClearColor.W));
                    break;
                case LoadAction.Discard:
                    Discard(view, TextureAspect.Color);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(rendering));
            }
        }
        if (depthStencil.HasValue) ApplyDepthStencilLoad(depthStencil.Value);
        _renderingColors = bound;
        _renderingDepthStencil = depthStencil;
    }

    public void EndRendering()
    {
        EnsureGraphics(nameof(EndRendering));
        BoundColorAttachment[]? colors = _renderingColors;
        if (colors is null) throw new InvalidOperationException("No rendering scope is active.");
        foreach (BoundColorAttachment attachment in colors)
        {
            if (attachment.Store == StoreAction.Discard) Discard(attachment.View, TextureAspect.Color);
        }
        if (_renderingDepthStencil is BoundDepthStencilAttachment depthStencil)
        {
            if (depthStencil.Depth is DepthAttachmentOperations { Store: StoreAction.Discard })
                Discard(depthStencil.View, TextureAspect.Depth);
            if (depthStencil.Stencil is StencilAttachmentOperations { Store: StoreAction.Discard })
                Discard(depthStencil.View, TextureAspect.Stencil);
        }
        _allocation.List.OMSetRenderTargets(Array.Empty<CpuDescriptorHandle>(), null);
        _renderingColors = null;
        _renderingDepthStencil = null;
    }

    public void SetPipeline(PipelineHandle pipeline)
    {
        EnsureRecording();
        if (Queue == QueueType.Copy) throw new InvalidOperationException("The copy queue cannot bind pipelines.");
        NativePipeline native = _device.GetPipeline(pipeline);
        if (native.Type == PipelineType.Raster && Queue != QueueType.Graphics)
            throw new InvalidOperationException("A raster pipeline requires a graphics command context.");
        if (_renderingColors is not null && native is not NativeRasterPipeline)
            throw new InvalidOperationException("A rendering scope accepts only raster pipelines.");
        if (_renderingColors is not null)
            ValidatePipelineCompatibility((NativeRasterPipeline)native, _renderingColors, _renderingDepthStencil);
        Track(native);
        _allocation.List.SetPipelineState(native.PipelineState);
        if (native is NativeRasterPipeline raster)
        {
            _allocation.List.SetGraphicsRootSignature(native.Layout.RootSignature);
            _allocation.List.IASetPrimitiveTopology(Mappings.Topology(raster.Topology));
        }
        else
        {
            _allocation.List.SetComputeRootSignature(native.Layout.RootSignature);
        }
        _pipeline = native;
        foreach ((uint groupIndex, BoundDescriptorGroup group) in _boundGroups.OrderBy(static pair => pair.Key))
        {
            if (groupIndex < native.Layout.Groups.Length && ReferenceEquals(group.Layout, native.Layout.Groups[groupIndex]))
                MaterializeGroup(groupIndex, group);
        }
    }

    public void SetBindGroup(uint groupIndex, BindGroupHandle group)
    {
        EnsureDescriptorCommand(nameof(SetBindGroup));
        NativeBindGroup native = _device.GetBindGroup(group);
        Track(native);
        BoundDescriptorGroup bound = new(native.Layout, native.Bindings);
        if (_pipeline is not null) MaterializeGroup(groupIndex, bound);
        _boundGroups[groupIndex] = bound;
    }

    public void SetBindings(uint groupIndex, BindGroupLayoutHandle layout, ReadOnlySpan<BindingWrite> writes)
    {
        EnsureDescriptorCommand(nameof(SetBindings));
        NativeBindGroupLayout nativeLayout = _device.GetBindGroupLayout(layout);
        FrozenBinding[] frozen = _device.ValidateAndFreezeBindings(layout, writes);
        Track(nativeLayout);
        foreach (FrozenBinding binding in frozen) Track(binding.Dependency);
        BoundDescriptorGroup bound = new(nativeLayout, frozen);
        if (_pipeline is not null) MaterializeGroup(groupIndex, bound);
        _boundGroups[groupIndex] = bound;
    }

    public unsafe void SetPushConstants(
        PipelineLayoutHandle layout,
        ShaderStage stages,
        uint byteOffset,
        ReadOnlySpan<byte> data)
    {
        EnsureDescriptorCommand(nameof(SetPushConstants));
        if (_pipeline is null) throw new InvalidOperationException("A pipeline must be selected before setting push constants.");
        NativePipelineLayout nativeLayout = _device.GetPipelineLayout(layout);
        if (!ReferenceEquals(nativeLayout, _pipeline.Layout))
            throw new ArgumentException("The push-constant layout is not the selected pipeline's layout.", nameof(layout));
        if (stages == 0 || (stages & ~(ShaderStage.Vertex | ShaderStage.Pixel | ShaderStage.Compute)) != 0)
            throw new ArgumentOutOfRangeException(nameof(stages));
        if (data.IsEmpty || (byteOffset & 3) != 0 || (data.Length & 3) != 0)
            throw new ArgumentException("Push-constant writes require a non-empty 4-byte-aligned offset and size.", nameof(data));
        if (_pipeline.Type == PipelineType.Compute && stages != ShaderStage.Compute)
            throw new ArgumentException("A compute pipeline accepts compute-stage push constants only.", nameof(stages));
        if (_pipeline.Type == PipelineType.Raster && (stages & ShaderStage.Compute) != 0)
            throw new ArgumentException("A raster pipeline cannot accept compute-stage push constants.", nameof(stages));

        ulong end = checked((ulong)byteOffset + (uint)data.Length);
        NativeRootConstant? selected = null;
        foreach (NativeRootConstant candidate in nativeLayout.Constants)
        {
            ulong candidateEnd = checked((ulong)candidate.Range.Offset + candidate.Range.Size);
            if (candidate.Range.Offset <= byteOffset && end <= candidateEnd &&
                (candidate.Range.Visibility & stages) == stages)
            {
                if (selected.HasValue) throw new InvalidOperationException("The push-constant write is ambiguous across layout ranges.");
                selected = candidate;
            }
        }
        NativeRootConstant root = selected ??
            throw new ArgumentException("The push-constant write is outside every compatible layout range.", nameof(data));
        Track(nativeLayout);
        uint destinationOffset = (byteOffset - root.Range.Offset) / 4;
        fixed (byte* source = data)
        {
            uint valueCount = checked((uint)data.Length / 4);
            if (_pipeline.Type == PipelineType.Compute)
                _allocation.List.SetComputeRoot32BitConstants(root.RootParameter, valueCount, source, destinationOffset);
            else
                _allocation.List.SetGraphicsRoot32BitConstants(root.RootParameter, valueCount, source, destinationOffset);
        }
    }
    public void SetViewport(in Viewport viewport)
    {
        EnsureGraphics(nameof(SetViewport));
        if (viewport.Width <= 0f || viewport.Height <= 0f || viewport.MinDepth < 0f || viewport.MaxDepth > 1f || viewport.MinDepth > viewport.MaxDepth)
        {
            throw new ArgumentOutOfRangeException(nameof(viewport));
        }
        _allocation.List.RSSetViewports([new D3D12Viewport(viewport.X, viewport.Y, viewport.Width, viewport.Height, viewport.MinDepth, viewport.MaxDepth)]);
    }

    public void SetScissor(in Rect rect)
    {
        EnsureGraphics(nameof(SetScissor));
        if (rect.Width < 0 || rect.Height < 0) throw new ArgumentOutOfRangeException(nameof(rect));
        int right = checked(rect.X + rect.Width);
        int bottom = checked(rect.Y + rect.Height);
        _allocation.List.RSSetScissorRects([new Vortice.RawRect(rect.X, rect.Y, right, bottom)]);
    }

    public void SetVertexBuffer(uint slot, BufferHandle buffer, ulong offset, uint stride)
    {
        EnsureGraphics(nameof(SetVertexBuffer));
        NativeBuffer native = _device.GetBuffer(buffer);
        if ((native.Desc.Usage & BufferUsage.Vertex) == 0) throw new ArgumentException("Buffer is missing Vertex usage.", nameof(buffer));
        if (stride == 0 || offset >= native.Desc.Size) throw new ArgumentOutOfRangeException(nameof(offset));
        ulong size = native.Desc.Size - offset;
        if (size > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(buffer), "A D3D12 vertex-buffer view cannot exceed 4 GiB.");
        Track(native);
        _allocation.List.IASetVertexBuffers(slot, new VertexBufferView(native.Resource.GPUVirtualAddress + offset, checked((uint)size), stride));
    }

    public void SetIndexBuffer(BufferHandle buffer, ulong offset, IndexFormat format)
    {
        EnsureGraphics(nameof(SetIndexBuffer));
        NativeBuffer native = _device.GetBuffer(buffer);
        if ((native.Desc.Usage & BufferUsage.Index) == 0) throw new ArgumentException("Buffer is missing Index usage.", nameof(buffer));
        uint elementSize = format == IndexFormat.UInt16 ? 2u : 4u;
        if (offset >= native.Desc.Size || offset % elementSize != 0) throw new ArgumentOutOfRangeException(nameof(offset));
        ulong size = native.Desc.Size - offset;
        if (size > uint.MaxValue) throw new ArgumentOutOfRangeException(nameof(buffer), "A D3D12 index-buffer view cannot exceed 4 GiB.");
        Track(native);
        Vortice.DXGI.Format nativeFormat = format == IndexFormat.UInt16 ? Vortice.DXGI.Format.R16_UInt : Vortice.DXGI.Format.R32_UInt;
        _allocation.List.IASetIndexBuffer(new IndexBufferView(native.Resource.GPUVirtualAddress + offset, checked((uint)size), nativeFormat));
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0)
    {
        EnsureCanDraw();
        _allocation.List.DrawInstanced(vertexCount, instanceCount, firstVertex, firstInstance);
    }

    public void DrawIndexed(uint indexCount, uint instanceCount = 1, uint firstIndex = 0, int vertexOffset = 0, uint firstInstance = 0)
    {
        EnsureCanDraw();
        _allocation.List.DrawIndexedInstanced(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }
    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ)
    {
        EnsureRecording();
        if (_renderingColors is not null) throw new InvalidOperationException("Dispatch is not permitted inside a rendering scope.");
        if (Queue == QueueType.Copy || _pipeline is not NativeComputePipeline)
            throw new InvalidOperationException("Dispatch requires a compute pipeline on a graphics or compute queue.");
        if (groupCountX == 0 || groupCountY == 0 || groupCountZ == 0)
            throw new ArgumentOutOfRangeException(nameof(groupCountX));
        ValidateBoundGroups();
        _allocation.List.Dispatch(groupCountX, groupCountY, groupCountZ);
    }

    public void PushDebugGroup(string name)
    {
        EnsureRecording();
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        _allocation.List.BeginEvent(name);
    }

    public void PopDebugGroup()
    {
        EnsureRecording();
        _allocation.List.EndEvent();
    }

    public CommandListHandle Finish()
    {
        EnsureRecording();
        if (_renderingColors is not null) throw new InvalidOperationException("EndRendering must be called before Finish.");
        _allocation.List.Close();
        _finished = true;
        return _device.Register(_allocation, _usage);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (!_finished) _device.Discard(_allocation, _usage);
    }

    private void EnsureRecording()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_finished) throw new InvalidOperationException("The command context has already been finished.");
        int current = Environment.CurrentManagedThreadId;
        int owner = Interlocked.CompareExchange(ref _ownerThread, current, 0);
        if (owner != 0 && owner != current) throw new InvalidOperationException("A command context may only be recorded by one thread.");
    }

    private void Track(NativeLifetime value)
    {
        if (!_usage.Add(value)) return;
        value.PinPending();
        switch (value)
        {
            case NativeBuffer { Parent: not null } buffer:
                Track(buffer.Parent);
                break;
            case NativeTexture { Parent: not null } texture:
                Track(texture.Parent);
                break;
            case NativeTextureView view:
                Track(view.Texture);
                break;
            case NativeBufferView view:
                Track(view.Buffer);
                break;
            case NativeBindGroup group:
                Track(group.Layout);
                foreach (FrozenBinding binding in group.Bindings) Track(binding.Dependency);
                break;
            case NativePipeline pipeline:
                Track(pipeline.Layout);
                if (pipeline is NativeRasterPipeline raster)
                {
                    Track(raster.VertexShader);
                    Track(raster.PixelShader);
                }
                else if (pipeline is NativeComputePipeline compute)
                {
                    Track(compute.Shader);
                }
                break;
            case NativePipelineLayout layout:
                foreach (NativeBindGroupLayout groupLayout in layout.Groups) Track(groupLayout);
                break;
        }
    }

    private NativeLifetime Resolve(ResourceHandle handle, out Vortice.Direct3D12.ID3D12Resource resource, out TextureDesc? texture)
    {
        if (!handle.IsValid) throw new ArgumentException("Resource handle is invalid.", nameof(handle));
        switch (handle.Kind)
        {
            case ResourceKind.Buffer:
            {
                NativeBuffer buffer = _device.GetBuffer(new BufferHandle(handle.Domain, handle.Slot, handle.Generation));
                resource = buffer.Resource;
                texture = null;
                return buffer;
            }
            case ResourceKind.Texture:
            {
                NativeTexture nativeTexture = _device.GetTexture(new TextureHandle(handle.Domain, handle.Slot, handle.Generation));
                resource = nativeTexture.Resource;
                texture = nativeTexture.Desc;
                return nativeTexture;
            }
            default:
                throw new ArgumentException("Resource handle kind is invalid.", nameof(handle));
        }
    }

    private static IEnumerable<uint> EnumerateSubresources(TextureDesc desc, TextureSubresourceRange requested)
    {
        TextureSubresourceRangeValidation.Normalize(
            desc,
            requested,
            out int firstMip,
            out int mipCount,
            out int firstLayer,
            out int layerCount,
            out TextureAspect aspects);
        TextureAspect[] planes = [TextureAspect.Color, TextureAspect.Depth, TextureAspect.Stencil];
        foreach (TextureAspect aspect in planes)
        {
            if ((aspects & aspect) == 0) continue;
            uint plane = aspect == TextureAspect.Stencil ? 1u : 0u;
            for (int layer = firstLayer; layer < firstLayer + layerCount; layer++)
            {
                for (int mip = firstMip; mip < firstMip + mipCount; mip++)
                {
                    yield return checked((uint)mip + (uint)layer * (uint)desc.MipLevels +
                        plane * (uint)desc.MipLevels * (uint)desc.ArrayLayers);
                }
            }
        }
    }

    private void ValidateTextureBufferLayout(
        in TextureDesc texture,
        in TextureCopyRegion region,
        ulong bufferSize,
        in TextureBufferLayout layout)
    {
        Device.ValidateCopyRegion(texture, region, out _, out _, out _);
        TextureCopyFootprint minimum = _device.GetTextureCopyFootprint(texture, region, layout.Offset);
        if (minimum.Layout.Offset != layout.Offset)
            throw new ArgumentException("D3D12 texture-copy buffer offsets must be 512-byte aligned.", nameof(layout));
        if (layout.BytesPerRow == 0 || (layout.BytesPerRow & 255) != 0 || layout.BytesPerRow < minimum.RowSizeInBytes)
            throw new ArgumentException("D3D12 texture-copy row pitch must be a 256-byte-aligned value covering the copied row.", nameof(layout));
        if (layout.RowsPerImage < (uint)region.Height)
            throw new ArgumentException("RowsPerImage is smaller than the copied texture height.", nameof(layout));
        ulong slicePitch = checked((ulong)layout.BytesPerRow * layout.RowsPerImage);
        if (region.Depth > 1 && (slicePitch & 511) != 0)
            throw new ArgumentException("Three-dimensional texture-copy slice pitch must preserve 512-byte placement alignment.", nameof(layout));
        ulong required = checked(
            (ulong)(region.Depth - 1) * slicePitch +
            (ulong)(region.Height - 1) * layout.BytesPerRow +
            minimum.RowSizeInBytes);
        Device.ValidateRange(bufferSize, layout.Offset, required);
    }

    private void CopyBufferTexture(
        NativeBuffer buffer,
        in TextureBufferLayout layout,
        NativeTexture texture,
        in TextureCopyRegion region,
        bool bufferToTexture)
    {
        uint subresource = Device.NativeSubresource(texture.Desc, region.MipLevel, region.ArrayLayer, region.Aspect);
        TextureCopyLocation textureLocation = new(texture.Resource, subresource);
        Vortice.DXGI.Format planeFormat = _device.GetTextureCopyPlaneFormat(texture.Desc, region);
        ulong slicePitch = checked((ulong)layout.BytesPerRow * layout.RowsPerImage);
        for (int slice = 0; slice < region.Depth; slice++)
        {
            PlacedSubresourceFootPrint placed = new()
            {
                Offset = checked(layout.Offset + (ulong)slice * slicePitch),
                Footprint = new SubresourceFootPrint(
                    planeFormat,
                    checked((uint)region.Width),
                    checked((uint)region.Height),
                    1,
                    layout.BytesPerRow),
            };
            TextureCopyLocation bufferLocation = new(buffer.Resource, placed);
            if (bufferToTexture)
            {
                _allocation.List.CopyTextureRegion(
                    textureLocation,
                    checked((uint)region.X),
                    checked((uint)region.Y),
                    checked((uint)(region.Z + slice)),
                    bufferLocation,
                    null);
            }
            else
            {
                Box sourceBox = new(
                    region.X,
                    region.Y,
                    checked(region.Z + slice),
                    checked(region.X + region.Width),
                    checked(region.Y + region.Height),
                    checked(region.Z + slice + 1));
                _allocation.List.CopyTextureRegion(bufferLocation, 0, 0, 0, textureLocation, sourceBox);
            }
        }
    }

    private void EnsureOutsideRendering(string operation)
    {
        if (_renderingColors is not null)
            throw new InvalidOperationException($"{operation} is not valid inside a rendering scope.");
    }

    private ResourceStates ResourceStateForQueue(ResourceState state)
    {
        if (Queue == QueueType.Copy &&
            state is not (ResourceState.Common or ResourceState.CopySource or ResourceState.CopyDestination))
        {
            throw new InvalidOperationException($"The copy queue cannot transition a resource to {state}.");
        }
        if (Queue == QueueType.Compute &&
            state is ResourceState.RenderTarget or ResourceState.DepthWrite or ResourceState.DepthRead or
                ResourceState.IndexBuffer or ResourceState.Present or ResourceState.ResolveSource or
                ResourceState.ResolveDestination)
        {
            throw new InvalidOperationException($"The compute queue cannot transition a resource to {state}.");
        }
        if (state == ResourceState.ShaderResource && Queue != QueueType.Graphics)
            return ResourceStates.NonPixelShaderResource;
        return Mappings.ResourceState(state);
    }

    private void EnsureGraphics(string operation)
    {
        EnsureRecording();
        if (Queue != QueueType.Graphics) throw new InvalidOperationException($"{operation} requires a graphics command context.");
    }

    private void EnsureCanDraw()
    {
        EnsureGraphics(nameof(Draw));
        if (_pipeline is not NativeRasterPipeline) throw new InvalidOperationException("A raster pipeline must be selected before drawing.");
        if (_renderingColors is null) throw new InvalidOperationException("A rendering scope must be active before drawing.");
        ValidateBoundGroups();
    }

    private void EnsureDescriptorCommand(string operation)
    {
        EnsureRecording();
        if (Queue == QueueType.Copy) throw new InvalidOperationException($"{operation} is not supported on a copy command context.");
    }

    private void ValidateBoundGroups()
    {
        NativePipeline pipeline = _pipeline ?? throw new InvalidOperationException("A pipeline must be selected first.");
        for (uint groupIndex = 0; groupIndex < (uint)pipeline.Layout.Groups.Length; groupIndex++)
        {
            if (!_boundGroups.TryGetValue(groupIndex, out BoundDescriptorGroup bound) ||
                !ReferenceEquals(bound.Layout, pipeline.Layout.Groups[groupIndex]))
            {
                throw new InvalidOperationException($"Pipeline descriptor group {groupIndex} is not bound with the required layout.");
            }
        }
    }

    private void MaterializeGroup(uint groupIndex, in BoundDescriptorGroup group)
    {
        NativePipeline pipeline = _pipeline ?? throw new InvalidOperationException("A pipeline must be selected before materializing descriptors.");
        if (groupIndex >= (uint)pipeline.Layout.Groups.Length ||
            !ReferenceEquals(group.Layout, pipeline.Layout.Groups[groupIndex]))
        {
            throw new ArgumentException($"Descriptor group {groupIndex} does not match the selected pipeline layout.", nameof(groupIndex));
        }

        EnsureDescriptorHeaps();
        DescriptorBlock resources = _allocation.Descriptors.AllocateResources(group.Layout.ResourceDescriptorCount);
        DescriptorBlock samplers = _allocation.Descriptors.AllocateSamplers(group.Layout.SamplerDescriptorCount);
        foreach (FrozenBinding binding in group.Bindings)
        {
            bool sampler = binding.Kind == BindingKind.Sampler;
            DescriptorBlock block = sampler ? samplers : resources;
            DescriptorHeapType type = sampler
                ? DescriptorHeapType.Sampler
                : DescriptorHeapType.ConstantBufferViewShaderResourceViewUnorderedAccessView;
            _device.NativeDevice.CopyDescriptorsSimple(
                1,
                block.CpuAt(binding.DescriptorOffset),
                binding.Descriptor.Handle,
                type);
        }

        foreach (NativeRootBinding root in pipeline.Layout.Bindings)
        {
            if (root.Group != groupIndex) continue;
            DescriptorBlock block = root.HeapType == DescriptorHeapType.Sampler ? samplers : resources;
            GpuDescriptorHandle descriptor = block.GpuAt(root.DescriptorOffset);
            if (pipeline.Type == PipelineType.Compute)
                _allocation.List.SetComputeRootDescriptorTable(root.RootParameter, descriptor);
            else
                _allocation.List.SetGraphicsRootDescriptorTable(root.RootParameter, descriptor);
        }
    }

    private void EnsureDescriptorHeaps()
    {
        if (_descriptorHeapsSet) return;
        _allocation.List.SetDescriptorHeaps(_allocation.Descriptors.Heaps);
        _descriptorHeapsSet = true;
    }

    private BoundDepthStencilAttachment BindDepthStencil(
        in DepthStencilAttachment attachment,
        int width,
        int height)
    {
        NativeTextureView view = _device.GetTextureView(attachment.View);
        if ((view.Usage & TextureViewUsage.DepthStencilAttachment) == 0)
            throw new ArgumentException("The depth-stencil attachment lacks DepthStencilAttachment view usage.", nameof(attachment));
        if (width > view.Width || height > view.Height)
            throw new ArgumentException("Rendering extent exceeds the depth-stencil attachment.", nameof(attachment));
        if (!attachment.Depth.HasValue && !attachment.Stencil.HasValue)
            throw new ArgumentException("A depth-stencil attachment must select at least one aspect.", nameof(attachment));

        if (attachment.Depth is DepthAttachmentOperations depth)
        {
            if ((view.Range.Aspect & TextureAspect.Depth) == 0)
                throw new ArgumentException("Depth operations require a view containing the depth aspect.", nameof(attachment));
            ValidateDepthOperations(depth);
        }
        if (attachment.Stencil is StencilAttachmentOperations stencil)
        {
            if (view.Format != Format.D24UNormS8UInt || (view.Range.Aspect & TextureAspect.Stencil) == 0)
                throw new ArgumentException("Stencil operations require a D24S8 view containing the stencil aspect.", nameof(attachment));
            ValidateStencilOperations(stencil);
        }

        bool depthReadOnly = !attachment.Depth.HasValue || attachment.Depth.Value.ReadOnly;
        bool stencilReadOnly = view.Format == Format.D24UNormS8UInt &&
            (!attachment.Stencil.HasValue || attachment.Stencil.Value.ReadOnly);
        CpuDescriptorHandle descriptor = view.GetDepthStencilDescriptor(depthReadOnly, stencilReadOnly);
        Track(view);
        return new BoundDepthStencilAttachment(view, attachment.Depth, attachment.Stencil, descriptor);
    }

    private static void ValidateDepthOperations(in DepthAttachmentOperations operations)
    {
        if (!Enum.IsDefined(operations.Load) || !Enum.IsDefined(operations.Store))
            throw new ArgumentOutOfRangeException(nameof(operations));
        if (operations.ReadOnly &&
            (operations.Load != LoadAction.Load || operations.Store != StoreAction.Store))
        {
            throw new ArgumentException("A read-only depth attachment must load and store its contents.", nameof(operations));
        }
        if (operations.Load == LoadAction.Clear &&
            (!float.IsFinite(operations.ClearValue) || operations.ClearValue < 0f || operations.ClearValue > 1f))
        {
            throw new ArgumentOutOfRangeException(nameof(operations), "A depth clear value must be finite and within [0, 1].");
        }
    }

    private static void ValidateStencilOperations(in StencilAttachmentOperations operations)
    {
        if (!Enum.IsDefined(operations.Load) || !Enum.IsDefined(operations.Store))
            throw new ArgumentOutOfRangeException(nameof(operations));
        if (operations.ReadOnly &&
            (operations.Load != LoadAction.Load || operations.Store != StoreAction.Store))
        {
            throw new ArgumentException("A read-only stencil attachment must load and store its contents.", nameof(operations));
        }
    }

    private void ApplyDepthStencilLoad(in BoundDepthStencilAttachment attachment)
    {
        ClearFlags clearFlags = ClearFlags.None;
        float clearDepth = 1f;
        byte clearStencil = 0;
        if (attachment.Depth is DepthAttachmentOperations depth)
        {
            if (depth.Load == LoadAction.Clear)
            {
                clearFlags |= ClearFlags.Depth;
                clearDepth = depth.ClearValue;
            }
            else if (depth.Load == LoadAction.Discard)
            {
                Discard(attachment.View, TextureAspect.Depth);
            }
        }
        if (attachment.Stencil is StencilAttachmentOperations stencil)
        {
            if (stencil.Load == LoadAction.Clear)
            {
                clearFlags |= ClearFlags.Stencil;
                clearStencil = stencil.ClearValue;
            }
            else if (stencil.Load == LoadAction.Discard)
            {
                Discard(attachment.View, TextureAspect.Stencil);
            }
        }
        if (clearFlags != ClearFlags.None)
        {
            _allocation.List.ClearDepthStencilView(
                attachment.Descriptor,
                clearFlags,
                clearDepth,
                clearStencil);
        }
    }

    private void ValidatePipelineCompatibility(
        NativeRasterPipeline? pipeline,
        ReadOnlySpan<BoundColorAttachment> colors,
        BoundDepthStencilAttachment? depthStencil)
    {
        if (pipeline is null) return;
        if (pipeline.DepthStencilFormat == Format.Unknown)
        {
            if (depthStencil.HasValue)
                throw new InvalidOperationException("The selected raster pipeline does not declare a depth-stencil format.");
        }
        else
        {
            if (!depthStencil.HasValue)
                throw new InvalidOperationException("The selected raster pipeline requires a depth-stencil attachment.");
            BoundDepthStencilAttachment attachment = depthStencil.Value;
            if (pipeline.DepthStencilFormat != attachment.View.Format)
                throw new InvalidOperationException("The selected raster pipeline depth-stencil format does not match the rendering scope.");
            if (pipeline.SampleCount != attachment.View.SampleCount)
                throw new InvalidOperationException("The selected raster pipeline sample count does not match the depth-stencil attachment.");
            if (pipeline.DepthStencil.DepthEnabled && !attachment.Depth.HasValue)
                throw new InvalidOperationException("The selected raster pipeline enables depth testing, but the rendering scope has no depth operations.");
            if (pipeline.DepthStencil.DepthWrite &&
                (!attachment.Depth.HasValue || attachment.Depth.Value.ReadOnly))
            {
                throw new InvalidOperationException("The selected raster pipeline writes depth, but the rendering scope exposes depth as read-only.");
            }
        }
        if (pipeline.ColorFormats.Length != colors.Length)
        {
            throw new InvalidOperationException("The selected raster pipeline color-format count does not match the rendering scope.");
        }
        for (int index = 0; index < colors.Length; index++)
        {
            if (pipeline.ColorFormats[index] != colors[index].View.Format)
            {
                throw new InvalidOperationException($"The selected raster pipeline format does not match color attachment {index}.");
            }
            if (pipeline.SampleCount != colors[index].View.SampleCount)
            {
                throw new InvalidOperationException($"The selected raster pipeline sample count does not match color attachment {index}.");
            }
        }
    }

    private void Discard(NativeTextureView view, TextureAspect aspect)
    {
        foreach (uint subresource in view.EnumerateAttachmentSubresources(aspect))
        {
            _allocation.List.DiscardResource(view.Texture.Resource, subresource, 1);
        }
    }

    private readonly record struct BoundColorAttachment(NativeTextureView View, StoreAction Store);
    private readonly record struct BoundDepthStencilAttachment(
        NativeTextureView View,
        DepthAttachmentOperations? Depth,
        StencilAttachmentOperations? Stencil,
        CpuDescriptorHandle Descriptor);
    private readonly record struct BoundDescriptorGroup(NativeBindGroupLayout Layout, FrozenBinding[] Bindings);
}
