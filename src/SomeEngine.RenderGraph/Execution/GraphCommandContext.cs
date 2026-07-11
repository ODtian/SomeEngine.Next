namespace SomeEngine.RenderGraph;

/// <summary>
/// Enforces the frozen pass capability envelope before forwarding commands to the backend.
/// Render scopes, barriers, command-list ownership, and opaque bind groups remain graph-owned.
/// </summary>
internal sealed class GraphCommandContext : ICommandContext
{
    private readonly ICommandContext _inner;
    private readonly GraphInvocation _invocation;
    private readonly int _pass;

    public GraphCommandContext(ICommandContext inner, GraphInvocation invocation, int pass)
    {
        _inner = inner;
        _invocation = invocation;
        _pass = pass;
    }

    public QueueType Queue => _inner.Queue;
    public bool IsFinished => _inner.IsFinished;

    public void Barriers(ReadOnlySpan<ResourceBarrier> barriers) =>
        throw new InvalidOperationException("Resource barriers are owned by the render graph.");

    public void CopyBuffer(
        BufferHandle source,
        ulong sourceOffset,
        BufferHandle destination,
        ulong destinationOffset,
        ulong size)
    {
        RequireBufferAccess(source, BufferUse.CopySource, ResourceEffect.Read, sourceOffset, size, nameof(CopyBuffer));
        RequireBufferAccess(destination, BufferUse.CopyDestination, ResourceEffect.Write, destinationOffset, size, nameof(CopyBuffer));
        _inner.CopyBuffer(source, sourceOffset, destination, destinationOffset, size);
    }

    public void CopyBufferToTexture(in BufferTextureCopy copy)
    {
        TextureDesc texture = FindTextureDesc(copy.Destination, nameof(CopyBufferToTexture));
        ulong size = CopyFootprintSize(texture, copy.DestinationRegion, copy.SourceLayout, nameof(copy));
        RequireBufferAccess(copy.Source, BufferUse.CopySource, ResourceEffect.Read, copy.SourceLayout.Offset, size, nameof(CopyBufferToTexture));
        RequireTextureAccess(
            copy.Destination,
            TextureUse.CopyDestination,
            ResourceEffect.Write,
            copy.DestinationRegion.MipLevel,
            copy.DestinationRegion.ArrayLayer,
            copy.DestinationRegion.Aspect,
            nameof(CopyBufferToTexture));
        _inner.CopyBufferToTexture(copy);
    }

    public void CopyTextureToBuffer(in TextureBufferCopy copy)
    {
        TextureDesc texture = FindTextureDesc(copy.Source, nameof(CopyTextureToBuffer));
        ulong size = CopyFootprintSize(texture, copy.SourceRegion, copy.DestinationLayout, nameof(copy));
        RequireTextureAccess(
            copy.Source,
            TextureUse.CopySource,
            ResourceEffect.Read,
            copy.SourceRegion.MipLevel,
            copy.SourceRegion.ArrayLayer,
            copy.SourceRegion.Aspect,
            nameof(CopyTextureToBuffer));
        RequireBufferAccess(
            copy.Destination,
            BufferUse.CopyDestination,
            ResourceEffect.Write,
            copy.DestinationLayout.Offset,
            size,
            nameof(CopyTextureToBuffer));
        _inner.CopyTextureToBuffer(copy);
    }

    public void ResolveTexture(in TextureResolveRegion resolve)
    {
        RequireTextureAccess(
            resolve.Source,
            TextureUse.ResolveSource,
            ResourceEffect.Read,
            resolve.SourceMipLevel,
            resolve.SourceArrayLayer,
            resolve.Aspect,
            nameof(ResolveTexture));
        RequireTextureAccess(
            resolve.Destination,
            TextureUse.ResolveDestination,
            ResourceEffect.Write,
            resolve.DestinationMipLevel,
            resolve.DestinationArrayLayer,
            resolve.Aspect,
            nameof(ResolveTexture));
        _inner.ResolveTexture(resolve);
    }

    public void BeginRendering(in RenderingInfo rendering) =>
        throw new InvalidOperationException("Rendering scopes are owned by the render graph.");

    public void EndRendering() =>
        throw new InvalidOperationException("Rendering scopes are owned by the render graph.");

    public void SetPipeline(PipelineHandle pipeline)
    {
        if (!Pass.Pipelines.Contains(pipeline))
            throw new InvalidOperationException("The pipeline was not frozen as an allowed choice for this pass.");
        _inner.SetPipeline(pipeline);
    }

    public void SetBindGroup(uint groupIndex, BindGroupHandle group) =>
        throw new NotSupportedException(
            "Opaque bind groups cannot be verified against render-graph shader mappings; use exact SetBindings writes.");

    public void SetBindings(uint groupIndex, BindGroupLayoutHandle layout, ReadOnlySpan<BindingWrite> writes)
    {
        HashSet<(uint Binding, uint Element)> unique = [];
        foreach (ref readonly BindingWrite write in writes)
        {
            if (!unique.Add((write.Binding, write.Element)))
                throw new ArgumentException("A descriptor element may be written only once per SetBindings call.", nameof(writes));
            ValidateBindingWrite(groupIndex, write);
        }
        _inner.SetBindings(groupIndex, layout, writes);
    }

    public void SetPushConstants(
        PipelineLayoutHandle layout,
        ShaderStage stages,
        uint byteOffset,
        ReadOnlySpan<byte> data)
    {
        if (!layout.IsValid || layout.Domain != _invocation.Domain)
            throw new ArgumentException("Push-constant layout must be a valid handle from this device.", nameof(layout));
        if (stages == 0 || (stages & ~(ShaderStage.Vertex | ShaderStage.Pixel | ShaderStage.Compute)) != 0)
            throw new ArgumentOutOfRangeException(nameof(stages));
        if (data.IsEmpty || (byteOffset & 3) != 0 || (data.Length & 3) != 0)
            throw new ArgumentException("Push-constant writes must be non-empty and four-byte aligned.", nameof(data));

        ulong end = checked((ulong)byteOffset + (uint)data.Length);
        ShaderStage remaining = stages;
        foreach (FrozenShaderContract shader in Pass.Shaders)
        {
            if ((remaining & shader.Stage) == 0) continue;
            foreach (PushConstantRange range in shader.PushConstants)
            {
                ulong rangeEnd = checked((ulong)range.Offset + range.Size);
                if (byteOffset >= range.Offset && end <= rangeEnd && (range.Visibility & shader.Stage) != 0)
                {
                    remaining &= ~shader.Stage;
                    break;
                }
            }
        }
        if (remaining != 0)
            throw new InvalidOperationException("Push-constant write is not covered by every requested shader stage contract in this pass.");
        _inner.SetPushConstants(layout, stages, byteOffset, data);
    }

    public void SetViewport(in Viewport viewport) => _inner.SetViewport(viewport);
    public void SetScissor(in Rect rect) => _inner.SetScissor(rect);

    public void SetVertexBuffer(uint slot, BufferHandle buffer, ulong offset, uint stride)
    {
        RequireBufferAccess(
            buffer,
            BufferUse.VertexOrConstant,
            ResourceEffect.Read,
            offset,
            RemainingBufferSize(buffer, offset, nameof(SetVertexBuffer)),
            nameof(SetVertexBuffer));
        _inner.SetVertexBuffer(slot, buffer, offset, stride);
    }

    public void SetIndexBuffer(BufferHandle buffer, ulong offset, IndexFormat format)
    {
        RequireBufferAccess(
            buffer,
            BufferUse.Index,
            ResourceEffect.Read,
            offset,
            RemainingBufferSize(buffer, offset, nameof(SetIndexBuffer)),
            nameof(SetIndexBuffer));
        _inner.SetIndexBuffer(buffer, offset, format);
    }

    public void Draw(uint vertexCount, uint instanceCount = 1, uint firstVertex = 0, uint firstInstance = 0) =>
        _inner.Draw(vertexCount, instanceCount, firstVertex, firstInstance);

    public void DrawIndexed(
        uint indexCount,
        uint instanceCount = 1,
        uint firstIndex = 0,
        int vertexOffset = 0,
        uint firstInstance = 0) =>
        _inner.DrawIndexed(indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);

    public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) =>
        _inner.Dispatch(groupCountX, groupCountY, groupCountZ);

    public void PushDebugGroup(string name) => _inner.PushDebugGroup(name);
    public void PopDebugGroup() => _inner.PopDebugGroup();

    public CommandListHandle Finish() =>
        throw new InvalidOperationException("Command-list completion is owned by the render graph.");

    public void Dispose() =>
        throw new InvalidOperationException("Command-context lifetime is owned by the render graph.");

    private FrozenPass Pass => _invocation.Frozen.Passes[_pass];

    private void RequireBufferAccess(
        BufferHandle handle,
        BufferUse use,
        ResourceEffect requiredEffect,
        ulong offset,
        ulong size,
        string operation)
    {
        if (!handle.IsValid || size == 0)
            throw new ArgumentException($"{operation} requires a valid, non-empty buffer range.");
        ulong end = checked(offset + size);
        for (int resource = 0; resource < _invocation.Buffers.Length; resource++)
        {
            if (_invocation.Buffers[resource] != handle) continue;
            foreach (FrozenAccess access in Pass.Accesses)
            {
                if (access.Kind != ResourceNodeKind.Buffer ||
                    access.Resource != resource ||
                    access.BufferUse != use ||
                    !Covers(access.Effect, requiredEffect))
                {
                    continue;
                }

                ulong accessEnd = checked(access.BufferRange.Offset + access.BufferRange.Size);
                if (offset >= access.BufferRange.Offset && end <= accessEnd) return;
            }
            throw new InvalidOperationException(
                $"{operation} uses buffer {handle} outside this pass's declared {requiredEffect}/{use} access range.");
        }
        throw new InvalidOperationException(
            $"{operation} uses a buffer that is not declared by this render-graph invocation.");
    }

    private void RequireTextureAccess(
        TextureHandle handle,
        TextureUse use,
        ResourceEffect requiredEffect,
        int mip,
        int layer,
        TextureAspect aspect,
        string operation)
    {
        if (!handle.IsValid) throw new ArgumentException($"{operation} requires a valid texture.");
        for (int resource = 0; resource < _invocation.Textures.Length; resource++)
        {
            if (_invocation.Textures[resource] != handle) continue;
            foreach (FrozenAccess access in Pass.Accesses)
            {
                TextureSubresourceRange range = access.TextureRange;
                if (access.Kind != ResourceNodeKind.Texture ||
                    access.Resource != resource ||
                    access.TextureUse != use ||
                    !Covers(access.Effect, requiredEffect) ||
                    (range.Aspect & aspect) == 0)
                {
                    continue;
                }

                if (mip >= range.FirstMip &&
                    mip < range.FirstMip + range.MipCount &&
                    layer >= range.FirstLayer &&
                    layer < range.FirstLayer + range.LayerCount)
                {
                    return;
                }
            }
            throw new InvalidOperationException(
                $"{operation} uses texture {handle} outside this pass's declared {requiredEffect}/{use} subresources.");
        }
        throw new InvalidOperationException(
            $"{operation} uses a texture that is not declared by this render-graph invocation.");
    }

    private void ValidateBindingWrite(uint group, in BindingWrite write)
    {
        bool matched = false;
        foreach (FrozenShaderContract shader in Pass.Shaders)
        foreach (FrozenShaderBindingAccess mapping in shader.Accesses)
        {
            if (mapping.Group != group ||
                mapping.Binding != write.Binding ||
                mapping.Element != write.Element)
            {
                continue;
            }

            matched = true;
            ShaderBinding binding = shader.Bindings.Single(candidate =>
                candidate.Group == mapping.Group && candidate.Binding == mapping.Binding);
            ValidateBindingValueKind(binding.Kind, write);
            switch (mapping.Kind)
            {
                case ShaderBindingAccessKind.BufferView:
                    if (write.ValueKind != BindingValueKind.BufferView ||
                        write.BufferView != _invocation.BufferViews[mapping.View])
                    {
                        throw new InvalidOperationException(
                            "The bound buffer view does not match the pass's exact shader mapping.");
                    }
                    break;
                case ShaderBindingAccessKind.TextureView:
                    if (write.ValueKind != BindingValueKind.TextureView ||
                        write.TextureView != _invocation.TextureViews[mapping.View])
                    {
                        throw new InvalidOperationException(
                            "The bound texture view does not match the pass's exact shader mapping.");
                    }
                    break;
                case ShaderBindingAccessKind.ExternallyManaged:
                    ValidateExternalBindingDomain(write);
                    break;
                default:
                    throw new InvalidOperationException("The frozen shader mapping kind is invalid.");
            }
        }

        if (!matched)
        {
            throw new InvalidOperationException(
                $"Descriptor ({group}, {write.Binding}) element {write.Element} is not frozen in this pass's shader contract.");
        }
    }

    private void ValidateExternalBindingDomain(in BindingWrite write)
    {
        DeviceDomain domain = write.ValueKind switch
        {
            BindingValueKind.TextureView when write.TextureView.IsValid => write.TextureView.Domain,
            BindingValueKind.BufferView when write.BufferView.IsValid => write.BufferView.Domain,
            BindingValueKind.Sampler when write.Sampler.IsValid => write.Sampler.Domain,
            _ => throw new ArgumentException(
                "An externally managed descriptor write must contain one valid handle."),
        };
        if (domain != _invocation.Domain)
            throw new ArgumentException("An externally managed descriptor belongs to another device domain.");
    }

    private static void ValidateBindingValueKind(BindingKind expected, in BindingWrite write)
    {
        BindingValueKind expectedValue = expected switch
        {
            BindingKind.ConstantBuffer or
            BindingKind.ReadOnlyBuffer or
            BindingKind.StorageBuffer => BindingValueKind.BufferView,
            BindingKind.SampledTexture or BindingKind.StorageTexture => BindingValueKind.TextureView,
            BindingKind.Sampler => BindingValueKind.Sampler,
            _ => throw new InvalidOperationException($"Unsupported shader binding kind {expected}."),
        };
        if (write.ValueKind != expectedValue)
        {
            throw new InvalidOperationException(
                $"Descriptor value kind {write.ValueKind} does not match shader binding kind {expected}.");
        }
    }

    private static bool Covers(ResourceEffect actual, ResourceEffect required) => required switch
    {
        ResourceEffect.Read => actual is ResourceEffect.Read or ResourceEffect.ReadWrite,
        ResourceEffect.Write => actual is ResourceEffect.Write or ResourceEffect.ReadWrite,
        ResourceEffect.ReadWrite => actual == ResourceEffect.ReadWrite,
        _ => false,
    };

    private static ulong CopyFootprintSize(
        in TextureDesc texture,
        in TextureCopyRegion region,
        in TextureBufferLayout layout,
        string parameter)
    {
        if (layout.BytesPerRow == 0 || layout.RowsPerImage == 0 ||
            region.Width <= 0 || region.Height <= 0 || region.Depth <= 0)
            throw new ArgumentException("Copy row pitch and row count must be non-zero.", parameter);
        uint texelSize = (texture.Format, region.Aspect) switch
        {
            (Format.R8UNorm, TextureAspect.Color) => 1,
            (Format.R8G8UNorm or Format.R16UInt or Format.R16Float, TextureAspect.Color) => 2,
            (Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb or Format.B8G8R8A8UNorm or
                Format.R16G16Float or Format.R32UInt or Format.R32Float, TextureAspect.Color) => 4,
            (Format.R16G16B16A16Float or Format.R32G32Float, TextureAspect.Color) => 8,
            (Format.R32G32B32Float, TextureAspect.Color) => 12,
            (Format.R32G32B32A32Float, TextureAspect.Color) => 16,
            (Format.D32Float, TextureAspect.Depth) => 4,
            (Format.D24UNormS8UInt, TextureAspect.Depth) => 4,
            (Format.D24UNormS8UInt, TextureAspect.Stencil) => 1,
            _ => throw new ArgumentException("Copy aspect is incompatible with the texture format.", parameter),
        };
        ulong rowSize = checked((ulong)(uint)region.Width * texelSize);
        if (layout.BytesPerRow < rowSize || layout.RowsPerImage < (uint)region.Height)
            throw new ArgumentException("Copy layout is smaller than the selected texture region.", parameter);
        return checked(
            (ulong)(uint)(region.Depth - 1) * layout.RowsPerImage * layout.BytesPerRow +
            (ulong)(uint)(region.Height - 1) * layout.BytesPerRow +
            rowSize);
    }

    private TextureDesc FindTextureDesc(TextureHandle handle, string operation)
    {
        for (int resource = 0; resource < _invocation.Textures.Length; resource++)
        {
            if (_invocation.Textures[resource] == handle)
                return _invocation.Frozen.Resources[resource].TextureDesc;
        }
        throw new InvalidOperationException($"{operation} uses a texture outside this render-graph invocation.");
    }

    private ulong RemainingBufferSize(BufferHandle handle, ulong offset, string operation)
    {
        for (int resource = 0; resource < _invocation.Buffers.Length; resource++)
        {
            if (_invocation.Buffers[resource] != handle) continue;
            ulong size = _invocation.Frozen.Resources[resource].BufferDesc.Size;
            if (offset >= size) throw new ArgumentOutOfRangeException(nameof(offset), $"{operation} starts outside the buffer.");
            return size - offset;
        }
        throw new InvalidOperationException($"{operation} uses a buffer outside this render-graph invocation.");
    }
}
