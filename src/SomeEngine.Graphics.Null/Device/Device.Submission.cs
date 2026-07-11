namespace SomeEngine.Graphics.Null;

public sealed partial class Device
{
    private void ExpandAndPinReferences(CommandReferences references)
    {
        ExpandAndValidateReferences(references);
        PinReferences(references);
    }

    private void PinReferences(CommandReferences references)
    {
        foreach (HeapHandle handle in references.Heaps) _heaps.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BufferHandle handle in references.Buffers) _buffers.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (TextureHandle handle in references.Textures) _textures.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (TextureViewHandle handle in references.TextureViews) _textureViews.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BufferViewHandle handle in references.BufferViews) _bufferViews.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (SamplerHandle handle in references.Samplers) _samplers.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BindGroupLayoutHandle handle in references.BindGroupLayouts) _bindGroupLayouts.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BindGroupHandle handle in references.BindGroups) _bindGroups.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (ShaderHandle handle in references.Shaders) _shaders.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (PipelineLayoutHandle handle in references.PipelineLayouts) _pipelineLayouts.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (PipelineHandle handle in references.Pipelines) _pipelines.Pin(handle.Domain, handle.Slot, handle.Generation);
    }

    private void CancelReferencePins(CommandReferences references)
    {
        foreach (HeapHandle handle in references.Heaps) _heaps.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BufferHandle handle in references.Buffers) _buffers.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (TextureHandle handle in references.Textures) _textures.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (TextureViewHandle handle in references.TextureViews) _textureViews.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BufferViewHandle handle in references.BufferViews) _bufferViews.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (SamplerHandle handle in references.Samplers) _samplers.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BindGroupLayoutHandle handle in references.BindGroupLayouts) _bindGroupLayouts.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BindGroupHandle handle in references.BindGroups) _bindGroups.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (ShaderHandle handle in references.Shaders) _shaders.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (PipelineLayoutHandle handle in references.PipelineLayouts) _pipelineLayouts.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (PipelineHandle handle in references.Pipelines) _pipelines.CancelPin(handle.Domain, handle.Slot, handle.Generation);
    }

    private void SubmitReferencePins(CommandReferences references, QueueType queue, ulong value)
    {
        foreach (HeapHandle handle in references.Heaps) _heaps.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (BufferHandle handle in references.Buffers) _buffers.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (TextureHandle handle in references.Textures) _textures.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (TextureViewHandle handle in references.TextureViews) _textureViews.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (BufferViewHandle handle in references.BufferViews) _bufferViews.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (SamplerHandle handle in references.Samplers) _samplers.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (BindGroupLayoutHandle handle in references.BindGroupLayouts) _bindGroupLayouts.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (BindGroupHandle handle in references.BindGroups) _bindGroups.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (ShaderHandle handle in references.Shaders) _shaders.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (PipelineLayoutHandle handle in references.PipelineLayouts) _pipelineLayouts.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (PipelineHandle handle in references.Pipelines) _pipelines.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
    }

    private SubmissionState ValidateSubmission(ReadOnlySpan<CommandListRecord> records)
    {
        SubmissionState state = new(this);
        foreach (ref readonly CommandListRecord record in records)
        {
            state.Execute(record.Commands);
        }
        return state;
    }

    private sealed class SubmissionState
    {
        private readonly Device _device;
        private readonly Dictionary<byte[], byte[]> _storage = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<BufferHandle, StagedBuffer> _buffers = [];
        private readonly Dictionary<TextureHandle, StagedTexture> _textures = [];
        private long _copies;
        private long _draws;
        private long _dispatches;

        public SubmissionState(Device device) => _device = device;

        public void Execute(RecordedCommand[] commands)
        {
            foreach (RecordedCommand command in commands)
            {
                switch (command)
                {
                    case BarrierCommand barriers:
                        foreach (ref readonly ResourceBarrier barrier in barriers.Barriers.AsSpan()) ApplyBarrier(barrier);
                        break;
                    case CopyBufferCommand copy:
                        CopyBuffer(copy);
                        break;
                    case CopyBufferToTextureCommand copy:
                        CopyBufferToTexture(copy.Copy);
                        break;
                    case CopyTextureToBufferCommand copy:
                        CopyTextureToBuffer(copy.Copy);
                        break;
                    case ResolveTextureCommand resolve:
                        ResolveTexture(resolve.Resolve);
                        break;
                    case BeginRenderingCommand rendering:
                        BeginRendering(rendering.Rendering);
                        break;
                    case SetVertexBufferCommand vertex:
                        RequireBufferState(vertex.Buffer, ResourceState.VertexOrConstantBuffer, "vertex buffer");
                        break;
                    case SetIndexBufferCommand index:
                        RequireBufferState(index.Buffer, ResourceState.IndexBuffer, "index buffer");
                        break;
                    case DrawCommand:
                    case DrawIndexedCommand:
                        _draws++;
                        break;
                    case DispatchCommand:
                        _dispatches++;
                        break;
                }
            }
        }

        public void Commit()
        {
            foreach ((byte[] source, byte[] staged) in _storage) staged.CopyTo(source, 0);
            foreach (StagedBuffer buffer in _buffers.Values) buffer.Source.State = buffer.State;
            foreach (StagedTexture texture in _textures.Values) texture.States.CopyTo(texture.Source.States, 0);
            _device._statistics = _device._statistics with
            {
                ExecutedCopies = _device._statistics.ExecutedCopies + _copies,
                Draws = _device._statistics.Draws + _draws,
                Dispatches = _device._statistics.Dispatches + _dispatches,
            };
        }

        private void ApplyBarrier(in ResourceBarrier barrier)
        {
            switch (barrier.Kind)
            {
                case BarrierKind.Transition when barrier.Resource.Kind == ResourceKind.Buffer:
                {
                    StagedBuffer buffer = Buffer(new BufferHandle(barrier.Resource.Domain, barrier.Resource.Slot, barrier.Resource.Generation));
                    if (BufferStateValidation.HasFixedState(buffer.Source.MemoryType))
                    {
                        if (!BufferStateValidation.IsFixedState(buffer.Source.MemoryType, barrier.Before) ||
                            !BufferStateValidation.IsFixedState(buffer.Source.MemoryType, barrier.After))
                        {
                            throw _device.ValidationError(
                                $"{buffer.Source.MemoryType} buffers have fixed logical state {BufferStateValidation.DescribeFixedState(buffer.Source.MemoryType)}.");
                        }
                        break;
                    }
                    if (buffer.State != barrier.Before)
                    {
                        throw _device.ValidationError($"Buffer transition expected {barrier.Before}, actual state is {buffer.State}.");
                    }
                    buffer.State = barrier.After;
                    break;
                }
                case BarrierKind.Transition when barrier.Resource.Kind == ResourceKind.Texture:
                {
                    StagedTexture texture = Texture(new TextureHandle(barrier.Resource.Domain, barrier.Resource.Slot, barrier.Resource.Generation));
                    foreach (int subresource in TextureLayout.EnumerateSubresources(texture.Source.Desc, barrier.TextureRange))
                    {
                        if (texture.States[subresource] != barrier.Before)
                        {
                            throw _device.ValidationError(
                                $"Texture subresource {subresource} transition expected {barrier.Before}, actual state is {texture.States[subresource]}.");
                        }
                    }
                    foreach (int subresource in TextureLayout.EnumerateSubresources(texture.Source.Desc, barrier.TextureRange))
                    {
                        texture.States[subresource] = barrier.After;
                    }
                    break;
                }
                case BarrierKind.UnorderedAccess when barrier.Resource.Kind == ResourceKind.Buffer:
                    RequireBufferState(
                        new BufferHandle(barrier.Resource.Domain, barrier.Resource.Slot, barrier.Resource.Generation),
                        ResourceState.UnorderedAccess,
                        "unordered-access barrier");
                    break;
                case BarrierKind.UnorderedAccess when barrier.Resource.Kind == ResourceKind.Texture:
                    RequireTextureState(
                        new TextureHandle(barrier.Resource.Domain, barrier.Resource.Slot, barrier.Resource.Generation),
                        barrier.TextureRange,
                        ResourceState.UnorderedAccess,
                        "unordered-access barrier");
                    break;
                case BarrierKind.Aliasing:
                    _ = Resource(barrier.Resource);
                    if (barrier.AliasingBefore.IsValid) _ = Resource(barrier.AliasingBefore);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(barrier));
            }
        }

        private void CopyBuffer(CopyBufferCommand copy)
        {
            StagedBuffer source = Buffer(copy.Source);
            StagedBuffer destination = Buffer(copy.Destination);
            RequireBufferState(source, ResourceState.CopySource, "CopyBuffer source");
            RequireBufferState(destination, ResourceState.CopyDestination, "CopyBuffer destination");
            int size = checked((int)copy.Size);
            source.Bytes.Slice(checked((int)copy.SourceOffset), size)
                .CopyTo(destination.Bytes.Slice(checked((int)copy.DestinationOffset), size));
            _copies++;
        }

        private void CopyBufferToTexture(in BufferTextureCopy copy)
        {
            StagedBuffer source = Buffer(copy.Source);
            StagedTexture destination = Texture(copy.Destination);
            RequireBufferState(source, ResourceState.CopySource, "CopyBufferToTexture source");
            TextureSubresourceRange range = new(
                copy.DestinationRegion.MipLevel,
                1,
                copy.DestinationRegion.ArrayLayer,
                1,
                copy.DestinationRegion.Aspect);
            RequireTextureState(destination, range, ResourceState.CopyDestination, "CopyBufferToTexture destination");
            CopyBufferTextureBytes(
                source,
                copy.SourceLayout,
                destination,
                copy.DestinationRegion,
                bufferToTexture: true);
            _copies++;
        }

        private void CopyTextureToBuffer(in TextureBufferCopy copy)
        {
            StagedTexture source = Texture(copy.Source);
            StagedBuffer destination = Buffer(copy.Destination);
            TextureSubresourceRange range = new(
                copy.SourceRegion.MipLevel,
                1,
                copy.SourceRegion.ArrayLayer,
                1,
                copy.SourceRegion.Aspect);
            RequireTextureState(source, range, ResourceState.CopySource, "CopyTextureToBuffer source");
            RequireBufferState(destination, ResourceState.CopyDestination, "CopyTextureToBuffer destination");
            CopyBufferTextureBytes(
                destination,
                copy.DestinationLayout,
                source,
                copy.SourceRegion,
                bufferToTexture: false);
            _copies++;
        }

        private void ResolveTexture(in TextureResolveRegion resolve)
        {
            StagedTexture source = Texture(resolve.Source);
            StagedTexture destination = Texture(resolve.Destination);
            TextureSubresourceRange sourceRange = new(
                resolve.SourceMipLevel,
                1,
                resolve.SourceArrayLayer,
                1,
                resolve.Aspect);
            TextureSubresourceRange destinationRange = new(
                resolve.DestinationMipLevel,
                1,
                resolve.DestinationArrayLayer,
                1,
                resolve.Aspect);
            RequireTextureState(source, sourceRange, ResourceState.ResolveSource, "ResolveTexture source");
            RequireTextureState(destination, destinationRange, ResourceState.ResolveDestination, "ResolveTexture destination");
            ResolveAverageColor(source, destination, resolve);
            _copies++;
        }

        private void BeginRendering(in RenderingInfo rendering)
        {
            foreach (ref readonly ColorAttachment color in rendering.Colors.Span)
            {
                TextureViewRecord view = _device.RequireTextureView(color.View);
                StagedTexture texture = Texture(view.Desc.Texture);
                RequireTextureState(texture, view.Desc.Range, ResourceState.RenderTarget, "color attachment");
                if (color.Load == LoadAction.Clear)
                    ClearColorTextureView(texture, view.Desc.Range, color.ClearColor);
            }
            if (rendering.DepthStencil is DepthStencilAttachment depth)
            {
                TextureViewRecord view = _device.RequireTextureView(depth.View);
                StagedTexture texture = Texture(view.Desc.Texture);
                TextureSubresourceRange baseRange = view.Desc.Range == default
                    ? new TextureSubresourceRange(0, texture.Source.Desc.MipLevels, 0, texture.Source.Desc.ArrayLayers, TextureLayout.AllowedAspects(texture.Source.Desc.Format))
                    : view.Desc.Range;
                if (depth.Depth is DepthAttachmentOperations depthOps)
                {
                    TextureSubresourceRange range = baseRange with { Aspect = TextureAspect.Depth };
                    RequireTextureState(texture, range, depthOps.ReadOnly ? ResourceState.DepthRead : ResourceState.DepthWrite, "depth attachment");
                    if (depthOps.Load == LoadAction.Clear) ClearTextureView(texture, range);
                }
                if (depth.Stencil is StencilAttachmentOperations stencilOps)
                {
                    TextureSubresourceRange range = baseRange with { Aspect = TextureAspect.Stencil };
                    RequireTextureState(texture, range, stencilOps.ReadOnly ? ResourceState.DepthRead : ResourceState.DepthWrite, "stencil attachment");
                    if (stencilOps.Load == LoadAction.Clear) ClearTextureView(texture, range);
                }
            }
        }

        private void ClearTextureView(StagedTexture texture, in TextureSubresourceRange range)
        {
            TextureLayout.NormalizeRange(
                texture.Source.Desc,
                range,
                out int firstMip,
                out int mipCount,
                out int firstLayer,
                out int layerCount,
                out TextureAspect aspects);
            foreach (TextureAspect aspect in TextureLayout.EnumerateAspects(aspects))
            for (int layer = firstLayer; layer < firstLayer + layerCount; layer++)
            for (int mip = firstMip; mip < firstMip + mipCount; mip++)
            {
                (int width, int height, int depth) = TextureLayout.GetMipExtent(texture.Source.Desc, mip);
                int bytesPerTexel = TextureLayout.GetBytesPerTexel(texture.Source.Desc.Format, aspect);
                int offset = checked(texture.Source.BaseOffset + (int)TextureLayout.GetSubresourceOffset(texture.Source.Desc, mip, layer, aspect));
                int size = checked(width * height * depth * texture.Source.Desc.SampleCount * bytesPerTexel);
                texture.Storage.AsSpan(offset, size).Clear();
            }
        }

        private static void ClearColorTextureView(
            StagedTexture texture,
            in TextureSubresourceRange range,
            in System.Numerics.Vector4 clearColor)
        {
            TextureDesc desc = texture.Source.Desc;
            TextureLayout.NormalizeRange(
                desc,
                range,
                out int firstMip,
                out int mipCount,
                out int firstLayer,
                out int layerCount,
                out TextureAspect aspects);
            if (aspects != TextureAspect.Color)
                throw new ArgumentOutOfRangeException(nameof(range), "A color clear requires the color aspect.");
            int bytesPerTexel = TextureLayout.GetBytesPerTexel(desc.Format, TextureAspect.Color);
            Span<byte> encoded = stackalloc byte[16];
            EncodeClearColor(desc.Format, clearColor, encoded[..bytesPerTexel]);
            for (int layer = firstLayer; layer < firstLayer + layerCount; layer++)
            for (int mip = firstMip; mip < firstMip + mipCount; mip++)
            {
                (int width, int height, int depth) = TextureLayout.GetMipExtent(desc, mip);
                int offset = checked(
                    texture.Source.BaseOffset +
                    (int)TextureLayout.GetSubresourceOffset(desc, mip, layer, TextureAspect.Color));
                int texelSamples = checked(width * height * depth * desc.SampleCount);
                Span<byte> target = texture.Storage.AsSpan(offset, checked(texelSamples * bytesPerTexel));
                for (int index = 0; index < texelSamples; index++)
                    encoded[..bytesPerTexel].CopyTo(target.Slice(index * bytesPerTexel, bytesPerTexel));
            }
        }

        private static void EncodeClearColor(
            Format format,
            in System.Numerics.Vector4 color,
            Span<byte> destination)
        {
            switch (format)
            {
                case Format.R8UNorm:
                    destination[0] = ToUnorm8(color.X);
                    return;
                case Format.R8G8UNorm:
                    destination[0] = ToUnorm8(color.X);
                    destination[1] = ToUnorm8(color.Y);
                    return;
                case Format.R8G8B8A8UNorm:
                case Format.R8G8B8A8UNormSrgb:
                    destination[0] = ToUnorm8(color.X);
                    destination[1] = ToUnorm8(color.Y);
                    destination[2] = ToUnorm8(color.Z);
                    destination[3] = ToUnorm8(color.W);
                    return;
                case Format.B8G8R8A8UNorm:
                    destination[0] = ToUnorm8(color.Z);
                    destination[1] = ToUnorm8(color.Y);
                    destination[2] = ToUnorm8(color.X);
                    destination[3] = ToUnorm8(color.W);
                    return;
                case Format.R16Float:
                    WriteHalf(destination, 0, color.X);
                    return;
                case Format.R16G16Float:
                    WriteHalf(destination, 0, color.X);
                    WriteHalf(destination, 1, color.Y);
                    return;
                case Format.R16G16B16A16Float:
                    WriteHalf(destination, 0, color.X);
                    WriteHalf(destination, 1, color.Y);
                    WriteHalf(destination, 2, color.Z);
                    WriteHalf(destination, 3, color.W);
                    return;
                case Format.R32Float:
                    WriteFloat(destination, 0, color.X);
                    return;
                case Format.R32G32Float:
                    WriteFloat(destination, 0, color.X);
                    WriteFloat(destination, 1, color.Y);
                    return;
                case Format.R32G32B32Float:
                    WriteFloat(destination, 0, color.X);
                    WriteFloat(destination, 1, color.Y);
                    WriteFloat(destination, 2, color.Z);
                    return;
                case Format.R32G32B32A32Float:
                    WriteFloat(destination, 0, color.X);
                    WriteFloat(destination, 1, color.Y);
                    WriteFloat(destination, 2, color.Z);
                    WriteFloat(destination, 3, color.W);
                    return;
                default:
                    throw new NotSupportedException($"Format {format} does not expose a simulated color clear.");
            }
        }

        private static byte ToUnorm8(float value) =>
            checked((byte)MathF.Round(Math.Clamp(value, 0f, 1f) * byte.MaxValue));

        private static void WriteHalf(Span<byte> destination, int component, float value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                destination.Slice(component * sizeof(ushort), sizeof(ushort)),
                BitConverter.HalfToUInt16Bits((Half)value));

        private static void WriteFloat(Span<byte> destination, int component, float value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                destination.Slice(component * sizeof(float), sizeof(float)),
                BitConverter.SingleToInt32Bits(value));

        private object Resource(ResourceHandle handle) => handle.Kind switch
        {
            ResourceKind.Buffer => Buffer(new BufferHandle(handle.Domain, handle.Slot, handle.Generation)),
            ResourceKind.Texture => Texture(new TextureHandle(handle.Domain, handle.Slot, handle.Generation)),
            _ => throw new ArgumentOutOfRangeException(nameof(handle)),
        };

        private StagedBuffer Buffer(BufferHandle handle)
        {
            if (_buffers.TryGetValue(handle, out StagedBuffer? staged)) return staged;
            BufferRecord source = _device.RequireBuffer(handle);
            staged = new StagedBuffer(source, Storage(source.Storage));
            _buffers.Add(handle, staged);
            return staged;
        }

        private StagedTexture Texture(TextureHandle handle)
        {
            if (_textures.TryGetValue(handle, out StagedTexture? staged)) return staged;
            TextureRecord source = _device.RequireTexture(handle);
            staged = new StagedTexture(source, Storage(source.Storage));
            _textures.Add(handle, staged);
            return staged;
        }

        private byte[] Storage(byte[] source)
        {
            if (_storage.TryGetValue(source, out byte[]? staged)) return staged;
            staged = source.ToArray();
            _storage.Add(source, staged);
            return staged;
        }

        private void RequireBufferState(BufferHandle handle, ResourceState required, string operation) =>
            RequireBufferState(Buffer(handle), required, operation);

        private void RequireBufferState(StagedBuffer buffer, ResourceState required, string operation)
        {
            if (!BufferStateValidation.Satisfies(buffer.Source.MemoryType, buffer.State, required))
            {
                throw _device.ValidationError($"{operation} requires state {required}; actual state is {buffer.State}.");
            }
        }

        private void RequireTextureState(
            TextureHandle handle,
            in TextureSubresourceRange range,
            ResourceState required,
            string operation) =>
            RequireTextureState(Texture(handle), range, required, operation);

        private void RequireTextureState(
            StagedTexture texture,
            in TextureSubresourceRange range,
            ResourceState required,
            string operation)
        {
            foreach (int subresource in TextureLayout.EnumerateSubresources(texture.Source.Desc, range))
            {
                if (texture.States[subresource] != required)
                {
                    throw _device.ValidationError(
                        $"{operation} requires state {required}; subresource {subresource} is {texture.States[subresource]}.");
                }
            }
        }

        private static void CopyBufferTextureBytes(
            StagedBuffer buffer,
            in TextureBufferLayout layout,
            StagedTexture texture,
            in TextureCopyRegion region,
            bool bufferToTexture)
        {
            (int fullWidth, int fullHeight, _) = TextureLayout.GetMipExtent(texture.Source.Desc, region.MipLevel);
            int bytesPerTexel = TextureLayout.GetBytesPerTexel(texture.Source.Desc.Format, region.Aspect);
            int tightRow = checked(region.Width * bytesPerTexel);
            int rowPitch = checked((int)layout.BytesPerRow);
            int imageRows = checked((int)layout.RowsPerImage);
            int textureSubresourceBase = checked(
                texture.Source.BaseOffset + (int)TextureLayout.GetSubresourceOffset(
                    texture.Source.Desc,
                    region.MipLevel,
                    region.ArrayLayer,
                    region.Aspect));
            for (int slice = 0; slice < region.Depth; slice++)
            {
                for (int row = 0; row < region.Height; row++)
                {
                    int bufferIndex = checked(
                        buffer.Source.BaseOffset + (int)layout.Offset + slice * imageRows * rowPitch + row * rowPitch);
                    int textureIndex = checked(
                        textureSubresourceBase + (((region.Z + slice) * fullHeight + region.Y + row) * fullWidth + region.X) * bytesPerTexel);
                    Span<byte> bufferRow = buffer.Storage.AsSpan(bufferIndex, tightRow);
                    Span<byte> textureRow = texture.Storage.AsSpan(textureIndex, tightRow);
                    if (bufferToTexture) bufferRow.CopyTo(textureRow);
                    else textureRow.CopyTo(bufferRow);
                }
            }
        }

        private static void ResolveAverageColor(
            StagedTexture source,
            StagedTexture destination,
            in TextureResolveRegion resolve)
        {
            TextureDesc sourceDesc = source.Source.Desc;
            TextureDesc destinationDesc = destination.Source.Desc;
            (int width, int height, int depth) = TextureLayout.GetMipExtent(sourceDesc, resolve.SourceMipLevel);
            int bytesPerTexel = TextureLayout.GetBytesPerTexel(sourceDesc.Format, TextureAspect.Color);
            int samples = sourceDesc.SampleCount;
            int sourceOffset = checked(
                source.Source.BaseOffset +
                (int)TextureLayout.GetSubresourceOffset(
                    sourceDesc,
                    resolve.SourceMipLevel,
                    resolve.SourceArrayLayer,
                    TextureAspect.Color));
            int destinationOffset = checked(
                destination.Source.BaseOffset +
                (int)TextureLayout.GetSubresourceOffset(
                    destinationDesc,
                    resolve.DestinationMipLevel,
                    resolve.DestinationArrayLayer,
                    TextureAspect.Color));
            int texelCount = checked(width * height * depth);
            ReadOnlySpan<byte> sourceBytes = source.Storage;
            Span<byte> destinationBytes = destination.Storage;
            for (int texel = 0; texel < texelCount; texel++)
            {
                int sourceTexel = checked(sourceOffset + texel * bytesPerTexel * samples);
                int destinationTexel = checked(destinationOffset + texel * bytesPerTexel);
                ResolveAverageTexel(
                    sourceDesc.Format,
                    sourceBytes.Slice(sourceTexel, checked(bytesPerTexel * samples)),
                    destinationBytes.Slice(destinationTexel, bytesPerTexel),
                    samples);
            }
        }

        private static void ResolveAverageTexel(
            Format format,
            ReadOnlySpan<byte> source,
            Span<byte> destination,
            int samples)
        {
            switch (format)
            {
                case Format.R8UNorm:
                    ResolveByteComponents(source, destination, samples, 1);
                    return;
                case Format.R8G8UNorm:
                    ResolveByteComponents(source, destination, samples, 2);
                    return;
                case Format.R8G8B8A8UNorm:
                case Format.R8G8B8A8UNormSrgb:
                case Format.B8G8R8A8UNorm:
                    ResolveByteComponents(source, destination, samples, 4);
                    return;
                case Format.R16Float:
                    ResolveHalfComponents(source, destination, samples, 1);
                    return;
                case Format.R16G16Float:
                    ResolveHalfComponents(source, destination, samples, 2);
                    return;
                case Format.R16G16B16A16Float:
                    ResolveHalfComponents(source, destination, samples, 4);
                    return;
                case Format.R32Float:
                    ResolveFloatComponents(source, destination, samples, 1);
                    return;
                case Format.R32G32Float:
                    ResolveFloatComponents(source, destination, samples, 2);
                    return;
                case Format.R32G32B32Float:
                    ResolveFloatComponents(source, destination, samples, 3);
                    return;
                case Format.R32G32B32A32Float:
                    ResolveFloatComponents(source, destination, samples, 4);
                    return;
                default:
                    throw new NotSupportedException($"Format {format} does not support Average resolve.");
            }
        }

        private static void ResolveByteComponents(
            ReadOnlySpan<byte> source,
            Span<byte> destination,
            int samples,
            int components)
        {
            for (int component = 0; component < components; component++)
            {
                int sum = 0;
                for (int sample = 0; sample < samples; sample++)
                    sum += source[sample * components + component];
                destination[component] = checked((byte)((sum + samples / 2) / samples));
            }
        }

        private static void ResolveHalfComponents(
            ReadOnlySpan<byte> source,
            Span<byte> destination,
            int samples,
            int components)
        {
            int sampleStride = checked(components * sizeof(ushort));
            for (int component = 0; component < components; component++)
            {
                double sum = 0;
                int componentOffset = component * sizeof(ushort);
                for (int sample = 0; sample < samples; sample++)
                {
                    ushort bits = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(
                        source.Slice(sample * sampleStride + componentOffset, sizeof(ushort)));
                    sum += (double)BitConverter.UInt16BitsToHalf(bits);
                }
                ushort resolved = BitConverter.HalfToUInt16Bits((Half)(sum / samples));
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(
                    destination.Slice(componentOffset, sizeof(ushort)),
                    resolved);
            }
        }

        private static void ResolveFloatComponents(
            ReadOnlySpan<byte> source,
            Span<byte> destination,
            int samples,
            int components)
        {
            int sampleStride = checked(components * sizeof(float));
            for (int component = 0; component < components; component++)
            {
                double sum = 0;
                int componentOffset = component * sizeof(float);
                for (int sample = 0; sample < samples; sample++)
                {
                    int bits = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(
                        source.Slice(sample * sampleStride + componentOffset, sizeof(float)));
                    sum += BitConverter.Int32BitsToSingle(bits);
                }
                int resolved = BitConverter.SingleToInt32Bits((float)(sum / samples));
                System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                    destination.Slice(componentOffset, sizeof(float)),
                    resolved);
            }
        }

        private sealed class StagedBuffer
        {
            public StagedBuffer(BufferRecord source, byte[] storage)
            {
                Source = source;
                Storage = storage;
                State = source.State;
            }

            public BufferRecord Source { get; }
            public byte[] Storage { get; }
            public ResourceState State { get; set; }
            public Span<byte> Bytes => Storage.AsSpan(Source.BaseOffset, checked((int)Source.Desc.Size));
        }

        private sealed class StagedTexture
        {
            public StagedTexture(TextureRecord source, byte[] storage)
            {
                Source = source;
                Storage = storage;
                States = source.States.ToArray();
            }

            public TextureRecord Source { get; }
            public byte[] Storage { get; }
            public ResourceState[] States { get; }
        }
    }
}
