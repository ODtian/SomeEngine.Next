using System.Numerics;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;
internal sealed unsafe partial class D3D12Backend
{
    public void BeginRendering(CommandContext context, in RenderingDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        command.RenderingArea = new Silk.NET.Maths.Box2D<int>(checked((int)desc.X), checked((int)desc.Y), checked((int)(desc.X + desc.Width)), checked((int)(desc.Y + desc.Height)));
        if (CanBeginSingleColorRendering(desc))
        {
            BeginSingleColorRendering(command, desc.Colors[0]);
            return;
        }

        var area = command.RenderingArea;
        int attachmentCount = PrepareRenderingDescription(command, desc);
        command.PrepareSwapchainUses(attachmentCount);
        CpuDescriptorHandle* colors = stackalloc CpuDescriptorHandle[desc.Colors.Length];
        try
        {
            for (int index = 0; index < desc.Colors.Length; index++)
            {
                ref readonly ColorAttachmentDesc attachment = ref desc.Colors[index];
                colors[index] = command.GetAttachmentDescriptor(attachment.View, command.RenderingColorSource(index));
                if (attachment.ResolveView is ColorAttachmentView resolveView)
                {
                    _ = command.GetAttachmentDescriptor(resolveView, command.RenderingColorResolveDestination(index)!);
                }
            }

            CpuDescriptorHandle depthStencil = default;
            CpuDescriptorHandle* depthStencilPointer = null;
            if (desc.DepthStencil is DepthStencilAttachmentDesc preparedDepthAttachment)
            {
                depthStencil = command.GetAttachmentDescriptor(preparedDepthAttachment.View, command.RenderingDepthStencilResource!);
                depthStencilPointer = &depthStencil;
            }

            float* clearValues = stackalloc float[4];
            D3D12CommandListFastCalls.SetRenderTargets(command, command.List, checked((uint)desc.Colors.Length), colors, depthStencilPointer);
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
                        CommandExecutionTrace.ClearRenderTargetView(command, command.List, colors[index], clearValues, 1, &area);
                        break;
                    case LoadType.Discard:
                        EmitDiscardResource(command, command.RenderingColorSource(index), command.RenderingColorSourceRange(index));
                        break;
                    default:
                        break;
                }
            }

            if (desc.DepthStencil is DepthStencilAttachmentDesc depthStencilAttachment)
                ApplyDepthStencilLoad(command, depthStencilAttachment, depthStencil, &area);
            command.CommitRenderingState();
        }
        catch
        {
            command.CancelRenderingState();
            throw;
        }
    }

    private void ApplyDepthStencilLoad(
        D3D12CommandContext command,
        in DepthStencilAttachmentDesc attachment,
        CpuDescriptorHandle descriptor,
        Silk.NET.Maths.Box2D<int>* area
    )
    {
        ClearFlags clearFlags = 0;
        TextureSubresourceRange range = command.RenderingDepthStencilRange;
        bool hasDepth = (range.Aspects & TextureAspects.Depth) != 0;
        bool hasStencil = (range.Aspects & TextureAspects.Stencil) != 0;
        if (hasDepth && attachment.DepthLoad == LoadType.Clear)
            clearFlags |= ClearFlags.Depth;
        else if (hasDepth && attachment.DepthLoad == LoadType.Discard)
            EmitDiscardResource(command, command.RenderingDepthStencilResource!, range with { Aspects = TextureAspects.Depth });
        if (hasStencil && attachment.StencilLoad == LoadType.Clear)
            clearFlags |= ClearFlags.Stencil;
        else if (hasStencil && attachment.StencilLoad == LoadType.Discard)
            EmitDiscardResource(command, command.RenderingDepthStencilResource!, range with { Aspects = TextureAspects.Stencil });
        if (clearFlags == 0)
            return;
        CommandExecutionTrace.ClearDepthStencilView(command, command.List, descriptor, clearFlags, attachment.ClearDepth, attachment.ClearStencil, 1, area);
    }

    private int PrepareRenderingDescription(D3D12CommandContext command, in RenderingDesc desc)
    {
        command.BeginRenderingPreparation(desc.Colors.Length);
        try
        {
            int attachmentCount = 0;
            for (int index = 0; index < desc.Colors.Length; index++)
            {
                attachmentCount += PrepareColorAttachment(command, index, desc.Colors[index]);
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

    private static bool CanBeginSingleColorRendering(in RenderingDesc desc) => desc.X == 0 && desc.Y == 0 && desc.Colors.Length == 1 && desc.Width == MipDimension(desc.Colors[0].View.Description.Texture.Info.Width, desc.Colors[0].View.Description.Range.FirstMipLevel) && desc.Height == MipDimension(desc.Colors[0].View.Description.Texture.Info.Height, desc.Colors[0].View.Description.Range.FirstMipLevel) && desc.DepthStencil is null && desc.Colors[0].ResolveView is null && desc.Colors[0].Store == StoreType.Store;
    [System.Runtime.CompilerServices.SkipLocalsInit]
    private void BeginSingleColorRendering(D3D12CommandContext command, in ColorAttachmentDesc attachment)
    {
        D3D12TextureResource source = command.RequireAttachment(attachment.View);
        TextureSubresourceRange range = attachment.View.Description.Range;
        command.BeginRenderingPreparation(0);
        command.PrepareSwapchainUses(1);
        try
        {
            CpuDescriptorHandle color = command.GetAttachmentDescriptor(attachment.View, source);
            D3D12CommandListFastCalls.SetRenderTargets(command, command.List, 1, &color, null);
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
                    D3D12CommandListFastCalls.ClearRenderTargetView(command, command.List, color, clearValues);
                    break;
                case LoadType.Discard:
                    EmitDiscardResource(command, source, range);
                    break;
                default:
                    break;
            }

            command.CommitRenderingState();
        }
        catch
        {
            command.CancelRenderingState();
            throw;
        }
    }

    private int PrepareColorAttachment(D3D12CommandContext command, int index, in ColorAttachmentDesc attachment)
    {
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

        command.PrepareRenderingColor(
            index,
            source,
            attachment.View.Description.Range,
            destination,
            destinationRange,
            attachment.View.Description.Format,
            resolveMode,
            attachment.Store == StoreType.Discard
        );
        return destination is null ? 1 : 2;
    }

    private void PrepareDepthStencilAttachment(D3D12CommandContext command, in DepthStencilAttachmentDesc depth)
    {
        D3D12TextureResource resource = command.RequireAttachment(depth.View);
        TextureSubresourceRange range = depth.View.Description.Range;
        bool hasDepth = (range.Aspects & TextureAspects.Depth) != 0;
        bool hasStencil = (range.Aspects & TextureAspects.Stencil) != 0;
        bool discardDepth = hasDepth && (depth.DepthLoad == LoadType.Discard || depth.DepthStore == StoreType.Discard);
        bool discardStencil = hasStencil && (depth.StencilLoad == LoadType.Discard || depth.StencilStore == StoreType.Discard);
        if (discardDepth)
        {
        }

        if (discardStencil)
        {
        }

        command.PrepareRenderingDepthStencil(
            resource,
            range,
            hasDepth && depth.DepthStore == StoreType.Discard,
            hasStencil && depth.StencilStore == StoreType.Discard
        );
    }

    public void EndRendering(CommandContext context)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        for (int index = 0; index < command.RenderingColorCount; index++)
        {
            if (command.RenderingColorResolveDestination(index)is not null)
                EmitColorResolve(command, index);
            if (command.RenderingColorDiscard(index))
            {
                EmitDiscardResource(command, command.RenderingColorSource(index), command.RenderingColorSourceRange(index));
            }
        }

        if (command.RenderingDepthStencilResource is D3D12TextureResource depthStencil)
        {
            if (command.RenderingDepthDiscard)
                EmitDiscardResource(command, depthStencil, command.RenderingDepthStencilRange with { Aspects = TextureAspects.Depth, });
            if (command.RenderingStencilDiscard)
                EmitDiscardResource(command, depthStencil, command.RenderingDepthStencilRange with { Aspects = TextureAspects.Stencil, });
        }

        command.CloseRendering();
    }

    private ResolveMode ValidateColorResolve(in ColorAttachmentDesc attachment, D3D12TextureResource source, D3D12TextureResource destination)
    {
        ColorAttachmentView resolveView = attachment.ResolveView!;
        TextureSubresourceRange sourceRange = attachment.View.Description.Range;
        TextureSubresourceRange destinationRange = resolveView.Description.Range;
        ResolveMode mode = ToResolveMode(attachment.ResolveType, attachment.View.Description.Format);
        return mode;
    }

    private void EmitColorResolve(D3D12CommandContext command, int index)
    {
        D3D12TextureResource source = command.RenderingColorSource(index);
        D3D12TextureResource destination = command.RenderingColorResolveDestination(index)!;
        TextureSubresourceRange sourceRange = command.RenderingColorSourceRange(index);
        TextureSubresourceRange destinationRange = command.RenderingColorDestinationRange(index);
        var area = command.RenderingArea;
        for (uint layer = 0; layer < sourceRange.ArrayLayerCount; layer++)
        {
            CommandTransferTrace.ResolveSubresourceRegion(command, command.List, destination.Native, NativeSubresource(
                destination.Info,
                destinationRange.FirstMipLevel,
                checked(destinationRange.FirstArrayLayer + layer),
                destinationRange.Aspects
            ), checked((uint)area.Min.X), checked((uint)area.Min.Y), source.Native, NativeSubresource(source.Info, sourceRange.FirstMipLevel, checked(sourceRange.FirstArrayLayer + layer), sourceRange.Aspects), &area, FormatMappings.ToDxgi(command.RenderingColorFormat(index)), command.RenderingColorResolveMode(index));
        }
    }

    private void EmitDiscardResource(D3D12CommandContext command, D3D12TextureResource native, in TextureSubresourceRange range)
    {
        var area = command.RenderingArea;
        if (native.Info.Dimension == TextureDimension.Texture3D)
        {
            for (uint mip = range.FirstMipLevel; mip < checked(range.FirstMipLevel + range.MipLevelCount); mip++)
            {
                DiscardRegion nativeRange = new(1, &area, NativeSubresource(native.Info, mip, 0, range.Aspects), 1);
                CommandExecutionTrace.DiscardResource(command, command.List, native.Native, &nativeRange);
            }

            return;
        }

        uint plane = FormatMappings.PlaneIndex(native.Info.Format, range.Aspects);
        for (uint layer = range.FirstArrayLayer; layer < checked(range.FirstArrayLayer + range.ArrayLayerCount); layer++)
        {
            uint first = checked(range.FirstMipLevel + layer * native.Info.MipLevelCount + plane * native.Info.MipLevelCount * native.Info.ArrayLayerCount);
            DiscardRegion nativeRange = new(1, &area, first, range.MipLevelCount);
            CommandExecutionTrace.DiscardResource(command, command.List, native.Native, &nativeRange);
        }
    }

    private sealed partial class D3D12CommandContext
    {
        private const byte RenderingResolve = 1;
        private const byte RenderingDiscard = 2;
        private readonly D3D12TextureResource? [] _renderingColorSources = new D3D12TextureResource? [8];
        private readonly D3D12TextureResource? [] _renderingColorDestinations = new D3D12TextureResource? [8];
        private readonly TextureSubresourceRange[] _renderingColorSourceRanges = new TextureSubresourceRange[8];
        private readonly TextureSubresourceRange[] _renderingColorDestinationRanges = new TextureSubresourceRange[8];
        private readonly Format[] _renderingColorFormats = new Format[8];
        private readonly ResolveMode[] _renderingColorResolveModes = new ResolveMode[8];
        private readonly byte[] _renderingColorActions = new byte[8];
        private int _renderingColorCount;
        private D3D12TextureResource? _renderingDepthStencilResource;
        private TextureSubresourceRange _renderingDepthStencilRange;
        private bool _renderingDepthDiscard;
        private bool _renderingStencilDiscard;
        private bool _renderingAttachmentsReady;
        private bool _renderingOpen;
        internal Silk.NET.Maths.Box2D<int> RenderingArea { get; set; }
        internal int RenderingColorCount => _renderingColorCount;
        internal D3D12TextureResource? RenderingDepthStencilResource => _renderingDepthStencilResource;
        internal TextureSubresourceRange RenderingDepthStencilRange => _renderingDepthStencilRange;
        internal bool RenderingDepthDiscard => _renderingDepthDiscard;
        internal bool RenderingStencilDiscard => _renderingStencilDiscard;

        internal D3D12TextureResource RenderingColorSource(int index) => _renderingColorSources[index]!;
        internal D3D12TextureResource? RenderingColorResolveDestination(int index) => _renderingColorDestinations[index];
        internal TextureSubresourceRange RenderingColorSourceRange(int index) => _renderingColorSourceRanges[index];
        internal TextureSubresourceRange RenderingColorDestinationRange(int index) => _renderingColorDestinationRanges[index];
        internal Format RenderingColorFormat(int index) => _renderingColorFormats[index];
        internal ResolveMode RenderingColorResolveMode(int index) => _renderingColorResolveModes[index];
        internal bool RenderingColorDiscard(int index) => (_renderingColorActions[index] & RenderingDiscard) != 0;
        internal void BeginRenderingPreparation(int colorCount)
        {
            _renderingColorCount = colorCount;
            _renderingAttachmentsReady = true;
        }

        internal void PrepareRenderingColor(
            int index,
            D3D12TextureResource source,
            in TextureSubresourceRange sourceRange,
            D3D12TextureResource? destination,
            in TextureSubresourceRange destinationRange,
            Format format,
            ResolveMode resolveMode,
            bool discard
        )
        {
            _renderingColorSources[index] = source;
            _renderingColorSourceRanges[index] = sourceRange;
            _renderingColorDestinations[index] = destination;
            _renderingColorDestinationRanges[index] = destinationRange;
            _renderingColorFormats[index] = format;
            _renderingColorResolveModes[index] = resolveMode;
            _renderingColorActions[index] = (byte)((destination is null ? 0 : RenderingResolve) | (discard ? RenderingDiscard : 0));
        }

        internal void PrepareRenderingDepthStencil(
            D3D12TextureResource resource,
            in TextureSubresourceRange range,
            bool discardDepth,
            bool discardStencil
        )
        {
            _renderingDepthStencilResource = resource;
            _renderingDepthStencilRange = range;
            _renderingDepthDiscard = discardDepth;
            _renderingStencilDiscard = discardStencil;
        }

        internal void CommitRenderingState()
        {
            _renderingAttachmentsReady = false;
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
            _renderingAttachmentsReady = false;
            _renderingOpen = false;
        }
    }
}
