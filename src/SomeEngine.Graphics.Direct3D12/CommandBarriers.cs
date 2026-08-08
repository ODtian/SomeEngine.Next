using Silk.NET.Direct3D12;
using NativeBufferBarrier = Silk.NET.Direct3D12.BufferBarrier;
using NativeTextureBarrier = Silk.NET.Direct3D12.TextureBarrier;
using NativeResourceBarrier = Silk.NET.Direct3D12.ResourceBarrier;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Barrier(CommandContext context, in MemoryBarrier barrier)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.EnhancedBarriers)
        {
            GlobalBarrier native = new()
            {
                SyncBefore = ToBarrierSync(barrier.SyncBefore),
                SyncAfter = ToBarrierSync(barrier.SyncAfter),
                AccessBefore = ToBarrierAccess(barrier.AccessBefore),
                AccessAfter = ToBarrierAccess(barrier.AccessAfter),
            };
            BarrierGroup group = new()
            {
                Type = BarrierType.Global,
                NumBarriers = 1,
                Anonymous = new BarrierGroupUnion { PGlobalBarriers = &native },
            };
            command.List->Barrier(1, &group);
            return;
        }

        EncodeLegacyMemoryBarrier(command, barrier);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void EncodeLegacyMemoryBarrier(
        D3D12CommandContext command,
        in MemoryBarrier barrier)
    {
        NativeResourceBarrier legacy = NeedsUavOrdering(
            barrier.AccessBefore,
            barrier.AccessAfter)
            ? CreateLegacyUavBarrier(null)
            : CreateLegacyAliasingBarrier(null, null);
        command.List->ResourceBarrier(1, &legacy);
    }

    public void Barrier(CommandContext context, in BufferBarrier barrier)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12Buffer buffer = NativeCast.Buffer(barrier.Buffer);
        command.Capture(buffer);
        if (command.EnhancedBarriers)
        {
            NativeBufferBarrier native = new(
                ToBarrierSync(barrier.SyncBefore),
                ToBarrierSync(barrier.SyncAfter),
                ToBarrierAccess(barrier.AccessBefore),
                ToBarrierAccess(barrier.AccessAfter),
                buffer.Native,
                0,
                buffer.Info.Size);
            BarrierGroup group = new(BarrierType.Buffer, 1, pBufferBarriers: &native);
            command.List->Barrier(1, &group);
            return;
        }

        NativeResourceBarrier legacy = CreateLegacyTransition(
            buffer.Native,
            ToLegacyState(command.QueueType, barrier.SyncBefore, barrier.AccessBefore),
            ToLegacyState(command.QueueType, barrier.SyncAfter, barrier.AccessAfter),
            uint.MaxValue,
            ResourceBarrierFlags.None);
        command.List->ResourceBarrier(1, &legacy);
    }

    public void Barrier(CommandContext context, in TextureBarrier barrier)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12TextureResource texture = NativeCast.Texture(barrier.Texture);
        command.Capture(texture);
        if (command.EnhancedBarriers)
        {
            EncodeEnhancedTextureBarriers(
                command,
                texture,
                barrier.Range,
                barrier.SyncBefore,
                barrier.SyncAfter,
                barrier.AccessBefore,
                barrier.AccessAfter,
                barrier.LayoutBefore,
                barrier.LayoutAfter,
                TextureBarrierFlags.None);
            command.RecordSwapchainState(texture, barrier.LayoutAfter, barrier.AccessAfter);
            return;
        }

        EncodeLegacyTextureTransitions(
            command,
            texture,
            barrier.Range,
            ToLegacyState(
                command.QueueType,
                barrier.SyncBefore,
                barrier.LayoutBefore,
                barrier.AccessBefore),
            ToLegacyState(
                command.QueueType,
                barrier.SyncAfter,
                barrier.LayoutAfter,
                barrier.AccessAfter));
        command.RecordSwapchainState(texture, barrier.LayoutAfter, barrier.AccessAfter);
    }

    public void Barrier(CommandContext context, in AliasingBarrier barrier)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.EnhancedBarriers)
        {
            foreach (ref readonly AliasingResource resource in barrier.Before)
                EncodeEnhancedAliasing(command, resource, activate: false);
            foreach (ref readonly AliasingResource resource in barrier.After)
                EncodeEnhancedAliasing(command, resource, activate: true);
            return;
        }

        if (barrier.Before.IsEmpty || barrier.After.IsEmpty)
        {
            NativeResourceBarrier native = CreateLegacyAliasingBarrier(
                barrier.Before.IsEmpty ? null : GetNativeResource(barrier.Before[0].Resource, command),
                barrier.After.IsEmpty ? null : GetNativeResource(barrier.After[0].Resource, command));
            command.List->ResourceBarrier(1, &native);
            return;
        }

        foreach (ref readonly AliasingResource before in barrier.Before)
        foreach (ref readonly AliasingResource after in barrier.After)
        {
            NativeResourceBarrier native = CreateLegacyAliasingBarrier(
                GetNativeResource(before.Resource, command),
                GetNativeResource(after.Resource, command));
            command.List->ResourceBarrier(1, &native);
        }
    }

    public void Barrier(CommandContext context, in QueueRelease barrier)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (barrier.Resource is D3D12Buffer buffer)
        {
            command.Capture(buffer);
            if (command.EnhancedBarriers)
            {
                NativeBufferBarrier native = new(
                    ToBarrierSync(barrier.Sync),
                    BarrierSync.None,
                    ToBarrierAccess(barrier.Access),
                    BarrierAccess.NoAccess,
                    buffer.Native,
                    0,
                    buffer.Info.Size);
                BarrierGroup group = new(BarrierType.Buffer, 1, pBufferBarriers: &native);
                command.List->Barrier(1, &group);
            }
            else
            {
                NativeResourceBarrier native = CreateLegacyTransition(
                    buffer.Native,
                    ToLegacyState(command.QueueType, barrier.Sync, barrier.Access),
                    ResourceStates.Common,
                    uint.MaxValue,
                    ResourceBarrierFlags.None);
                command.List->ResourceBarrier(1, &native);
            }
            return;
        }

        D3D12TextureResource texture = NativeCast.Texture((Texture)barrier.Resource);
        command.Capture(texture);
        TextureSubresourceRange range = barrier.TextureRange
            ?? throw new ArgumentException("A Texture release requires a subresource range.", nameof(barrier));
        TextureLayout layout = barrier.Layout
            ?? throw new ArgumentException("A Texture release requires a layout.", nameof(barrier));
        if (command.EnhancedBarriers)
        {
            EncodeEnhancedTextureBarriers(
                command,
                texture,
                range,
                barrier.Sync,
                PipelineSync.None,
                barrier.Access,
                ResourceAccess.NoAccess,
                layout,
                TextureLayout.Common,
                TextureBarrierFlags.None);
            command.RecordSwapchainState(
                texture,
                TextureLayout.Common,
                ResourceAccess.NoAccess);
        }
        else
        {
            EncodeLegacyTextureTransitions(
                command,
                texture,
                range,
                ToLegacyState(command.QueueType, barrier.Sync, layout, barrier.Access),
                ResourceStates.Common);
            command.RecordSwapchainState(
                texture,
                TextureLayout.Common,
                ResourceAccess.NoAccess);
        }
    }

    public void Barrier(CommandContext context, in QueueAcquire barrier)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (barrier.Resource is D3D12Buffer buffer)
        {
            command.Capture(buffer);
            if (command.EnhancedBarriers)
            {
                NativeBufferBarrier native = new(
                    BarrierSync.None,
                    ToBarrierSync(barrier.Sync),
                    BarrierAccess.NoAccess,
                    ToBarrierAccess(barrier.Access),
                    buffer.Native,
                    0,
                    buffer.Info.Size);
                BarrierGroup group = new(BarrierType.Buffer, 1, pBufferBarriers: &native);
                command.List->Barrier(1, &group);
            }
            else
            {
                NativeResourceBarrier native = CreateLegacyTransition(
                    buffer.Native,
                    ResourceStates.Common,
                    ToLegacyState(command.QueueType, barrier.Sync, barrier.Access),
                    uint.MaxValue,
                    ResourceBarrierFlags.None);
                command.List->ResourceBarrier(1, &native);
            }
            return;
        }

        D3D12TextureResource texture = NativeCast.Texture((Texture)barrier.Resource);
        command.Capture(texture);
        TextureSubresourceRange range = barrier.TextureRange
            ?? throw new ArgumentException("A Texture acquire requires a subresource range.", nameof(barrier));
        TextureLayout layout = barrier.Layout
            ?? throw new ArgumentException("A Texture acquire requires a layout.", nameof(barrier));
        if (command.EnhancedBarriers)
        {
            EncodeEnhancedTextureBarriers(
                command,
                texture,
                range,
                PipelineSync.None,
                barrier.Sync,
                ResourceAccess.NoAccess,
                barrier.Access,
                TextureLayout.Common,
                layout,
                TextureBarrierFlags.None);
            command.RecordSwapchainState(texture, layout, barrier.Access);
        }
        else
        {
            EncodeLegacyTextureTransitions(
                command,
                texture,
                range,
                ResourceStates.Common,
                ToLegacyState(command.QueueType, barrier.Sync, layout, barrier.Access));
            command.RecordSwapchainState(texture, layout, barrier.Access);
        }
    }

    private static void EncodeEnhancedTextureBarriers(
        D3D12CommandContext command,
        D3D12TextureResource texture,
        in TextureSubresourceRange range,
        PipelineSync syncBefore,
        PipelineSync syncAfter,
        ResourceAccess accessBefore,
        ResourceAccess accessAfter,
        TextureLayout layoutBefore,
        TextureLayout layoutAfter,
        TextureBarrierFlags flags)
    {
        ResolveEnhancedTextureLayouts(
            command.QueueType,
            layoutBefore,
            layoutAfter,
            out BarrierLayout nativeLayoutBefore,
            out BarrierLayout nativeLayoutAfter);
        Span<TextureAspects> aspects = stackalloc TextureAspects[3];
        int aspectCount = ExpandBarrierAspects(texture.Info, range, aspects);
        NativeTextureBarrier* native = stackalloc NativeTextureBarrier[aspectCount];
        for (int index = 0; index < aspectCount; index++)
        {
            native[index] = CreateEnhancedTextureBarrier(
                texture,
                range with { Aspects = aspects[index] },
                syncBefore,
                syncAfter,
                accessBefore,
                accessAfter,
                nativeLayoutBefore,
                nativeLayoutAfter,
                flags);
        }
        BarrierGroup group = new(
            BarrierType.Texture,
            checked((uint)aspectCount),
            pTextureBarriers: native);
        command.List->Barrier(1, &group);
    }

    private static void EncodeLegacyTextureTransitions(
        D3D12CommandContext command,
        D3D12TextureResource texture,
        in TextureSubresourceRange range,
        ResourceStates before,
        ResourceStates after)
    {
        Span<TextureAspects> aspects = stackalloc TextureAspects[3];
        int aspectCount = ExpandBarrierAspects(texture.Info, range, aspects);
        for (int aspectIndex = 0; aspectIndex < aspectCount; aspectIndex++)
        for (uint layer = range.FirstArrayLayer;
             layer < range.FirstArrayLayer + range.ArrayLayerCount;
             layer++)
        for (uint mip = range.FirstMipLevel;
             mip < range.FirstMipLevel + range.MipLevelCount;
             mip++)
        {
            NativeResourceBarrier native = CreateLegacyTransition(
                texture.Native,
                before,
                after,
                NativeSubresource(texture.Info, mip, layer, aspects[aspectIndex]),
                ResourceBarrierFlags.None);
            command.List->ResourceBarrier(1, &native);
        }
    }

    internal static int ExpandBarrierAspects(
        TextureInfo info,
        in TextureSubresourceRange range,
        Span<TextureAspects> destination)
    {
        if (range.MipLevelCount == 0 ||
            range.FirstMipLevel >= info.MipLevelCount ||
            range.MipLevelCount > info.MipLevelCount - range.FirstMipLevel ||
            range.ArrayLayerCount == 0 ||
            range.FirstArrayLayer >= info.ArrayLayerCount ||
            range.ArrayLayerCount > info.ArrayLayerCount - range.FirstArrayLayer)
        {
            throw new ArgumentOutOfRangeException(nameof(range));
        }

        TextureAspects requested = range.Aspects;
        TextureAspects named = requested & (
            TextureAspects.Color |
            TextureAspects.Depth |
            TextureAspects.Stencil);
        TextureAspects planes = requested & (
            TextureAspects.Plane0 |
            TextureAspects.Plane1 |
            TextureAspects.Plane2);
        if (requested == TextureAspects.None ||
            (named != TextureAspects.None && planes != TextureAspects.None))
        {
            throw new ArgumentException(
                "A Texture barrier must use either named aspects or plane aspects.",
                nameof(range));
        }

        int count = 0;
        if (!FormatMappings.IsDepthStencil(info.Format))
        {
            if (requested is not (TextureAspects.Color or TextureAspects.Plane0))
            {
                throw new ArgumentException(
                    $"Texture format {info.Format} exposes Color/Plane0, not {requested}.",
                    nameof(range));
            }
            destination[count++] = requested;
            return count;
        }

        uint planeCount = FormatMappings.PlaneCount(info.Format);
        TextureAspects allowed = named != TextureAspects.None
            ? TextureAspects.Depth | (planeCount > 1 ? TextureAspects.Stencil : TextureAspects.None)
            : TextureAspects.Plane0 | (planeCount > 1 ? TextureAspects.Plane1 : TextureAspects.None);
        if ((requested & ~allowed) != 0)
            throw new ArgumentException("The Texture barrier selects an unavailable aspect.", nameof(range));
        TextureAspects first = named != TextureAspects.None
            ? TextureAspects.Depth
            : TextureAspects.Plane0;
        TextureAspects second = named != TextureAspects.None
            ? TextureAspects.Stencil
            : TextureAspects.Plane1;
        if ((requested & first) != 0)
            destination[count++] = first;
        if ((requested & second) != 0)
            destination[count++] = second;
        return count;
    }

    private static TextureAspects DefaultBarrierAspects(Format format) =>
        !FormatMappings.IsDepthStencil(format)
            ? TextureAspects.Color
            : FormatMappings.PlaneCount(format) == 1
                ? TextureAspects.Depth
                : TextureAspects.Depth | TextureAspects.Stencil;

    private static NativeTextureBarrier CreateEnhancedTextureBarrier(
        D3D12TextureResource texture,
        in TextureSubresourceRange range,
        PipelineSync syncBefore,
        PipelineSync syncAfter,
        ResourceAccess accessBefore,
        ResourceAccess accessAfter,
        BarrierLayout layoutBefore,
        BarrierLayout layoutAfter,
        TextureBarrierFlags flags)
    {
        uint plane = FormatMappings.PlaneIndex(texture.Info.Format, range.Aspects);
        return new NativeTextureBarrier(
            ToBarrierSync(syncBefore),
            ToBarrierSync(syncAfter),
            ToBarrierAccess(accessBefore),
            ToBarrierAccess(accessAfter),
            layoutBefore,
            layoutAfter,
            texture.Native,
            new BarrierSubresourceRange(
                range.FirstMipLevel,
                range.MipLevelCount,
                range.FirstArrayLayer,
                range.ArrayLayerCount,
                plane,
                1),
            flags);
    }

    private static void ResolveEnhancedTextureLayouts(
        QueueType queueType,
        TextureLayout layoutBefore,
        TextureLayout layoutAfter,
        out BarrierLayout nativeLayoutBefore,
        out BarrierLayout nativeLayoutAfter)
    {
        if (queueType != QueueType.Copy)
        {
            nativeLayoutBefore = ToBarrierLayout(layoutBefore);
            nativeLayoutAfter = ToBarrierLayout(layoutAfter);
            return;
        }

        if (layoutBefore == TextureLayout.Undefined &&
            layoutAfter == TextureLayout.Undefined)
        {
            nativeLayoutBefore = BarrierLayout.Undefined;
            nativeLayoutAfter = BarrierLayout.Undefined;
            return;
        }

        if (IsCopyQueueCommonLayout(layoutBefore) &&
            IsCopyQueueCommonLayout(layoutAfter))
        {
            // Enhanced-barrier Copy queues have exactly one usable Texture
            // layout: COMMON. COPY_SOURCE/COPY_DEST remain distinct portable
            // access conditions, but both are encoded in that native layout.
            nativeLayoutBefore = BarrierLayout.Common;
            nativeLayoutAfter = BarrierLayout.Common;
            return;
        }

        throw new InvalidOperationException(
            "A Copy CommandContext cannot perform Texture layout transitions. " +
            "Transfer the Texture through QueueRelease/QueueAcquire so the Copy queue sees COMMON.");
    }

    private static bool IsCopyQueueCommonLayout(TextureLayout layout) => layout is
        TextureLayout.Common or
        TextureLayout.QueueCommon or
        TextureLayout.CopySource or
        TextureLayout.CopyDestination;

    private static void EncodeEnhancedAliasing(
        D3D12CommandContext command,
        in AliasingResource resource,
        bool activate)
    {
        if (resource.Resource is D3D12Buffer buffer)
        {
            command.Capture(buffer);
            NativeBufferBarrier native = activate
                ? new NativeBufferBarrier(
                    BarrierSync.None,
                    BarrierSync.All,
                    BarrierAccess.NoAccess,
                    BarrierAccess.Common,
                    buffer.Native,
                    0,
                    buffer.Info.Size)
                : new NativeBufferBarrier(
                    BarrierSync.All,
                    BarrierSync.None,
                    BarrierAccess.Common,
                    BarrierAccess.NoAccess,
                    buffer.Native,
                    0,
                    buffer.Info.Size);
            BarrierGroup group = new(BarrierType.Buffer, 1, pBufferBarriers: &native);
            command.List->Barrier(1, &group);
            return;
        }

        D3D12TextureResource texture = NativeCast.Texture((Texture)resource.Resource);
        command.Capture(texture);
        TextureSubresourceRange range = resource.TextureRange ?? new TextureSubresourceRange(
            0,
            texture.Info.MipLevelCount,
            0,
            texture.Info.ArrayLayerCount,
            DefaultBarrierAspects(texture.Info.Format));
        EncodeEnhancedTextureBarriers(
            command,
            texture,
            range,
            activate ? PipelineSync.None : PipelineSync.All,
            activate ? PipelineSync.All : PipelineSync.None,
            activate ? ResourceAccess.NoAccess : ResourceAccess.Common,
            activate ? ResourceAccess.Common : ResourceAccess.NoAccess,
            TextureLayout.Undefined,
            TextureLayout.Undefined,
            activate ? TextureBarrierFlags.Discard : TextureBarrierFlags.None);
    }

    private static ID3D12Resource* GetNativeResource(
        Resource resource,
        D3D12CommandContext command)
    {
        if (resource is D3D12Buffer buffer)
        {
            command.Capture(buffer);
            return buffer.Native;
        }
        if (resource is Texture publicTexture)
        {
            D3D12TextureResource texture = NativeCast.Texture(publicTexture);
            command.Capture(texture);
            return texture.Native;
        }
        throw new ArgumentException("The resource does not belong to this D3D12 backend.", nameof(resource));
    }

    private static NativeResourceBarrier CreateLegacyTransition(
        ID3D12Resource* resource,
        ResourceStates before,
        ResourceStates after,
        uint subresource,
        ResourceBarrierFlags flags)
    {
        NativeResourceBarrier result = new()
        {
            Type = ResourceBarrierType.Transition,
            Flags = flags,
        };
        result.Transition = new ResourceTransitionBarrier(
            resource,
            subresource,
            before,
            after);
        return result;
    }

    private static NativeResourceBarrier CreateLegacyUavBarrier(ID3D12Resource* resource)
    {
        NativeResourceBarrier result = new()
        {
            Type = ResourceBarrierType.Uav,
            Flags = ResourceBarrierFlags.None,
        };
        result.UAV = new ResourceUavBarrier(resource);
        return result;
    }

    private static NativeResourceBarrier CreateLegacyAliasingBarrier(
        ID3D12Resource* before,
        ID3D12Resource* after)
    {
        NativeResourceBarrier result = new()
        {
            Type = ResourceBarrierType.Aliasing,
            Flags = ResourceBarrierFlags.None,
        };
        result.Aliasing = new ResourceAliasingBarrier(before, after);
        return result;
    }

    internal static bool NeedsUavOrdering(ResourceAccess before, ResourceAccess after) =>
        ((before | after) & (
            ResourceAccess.UnorderedAccess |
            ResourceAccess.RayTracingAccelerationStructureRead |
            ResourceAccess.RayTracingAccelerationStructureWrite)) != 0;

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static BarrierSync ToBarrierSync(PipelineSync value)
    {
        // The low fourteen RHI bits intentionally track D3D12's common sync
        // range. D3D12 aliases ExecuteIndirect and Predication, so values
        // through ExecuteIndirect shift once while Predication/AllShading/
        // NonPixelShading retain their bit positions.
        ulong raw = (ulong)value;
        if (raw <= 0x3FFF)
            return (BarrierSync)(((raw & 0x07FF) << 1) | (raw & 0x3800));
        return ToBarrierSyncSlow(value);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static BarrierSync ToBarrierSyncSlow(PipelineSync value)
    {
        BarrierSync single = value switch
        {
            PipelineSync.None => BarrierSync.None,
            PipelineSync.Draw => BarrierSync.Draw,
            PipelineSync.IndexInput => BarrierSync.IndexInput,
            PipelineSync.VertexShading => BarrierSync.VertexShading,
            PipelineSync.PixelShading => BarrierSync.PixelShading,
            PipelineSync.DepthStencil => BarrierSync.DepthStencil,
            PipelineSync.RenderTarget => BarrierSync.RenderTarget,
            PipelineSync.ComputeShading => BarrierSync.ComputeShading,
            PipelineSync.RayTracing => BarrierSync.Raytracing,
            PipelineSync.Copy => BarrierSync.Copy,
            PipelineSync.Resolve => BarrierSync.Resolve,
            PipelineSync.ExecuteIndirect => BarrierSync.ExecuteIndirect,
            PipelineSync.Predication => BarrierSync.Predication,
            PipelineSync.AllShading => BarrierSync.AllShading,
            PipelineSync.NonPixelShading => BarrierSync.NonPixelShading,
            PipelineSync.Clear => BarrierSync.ClearUnorderedAccessView,
            PipelineSync.AccelerationStructureCopy => BarrierSync.CopyRaytracingAccelerationStructure,
            PipelineSync.EmitAccelerationStructurePostBuildInfo =>
                BarrierSync.EmitRaytracingAccelerationStructurePostbuildInfo,
            PipelineSync.BuildRayTracingAccelerationStructure =>
                BarrierSync.BuildRaytracingAccelerationStructure,
            PipelineSync.CopyRayTracingAccelerationStructure =>
                BarrierSync.CopyRaytracingAccelerationStructure,
            PipelineSync.Split => BarrierSync.Split,
            PipelineSync.All => BarrierSync.All,
            _ => BarrierSync.None,
        };
        if (value == PipelineSync.None || single != BarrierSync.None)
            return single;
        return ToCompositeBarrierSync(value);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static BarrierSync ToCompositeBarrierSync(PipelineSync value)
    {
        BarrierSync result = BarrierSync.None;
        PipelineSync remaining = value;
        Add(PipelineSync.Draw, BarrierSync.Draw);
        Add(PipelineSync.IndexInput, BarrierSync.IndexInput);
        Add(PipelineSync.VertexShading, BarrierSync.VertexShading);
        Add(PipelineSync.PixelShading, BarrierSync.PixelShading);
        Add(PipelineSync.DepthStencil, BarrierSync.DepthStencil);
        Add(PipelineSync.RenderTarget, BarrierSync.RenderTarget);
        Add(PipelineSync.ComputeShading, BarrierSync.ComputeShading);
        Add(PipelineSync.RayTracing, BarrierSync.Raytracing);
        Add(PipelineSync.Copy, BarrierSync.Copy);
        Add(PipelineSync.Resolve, BarrierSync.Resolve);
        Add(PipelineSync.ExecuteIndirect, BarrierSync.ExecuteIndirect);
        Add(PipelineSync.Predication, BarrierSync.Predication);
        Add(PipelineSync.AllShading, BarrierSync.AllShading);
        Add(PipelineSync.NonPixelShading, BarrierSync.NonPixelShading);
        Add(PipelineSync.Clear, BarrierSync.ClearUnorderedAccessView);
        Add(
            PipelineSync.EmitAccelerationStructurePostBuildInfo,
            BarrierSync.EmitRaytracingAccelerationStructurePostbuildInfo);
        Add(
            PipelineSync.BuildRayTracingAccelerationStructure,
            BarrierSync.BuildRaytracingAccelerationStructure);
        Add(
            PipelineSync.CopyRayTracingAccelerationStructure,
            BarrierSync.CopyRaytracingAccelerationStructure);
        Add(PipelineSync.AccelerationStructureCopy, BarrierSync.CopyRaytracingAccelerationStructure);
        Add(PipelineSync.Split, BarrierSync.Split);
        if (remaining != PipelineSync.None)
            throw new ArgumentOutOfRangeException(nameof(value));
        return result;

        void Add(PipelineSync source, BarrierSync destination)
        {
            if ((remaining & source) == 0)
                return;
            remaining &= ~source;
            result |= destination;
        }
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static BarrierAccess ToBarrierAccess(ResourceAccess value)
    {
        ulong raw = (ulong)value;
        if (raw == 0)
            return BarrierAccess.NoAccess;
        if (raw <= 0x7FFFF)
        {
            // Common has no native bit. IndirectArgument and Predication
            // share one native bit; every access after them shifts twice.
            ulong native =
                ((raw >> 1) & 0x03FF) |
                ((raw & 0x0800) >> 2) |
                ((raw & 0x7F000) >> 2);
            return (BarrierAccess)native;
        }
        return ToBarrierAccessSlow(value);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static BarrierAccess ToBarrierAccessSlow(ResourceAccess value)
    {
        BarrierAccess single = value switch
        {
            ResourceAccess.NoAccess => BarrierAccess.NoAccess,
            ResourceAccess.Common => BarrierAccess.Common,
            ResourceAccess.VertexBuffer => BarrierAccess.VertexBuffer,
            ResourceAccess.ConstantBuffer => BarrierAccess.ConstantBuffer,
            ResourceAccess.IndexBuffer => BarrierAccess.IndexBuffer,
            ResourceAccess.RenderTarget => BarrierAccess.RenderTarget,
            ResourceAccess.UnorderedAccess => BarrierAccess.UnorderedAccess,
            ResourceAccess.DepthStencilWrite => BarrierAccess.DepthStencilWrite,
            ResourceAccess.DepthStencilRead => BarrierAccess.DepthStencilRead,
            ResourceAccess.ShaderResource => BarrierAccess.ShaderResource,
            ResourceAccess.StreamOutput => BarrierAccess.StreamOutput,
            ResourceAccess.IndirectArgument => BarrierAccess.IndirectArgument,
            ResourceAccess.Predication => BarrierAccess.Predication,
            ResourceAccess.CopyDestination => BarrierAccess.CopyDest,
            ResourceAccess.CopySource => BarrierAccess.CopySource,
            ResourceAccess.ResolveDestination => BarrierAccess.ResolveDest,
            ResourceAccess.ResolveSource => BarrierAccess.ResolveSource,
            ResourceAccess.RayTracingAccelerationStructureRead =>
                BarrierAccess.RaytracingAccelerationStructureRead,
            ResourceAccess.RayTracingAccelerationStructureWrite =>
                BarrierAccess.RaytracingAccelerationStructureWrite,
            ResourceAccess.ShadingRateSource => BarrierAccess.ShadingRateSource,
            _ => BarrierAccess.Common,
        };
        if (value == ResourceAccess.NoAccess || single != BarrierAccess.Common || value == ResourceAccess.Common)
            return single;
        return ToCompositeBarrierAccess(value);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static BarrierAccess ToCompositeBarrierAccess(ResourceAccess value)
    {
        // D3D12_BARRIER_ACCESS_COMMON is the zero value used as the bitmask
        // accumulator. NO_ACCESS is a dedicated high-bit sentinel and may only
        // appear by itself.
        BarrierAccess result = BarrierAccess.Common;
        ResourceAccess remaining = value;
        Add(ResourceAccess.Common, BarrierAccess.Common);
        Add(ResourceAccess.VertexBuffer, BarrierAccess.VertexBuffer);
        Add(ResourceAccess.ConstantBuffer, BarrierAccess.ConstantBuffer);
        Add(ResourceAccess.IndexBuffer, BarrierAccess.IndexBuffer);
        Add(ResourceAccess.RenderTarget, BarrierAccess.RenderTarget);
        Add(ResourceAccess.UnorderedAccess, BarrierAccess.UnorderedAccess);
        Add(ResourceAccess.DepthStencilWrite, BarrierAccess.DepthStencilWrite);
        Add(ResourceAccess.DepthStencilRead, BarrierAccess.DepthStencilRead);
        Add(ResourceAccess.ShaderResource, BarrierAccess.ShaderResource);
        Add(ResourceAccess.StreamOutput, BarrierAccess.StreamOutput);
        Add(ResourceAccess.IndirectArgument, BarrierAccess.IndirectArgument);
        Add(ResourceAccess.Predication, BarrierAccess.Predication);
        Add(ResourceAccess.CopyDestination, BarrierAccess.CopyDest);
        Add(ResourceAccess.CopySource, BarrierAccess.CopySource);
        Add(ResourceAccess.ResolveDestination, BarrierAccess.ResolveDest);
        Add(ResourceAccess.ResolveSource, BarrierAccess.ResolveSource);
        Add(
            ResourceAccess.RayTracingAccelerationStructureRead,
            BarrierAccess.RaytracingAccelerationStructureRead);
        Add(
            ResourceAccess.RayTracingAccelerationStructureWrite,
            BarrierAccess.RaytracingAccelerationStructureWrite);
        Add(ResourceAccess.ShadingRateSource, BarrierAccess.ShadingRateSource);
        if (remaining != ResourceAccess.NoAccess)
            throw new ArgumentOutOfRangeException(nameof(value));
        return result;

        void Add(ResourceAccess source, BarrierAccess destination)
        {
            if ((remaining & source) == 0)
                return;
            remaining &= ~source;
            result |= destination;
        }
    }

    internal static BarrierLayout ToBarrierLayout(TextureLayout layout) => layout switch
    {
        TextureLayout.Undefined => BarrierLayout.Undefined,
        TextureLayout.Common => BarrierLayout.Common,
        TextureLayout.Present => BarrierLayout.Present,
        TextureLayout.RenderTarget => BarrierLayout.RenderTarget,
        TextureLayout.UnorderedAccess => BarrierLayout.UnorderedAccess,
        TextureLayout.DepthStencilWrite => BarrierLayout.DepthStencilWrite,
        TextureLayout.DepthStencilRead => BarrierLayout.DepthStencilRead,
        TextureLayout.ShaderResource => BarrierLayout.ShaderResource,
        TextureLayout.CopySource => BarrierLayout.CopySource,
        TextureLayout.CopyDestination => BarrierLayout.CopyDest,
        TextureLayout.ResolveSource => BarrierLayout.ResolveSource,
        TextureLayout.ResolveDestination => BarrierLayout.ResolveDest,
        TextureLayout.ShadingRateSource => BarrierLayout.ShadingRateSource,
        TextureLayout.QueueCommon => BarrierLayout.Common,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    internal static ResourceStates ToLegacyState(
        QueueType queueType,
        PipelineSync sync,
        TextureLayout layout,
        ResourceAccess access) => layout switch
    {
        TextureLayout.Undefined => ResourceStates.Common,
        TextureLayout.Common or TextureLayout.QueueCommon =>
            ToLegacyState(queueType, sync, access),
        TextureLayout.Present => ResourceStates.Present,
        TextureLayout.RenderTarget => ResourceStates.RenderTarget,
        TextureLayout.UnorderedAccess => ResourceStates.UnorderedAccess,
        TextureLayout.DepthStencilWrite => ResourceStates.DepthWrite,
        TextureLayout.DepthStencilRead => ResourceStates.DepthRead,
        TextureLayout.ShaderResource => ToLegacyShaderResourceState(queueType, sync),
        TextureLayout.CopySource => ResourceStates.CopySource,
        TextureLayout.CopyDestination => ResourceStates.CopyDest,
        TextureLayout.ResolveSource => ResourceStates.ResolveSource,
        TextureLayout.ResolveDestination => ResourceStates.ResolveDest,
        TextureLayout.ShadingRateSource => ResourceStates.ShadingRateSource,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    internal static ResourceStates ToLegacyState(
        QueueType queueType,
        PipelineSync sync,
        ResourceAccess value)
    {
        if (value is ResourceAccess.NoAccess or ResourceAccess.Common)
            return ResourceStates.Common;
        ResourceStates result = ResourceStates.Common;
        ResourceAccess remaining = value;
        Add(ResourceAccess.Common, ResourceStates.Common);
        Add(ResourceAccess.VertexBuffer, ResourceStates.VertexAndConstantBuffer);
        Add(ResourceAccess.ConstantBuffer, ResourceStates.VertexAndConstantBuffer);
        Add(ResourceAccess.IndexBuffer, ResourceStates.IndexBuffer);
        Add(ResourceAccess.RenderTarget, ResourceStates.RenderTarget);
        Add(ResourceAccess.UnorderedAccess, ResourceStates.UnorderedAccess);
        Add(ResourceAccess.DepthStencilWrite, ResourceStates.DepthWrite);
        Add(ResourceAccess.DepthStencilRead, ResourceStates.DepthRead);
        if ((remaining & ResourceAccess.ShaderResource) != 0)
        {
            remaining &= ~ResourceAccess.ShaderResource;
            result |= ToLegacyShaderResourceState(queueType, sync);
        }
        Add(ResourceAccess.StreamOutput, ResourceStates.StreamOut);
        Add(ResourceAccess.IndirectArgument, ResourceStates.IndirectArgument);
        Add(ResourceAccess.Predication, ResourceStates.Predication);
        Add(ResourceAccess.CopyDestination, ResourceStates.CopyDest);
        Add(ResourceAccess.CopySource, ResourceStates.CopySource);
        Add(ResourceAccess.ResolveDestination, ResourceStates.ResolveDest);
        Add(ResourceAccess.ResolveSource, ResourceStates.ResolveSource);
        Add(
            ResourceAccess.RayTracingAccelerationStructureRead,
            ResourceStates.RaytracingAccelerationStructure);
        Add(
            ResourceAccess.RayTracingAccelerationStructureWrite,
            ResourceStates.RaytracingAccelerationStructure);
        Add(ResourceAccess.ShadingRateSource, ResourceStates.ShadingRateSource);
        if (remaining != ResourceAccess.NoAccess)
            throw new ArgumentOutOfRangeException(nameof(value));
        return result;

        void Add(ResourceAccess source, ResourceStates destination)
        {
            if ((remaining & source) == 0)
                return;
            remaining &= ~source;
            result |= destination;
        }
    }

    internal static ResourceStates ToLegacyShaderResourceState(
        QueueType queueType,
        PipelineSync sync)
    {
        if (queueType == QueueType.Copy)
        {
            throw new InvalidOperationException(
                "A Copy CommandContext cannot use ShaderResource access.");
        }

        ResourceStates result = ResourceStates.Common;
        if (queueType == QueueType.Graphics &&
            (sync & (
                PipelineSync.Draw |
                PipelineSync.PixelShading |
                PipelineSync.AllShading)) != 0)
        {
            result |= ResourceStates.PixelShaderResource;
        }

        if ((sync & (
                PipelineSync.Draw |
                PipelineSync.VertexShading |
                PipelineSync.ComputeShading |
                PipelineSync.RayTracing |
                PipelineSync.AllShading |
                PipelineSync.NonPixelShading)) != 0)
        {
            result |= ResourceStates.NonPixelShaderResource;
        }

        if (result != ResourceStates.Common)
            return result;

        // ResourceBarrier has no independent synchronization scope. If the
        // portable scope does not name a shader stage (for example Split),
        // expose every shader-resource state legal on the selected Queue.
        return queueType switch
        {
            QueueType.Graphics =>
                ResourceStates.PixelShaderResource |
                ResourceStates.NonPixelShaderResource,
            QueueType.Compute => ResourceStates.NonPixelShaderResource,
            _ => throw new ArgumentOutOfRangeException(nameof(queueType)),
        };
    }
}
