using Silk.NET.Direct3D12;
using NativeBufferBarrier = Silk.NET.Direct3D12.BufferBarrier;
using NativeTextureBarrier = Silk.NET.Direct3D12.TextureBarrier;
using NativeResourceBarrier = Silk.NET.Direct3D12.ResourceBarrier;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void Barrier(CommandContext context, in MemoryBarrier barrier)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (command.EnhancedBarriers)
        {
            ResolveBarrierSyncs(
                barrier.SyncBefore,
                barrier.SyncAfter,
                barrier.Phase,
                out BarrierSync syncBefore,
                out BarrierSync syncAfter);
            GlobalBarrier native = new()
            {
                SyncBefore = syncBefore,
                SyncAfter = syncAfter,
                AccessBefore = ToBarrierAccess(barrier.AccessBefore),
                AccessAfter = ToBarrierAccess(barrier.AccessAfter),
            };
            BarrierGroup group = new()
            {
                Type = BarrierType.Global,
                NumBarriers = 1,
                Anonymous = new BarrierGroupUnion { PGlobalBarriers = &native },
            };
            D3D12CommandListFastCalls.Barrier(command.List, 1, &group);
            return;
        }

        if (barrier.Phase != BarrierPhase.Complete)
        {
            throw new NotSupportedException(
                "Split memory barriers require D3D12 enhanced-barrier support.");
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
        D3D12CommandListFastCalls.ResourceBarrier(command.List, 1, &legacy);
    }

    public void Barrier(CommandContext context, in BufferBarrier barrier)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12Buffer buffer = RequireBuffer(barrier.Buffer);
        command.PrepareCaptures(1, 0, 1);
        command.Capture(buffer);
        if (command.EnhancedBarriers)
        {
            ResolveBarrierSyncs(
                barrier.SyncBefore,
                barrier.SyncAfter,
                barrier.Phase,
                out BarrierSync syncBefore,
                out BarrierSync syncAfter);
            NativeBufferBarrier native = new(
                syncBefore,
                syncAfter,
                ToBarrierAccess(barrier.AccessBefore),
                ToBarrierAccess(barrier.AccessAfter),
                buffer.Native,
                0,
                buffer.Info.Size);
            BarrierGroup group = new(BarrierType.Buffer, 1, pBufferBarriers: &native);
            D3D12CommandListFastCalls.Barrier(command.List, 1, &group);
            return;
        }

        NativeResourceBarrier legacy = CreateLegacyTransition(
            buffer.Native,
            ToLegacyState(command.QueueType, barrier.SyncBefore, barrier.AccessBefore),
            ToLegacyState(command.QueueType, barrier.SyncAfter, barrier.AccessAfter),
            uint.MaxValue,
            ToLegacyBarrierFlags(barrier.Phase));
        D3D12CommandListFastCalls.ResourceBarrier(command.List, 1, &legacy);
    }

    public void Barrier(CommandContext context, in TextureBarrier barrier)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12TextureResource texture = RequireTexture(barrier.Texture);
        command.PrepareCaptures(1, 0, 1);
        command.PrepareSwapchainUses(1);
        command.Capture(texture);
        if (command.EnhancedBarriers)
        {
            ResolveBarrierSyncs(
                barrier.SyncBefore,
                barrier.SyncAfter,
                barrier.Phase,
                out BarrierSync syncBefore,
                out BarrierSync syncAfter);
            EncodeEnhancedTextureBarriers(
                command,
                texture,
                barrier.Range,
                syncBefore,
                syncAfter,
                ToBarrierAccess(barrier.AccessBefore),
                ToBarrierAccess(barrier.AccessAfter),
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
                barrier.AccessAfter),
            ToLegacyBarrierFlags(barrier.Phase));
        command.RecordSwapchainState(texture, barrier.LayoutAfter, barrier.AccessAfter);
    }

    public void Barrier(CommandContext context, in AliasingBarrier barrier)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (barrier.Before.IsEmpty && barrier.After.IsEmpty)
            throw new ArgumentException(
                "AliasingBarrier requires at least one resource.",
                nameof(barrier));
        foreach (ref readonly AliasingResource resource in barrier.Before)
            RequireValidAliasingResource(resource);
        foreach (ref readonly AliasingResource resource in barrier.After)
            RequireValidAliasingResource(resource);
        int aliasCount = checked(barrier.Before.Length + barrier.After.Length);
        command.PrepareCaptures(aliasCount, 0, aliasCount);
        command.PrepareSwapchainUses(aliasCount);

        if (command.EnhancedBarriers)
        {
            // Separate ordered Barrier calls plus ALL->ALL scopes complete all
            // preceding access before any after-side discard. NO_ACCESS makes
            // the old contents unnecessary; it does not promise a write flush. The
            // after entries deliberately remain NO_ACCESS/UNDEFINED; a later
            // ordinary barrier declares the real first use.
            foreach (ref readonly AliasingResource resource in barrier.Before)
                EncodeEnhancedAliasing(command, resource, activate: false);
            foreach (ref readonly AliasingResource resource in barrier.After)
                EncodeEnhancedAliasing(command, resource, activate: true);
            return;
        }

        ID3D12Resource* before = null;
        ID3D12Resource* after = null;
        if (RequiresGlobalLegacyAliasingBarrier(barrier.Before.Length, barrier.After.Length))
        {
            foreach (ref readonly AliasingResource resource in barrier.Before)
                CaptureAliasingResource(command, resource.Resource);
            foreach (ref readonly AliasingResource resource in barrier.After)
                CaptureAliasingResource(command, resource.Resource);
        }
        else
        {
            if (!barrier.Before.IsEmpty)
                before = GetAliasingNativeResource(barrier.Before[0].Resource, command);
            if (!barrier.After.IsEmpty)
                after = GetAliasingNativeResource(barrier.After[0].Resource, command);
        }
        NativeResourceBarrier native = CreateLegacyAliasingBarrier(before, after);
        D3D12CommandListFastCalls.ResourceBarrier(command.List, 1, &native);
    }

    public void Barrier(CommandContext context, in QueueRelease barrier)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (barrier.Resource is Buffer publicBuffer)
        {
            D3D12Buffer buffer = RequireBuffer(publicBuffer);
            command.PrepareCaptures(1, 0, 1);
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
                D3D12CommandListFastCalls.Barrier(command.List, 1, &group);
            }
            else
            {
                NativeResourceBarrier native = CreateLegacyTransition(
                    buffer.Native,
                    ToLegacyState(command.QueueType, barrier.Sync, barrier.Access),
                    ResourceStates.Common,
                    uint.MaxValue,
                    ResourceBarrierFlags.None);
                D3D12CommandListFastCalls.ResourceBarrier(command.List, 1, &native);
            }
            return;
        }

        D3D12TextureResource texture = RequireTexture((Texture)barrier.Resource);
        TextureSubresourceRange range = barrier.TextureRange
            ?? throw new ArgumentException("A Texture release requires a subresource range.", nameof(barrier));
        TextureLayout layout = barrier.Layout
            ?? throw new ArgumentException("A Texture release requires a layout.", nameof(barrier));
        command.PrepareCaptures(1, 0, 1);
        command.PrepareSwapchainUses(1);
        command.Capture(texture);
        if (command.EnhancedBarriers)
        {
            EncodeEnhancedTextureBarriers(
                command,
                texture,
                range,
                ToBarrierSync(barrier.Sync),
                BarrierSync.None,
                ToBarrierAccess(barrier.Access),
                BarrierAccess.NoAccess,
                layout,
                TextureLayout.General,
                TextureBarrierFlags.None);
            command.RecordSwapchainState(
                texture,
                TextureLayout.General,
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
                TextureLayout.General,
                ResourceAccess.NoAccess);
        }
    }

    public void Barrier(CommandContext context, in QueueAcquire barrier)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (barrier.Resource is Buffer publicBuffer)
        {
            D3D12Buffer buffer = RequireBuffer(publicBuffer);
            command.PrepareCaptures(1, 0, 1);
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
                D3D12CommandListFastCalls.Barrier(command.List, 1, &group);
            }
            else
            {
                NativeResourceBarrier native = CreateLegacyTransition(
                    buffer.Native,
                    ResourceStates.Common,
                    ToLegacyState(command.QueueType, barrier.Sync, barrier.Access),
                    uint.MaxValue,
                    ResourceBarrierFlags.None);
                D3D12CommandListFastCalls.ResourceBarrier(command.List, 1, &native);
            }
            return;
        }

        D3D12TextureResource texture = RequireTexture((Texture)barrier.Resource);
        TextureSubresourceRange range = barrier.TextureRange
            ?? throw new ArgumentException("A Texture acquire requires a subresource range.", nameof(barrier));
        TextureLayout layout = barrier.Layout
            ?? throw new ArgumentException("A Texture acquire requires a layout.", nameof(barrier));
        command.PrepareCaptures(1, 0, 1);
        command.PrepareSwapchainUses(1);
        command.Capture(texture);
        if (command.EnhancedBarriers)
        {
            EncodeEnhancedTextureBarriers(
                command,
                texture,
                range,
                BarrierSync.None,
                ToBarrierSync(barrier.Sync),
                BarrierAccess.NoAccess,
                ToBarrierAccess(barrier.Access),
                TextureLayout.General,
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
        BarrierSync syncBefore,
        BarrierSync syncAfter,
        BarrierAccess accessBefore,
        BarrierAccess accessAfter,
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
        D3D12CommandListFastCalls.Barrier(command.List, 1, &group);
    }

    private static void EncodeLegacyTextureTransitions(
        D3D12CommandContext command,
        D3D12TextureResource texture,
        in TextureSubresourceRange range,
        ResourceStates before,
        ResourceStates after,
        ResourceBarrierFlags flags = ResourceBarrierFlags.None)
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
                flags);
            D3D12CommandListFastCalls.ResourceBarrier(command.List, 1, &native);
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
        BarrierSync syncBefore,
        BarrierSync syncAfter,
        BarrierAccess accessBefore,
        BarrierAccess accessAfter,
        BarrierLayout layoutBefore,
        BarrierLayout layoutAfter,
        TextureBarrierFlags flags)
    {
        uint plane = FormatMappings.PlaneIndex(texture.Info.Format, range.Aspects);
        return new NativeTextureBarrier(
            syncBefore,
            syncAfter,
            accessBefore,
            accessAfter,
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
        TextureLayout.General or
        TextureLayout.CopySource or
        TextureLayout.CopyDestination;

    private void EncodeEnhancedAliasing(
        D3D12CommandContext command,
        in AliasingResource resource,
        bool activate)
    {
        EnhancedAliasingBarrierState state =
            GetEnhancedAliasingBarrierState(activate);
        if (resource.Resource is Buffer publicBuffer)
        {
            D3D12Buffer buffer = RequireBuffer(publicBuffer);
            command.Capture(buffer);
            NativeBufferBarrier native = new(
                ToBarrierSync(state.SyncBefore),
                ToBarrierSync(state.SyncAfter),
                state.AccessBefore,
                state.AccessAfter,
                buffer.Native,
                0,
                buffer.Info.Size);
            BarrierGroup group = new(BarrierType.Buffer, 1, pBufferBarriers: &native);
            D3D12CommandListFastCalls.Barrier(command.List, 1, &group);
            return;
        }

        if (resource.Resource is AccelerationStructure publicStructure)
        {
            D3D12AccelerationStructure structure =
                RequireAccelerationStructure(publicStructure);
            D3D12Buffer storage = structure.Storage;
            command.Capture(structure);
            NativeBufferBarrier native = new(
                ToBarrierSync(state.SyncBefore),
                ToBarrierSync(state.SyncAfter),
                state.AccessBefore,
                state.AccessAfter,
                storage.Native,
                0,
                storage.Info.Size);
            BarrierGroup group = new(BarrierType.Buffer, 1, pBufferBarriers: &native);
            D3D12CommandListFastCalls.Barrier(command.List, 1, &group);
            return;
        }

        if (resource.Resource is Texture publicTexture)
        {
            D3D12TextureResource texture = RequireTexture(publicTexture);
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
                ToBarrierSync(state.SyncBefore),
                ToBarrierSync(state.SyncAfter),
                state.AccessBefore,
                state.AccessAfter,
                TextureLayout.Undefined,
                TextureLayout.Undefined,
                state.Discard ? TextureBarrierFlags.Discard : TextureBarrierFlags.None);
        }
    }

    internal static EnhancedAliasingBarrierState GetEnhancedAliasingBarrierState(
        bool activate) => new(
            PipelineSync.All,
            PipelineSync.All,
            activate ? BarrierAccess.NoAccess : BarrierAccess.Common,
            BarrierAccess.NoAccess,
            activate);

    internal readonly record struct EnhancedAliasingBarrierState(
        PipelineSync SyncBefore,
        PipelineSync SyncAfter,
        BarrierAccess AccessBefore,
        BarrierAccess AccessAfter,
        bool Discard);

    private void RequireValidAliasingResource(in AliasingResource resource)
    {
        ArgumentNullException.ThrowIfNull(resource.Resource);
        if (resource.Resource is Buffer buffer)
        {
            _ = RequireBuffer(buffer);
            if (resource.TextureRange is not null)
                throw new ArgumentException(
                    "A Buffer aliasing entry cannot contain a Texture subresource range.",
                    nameof(resource));
            return;
        }
        if (resource.Resource is AccelerationStructure structure)
        {
            _ = RequireAccelerationStructure(structure);
            if (resource.TextureRange is not null)
                throw new ArgumentException(
                    "An AccelerationStructure aliasing entry cannot contain a Texture subresource range.",
                    nameof(resource));
            return;
        }
        if (resource.Resource is Texture publicTexture)
        {
            _ = RequireTexture(publicTexture);
            TextureSubresourceRange range = resource.TextureRange ?? new TextureSubresourceRange(
                0,
                publicTexture.Info.MipLevelCount,
                0,
                publicTexture.Info.ArrayLayerCount,
                DefaultBarrierAspects(publicTexture.Info.Format));
            Span<TextureAspects> aspects = stackalloc TextureAspects[3];
            _ = ExpandBarrierAspects(publicTexture.Info, range, aspects);
            return;
        }
        throw new ArgumentException(
            "AliasingBarrier requires a Buffer, Texture, or AccelerationStructure.",
            nameof(resource));
    }

    private void CaptureAliasingResource(
        D3D12CommandContext command,
        Resource resource)
    {
        if (resource is Buffer publicBuffer)
        {
            command.Capture(RequireBuffer(publicBuffer));
            return;
        }
        if (resource is Texture publicTexture)
        {
            command.Capture(RequireTexture(publicTexture));
            return;
        }
        if (resource is AccelerationStructure publicStructure)
        {
            command.Capture(RequireAccelerationStructure(publicStructure));
            return;
        }
        throw new System.Diagnostics.UnreachableException(
            "Aliasing resource kind was validated before capture.");
    }

    private ID3D12Resource* GetAliasingNativeResource(
        Resource resource,
        D3D12CommandContext command)
    {
        if (resource is Buffer publicBuffer)
        {
            D3D12Buffer buffer = RequireBuffer(publicBuffer);
            command.Capture(buffer);
            return buffer.Native;
        }
        if (resource is Texture publicTexture)
        {
            D3D12TextureResource texture = RequireTexture(publicTexture);
            command.Capture(texture);
            return texture.Native;
        }
        if (resource is AccelerationStructure publicStructure)
        {
            D3D12AccelerationStructure structure =
                RequireAccelerationStructure(publicStructure);
            command.Capture(structure);
            return structure.Storage.Native;
        }
        throw new System.Diagnostics.UnreachableException(
            "Aliasing resource kind was validated before native mapping.");
    }

    internal static bool RequiresGlobalLegacyAliasingBarrier(
        int beforeCount,
        int afterCount) => beforeCount > 1 || afterCount > 1;

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

    private static void ResolveBarrierSyncs(
        PipelineSync syncBefore,
        PipelineSync syncAfter,
        BarrierPhase phase,
        out BarrierSync nativeSyncBefore,
        out BarrierSync nativeSyncAfter)
    {
        if (!Enum.IsDefined(phase))
            throw new ArgumentOutOfRangeException(nameof(phase));
        nativeSyncBefore = phase == BarrierPhase.End
            ? BarrierSync.Split
            : ToBarrierSync(syncBefore);
        nativeSyncAfter = phase == BarrierPhase.Begin
            ? BarrierSync.Split
            : ToBarrierSync(syncAfter);
    }

    private static ResourceBarrierFlags ToLegacyBarrierFlags(BarrierPhase phase) =>
        phase switch
        {
            BarrierPhase.Complete => ResourceBarrierFlags.None,
            BarrierPhase.Begin => ResourceBarrierFlags.BeginOnly,
            BarrierPhase.End => ResourceBarrierFlags.EndOnly,
            _ => throw new ArgumentOutOfRangeException(nameof(phase)),
        };

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
        if (value == ResourceAccess.NoAccess || single != BarrierAccess.Common)
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
        TextureLayout.General => BarrierLayout.Common,
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
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };

    internal static ResourceStates ToLegacyState(
        QueueType queueType,
        PipelineSync sync,
        TextureLayout layout,
        ResourceAccess access) => layout switch
    {
        TextureLayout.Undefined => ResourceStates.Common,
        TextureLayout.General =>
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
        if (value == ResourceAccess.NoAccess)
            return ResourceStates.Common;
        ResourceStates result = ResourceStates.Common;
        ResourceAccess remaining = value;
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
