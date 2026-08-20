using System.Numerics;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public void BeginRendering(CommandContext context, in RenderingDesc desc)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        command.RequireRenderingClosed();
        ValidateRenderingDescription(desc);
        if (CanBeginSingleColorRendering(desc))
        {
            BeginSingleColorRendering(command, desc.Colors[0]);
            return;
        }
        int attachmentCount = PrepareRenderingDescription(command, desc);
        command.PrepareCaptures(attachmentCount, attachmentCount, attachmentCount);
        command.PrepareSwapchainUses(attachmentCount);
        CpuDescriptorHandle* colors = stackalloc CpuDescriptorHandle[desc.Colors.Length];
        try
        {
            for (int index = 0; index < desc.Colors.Length; index++)
            {
                ref readonly ColorAttachmentDesc attachment = ref desc.Colors[index];
                colors[index] = command.Capture(
                    attachment.View,
                    command.RenderingColorSource(index));
                if (attachment.ResolveView is ColorAttachmentView resolveView)
                {
                    _ = command.Capture(
                        resolveView,
                        command.RenderingColorResolveDestination(index)!);
                }
            }
            CpuDescriptorHandle depthStencil = default;
            CpuDescriptorHandle* depthStencilPointer = null;
            if (desc.DepthStencil is DepthStencilAttachmentDesc preparedDepthAttachment)
            {
                depthStencil = command.Capture(
                    preparedDepthAttachment.View,
                    command.RenderingDepthStencilResource!);
                depthStencilPointer = &depthStencil;
            }
            float* clearValues = stackalloc float[4];

            D3D12CommandListFastCalls.SetRenderTargets(
                command.List,
                checked((uint)desc.Colors.Length),
                colors,
                depthStencilPointer);

            for (int index = 0; index < desc.Colors.Length; index++)
            {
                ref readonly ColorAttachmentDesc attachment = ref desc.Colors[index];
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
                        D3D12CommandListFastCalls.ClearRenderTargetView(
                            command.List,
                            colors[index],
                            clearValues);
                        break;
                    case LoadType.Discard:
                        EmitDiscardResource(
                            command,
                            command.RenderingColorSource(index),
                            command.RenderingColorSourceRange(index));
                        break;
                    default:
                        throw new ArgumentOutOfRangeException(nameof(desc));
                }
            }

            if (desc.DepthStencil is DepthStencilAttachmentDesc depthStencilAttachment)
            {
                ClearFlags clearFlags = 0;
                if (depthStencilAttachment.DepthLoad == LoadType.Clear)
                    clearFlags |= ClearFlags.Depth;
                else if (depthStencilAttachment.DepthLoad == LoadType.Discard)
                    EmitDiscardResource(
                        command,
                        command.RenderingDepthStencilResource!,
                        command.RenderingDepthStencilRange with
                        {
                            Aspects = TextureAspects.Depth,
                        });
                if (depthStencilAttachment.StencilLoad == LoadType.Clear)
                    clearFlags |= ClearFlags.Stencil;
                else if (depthStencilAttachment.StencilLoad == LoadType.Discard)
                    EmitDiscardResource(
                        command,
                        command.RenderingDepthStencilResource!,
                        command.RenderingDepthStencilRange with
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

    private int PrepareRenderingDescription(
        D3D12CommandContext command,
        in RenderingDesc desc)
    {
        command.BeginRenderingPreparation(desc.Colors.Length);
        try
        {
            int attachmentCount = 0;
            for (int index = 0; index < desc.Colors.Length; index++)
            {
                attachmentCount += PrepareColorAttachment(
                    command,
                    index,
                    desc.Colors[index]);
            }
            if (desc.DepthStencil is DepthStencilAttachmentDesc depth)
            {
                PrepareDepthStencilAttachment(command, depth);
                attachmentCount++;
            }
            return attachmentCount;
        }
        catch
        {
            command.CancelRenderingState();
            throw;
        }
    }

    private static void ValidateRenderingDescription(in RenderingDesc desc)
    {
        if (desc.Colors.Length > 8 || desc.Width == 0 || desc.Height == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));

        const RenderingOptions knownOptions =
            RenderingOptions.AllowUnorderedAccessWrites |
            RenderingOptions.Suspending |
            RenderingOptions.Resuming;
        if ((desc.Options & ~knownOptions) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        if ((desc.Options & (RenderingOptions.Suspending | RenderingOptions.Resuming)) != 0)
        {
            throw new NotSupportedException(
                "Suspended rendering scopes are not yet representable by the D3D12 OM encoder.");
        }
    }

    private static bool CanBeginSingleColorRendering(in RenderingDesc desc) =>
        desc.Colors.Length == 1 &&
        desc.DepthStencil is null &&
        desc.Colors[0].ResolveView is null &&
        desc.Colors[0].Store == StoreType.Store;

    [System.Runtime.CompilerServices.SkipLocalsInit]
    private void BeginSingleColorRendering(
        D3D12CommandContext command,
        in ColorAttachmentDesc attachment)
    {
        if (attachment.Load is not (LoadType.Load or LoadType.Clear or LoadType.Discard))
            throw new ArgumentOutOfRangeException(nameof(attachment));

        D3D12TextureResource source = command.RequireAttachment(attachment.View);
        TextureSubresourceRange range = attachment.View.Description.Range;
        if (attachment.Load == LoadType.Discard)
            ValidateDiscardView(source, range);

        command.BeginRenderingPreparation(0);
        command.PrepareCaptures(1, 1, 1);
        command.PrepareSwapchainUses(1);
        try
        {
            CpuDescriptorHandle color = command.Capture(attachment.View, source);
            D3D12CommandListFastCalls.SetRenderTargets(
                command.List,
                1,
                &color,
                null);

            switch (attachment.Load)
            {
                case LoadType.Load:
                    break;
                case LoadType.Clear:
                    Vector4 clear = attachment.ClearValue;
                    float* clearValues = stackalloc float[4];
                    clearValues[0] = clear.X;
                    clearValues[1] = clear.Y;
                    clearValues[2] = clear.Z;
                    clearValues[3] = clear.W;
                    D3D12CommandListFastCalls.ClearRenderTargetView(
                        command.List,
                        color,
                        clearValues);
                    break;
                case LoadType.Discard:
                    EmitDiscardResource(
                        command,
                        source,
                        range);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(attachment));
            }
            command.CommitRenderingState();
        }
        catch
        {
            command.CancelRenderingState();
            throw;
        }
    }

    private int PrepareColorAttachment(
        D3D12CommandContext command,
        int index,
        in ColorAttachmentDesc attachment)
    {
        if (attachment.Load is not (LoadType.Load or LoadType.Clear or LoadType.Discard) ||
            attachment.Store is not (StoreType.Store or StoreType.Discard))
            throw new ArgumentOutOfRangeException(nameof(attachment));
        D3D12TextureResource source = command.RequireAttachment(attachment.View);
        D3D12TextureResource? destination = null;
        TextureSubresourceRange destinationRange = default;
        ResolveMode resolveMode = default;
        if (attachment.ResolveView is ColorAttachmentView resolveView)
        {
            destination = command.RequireAttachment(resolveView);
            destinationRange = resolveView.Description.Range;
            resolveMode = ValidateColorResolve(attachment, source, destination);
        }
        if (attachment.Load == LoadType.Discard || attachment.Store == StoreType.Discard)
            ValidateDiscardView(source, attachment.View.Description.Range);
        command.PrepareRenderingColor(
            index,
            source,
            attachment.View.Description.Range,
            destination,
            destinationRange,
            attachment.View.Description.Format,
            resolveMode,
            attachment.Store == StoreType.Discard);
        return destination is null ? 1 : 2;
    }

    private void PrepareDepthStencilAttachment(
        D3D12CommandContext command,
        in DepthStencilAttachmentDesc depth)
    {
        if (depth.DepthLoad is not (LoadType.Load or LoadType.Clear or LoadType.Discard) ||
            depth.DepthStore is not (StoreType.Store or StoreType.Discard) ||
            depth.StencilLoad is not (LoadType.Load or LoadType.Clear or LoadType.Discard) ||
            depth.StencilStore is not (StoreType.Store or StoreType.Discard))
        {
            throw new ArgumentOutOfRangeException(nameof(depth));
        }

        D3D12TextureResource resource = command.RequireAttachment(depth.View);
        TextureSubresourceRange range = depth.View.Description.Range;
        if (depth.DepthLoad == LoadType.Discard || depth.DepthStore == StoreType.Discard)
        {
            ValidateDiscardView(
                resource,
                range with { Aspects = TextureAspects.Depth });
        }
        if (depth.StencilLoad == LoadType.Discard || depth.StencilStore == StoreType.Discard)
        {
            ValidateDiscardView(
                resource,
                range with { Aspects = TextureAspects.Stencil });
        }
        command.PrepareRenderingDepthStencil(
            resource,
            range,
            depth.DepthStore == StoreType.Discard,
            depth.StencilStore == StoreType.Discard);
    }

    public void EndRendering(CommandContext context)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        command.RequireRenderingOpen();

        for (int index = 0; index < command.RenderingColorCount; index++)
        {
            if (command.RenderingColorResolveDestination(index) is not null)
                EmitColorResolve(command, index);
            if (command.RenderingColorDiscard(index))
            {
                EmitDiscardResource(
                    command,
                    command.RenderingColorSource(index),
                    command.RenderingColorSourceRange(index));
            }
        }

        if (command.RenderingDepthStencilResource is D3D12TextureResource depthStencil)
        {
            if (command.RenderingDepthDiscard)
                EmitDiscardResource(
                    command,
                    depthStencil,
                    command.RenderingDepthStencilRange with
                    {
                        Aspects = TextureAspects.Depth,
                    });
            if (command.RenderingStencilDiscard)
                EmitDiscardResource(
                    command,
                    depthStencil,
                    command.RenderingDepthStencilRange with
                    {
                        Aspects = TextureAspects.Stencil,
                    });
        }

        command.CloseRendering();
    }

    private ResolveMode ValidateColorResolve(
        in ColorAttachmentDesc attachment,
        D3D12TextureResource source,
        D3D12TextureResource destination)
    {
        ColorAttachmentView resolveView = attachment.ResolveView ??
            throw new ArgumentException("A resolve attachment is missing its destination view.");

        TextureSubresourceRange sourceRange = attachment.View.Description.Range;
        TextureSubresourceRange destinationRange = resolveView.Description.Range;
        ResolveTextureRange(source.Info, sourceRange, TextureAspects.Color);
        ResolveTextureRange(destination.Info, destinationRange, TextureAspects.Color);
        ResolveMode mode = ToResolveMode(
            attachment.ResolveType,
            attachment.View.Description.Format);

        if (source.Info.Dimension != TextureDimension.Texture2D ||
            destination.Info.Dimension != TextureDimension.Texture2D ||
            source.Info.SampleCount <= 1 ||
            destination.Info.SampleCount != 1 ||
            sourceRange.MipLevelCount != 1 ||
            destinationRange.MipLevelCount != 1 ||
            sourceRange.ArrayLayerCount != destinationRange.ArrayLayerCount ||
            attachment.View.Description.Format != resolveView.Description.Format ||
            MipDimension(source.Info.Width, sourceRange.FirstMipLevel) !=
                MipDimension(destination.Info.Width, destinationRange.FirstMipLevel) ||
            MipDimension(source.Info.Height, sourceRange.FirstMipLevel) !=
                MipDimension(destination.Info.Height, destinationRange.FirstMipLevel))
        {
            throw new ArgumentException(
                "A rendering resolve requires equally sized compatible 2D array ranges from " +
                "multisampled source storage to single-sampled destination storage.");
        }
        return mode;
    }

    private void EmitColorResolve(
        D3D12CommandContext command,
        int index)
    {
        D3D12TextureResource source = command.RenderingColorSource(index);
        D3D12TextureResource destination =
            command.RenderingColorResolveDestination(index)!;
        TextureSubresourceRange sourceRange =
            command.RenderingColorSourceRange(index);
        TextureSubresourceRange destinationRange =
            command.RenderingColorDestinationRange(index);
        for (uint layer = 0; layer < sourceRange.ArrayLayerCount; layer++)
        {
            command.List->ResolveSubresourceRegion(
                destination.Native,
                NativeSubresource(
                    destination.Info,
                    destinationRange.FirstMipLevel,
                    checked(destinationRange.FirstArrayLayer + layer),
                    destinationRange.Aspects),
                0,
                0,
                source.Native,
                NativeSubresource(
                    source.Info,
                    sourceRange.FirstMipLevel,
                    checked(sourceRange.FirstArrayLayer + layer),
                    sourceRange.Aspects),
                null,
                FormatMappings.ToDxgi(command.RenderingColorFormat(index)),
                command.RenderingColorResolveMode(index));
        }
    }

    private void ValidateDiscardView(
        D3D12TextureResource native,
        in TextureSubresourceRange range)
    {
        TextureAspects allowed = FormatMappings.IsDepthStencil(native.Info.Format)
            ? TextureAspects.Depth | TextureAspects.Stencil
            : TextureAspects.Color;
        ResolveTextureRange(native.Info, range, allowed);
        if (native.Info.Dimension != TextureDimension.Texture3D)
            return;

        for (uint mip = range.FirstMipLevel;
             mip < checked(range.FirstMipLevel + range.MipLevelCount);
             mip++)
        {
            uint mipDepth = MipDimension(native.Info.Depth, mip);
            if (range.FirstArrayLayer != 0 || range.ArrayLayerCount != mipDepth)
            {
                throw new NotSupportedException(
                    "D3D12 cannot express a partial W-slice DiscardResource range for a 3D Texture.");
            }
        }
    }

    private void EmitDiscardResource(
        D3D12CommandContext command,
        D3D12TextureResource native,
        in TextureSubresourceRange range)
    {
        if (native.Info.Dimension == TextureDimension.Texture3D)
        {
            for (uint mip = range.FirstMipLevel;
                 mip < checked(range.FirstMipLevel + range.MipLevelCount);
                 mip++)
            {
                DiscardRegion nativeRange = new(
                    0,
                    null,
                    NativeSubresource(native.Info, mip, 0, range.Aspects),
                    1);
                command.List->DiscardResource(native.Native, &nativeRange);
            }
            return;
        }

        uint plane = FormatMappings.PlaneIndex(native.Info.Format, range.Aspects);
        for (uint layer = range.FirstArrayLayer;
             layer < checked(range.FirstArrayLayer + range.ArrayLayerCount);
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
        private const byte RenderingResolve = 1;
        private const byte RenderingDiscard = 2;
        private readonly D3D12TextureResource?[] _renderingColorSources =
            new D3D12TextureResource?[8];
        private readonly D3D12TextureResource?[] _renderingColorDestinations =
            new D3D12TextureResource?[8];
        private readonly TextureSubresourceRange[] _renderingColorSourceRanges =
            new TextureSubresourceRange[8];
        private readonly TextureSubresourceRange[] _renderingColorDestinationRanges =
            new TextureSubresourceRange[8];
        private readonly Format[] _renderingColorFormats = new Format[8];
        private readonly ResolveMode[] _renderingColorResolveModes = new ResolveMode[8];
        private readonly byte[] _renderingColorActions = new byte[8];
        private int _renderingColorCount;
        private D3D12TextureResource? _renderingDepthStencilResource;
        private TextureSubresourceRange _renderingDepthStencilRange;
        private bool _renderingDepthDiscard;
        private bool _renderingStencilDiscard;
        private bool _renderingPrepared;
        private bool _renderingOpen;

        internal int RenderingColorCount => _renderingColorCount;
        internal D3D12TextureResource? RenderingDepthStencilResource =>
            _renderingDepthStencilResource;
        internal TextureSubresourceRange RenderingDepthStencilRange =>
            _renderingDepthStencilRange;
        internal bool RenderingDepthDiscard => _renderingDepthDiscard;
        internal bool RenderingStencilDiscard => _renderingStencilDiscard;

        internal D3D12TextureResource RenderingColorSource(int index) =>
            _renderingColorSources[index]!;

        internal D3D12TextureResource? RenderingColorResolveDestination(int index) =>
            _renderingColorDestinations[index];

        internal TextureSubresourceRange RenderingColorSourceRange(int index) =>
            _renderingColorSourceRanges[index];

        internal TextureSubresourceRange RenderingColorDestinationRange(int index) =>
            _renderingColorDestinationRanges[index];

        internal Format RenderingColorFormat(int index) =>
            _renderingColorFormats[index];

        internal ResolveMode RenderingColorResolveMode(int index) =>
            _renderingColorResolveModes[index];

        internal bool RenderingColorDiscard(int index) =>
            (_renderingColorActions[index] & RenderingDiscard) != 0;

        internal void RequireRenderingClosed()
        {
            if (_renderingPrepared || _renderingOpen)
                throw new InvalidOperationException("A rendering scope is already active.");
        }

        internal void RequireRenderingOpen()
        {
            if (!_renderingOpen || _renderingPrepared)
                throw new InvalidOperationException("No rendering scope is active.");
        }

        internal void BeginRenderingPreparation(int colorCount)
        {
            if ((uint)colorCount > 8u)
                throw new ArgumentOutOfRangeException(nameof(colorCount));
            _renderingColorCount = colorCount;
            _renderingPrepared = true;
        }

        internal void PrepareRenderingColor(
            int index,
            D3D12TextureResource source,
            in TextureSubresourceRange sourceRange,
            D3D12TextureResource? destination,
            in TextureSubresourceRange destinationRange,
            Format format,
            ResolveMode resolveMode,
            bool discard)
        {
            _renderingColorSources[index] = source;
            _renderingColorSourceRanges[index] = sourceRange;
            _renderingColorDestinations[index] = destination;
            _renderingColorDestinationRanges[index] = destinationRange;
            _renderingColorFormats[index] = format;
            _renderingColorResolveModes[index] = resolveMode;
            _renderingColorActions[index] = (byte)(
                (destination is null ? 0 : RenderingResolve) |
                (discard ? RenderingDiscard : 0));
        }

        internal void PrepareRenderingDepthStencil(
            D3D12TextureResource resource,
            in TextureSubresourceRange range,
            bool discardDepth,
            bool discardStencil)
        {
            _renderingDepthStencilResource = resource;
            _renderingDepthStencilRange = range;
            _renderingDepthDiscard = discardDepth;
            _renderingStencilDiscard = discardStencil;
        }

        internal void CommitRenderingState()
        {
            if (!_renderingPrepared)
                throw new InvalidOperationException("The rendering scope was not prepared.");
            _renderingPrepared = false;
            _renderingOpen = true;
        }

        internal void CloseRendering() => ResetRenderingState();

        internal void CancelRenderingState() => ResetRenderingState();

        internal void ResetRenderingState()
        {
            Array.Clear(_renderingColorSources, 0, _renderingColorCount);
            Array.Clear(_renderingColorDestinations, 0, _renderingColorCount);
            Array.Clear(_renderingColorActions, 0, _renderingColorCount);
            _renderingColorCount = 0;
            _renderingDepthStencilResource = null;
            _renderingDepthStencilRange = default;
            _renderingDepthDiscard = false;
            _renderingStencilDiscard = false;
            _renderingPrepared = false;
            _renderingOpen = false;
        }
    }
}
