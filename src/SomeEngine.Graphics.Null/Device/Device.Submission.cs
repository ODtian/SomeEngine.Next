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
        PinResourceReferences(references);
        PinPipelineReferences(references);
    }

    private void PinResourceReferences(CommandReferences references)
    {
        foreach (HeapHandle handle in references.Heaps) _heaps.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BufferHandle handle in references.Buffers) _buffers.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (TextureHandle handle in references.Textures) _textures.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (TextureViewHandle handle in references.TextureViews) _textureViews.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BufferViewHandle handle in references.BufferViews) _bufferViews.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (SamplerHandle handle in references.Samplers) _samplers.Pin(handle.Domain, handle.Slot, handle.Generation);
    }

    private void PinPipelineReferences(CommandReferences references)
    {
        foreach (BindGroupLayoutHandle handle in references.BindGroupLayouts) _bindGroupLayouts.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BindGroupHandle handle in references.BindGroups) _bindGroups.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (ShaderHandle handle in references.Shaders) _shaders.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (PipelineLayoutHandle handle in references.PipelineLayouts) _pipelineLayouts.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (PipelineHandle handle in references.Pipelines) _pipelines.Pin(handle.Domain, handle.Slot, handle.Generation);
        foreach (QueryPoolHandle handle in references.QueryPools) _queryPools.Pin(handle.Domain, handle.Slot, handle.Generation);
    }

    private void CancelReferencePins(CommandReferences references)
    {
        CancelResourceReferencePins(references);
        CancelPipelineReferencePins(references);
    }

    private void CancelResourceReferencePins(CommandReferences references)
    {
        foreach (HeapHandle handle in references.Heaps) _heaps.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BufferHandle handle in references.Buffers) _buffers.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (TextureHandle handle in references.Textures) _textures.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (TextureViewHandle handle in references.TextureViews) _textureViews.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BufferViewHandle handle in references.BufferViews) _bufferViews.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (SamplerHandle handle in references.Samplers) _samplers.CancelPin(handle.Domain, handle.Slot, handle.Generation);
    }

    private void CancelPipelineReferencePins(CommandReferences references)
    {
        foreach (BindGroupLayoutHandle handle in references.BindGroupLayouts) _bindGroupLayouts.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (BindGroupHandle handle in references.BindGroups) _bindGroups.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (ShaderHandle handle in references.Shaders) _shaders.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (PipelineLayoutHandle handle in references.PipelineLayouts) _pipelineLayouts.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (PipelineHandle handle in references.Pipelines) _pipelines.CancelPin(handle.Domain, handle.Slot, handle.Generation);
        foreach (QueryPoolHandle handle in references.QueryPools) _queryPools.CancelPin(handle.Domain, handle.Slot, handle.Generation);
    }

    private void SubmitReferencePins(CommandReferences references, QueueType queue, ulong value)
    {
        SubmitResourceReferencePins(references, queue, value);
        SubmitPipelineReferencePins(references, queue, value);
    }

    private void SubmitResourceReferencePins(CommandReferences references, QueueType queue, ulong value)
    {
        foreach (HeapHandle handle in references.Heaps) _heaps.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (BufferHandle handle in references.Buffers) _buffers.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (TextureHandle handle in references.Textures) _textures.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (TextureViewHandle handle in references.TextureViews) _textureViews.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (BufferViewHandle handle in references.BufferViews) _bufferViews.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (SamplerHandle handle in references.Samplers) _samplers.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
    }

    private void SubmitPipelineReferencePins(CommandReferences references, QueueType queue, ulong value)
    {
        foreach (BindGroupLayoutHandle handle in references.BindGroupLayouts) _bindGroupLayouts.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (BindGroupHandle handle in references.BindGroups) _bindGroups.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (ShaderHandle handle in references.Shaders) _shaders.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (PipelineLayoutHandle handle in references.PipelineLayouts) _pipelineLayouts.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (PipelineHandle handle in references.Pipelines) _pipelines.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
        foreach (QueryPoolHandle handle in references.QueryPools) _queryPools.SubmitPin(handle.Domain, handle.Slot, handle.Generation, queue, value);
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

    private sealed partial class SubmissionState
    {
        private readonly Device _device;
        private readonly Dictionary<byte[], byte[]> _storage = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<BufferHandle, StagedBuffer> _buffers = [];
        private readonly Dictionary<TextureHandle, StagedTexture> _textures = [];
        private readonly Dictionary<QueryPoolHandle, StagedQueryPool> _queries = [];
        private long _copies;
        private long _draws;
        private long _dispatches;
        private long _debugMarkers;
        private ulong _vertices;
        private ulong _computeInvocations;
        private ulong _timestampCounter;

        public SubmissionState(Device device)
        {
            _device = device;
            _timestampCounter = device._timestampCounter;
        }

        public void Execute(RecordedCommand[] commands)
        {
            foreach (RecordedCommand command in commands)
            {
                if (ExecuteTransferCommand(command)) continue;
                if (ExecuteRenderingCommand(command)) continue;
                if (ExecuteWorkCommand(command)) continue;
                if (ExecuteQueryCommand(command)) continue;
                if (command is InsertDebugMarkerCommand) _debugMarkers++;
            }
        }

        private bool ExecuteTransferCommand(RecordedCommand command)
        {
            switch (command)
            {
                case BarrierCommand barriers: ExecuteBarriers(barriers); return true;
                case CopyBufferCommand copy: CopyBuffer(copy); return true;
                case CopyBufferToTextureCommand copy: CopyBufferToTexture(copy.Copy); return true;
                case CopyTextureToBufferCommand copy: CopyTextureToBuffer(copy.Copy); return true;
                case CopyTextureCommand copy: CopyTexture(copy.Copy); return true;
                case ClearBufferCommand clear: ClearBuffer(clear); return true;
                case ClearTextureCommand clear: ClearTexture(clear); return true;
                default: return false;
            }
        }

        private bool ExecuteRenderingCommand(RecordedCommand command)
        {
            switch (command)
            {
                case ClearDepthStencilTextureCommand clear: ClearDepthStencilTexture(clear); return true;
                case ResolveTextureCommand resolve: ResolveTexture(resolve.Resolve); return true;
                case BeginRenderingCommand rendering: BeginRendering(rendering.Rendering); return true;
                case SetVertexBufferCommand vertex:
                    RequireBufferState(vertex.Buffer, ResourceState.VertexOrConstantBuffer, "vertex buffer");
                    return true;
                case SetIndexBufferCommand index:
                    RequireBufferState(index.Buffer, ResourceState.IndexBuffer, "index buffer");
                    return true;
                default: return false;
            }
        }

        private bool ExecuteWorkCommand(RecordedCommand command)
        {
            switch (command)
            {
                case DrawCommand draw:
                    _draws++;
                    _vertices = checked(_vertices + (ulong)draw.VertexCount * draw.InstanceCount);
                    return true;
                case DrawIndexedCommand draw:
                    _draws++;
                    _vertices = checked(_vertices + (ulong)draw.IndexCount * draw.InstanceCount);
                    return true;
                case DispatchCommand dispatch:
                    _dispatches++;
                    _computeInvocations = checked(
                        _computeInvocations + (ulong)dispatch.X * dispatch.Y * dispatch.Z);
                    return true;
                case DrawIndirectCommand draw: ExecuteDrawIndirect(draw); return true;
                case DrawIndexedIndirectCommand draw: ExecuteDrawIndexedIndirect(draw); return true;
                case DispatchIndirectCommand dispatch: ExecuteDispatchIndirect(dispatch); return true;
                default: return false;
            }
        }

        private bool ExecuteQueryCommand(RecordedCommand command)
        {
            switch (command)
            {
                case ResetQueryPoolCommand reset: ResetQueryPool(reset); return true;
                case BeginQueryCommand begin: BeginQuery(begin); return true;
                case EndQueryCommand end: EndQuery(end); return true;
                case WriteTimestampCommand timestamp: WriteTimestamp(timestamp); return true;
                case ResolveQueryPoolCommand resolve: ResolveQueryPool(resolve); return true;
                default: return false;
            }
        }

        private void ExecuteBarriers(BarrierCommand command)
        {
            foreach (ref readonly ResourceBarrier barrier in command.Barriers.AsSpan()) ApplyBarrier(barrier);
        }

        public void Commit()
        {
            foreach ((byte[] source, byte[] staged) in _storage) staged.CopyTo(source, 0);
            foreach (StagedBuffer buffer in _buffers.Values) buffer.Source.State = buffer.State;
            foreach (StagedTexture texture in _textures.Values) texture.States.CopyTo(texture.Source.States, 0);
            foreach (StagedQueryPool query in _queries.Values) query.Commit();
            _device._timestampCounter = _timestampCounter;
            _device._statistics = _device._statistics with
            {
                ExecutedCopies = _device._statistics.ExecutedCopies + _copies,
                Draws = _device._statistics.Draws + _draws,
                Dispatches = _device._statistics.Dispatches + _dispatches,
                DebugMarkers = _device._statistics.DebugMarkers + _debugMarkers,
            };
        }

        private void ExecuteDrawIndirect(DrawIndirectCommand command)
        {
            StagedBuffer arguments = Buffer(command.ArgumentBuffer);
            RequireBufferState(arguments, ResourceState.IndirectArgument, "DrawIndirect arguments");
            uint count = ResolveIndirectCount(command.MaxCommandCount, command.CountBuffer, command.CountBufferOffset);
            for (uint index = 0; index < count; index++)
            {
                int offset = checked((int)(command.ArgumentOffset + (ulong)index * command.CommandStride));
                ReadOnlySpan<byte> bytes = arguments.Bytes.Slice(offset, checked((int)DrawIndirectArguments.ByteSize));
                uint vertexCount = ReadUInt32(bytes, 0);
                uint instanceCount = ReadUInt32(bytes, 4);
                _draws++;
                _vertices = checked(_vertices + (ulong)vertexCount * instanceCount);
            }
        }

        private void ExecuteDrawIndexedIndirect(DrawIndexedIndirectCommand command)
        {
            StagedBuffer arguments = Buffer(command.ArgumentBuffer);
            RequireBufferState(arguments, ResourceState.IndirectArgument, "DrawIndexedIndirect arguments");
            uint count = ResolveIndirectCount(command.MaxCommandCount, command.CountBuffer, command.CountBufferOffset);
            for (uint index = 0; index < count; index++)
            {
                int offset = checked((int)(command.ArgumentOffset + (ulong)index * command.CommandStride));
                ReadOnlySpan<byte> bytes = arguments.Bytes.Slice(offset, checked((int)DrawIndexedIndirectArguments.ByteSize));
                uint indexCount = ReadUInt32(bytes, 0);
                uint instanceCount = ReadUInt32(bytes, 4);
                _draws++;
                _vertices = checked(_vertices + (ulong)indexCount * instanceCount);
            }
        }

        private void ExecuteDispatchIndirect(DispatchIndirectCommand command)
        {
            StagedBuffer arguments = Buffer(command.ArgumentBuffer);
            RequireBufferState(arguments, ResourceState.IndirectArgument, "DispatchIndirect arguments");
            uint count = ResolveIndirectCount(command.MaxCommandCount, command.CountBuffer, command.CountBufferOffset);
            for (uint index = 0; index < count; index++)
            {
                int offset = checked((int)(command.ArgumentOffset + (ulong)index * command.CommandStride));
                ReadOnlySpan<byte> bytes = arguments.Bytes.Slice(offset, checked((int)DispatchIndirectArguments.ByteSize));
                uint x = ReadUInt32(bytes, 0);
                uint y = ReadUInt32(bytes, 4);
                uint z = ReadUInt32(bytes, 8);
                _dispatches++;
                _computeInvocations = checked(_computeInvocations + (ulong)x * y * z);
            }
        }

        private uint ResolveIndirectCount(uint maximum, BufferHandle countBuffer, ulong countBufferOffset)
        {
            if (countBuffer == default) return maximum;
            StagedBuffer counts = Buffer(countBuffer);
            RequireBufferState(counts, ResourceState.IndirectArgument, "indirect count buffer");
            uint value = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(
                counts.Bytes.Slice(checked((int)countBufferOffset), sizeof(uint)));
            return Math.Min(maximum, value);
        }

        private void ResetQueryPool(ResetQueryPoolCommand command)
        {
            StagedQueryPool pool = QueryPool(command.Pool);
            for (uint query = command.FirstQuery; query < command.FirstQuery + command.QueryCount; query++)
            {
                int index = checked((int)query);
                pool.Values[index].AsSpan().Clear();
                pool.Ready[index] = false;
                pool.Active[index] = null;
            }
        }

        private void BeginQuery(BeginQueryCommand command)
        {
            StagedQueryPool pool = QueryPool(command.Pool);
            int index = checked((int)command.QueryIndex);
            if (pool.Active[index] is not null) throw _device.ValidationError("A query is already active.");
            pool.Ready[index] = false;
            pool.Active[index] = new QueryCounters(_vertices, checked((ulong)_draws), _computeInvocations);
        }

        private void EndQuery(EndQueryCommand command)
        {
            StagedQueryPool pool = QueryPool(command.Pool);
            int index = checked((int)command.QueryIndex);
            QueryCounters start = pool.Active[index] ?? throw _device.ValidationError("A query was ended without being begun.");
            pool.Active[index] = null;
            switch (pool.Source.Desc.Type)
            {
                case QueryType.Occlusion:
                    WriteUInt64(pool.Values[index], checked((ulong)_draws) - start.Draws);
                    break;
                case QueryType.PipelineStatistics:
                    WritePipelineStatistics(pool.Values[index], start);
                    break;
                default:
                    throw _device.ValidationError("Only scoped queries can be ended.");
            }
            pool.Ready[index] = true;
        }

        private void WriteTimestamp(WriteTimestampCommand command)
        {
            StagedQueryPool pool = QueryPool(command.Pool);
            int index = checked((int)command.QueryIndex);
            WriteUInt64(pool.Values[index], ++_timestampCounter);
            pool.Ready[index] = true;
        }

        private void ResolveQueryPool(ResolveQueryPoolCommand command)
        {
            StagedQueryPool pool = QueryPool(command.Pool);
            StagedBuffer destination = Buffer(command.Destination);
            RequireBufferState(destination, ResourceState.CopyDestination, "query resolve destination");
            for (uint query = 0; query < command.QueryCount; query++)
            {
                int queryIndex = checked((int)(command.FirstQuery + query));
                if (!pool.Ready[queryIndex])
                    throw _device.ValidationError($"Query {queryIndex} has no result to resolve.");
                int destinationOffset = checked((int)(command.DestinationOffset + (ulong)query * command.DestinationStride));
                pool.Values[queryIndex].AsSpan().CopyTo(destination.Bytes.Slice(destinationOffset));
            }
            _copies++;
        }

        private void WritePipelineStatistics(byte[] destination, QueryCounters start)
        {
            ulong vertices = _vertices - start.Vertices;
            ulong draws = checked((ulong)_draws) - start.Draws;
            ulong compute = _computeInvocations - start.ComputeInvocations;
            Span<byte> bytes = destination;
            ulong[] values =
            [
                vertices,
                draws,
                vertices,
                0,
                0,
                draws,
                draws,
                vertices,
                0,
                0,
                compute,
            ];
            for (int index = 0; index < values.Length; index++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(
                    bytes.Slice(index * sizeof(ulong), sizeof(ulong)),
                    values[index]);
            }
        }

        private StagedQueryPool QueryPool(QueryPoolHandle handle)
        {
            if (_queries.TryGetValue(handle, out StagedQueryPool? staged)) return staged;
            staged = new StagedQueryPool(_device.RequireQueryPool(handle));
            _queries.Add(handle, staged);
            return staged;
        }

        private static uint ReadUInt32(ReadOnlySpan<byte> bytes, int offset) =>
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset, sizeof(uint)));

        private static void WriteUInt64(byte[] destination, ulong value) =>
            System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, value);
    }
}

public sealed partial class Device
{
    private sealed partial class SubmissionState
    {
        private void ApplyBarrier(in ResourceBarrier barrier)
        {
            switch (barrier.Kind)
            {
                case BarrierKind.Transition when barrier.Resource.Kind == ResourceKind.Buffer:
                    ApplyBufferTransition(barrier);
                    break;
                case BarrierKind.Transition when barrier.Resource.Kind == ResourceKind.Texture:
                    ApplyTextureTransition(barrier);
                    break;
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

        private void ApplyBufferTransition(in ResourceBarrier barrier)
        {
            StagedBuffer buffer = Buffer(new BufferHandle(
                barrier.Resource.Domain,
                barrier.Resource.Slot,
                barrier.Resource.Generation));
            if (BufferStateValidation.HasFixedState(buffer.Source.MemoryType))
            {
                if (!BufferStateValidation.IsFixedState(buffer.Source.MemoryType, barrier.Before) ||
                    !BufferStateValidation.IsFixedState(buffer.Source.MemoryType, barrier.After))
                {
                    throw _device.ValidationError(
                        $"{buffer.Source.MemoryType} buffers have fixed logical state {BufferStateValidation.DescribeFixedState(buffer.Source.MemoryType)}.");
                }
                return;
            }
            if (buffer.State != barrier.Before)
            {
                throw _device.ValidationError(
                    $"Buffer transition expected {barrier.Before}, actual state is {buffer.State}.");
            }
            buffer.State = barrier.After;
        }

        private void ApplyTextureTransition(in ResourceBarrier barrier)
        {
            StagedTexture texture = Texture(new TextureHandle(
                barrier.Resource.Domain,
                barrier.Resource.Slot,
                barrier.Resource.Generation));
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

        private void CopyTexture(in TextureToTextureCopy copy)
        {
            StagedTexture source = Texture(copy.Source);
            StagedTexture destination = Texture(copy.Destination);
            TextureSubresourceRange sourceRange = new(
                copy.SourceRegion.MipLevel, 1, copy.SourceRegion.ArrayLayer, 1, copy.SourceRegion.Aspect);
            TextureSubresourceRange destinationRange = new(
                copy.DestinationRegion.MipLevel, 1, copy.DestinationRegion.ArrayLayer, 1, copy.DestinationRegion.Aspect);
            RequireTextureState(source, sourceRange, ResourceState.CopySource, "CopyTexture source");
            RequireTextureState(destination, destinationRange, ResourceState.CopyDestination, "CopyTexture destination");
            CopyTextureBytes(source, copy.SourceRegion, destination, copy.DestinationRegion);
            _copies++;
        }

        private void ClearBuffer(ClearBufferCommand command)
        {
            StagedBuffer buffer = Buffer(command.Buffer);
            RequireBufferState(buffer, ResourceState.CopyDestination, "ClearBuffer destination");
            Span<byte> target = buffer.Bytes.Slice(
                checked((int)command.Range.Offset),
                checked((int)command.Range.Size));
            Span<byte> pattern = stackalloc byte[sizeof(uint)];
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pattern, command.Pattern);
            for (int offset = 0; offset < target.Length; offset++)
                target[offset] = pattern[offset & 3];
        }

        private void ClearTexture(ClearTextureCommand command)
        {
            StagedTexture texture = Texture(command.Texture);
            RequireTextureState(texture, command.Range, ResourceState.RenderTarget, "ClearTexture destination");
            ClearColorTextureView(texture, command.Range, command.Color);
        }

        private void ClearDepthStencilTexture(ClearDepthStencilTextureCommand command)
        {
            StagedTexture texture = Texture(command.Texture);
            RequireTextureState(texture, command.Range, ResourceState.DepthWrite, "ClearDepthStencilTexture destination");
            TextureLayout.NormalizeRange(texture.Source.Desc, command.Range,
                out int firstMip, out int mipCount, out int firstLayer, out int layerCount, out TextureAspect aspects);
            foreach (TextureAspect aspect in TextureLayout.EnumerateAspects(aspects))
            for (int layer = firstLayer; layer < firstLayer + layerCount; layer++)
            for (int mip = firstMip; mip < firstMip + mipCount; mip++)
            {
                (int width, int height, int depth) = TextureLayout.GetMipExtent(texture.Source.Desc, mip);
                int bytesPerTexel = TextureLayout.GetBytesPerTexel(texture.Source.Desc.Format, aspect);
                int samples = checked(width * height * depth * texture.Source.Desc.SampleCount);
                int baseOffset = checked(texture.Source.BaseOffset +
                    (int)TextureLayout.GetSubresourceOffset(texture.Source.Desc, mip, layer, aspect));
                Span<byte> bytes = texture.Storage.AsSpan(baseOffset, checked(samples * bytesPerTexel));
                if (aspect == TextureAspect.Stencil)
                {
                    bytes.Fill(command.Stencil);
                    continue;
                }
                if (texture.Source.Desc.Format == Format.D32Float)
                {
                    int bits = BitConverter.SingleToInt32Bits(command.Depth);
                    for (int texel = 0; texel < samples; texel++)
                        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(
                            bytes.Slice(texel * sizeof(float), sizeof(float)), bits);
                    continue;
                }
                uint depth24 = checked((uint)MathF.Round(command.Depth * 0xFFFFFFu));
                for (int texel = 0; texel < samples; texel++)
                    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
                        bytes.Slice(texel * sizeof(uint), sizeof(uint)), depth24);
            }
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
                BeginColorAttachment(color);
            }
            if (rendering.DepthStencil is DepthStencilAttachment depth)
            {
                BeginDepthStencilAttachment(depth);
            }
        }

        private void BeginColorAttachment(in ColorAttachment color)
        {
            TextureViewRecord view = _device.RequireTextureView(color.View);
            StagedTexture texture = Texture(view.Desc.Texture);
            RequireTextureState(texture, view.Desc.Range, ResourceState.RenderTarget, "color attachment");
            if (color.Load == LoadAction.Clear)
                ClearColorTextureView(texture, view.Desc.Range, color.ClearColor);
        }

        private void BeginDepthStencilAttachment(in DepthStencilAttachment attachment)
        {
            TextureViewRecord view = _device.RequireTextureView(attachment.View);
            StagedTexture texture = Texture(view.Desc.Texture);
            TextureSubresourceRange baseRange = view.Desc.Range == default
                ? new TextureSubresourceRange(
                    0,
                    texture.Source.Desc.MipLevels,
                    0,
                    texture.Source.Desc.ArrayLayers,
                    TextureLayout.AllowedAspects(texture.Source.Desc.Format))
                : view.Desc.Range;
            if (attachment.Depth is DepthAttachmentOperations depth)
            {
                ApplyDepthStencilOperations(
                    texture,
                    baseRange,
                    TextureAspect.Depth,
                    depth.ReadOnly,
                    depth.Load,
                    "depth attachment");
            }
            if (attachment.Stencil is StencilAttachmentOperations stencil)
            {
                ApplyDepthStencilOperations(
                    texture,
                    baseRange,
                    TextureAspect.Stencil,
                    stencil.ReadOnly,
                    stencil.Load,
                    "stencil attachment");
            }
        }

        private void ApplyDepthStencilOperations(
            StagedTexture texture,
            in TextureSubresourceRange baseRange,
            TextureAspect aspect,
            bool readOnly,
            LoadAction load,
            string operation)
        {
            TextureSubresourceRange range = baseRange with { Aspect = aspect };
            RequireTextureState(
                texture,
                range,
                readOnly ? ResourceState.DepthRead : ResourceState.DepthWrite,
                operation);
            if (load == LoadAction.Clear) ClearTextureView(texture, range);
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
    }
}

public sealed partial class Device
{
    private sealed partial class SubmissionState
    {
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

        private static void CopyTextureBytes(
            StagedTexture source,
            in TextureCopyRegion sourceRegion,
            StagedTexture destination,
            in TextureCopyRegion destinationRegion)
        {
            (int sourceWidth, int sourceHeight, _) = TextureLayout.GetMipExtent(source.Source.Desc, sourceRegion.MipLevel);
            (int destinationWidth, int destinationHeight, _) = TextureLayout.GetMipExtent(destination.Source.Desc, destinationRegion.MipLevel);
            int bytesPerTexel = checked(
                TextureLayout.GetBytesPerTexel(source.Source.Desc.Format, sourceRegion.Aspect) *
                source.Source.Desc.SampleCount);
            int rowBytes = checked(sourceRegion.Width * bytesPerTexel);
            int sourceBase = checked(source.Source.BaseOffset +
                (int)TextureLayout.GetSubresourceOffset(source.Source.Desc, sourceRegion.MipLevel, sourceRegion.ArrayLayer, sourceRegion.Aspect));
            int destinationBase = checked(destination.Source.BaseOffset +
                (int)TextureLayout.GetSubresourceOffset(destination.Source.Desc, destinationRegion.MipLevel, destinationRegion.ArrayLayer, destinationRegion.Aspect));
            for (int slice = 0; slice < sourceRegion.Depth; slice++)
            for (int row = 0; row < sourceRegion.Height; row++)
            {
                int sourceOffset = checked(sourceBase +
                    (((sourceRegion.Z + slice) * sourceHeight + sourceRegion.Y + row) * sourceWidth + sourceRegion.X) * bytesPerTexel);
                int destinationOffset = checked(destinationBase +
                    (((destinationRegion.Z + slice) * destinationHeight + destinationRegion.Y + row) * destinationWidth + destinationRegion.X) * bytesPerTexel);
                source.Storage.AsSpan(sourceOffset, rowBytes).CopyTo(
                    destination.Storage.AsSpan(destinationOffset, rowBytes));
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

        private sealed class StagedQueryPool
        {
            public StagedQueryPool(QueryPoolRecord source)
            {
                Source = source;
                Values = source.Values.Select(static value => value.ToArray()).ToArray();
                Ready = source.Ready.ToArray();
                Active = new QueryCounters?[source.Values.Length];
            }

            public QueryPoolRecord Source { get; }
            public byte[][] Values { get; }
            public bool[] Ready { get; }
            public QueryCounters?[] Active { get; }

            public void Commit()
            {
                for (int index = 0; index < Values.Length; index++)
                    Values[index].CopyTo(Source.Values[index], 0);
                Ready.CopyTo(Source.Ready, 0);
            }
        }

        private readonly record struct QueryCounters(
            ulong Vertices,
            ulong Draws,
            ulong ComputeInvocations);
    }
}
