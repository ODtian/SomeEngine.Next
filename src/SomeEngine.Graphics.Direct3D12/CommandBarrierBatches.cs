using Silk.NET.Direct3D12;
using NativeBufferBarrier = Silk.NET.Direct3D12.BufferBarrier;
using NativeTextureBarrier = Silk.NET.Direct3D12.TextureBarrier;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private const int MaximumStackBatchedBarrierCount = 256;

    public void Barrier(CommandContext context, in BarrierBatch barriers)
    {
        if (barriers.IsEmpty)
            return;

        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (!command.EnhancedBarriers || !TryResolveBatchCounts(
                barriers,
                out int bufferCount,
                out int textureCount))
        {
            EncodeBarrierBatchIndividually(context, barriers);
            return;
        }

        int globalCount = checked(
            barriers.MemoryBarriers.Length +
            (HasFoldableCommonTextureBarrier(command, barriers.TextureBarriers) ? 1 : 0));
        int maximumTextureBarrierCount = checked(textureCount * 3);
        if (checked(globalCount + bufferCount + maximumTextureBarrierCount) >
            MaximumStackBatchedBarrierCount)
        {
            EncodeBarrierBatchIndividually(context, barriers);
            return;
        }

        EncodeEnhancedBarrierBatch(
            command,
            barriers,
            globalCount,
            bufferCount,
            textureCount,
            maximumTextureBarrierCount);
    }

    private void EncodeEnhancedBarrierBatch(
        D3D12CommandContext command,
        in BarrierBatch barriers,
        int globalCount,
        int bufferCount,
        int textureCount,
        int maximumTextureBarrierCount)
    {
        int resourceCount = checked(bufferCount + textureCount);
        command.PrepareCaptures(resourceCount, 0, resourceCount);
        command.PrepareSwapchainUses(textureCount);
        GlobalBarrier* nativeGlobals = stackalloc GlobalBarrier[Math.Max(globalCount, 1)];
        NativeBufferBarrier* nativeBuffers = stackalloc NativeBufferBarrier[Math.Max(bufferCount, 1)];
        NativeTextureBarrier* nativeTextures =
            stackalloc NativeTextureBarrier[Math.Max(maximumTextureBarrierCount, 1)];
        int globalDestination = AppendMemoryBarriers(barriers.MemoryBarriers, nativeGlobals);
        int bufferDestination = 0;
        int textureDestination = 0;
        AppendQueueAcquires(
            command,
            barriers.QueueAcquires,
            nativeBuffers,
            ref bufferDestination,
            nativeTextures,
            ref textureDestination);
        AppendBufferBarriers(
            command,
            barriers.BufferBarriers,
            nativeBuffers,
            ref bufferDestination);
        AppendTextureBarriersAndFoldCommon(
            command,
            barriers.TextureBarriers,
            nativeGlobals,
            ref globalDestination,
            nativeTextures,
            ref textureDestination);
        AppendQueueReleases(
            command,
            barriers.QueueReleases,
            nativeBuffers,
            ref bufferDestination,
            nativeTextures,
            ref textureDestination);
        EmitEnhancedBarrierGroups(
            command,
            nativeGlobals,
            globalDestination,
            nativeBuffers,
            bufferDestination,
            nativeTextures,
            textureDestination);
    }

    private static int AppendMemoryBarriers(
        ReadOnlySpan<MemoryBarrier> barriers,
        GlobalBarrier* destination)
    {
        int count = 0;
        foreach (ref readonly MemoryBarrier barrier in barriers)
        {
            ResolveBarrierSyncs(
                barrier.SyncBefore,
                barrier.SyncAfter,
                barrier.Phase,
                out BarrierSync syncBefore,
                out BarrierSync syncAfter);
            destination[count++] = new GlobalBarrier(
                syncBefore,
                syncAfter,
                ToBarrierAccess(barrier.AccessBefore),
                ToBarrierAccess(barrier.AccessAfter));
        }
        return count;
    }

    private void AppendBufferBarriers(
        D3D12CommandContext command,
        ReadOnlySpan<BufferBarrier> barriers,
        NativeBufferBarrier* destination,
        ref int count)
    {
        foreach (ref readonly BufferBarrier barrier in barriers)
        {
            D3D12Buffer buffer = RequireBuffer(barrier.Buffer);
            command.Capture(buffer);
            ResolveBarrierSyncs(
                barrier.SyncBefore,
                barrier.SyncAfter,
                barrier.Phase,
                out BarrierSync syncBefore,
                out BarrierSync syncAfter);
            destination[count++] = new NativeBufferBarrier(
                syncBefore,
                syncAfter,
                ToBarrierAccess(barrier.AccessBefore),
                ToBarrierAccess(barrier.AccessAfter),
                buffer.Native,
                0,
                buffer.Info.Size);
        }
    }

    private void AppendTextureBarriers(
        D3D12CommandContext command,
        ReadOnlySpan<TextureBarrier> barriers,
        NativeTextureBarrier* destination,
        ref int count)
    {
        foreach (ref readonly TextureBarrier barrier in barriers)
        {
            D3D12TextureResource texture = RequireTexture(barrier.Texture);
            command.Capture(texture);
            ResolveBarrierSyncs(
                barrier.SyncBefore,
                barrier.SyncAfter,
                barrier.Phase,
                out BarrierSync syncBefore,
                out BarrierSync syncAfter);
            count = AppendEnhancedTextureBarriers(
                command,
                texture,
                barrier.Range,
                syncBefore,
                syncAfter,
                ToBarrierAccess(barrier.AccessBefore),
                ToBarrierAccess(barrier.AccessAfter),
                barrier.LayoutBefore,
                barrier.LayoutAfter,
                TextureBarrierFlags.None,
                destination,
                count);
            command.RecordSwapchainState(texture, barrier.LayoutAfter, barrier.AccessAfter);
        }
    }

    private void AppendTextureBarriersAndFoldCommon(
        D3D12CommandContext command,
        ReadOnlySpan<TextureBarrier> barriers,
        GlobalBarrier* globals,
        ref int globalCount,
        NativeTextureBarrier* textures,
        ref int textureCount)
    {
        BarrierSync foldedSyncBefore = BarrierSync.None;
        BarrierSync foldedSyncAfter = BarrierSync.None;
        BarrierAccess foldedAccessBefore = BarrierAccess.NoAccess;
        BarrierAccess foldedAccessAfter = BarrierAccess.NoAccess;
        bool folded = false;
        Span<TextureAspects> aspects = stackalloc TextureAspects[3];
        foreach (ref readonly TextureBarrier barrier in barriers)
        {
            if (!CanFoldCommonTextureBarrier(command, barrier))
            {
                AppendTextureBarrier(command, barrier, textures, ref textureCount);
                continue;
            }

            D3D12TextureResource texture = RequireTexture(barrier.Texture);
            _ = ExpandBarrierAspects(texture.Info, barrier.Range, aspects);
            command.Capture(texture);
            ResolveBarrierSyncs(
                barrier.SyncBefore,
                barrier.SyncAfter,
                barrier.Phase,
                out BarrierSync syncBefore,
                out BarrierSync syncAfter);
            foldedSyncBefore |= syncBefore;
            foldedSyncAfter |= syncAfter;
            BarrierAccess accessBefore = ToBarrierAccess(barrier.AccessBefore);
            BarrierAccess accessAfter = ToBarrierAccess(barrier.AccessAfter);
            foldedAccessBefore = folded
                ? foldedAccessBefore | accessBefore
                : accessBefore;
            foldedAccessAfter = folded
                ? foldedAccessAfter | accessAfter
                : accessAfter;
            command.RecordSwapchainState(
                texture,
                barrier.LayoutAfter,
                barrier.AccessAfter);
            folded = true;
        }
        if (folded)
        {
            globals[globalCount++] = new GlobalBarrier(
                foldedSyncBefore,
                foldedSyncAfter,
                foldedAccessBefore,
                foldedAccessAfter);
        }
    }

    private void AppendTextureBarrier(
        D3D12CommandContext command,
        in TextureBarrier barrier,
        NativeTextureBarrier* destination,
        ref int count)
    {
        D3D12TextureResource texture = RequireTexture(barrier.Texture);
        command.Capture(texture);
        ResolveBarrierSyncs(
            barrier.SyncBefore,
            barrier.SyncAfter,
            barrier.Phase,
            out BarrierSync syncBefore,
            out BarrierSync syncAfter);
        count = AppendEnhancedTextureBarriers(
            command,
            texture,
            barrier.Range,
            syncBefore,
            syncAfter,
            ToBarrierAccess(barrier.AccessBefore),
            ToBarrierAccess(barrier.AccessAfter),
            barrier.LayoutBefore,
            barrier.LayoutAfter,
            TextureBarrierFlags.None,
            destination,
            count);
        command.RecordSwapchainState(
            texture,
            barrier.LayoutAfter,
            barrier.AccessAfter);
    }

    private static bool HasFoldableCommonTextureBarrier(
        D3D12CommandContext command,
        ReadOnlySpan<TextureBarrier> barriers)
    {
        foreach (ref readonly TextureBarrier barrier in barriers)
            if (CanFoldCommonTextureBarrier(command, barrier))
                return true;
        return false;
    }

    private static bool CanFoldCommonTextureBarrier(
        D3D12CommandContext command,
        in TextureBarrier barrier) =>
        command.NativeBackend.UseQueueSpecificCommonLayouts &&
        command.QueueType != QueueType.Copy &&
        barrier.Phase == BarrierPhase.Complete &&
        barrier.LayoutBefore == TextureLayout.General &&
        barrier.LayoutAfter == TextureLayout.General &&
        barrier.AccessBefore != ResourceAccess.NoAccess &&
        barrier.AccessAfter != ResourceAccess.NoAccess;

    private void AppendQueueAcquires(
        D3D12CommandContext command,
        ReadOnlySpan<QueueAcquire> barriers,
        NativeBufferBarrier* bufferDestination,
        ref int bufferCount,
        NativeTextureBarrier* textureDestination,
        ref int textureCount)
    {
        foreach (ref readonly QueueAcquire barrier in barriers)
        {
            if (barrier.Resource is Buffer publicBuffer)
            {
                D3D12Buffer buffer = RequireBuffer(publicBuffer);
                command.Capture(buffer);
                bufferDestination[bufferCount++] = new NativeBufferBarrier(
                    BarrierSync.None,
                    ToBarrierSync(barrier.Sync),
                    BarrierAccess.NoAccess,
                    ToBarrierAccess(barrier.Access),
                    buffer.Native,
                    0,
                    buffer.Info.Size);
                continue;
            }

            D3D12TextureResource texture = RequireTexture((Texture)barrier.Resource);
            TextureSubresourceRange range = barrier.TextureRange ??
                throw new ArgumentException(
                    "A Texture acquire requires a subresource range.",
                    nameof(barriers));
            TextureLayout layout = barrier.Layout ??
                throw new ArgumentException(
                    "A Texture acquire requires a layout.",
                    nameof(barriers));
            command.Capture(texture);
            textureCount = AppendEnhancedTextureBarriers(
                command,
                texture,
                range,
                BarrierSync.None,
                ToBarrierSync(barrier.Sync),
                BarrierAccess.NoAccess,
                ToBarrierAccess(barrier.Access),
                TextureLayout.General,
                layout,
                TextureBarrierFlags.None,
                textureDestination,
                textureCount);
            command.RecordSwapchainState(texture, layout, barrier.Access);
        }
    }

    private void AppendQueueReleases(
        D3D12CommandContext command,
        ReadOnlySpan<QueueRelease> barriers,
        NativeBufferBarrier* bufferDestination,
        ref int bufferCount,
        NativeTextureBarrier* textureDestination,
        ref int textureCount)
    {
        foreach (ref readonly QueueRelease barrier in barriers)
        {
            if (barrier.Resource is Buffer publicBuffer)
            {
                D3D12Buffer buffer = RequireBuffer(publicBuffer);
                command.Capture(buffer);
                bufferDestination[bufferCount++] = new NativeBufferBarrier(
                    ToBarrierSync(barrier.Sync),
                    BarrierSync.None,
                    ToBarrierAccess(barrier.Access),
                    BarrierAccess.NoAccess,
                    buffer.Native,
                    0,
                    buffer.Info.Size);
                continue;
            }

            D3D12TextureResource texture = RequireTexture((Texture)barrier.Resource);
            TextureSubresourceRange range = barrier.TextureRange ??
                throw new ArgumentException(
                    "A Texture release requires a subresource range.",
                    nameof(barriers));
            TextureLayout layout = barrier.Layout ??
                throw new ArgumentException(
                    "A Texture release requires a layout.",
                    nameof(barriers));
            command.Capture(texture);
            textureCount = AppendEnhancedTextureBarriers(
                command,
                texture,
                range,
                ToBarrierSync(barrier.Sync),
                BarrierSync.None,
                ToBarrierAccess(barrier.Access),
                BarrierAccess.NoAccess,
                layout,
                TextureLayout.General,
                TextureBarrierFlags.None,
                textureDestination,
                textureCount);
            command.RecordSwapchainState(
                texture,
                TextureLayout.General,
                ResourceAccess.NoAccess);
        }
    }

    private static void EmitEnhancedBarrierGroups(
        D3D12CommandContext command,
        GlobalBarrier* globals,
        int globalCount,
        NativeBufferBarrier* buffers,
        int bufferCount,
        NativeTextureBarrier* textures,
        int textureCount)
    {
        BarrierGroup* groups = stackalloc BarrierGroup[3];
        int groupCount = 0;
        if (globalCount != 0)
        {
            groups[groupCount++] = new BarrierGroup
            {
                Type = BarrierType.Global,
                NumBarriers = checked((uint)globalCount),
                Anonymous = new BarrierGroupUnion { PGlobalBarriers = globals },
            };
        }
        if (bufferCount != 0)
        {
            groups[groupCount++] = new BarrierGroup(
                BarrierType.Buffer,
                checked((uint)bufferCount),
                pBufferBarriers: buffers);
        }
        if (textureCount != 0)
        {
            groups[groupCount++] = new BarrierGroup(
                BarrierType.Texture,
                checked((uint)textureCount),
                pTextureBarriers: textures);
        }
        D3D12CommandListFastCalls.Barrier(
            command.List,
            checked((uint)groupCount),
            groups);
    }

    private static bool TryResolveBatchCounts(
        in BarrierBatch barriers,
        out int bufferCount,
        out int textureCount)
    {
        bufferCount = barriers.BufferBarriers.Length;
        textureCount = barriers.TextureBarriers.Length;
        foreach (ref readonly QueueAcquire barrier in barriers.QueueAcquires)
        {
            if (barrier.Resource is Buffer)
                bufferCount++;
            else if (barrier.Resource is Texture)
                textureCount++;
            else
                return false;
        }
        foreach (ref readonly QueueRelease barrier in barriers.QueueReleases)
        {
            if (barrier.Resource is Buffer)
                bufferCount++;
            else if (barrier.Resource is Texture)
                textureCount++;
            else
                return false;
        }
        return true;
    }

    private static int AppendEnhancedTextureBarriers(
        D3D12CommandContext command,
        D3D12TextureResource texture,
        in TextureSubresourceRange range,
        BarrierSync syncBefore,
        BarrierSync syncAfter,
        BarrierAccess accessBefore,
        BarrierAccess accessAfter,
        TextureLayout layoutBefore,
        TextureLayout layoutAfter,
        TextureBarrierFlags flags,
        NativeTextureBarrier* destination,
        int destinationIndex)
    {
        ResolveEnhancedTextureLayouts(
            command.QueueType,
            command.NativeBackend.UseQueueSpecificCommonLayouts,
            layoutBefore,
            layoutAfter,
            out BarrierLayout nativeLayoutBefore,
            out BarrierLayout nativeLayoutAfter);
        if (range.Aspects == TextureAspects.Color &&
            !FormatMappings.IsDepthStencil(texture.Info.Format))
        {
            ValidateTextureBarrierRange(texture.Info, range);
            destination[destinationIndex++] = new NativeTextureBarrier(
                syncBefore,
                syncAfter,
                accessBefore,
                accessAfter,
                nativeLayoutBefore,
                nativeLayoutAfter,
                texture.Native,
                new BarrierSubresourceRange(
                    range.FirstMipLevel,
                    range.MipLevelCount,
                    range.FirstArrayLayer,
                    range.ArrayLayerCount,
                    0,
                    1),
                flags);
            return destinationIndex;
        }
        Span<TextureAspects> aspects = stackalloc TextureAspects[3];
        int aspectCount = ExpandBarrierAspects(texture.Info, range, aspects);
        for (int index = 0; index < aspectCount; index++)
        {
            destination[destinationIndex++] = CreateEnhancedTextureBarrier(
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
        return destinationIndex;
    }

    private static void ValidateTextureBarrierRange(
        in TextureInfo info,
        in TextureSubresourceRange range)
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
    }

    private void EncodeBarrierBatchIndividually(
        CommandContext context,
        in BarrierBatch barriers)
    {
        foreach (ref readonly MemoryBarrier barrier in barriers.MemoryBarriers)
            Barrier(context, barrier);
        foreach (ref readonly QueueAcquire barrier in barriers.QueueAcquires)
            Barrier(context, barrier);
        foreach (ref readonly BufferBarrier barrier in barriers.BufferBarriers)
            Barrier(context, barrier);
        foreach (ref readonly TextureBarrier barrier in barriers.TextureBarriers)
            Barrier(context, barrier);
        foreach (ref readonly QueueRelease barrier in barriers.QueueReleases)
            Barrier(context, barrier);
    }
}
