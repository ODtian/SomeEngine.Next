namespace SomeEngine.Graphics.Null;

public sealed partial class Device
{
    internal RenderingInfo FreezeRenderingInfo(in RenderingInfo rendering, CommandReferences references)
    {
        lock (_gate)
        {
            EnsureNotDisposed();
            if (rendering.Width <= 0 || rendering.Height <= 0) throw new ArgumentOutOfRangeException(nameof(rendering));
            ColorAttachment[] colors = rendering.Colors.ToArray();
            if (colors.Length == 0 && rendering.DepthStencil is null)
                throw new ArgumentException("Rendering requires at least one attachment.", nameof(rendering));
            foreach (ref readonly ColorAttachment color in colors.AsSpan())
            {
                TextureViewRecord view = RequireTextureView(color.View);
                if (!view.Desc.Usage.HasFlag(TextureViewUsage.ColorAttachment))
                    throw ValidationError("A color attachment requires ColorAttachment view usage.");
                TextureRecord texture = RequireTexture(view.Desc.Texture);
                if (TextureLayout.IsDepth(texture.Desc.Format)) throw ValidationError("A depth texture cannot be used as a color attachment.");
                ValidateAttachmentExtent(texture.Desc, view.Desc.Range, rendering.Width, rendering.Height);
                references.TextureViews.Add(color.View);
            }
            if (rendering.DepthStencil is DepthStencilAttachment depth)
            {
                TextureViewRecord view = RequireTextureView(depth.View);
                if (!view.Desc.Usage.HasFlag(TextureViewUsage.DepthStencilAttachment))
                    throw ValidationError("A depth attachment requires DepthStencilAttachment view usage.");
                TextureRecord texture = RequireTexture(view.Desc.Texture);
                if (!TextureLayout.IsDepth(texture.Desc.Format)) throw ValidationError("A color texture cannot be used as a depth attachment.");
                ValidateDepthStencilOperations(texture.Desc, view.Desc.Range, depth);
                ValidateAttachmentExtent(texture.Desc, view.Desc.Range, rendering.Width, rendering.Height);
                references.TextureViews.Add(depth.View);
            }
            return new RenderingInfo(colors, rendering.DepthStencil, rendering.Width, rendering.Height);
        }
    }

    private void ExpandAndValidateReferences(CommandReferences references)
    {
        foreach (PipelineHandle handle in references.Pipelines.ToArray())
        {
            PipelineRecord pipeline = RequirePipeline(handle);
            references.PipelineLayouts.Add(pipeline.Layout);
            references.Shaders.Add(pipeline.FirstShader);
            if (pipeline.SecondShader.IsValid) references.Shaders.Add(pipeline.SecondShader);
        }
        foreach (PipelineLayoutHandle handle in references.PipelineLayouts.ToArray())
        {
            PipelineLayoutRecord layout = RequirePipelineLayout(handle);
            foreach (BindGroupLayoutHandle group in layout.Groups) references.BindGroupLayouts.Add(group);
        }
        foreach (BindGroupHandle handle in references.BindGroups.ToArray())
        {
            BindGroupRecord group = RequireBindGroup(handle);
            references.BindGroupLayouts.Add(group.Layout);
            foreach (BindingWrite write in group.Writes) AddBindingReference(write, references);
        }
        foreach (TextureViewHandle handle in references.TextureViews.ToArray())
        {
            TextureViewRecord view = RequireTextureView(handle);
            references.Textures.Add(view.Desc.Texture);
        }
        foreach (BufferViewHandle handle in references.BufferViews.ToArray())
        {
            BufferViewRecord view = RequireBufferView(handle);
            references.Buffers.Add(view.Desc.Buffer);
        }
        foreach (BufferHandle handle in references.Buffers.ToArray())
        {
            BufferRecord buffer = RequireBuffer(handle);
            if (buffer.Heap.IsValid) references.Heaps.Add(buffer.Heap);
        }
        foreach (TextureHandle handle in references.Textures.ToArray())
        {
            TextureRecord texture = RequireTexture(handle);
            if (texture.Heap.IsValid) references.Heaps.Add(texture.Heap);
        }
        foreach (HeapHandle handle in references.Heaps) _ = RequireHeap(handle);
        foreach (TextureViewHandle handle in references.TextureViews) _ = RequireTextureView(handle);
        foreach (BufferViewHandle handle in references.BufferViews) _ = RequireBufferView(handle);
        foreach (SamplerHandle handle in references.Samplers) _ = RequireSampler(handle);
        foreach (BindGroupLayoutHandle handle in references.BindGroupLayouts) _ = RequireBindGroupLayout(handle);
        foreach (ShaderHandle handle in references.Shaders) _ = RequireShader(handle);
    }

    private void AddBindingReference(in BindingWrite write, CommandReferences references)
    {
        switch (write.ValueKind)
        {
            case BindingValueKind.TextureView:
                references.TextureViews.Add(write.TextureView);
                break;
            case BindingValueKind.BufferView:
                references.BufferViews.Add(write.BufferView);
                break;
            case BindingValueKind.Sampler:
                references.Samplers.Add(write.Sampler);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(write));
        }
    }

    private static void ValidateTextureCopyLayout(
        in TextureDesc texture,
        in TextureCopyRegion region,
        in TextureBufferLayout layout,
        ulong bufferSize)
    {
        (int width, int height, int depth, int bytesPerTexel) = TextureLayout.ValidateCopyRegion(texture, region);
        ulong tightRow = checked((ulong)width * (ulong)bytesPerTexel);
        ulong rowPitch = layout.BytesPerRow;
        ulong imageRows = layout.RowsPerImage;
        if (rowPitch < tightRow || imageRows < (ulong)height) throw new ArgumentOutOfRangeException(nameof(layout));
        ulong required = checked((ulong)(depth - 1) * imageRows * rowPitch + (ulong)(height - 1) * rowPitch + tightRow);
        ValidateByteRange(bufferSize, layout.Offset, required);
    }

    private static void ValidateAttachmentExtent(in TextureDesc texture, in TextureSubresourceRange range, int width, int height)
    {
        TextureLayout.NormalizeRange(texture, range, out int firstMip, out int mipCount, out _, out int layerCount);
        if (mipCount != 1 || layerCount != 1) throw new ArgumentException("An attachment view must select one mip and one layer.", nameof(range));
        (int mipWidth, int mipHeight, _) = TextureLayout.GetMipExtent(texture, firstMip);
        if (width > mipWidth || height > mipHeight) throw new ArgumentOutOfRangeException(nameof(width));
    }

    private static void ValidateDepthStencilOperations(
        in TextureDesc texture,
        in TextureSubresourceRange range,
        in DepthStencilAttachment attachment)
    {
        if (attachment.Depth is null && attachment.Stencil is null)
            throw new ArgumentException("A depth-stencil attachment must select at least one plane.", nameof(attachment));
        if (texture.Format == Format.D32Float && attachment.Stencil is not null)
            throw new ArgumentException("D32Float does not expose a stencil plane.", nameof(attachment));
        TextureAspect aspects = range == default ? TextureLayout.AllowedAspects(texture.Format) : range.Aspect;
        if (attachment.Depth is DepthAttachmentOperations depth)
        {
            ValidateAttachmentActions(depth.Load, depth.Store, depth.ReadOnly, nameof(attachment.Depth));
            if ((aspects & TextureAspect.Depth) == 0) throw new ArgumentException("The view does not include the depth plane.", nameof(attachment));
            if (depth.ClearValue is < 0f or > 1f || float.IsNaN(depth.ClearValue))
                throw new ArgumentOutOfRangeException(nameof(attachment), "Depth clear value must be in [0, 1].");
        }
        if (attachment.Stencil is StencilAttachmentOperations stencil)
        {
            ValidateAttachmentActions(stencil.Load, stencil.Store, stencil.ReadOnly, nameof(attachment.Stencil));
            if ((aspects & TextureAspect.Stencil) == 0) throw new ArgumentException("The view does not include the stencil plane.", nameof(attachment));
        }
    }

    private static void ValidateAttachmentActions(LoadAction load, StoreAction store, bool readOnly, string parameter)
    {
        if (!Enum.IsDefined(load) || !Enum.IsDefined(store)) throw new ArgumentOutOfRangeException(parameter);
        if (readOnly && (load != LoadAction.Load || store != StoreAction.Store))
            throw new ArgumentException("Read-only attachment planes require Load/Store operations.", parameter);
    }

}
