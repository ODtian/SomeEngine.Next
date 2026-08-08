using System.Numerics;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public void BeginRendering(CommandContext context, in RenderingDesc desc)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (desc.Colors.Length > 8)
            throw new ArgumentOutOfRangeException(nameof(desc));

        command.PrepareRenderingState(desc);
        try
        {
            ReadOnlySpan<ColorAttachmentDesc> capturedColors = command.RenderingColors;
            CpuDescriptorHandle* colors = stackalloc CpuDescriptorHandle[capturedColors.Length];
            float* clearValues = stackalloc float[4];
            for (int index = 0; index < capturedColors.Length; index++)
            {
                ColorAttachmentView view = capturedColors[index].View;
                colors[index] = ((INativeDescriptor)view).NativeDescriptor.Cpu;
                command.Capture(view);
                if (capturedColors[index].ResolveView is ColorAttachmentView resolveView)
                    command.Capture(resolveView);
            }

            CpuDescriptorHandle depthStencil = default;
            CpuDescriptorHandle* depthStencilPointer = null;
            if (command.RenderingDepthStencil is DepthStencilAttachmentDesc depthAttachment)
            {
                depthStencil = ((INativeDescriptor)depthAttachment.View).NativeDescriptor.Cpu;
                depthStencilPointer = &depthStencil;
                command.Capture(depthAttachment.View);
            }

            command.List->OMSetRenderTargets(
                checked((uint)capturedColors.Length),
                colors,
                false,
                depthStencilPointer);

            for (int index = 0; index < capturedColors.Length; index++)
            {
                ColorAttachmentDesc attachment = capturedColors[index];
                switch (attachment.Load)
                {
                    case LoadType.Load:
                        break;
                    case LoadType.Clear:
                        Vector4 clear = attachment.ClearValue;
                        clearValues[0] = clear.X;
                        clearValues[1] = clear.Y;
                        clearValues[2] = clear.Z;
                        clearValues[3] = clear.W;
                        command.List->ClearRenderTargetView(colors[index], clearValues, 0, null);
                        break;
                    case LoadType.Discard:
                        DiscardView(command, attachment.View.Resource, attachment.View.Description.Range);
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(desc));
                }
            }

            if (command.RenderingDepthStencil is DepthStencilAttachmentDesc depthStencilAttachment)
            {
                ClearFlags clearFlags = 0;
                if (depthStencilAttachment.DepthLoad == LoadType.Clear)
                    clearFlags |= ClearFlags.Depth;
                else if (depthStencilAttachment.DepthLoad == LoadType.Discard)
                    DiscardView(
                        command,
                        depthStencilAttachment.View.Resource,
                        depthStencilAttachment.View.Description.Range with
                        {
                            Aspects = TextureAspects.Depth,
                        });
                if (depthStencilAttachment.StencilLoad == LoadType.Clear)
                    clearFlags |= ClearFlags.Stencil;
                else if (depthStencilAttachment.StencilLoad == LoadType.Discard)
                    DiscardView(
                        command,
                        depthStencilAttachment.View.Resource,
                        depthStencilAttachment.View.Description.Range with
                        {
                            Aspects = TextureAspects.Stencil,
                        });
                if (clearFlags != 0)
                {
                    command.List->ClearDepthStencilView(
                        depthStencil,
                        clearFlags,
                        depthStencilAttachment.ClearDepth,
                        depthStencilAttachment.ClearStencil,
                        0,
                        null);
                }
            }

            command.CommitRenderingState();
        }
        catch
        {
            command.CancelRenderingState();
            throw;
        }
    }

    public void EndRendering(CommandContext context)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        foreach (ColorAttachmentDesc attachment in command.RenderingColors)
        {
            if (attachment.ResolveView is ColorAttachmentView resolveView)
            {
                D3D12TextureResource source = NativeCast.Texture(attachment.View.Resource);
                D3D12TextureResource destination = NativeCast.Texture(resolveView.Resource);
                TextureSubresourceRange sourceRange = attachment.View.Description.Range;
                TextureSubresourceRange destinationRange = resolveView.Description.Range;
                command.List->ResolveSubresourceRegion(
                    destination.Native,
                    NativeSubresource(
                        destination.Info,
                        destinationRange.FirstMipLevel,
                        destinationRange.FirstArrayLayer,
                        destinationRange.Aspects),
                    0,
                    0,
                    source.Native,
                    NativeSubresource(
                        source.Info,
                        sourceRange.FirstMipLevel,
                        sourceRange.FirstArrayLayer,
                        sourceRange.Aspects),
                    null,
                    FormatMappings.ToDxgi(attachment.View.Description.Format),
                    ToResolveMode(attachment.ResolveType));
            }
            if (attachment.Store == StoreType.Discard)
                DiscardView(command, attachment.View.Resource, attachment.View.Description.Range);
        }

        if (command.RenderingDepthStencil is DepthStencilAttachmentDesc depthStencil)
        {
            if (depthStencil.DepthStore == StoreType.Discard)
                DiscardView(
                    command,
                    depthStencil.View.Resource,
                    depthStencil.View.Description.Range with
                    {
                        Aspects = TextureAspects.Depth,
                    });
            if (depthStencil.StencilStore == StoreType.Discard)
                DiscardView(
                    command,
                    depthStencil.View.Resource,
                    depthStencil.View.Description.Range with
                    {
                        Aspects = TextureAspects.Stencil,
                    });
        }

        command.CloseRendering();
    }

    private static void DiscardView(
        D3D12CommandContext command,
        Texture texture,
        in TextureSubresourceRange range)
    {
        D3D12TextureResource native = NativeCast.Texture(texture);
        uint plane = FormatMappings.PlaneIndex(native.Info.Format, range.Aspects);
        for (uint layer = range.FirstArrayLayer;
             layer < range.FirstArrayLayer + range.ArrayLayerCount;
             layer++)
        {
            uint first = checked(
                range.FirstMipLevel +
                layer * native.Info.MipLevelCount +
                plane * native.Info.MipLevelCount * native.Info.ArrayLayerCount);
            DiscardRegion nativeRange = new(
                0,
                null,
                first,
                range.MipLevelCount);
            command.List->DiscardResource(native.Native, &nativeRange);
        }
    }

    private sealed partial class D3D12CommandContext
    {
        private ColorAttachmentDesc[] _renderingColors = [];
        private int _renderingColorCount;
        private DepthStencilAttachmentDesc? _renderingDepthStencil;
        private bool _renderingPrepared;

        internal ReadOnlySpan<ColorAttachmentDesc> RenderingColors =>
            _renderingColors.AsSpan(0, _renderingColorCount);
        internal DepthStencilAttachmentDesc? RenderingDepthStencil => _renderingDepthStencil;

        internal void PrepareRenderingState(in RenderingDesc description)
        {
            if (_renderingColors.Length < description.Colors.Length)
                Array.Resize(ref _renderingColors, description.Colors.Length);
            description.Colors.CopyTo(_renderingColors);
            _renderingColorCount = description.Colors.Length;
            _renderingDepthStencil = description.DepthStencil;
            _renderingPrepared = true;
        }

        internal void CommitRenderingState()
        {
            if (!_renderingPrepared)
                throw new InvalidOperationException("The rendering scope was not prepared.");
        }

        internal void CloseRendering()
        {
            ResetRenderingState();
        }

        internal void CancelRenderingState() => ResetRenderingState();

        internal void ResetRenderingState()
        {
            Array.Clear(_renderingColors, 0, _renderingColorCount);
            _renderingColorCount = 0;
            _renderingDepthStencil = null;
            _renderingPrepared = false;
        }
    }
}
