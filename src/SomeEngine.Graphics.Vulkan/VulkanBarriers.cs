namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    internal void Barrier(CommandContext context, in MemoryBarrier barrier)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        if (barrier.Phase == BarrierPhase.End)
            return;
        MemoryBarrier2 native = new()
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = ToNative(barrier.SyncBefore),
            SrcAccessMask = ToNative(barrier.AccessBefore),
            DstStageMask = ToNative(barrier.SyncAfter),
            DstAccessMask = ToNative(barrier.AccessAfter),
        };
        EmitBarriers(command.NativeRecording, new ReadOnlySpan<MemoryBarrier2>(&native, 1), [], []);
    }

    internal void Barrier(CommandContext context, in BufferBarrier barrier)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanBuffer buffer = RequireBuffer((VulkanDevice)command.Device, barrier.Buffer, nameof(barrier));
        command.Capture(buffer);
        if (barrier.Phase == BarrierPhase.End)
            return;
        BufferMemoryBarrier2 native = CreateBufferBarrier(
            buffer,
            barrier.SyncBefore,
            barrier.SyncAfter,
            barrier.AccessBefore,
            barrier.AccessAfter,
            Vk.QueueFamilyIgnored,
            Vk.QueueFamilyIgnored);
        EmitBarriers(command.NativeRecording, [], new ReadOnlySpan<BufferMemoryBarrier2>(&native, 1), []);
    }

    internal void Barrier(CommandContext context, in TextureBarrier barrier)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanTexture texture = RequireTexture((VulkanDevice)command.Device, barrier.Texture, nameof(barrier));
        ValidateTextureRange(texture.Info, barrier.Range);
        command.Capture(texture);
        if (barrier.Phase == BarrierPhase.End)
            return;
        ImageMemoryBarrier2 native = CreateImageBarrier(
            texture,
            barrier.Range,
            barrier.SyncBefore,
            barrier.SyncAfter,
            barrier.AccessBefore,
            barrier.AccessAfter,
            barrier.LayoutBefore,
            barrier.LayoutAfter,
            Vk.QueueFamilyIgnored,
            Vk.QueueFamilyIgnored);
        EmitBarriers(command.NativeRecording, [], [], new ReadOnlySpan<ImageMemoryBarrier2>(&native, 1));
    }

    internal void Barrier(CommandContext context, in AliasingBarrier barrier)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        foreach (ref readonly AliasingResource resource in barrier.Before)
            CaptureAliasingResource(command, resource);
        foreach (ref readonly AliasingResource resource in barrier.After)
            CaptureAliasingResource(command, resource);
        MemoryBarrier2 native = new()
        {
            SType = StructureType.MemoryBarrier2,
            SrcStageMask = PipelineStageFlags2.AllCommandsBit,
            SrcAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
            DstStageMask = PipelineStageFlags2.AllCommandsBit,
            DstAccessMask = AccessFlags2.MemoryReadBit | AccessFlags2.MemoryWriteBit,
        };
        EmitBarriers(command.NativeRecording, new ReadOnlySpan<MemoryBarrier2>(&native, 1), [], []);
    }

    internal void Barrier(CommandContext context, in QueueRelease barrier)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        uint sourceFamily = device.GetQueue(command.QueueType, command.QueueIndex).FamilyIndex;
        uint destinationFamily = device.GetFirstQueueFamily(barrier.DestinationQueueType);
        NormalizeQueueFamilies(ref sourceFamily, ref destinationFamily);
        if (barrier.Resource is RhiBuffer publicBuffer)
        {
            VulkanBuffer buffer = RequireBuffer(device, publicBuffer, nameof(barrier));
            command.Capture(buffer);
            BufferMemoryBarrier2 native = CreateBufferBarrier(
                buffer,
                barrier.Sync,
                PipelineSync.None,
                barrier.Access,
                ResourceAccess.NoAccess,
                sourceFamily,
                destinationFamily);
            EmitBarriers(command.NativeRecording, [], new ReadOnlySpan<BufferMemoryBarrier2>(&native, 1), []);
            return;
        }
        VulkanTexture texture = RequireTexture(device, (RhiTexture)barrier.Resource, nameof(barrier));
        TextureSubresourceRange range = barrier.TextureRange
            ?? throw new ArgumentException("A Texture QueueRelease requires a subresource range.", nameof(barrier));
        TextureLayout layout = barrier.Layout
            ?? throw new ArgumentException("A Texture QueueRelease requires a layout.", nameof(barrier));
        ValidateTextureRange(texture.Info, range);
        command.Capture(texture);
        ImageMemoryBarrier2 image = CreateImageBarrier(
            texture,
            range,
            barrier.Sync,
            PipelineSync.None,
            barrier.Access,
            ResourceAccess.NoAccess,
            layout,
            layout,
            sourceFamily,
            destinationFamily);
        EmitBarriers(command.NativeRecording, [], [], new ReadOnlySpan<ImageMemoryBarrier2>(&image, 1));
    }

    internal void Barrier(CommandContext context, in QueueAcquire barrier)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        VulkanDevice device = (VulkanDevice)command.Device;
        uint sourceFamily = device.GetFirstQueueFamily(barrier.SourceQueueType);
        uint destinationFamily = device.GetQueue(command.QueueType, command.QueueIndex).FamilyIndex;
        NormalizeQueueFamilies(ref sourceFamily, ref destinationFamily);
        if (barrier.Resource is RhiBuffer publicBuffer)
        {
            VulkanBuffer buffer = RequireBuffer(device, publicBuffer, nameof(barrier));
            command.Capture(buffer);
            BufferMemoryBarrier2 native = CreateBufferBarrier(
                buffer,
                PipelineSync.None,
                barrier.Sync,
                ResourceAccess.NoAccess,
                barrier.Access,
                sourceFamily,
                destinationFamily);
            EmitBarriers(command.NativeRecording, [], new ReadOnlySpan<BufferMemoryBarrier2>(&native, 1), []);
            return;
        }
        VulkanTexture texture = RequireTexture(device, (RhiTexture)barrier.Resource, nameof(barrier));
        TextureSubresourceRange range = barrier.TextureRange
            ?? throw new ArgumentException("A Texture QueueAcquire requires a subresource range.", nameof(barrier));
        TextureLayout layout = barrier.Layout
            ?? throw new ArgumentException("A Texture QueueAcquire requires a layout.", nameof(barrier));
        ValidateTextureRange(texture.Info, range);
        command.Capture(texture);
        ImageMemoryBarrier2 image = CreateImageBarrier(
            texture,
            range,
            PipelineSync.None,
            barrier.Sync,
            ResourceAccess.NoAccess,
            barrier.Access,
            layout,
            layout,
            sourceFamily,
            destinationFamily);
        EmitBarriers(command.NativeRecording, [], [], new ReadOnlySpan<ImageMemoryBarrier2>(&image, 1));
    }

    internal void Barrier(CommandContext context, in BarrierBatch barriers)
    {
        VulkanCommandContext command = RequireCommandContext(context, nameof(context));
        int memoryCapacity = barriers.MemoryBarriers.Length;
        int bufferCapacity = barriers.BufferBarriers.Length +
            barriers.QueueAcquires.Length + barriers.QueueReleases.Length;
        int imageCapacity = barriers.TextureBarriers.Length +
            barriers.QueueAcquires.Length + barriers.QueueReleases.Length;
        command.PrepareBarrierStorage(
            memoryCapacity,
            bufferCapacity,
            imageCapacity,
            out Span<MemoryBarrier2> memories,
            out Span<BufferMemoryBarrier2> buffers,
            out Span<ImageMemoryBarrier2> images);
        int memoryCount = 0;
        int bufferCount = 0;
        int imageCount = 0;
        foreach (ref readonly MemoryBarrier barrier in barriers.MemoryBarriers)
        {
            if (barrier.Phase == BarrierPhase.End)
                continue;
            memories[memoryCount++] = new MemoryBarrier2
            {
                SType = StructureType.MemoryBarrier2,
                SrcStageMask = ToNative(barrier.SyncBefore),
                SrcAccessMask = ToNative(barrier.AccessBefore),
                DstStageMask = ToNative(barrier.SyncAfter),
                DstAccessMask = ToNative(barrier.AccessAfter),
            };
        }
        foreach (ref readonly QueueAcquire barrier in barriers.QueueAcquires)
            AppendQueueAcquire(command, barrier, buffers, ref bufferCount, images, ref imageCount);
        foreach (ref readonly BufferBarrier barrier in barriers.BufferBarriers)
            AppendBufferBarrier(command, barrier, buffers, ref bufferCount);
        foreach (ref readonly TextureBarrier barrier in barriers.TextureBarriers)
            AppendTextureBarrier(command, barrier, images, ref imageCount);
        foreach (ref readonly QueueRelease barrier in barriers.QueueReleases)
            AppendQueueRelease(command, barrier, buffers, ref bufferCount, images, ref imageCount);
        EmitBarriers(
            command.NativeRecording,
            memories[..memoryCount],
            buffers[..bufferCount],
            images[..imageCount]);
    }

    private void AppendBufferBarrier(
        VulkanCommandContext command,
        in BufferBarrier barrier,
        Span<BufferMemoryBarrier2> destination,
        ref int count)
    {
        VulkanBuffer buffer = RequireBuffer((VulkanDevice)command.Device, barrier.Buffer, nameof(barrier));
        command.Capture(buffer);
        if (barrier.Phase == BarrierPhase.End)
            return;
        destination[count++] = CreateBufferBarrier(
            buffer,
            barrier.SyncBefore,
            barrier.SyncAfter,
            barrier.AccessBefore,
            barrier.AccessAfter,
            Vk.QueueFamilyIgnored,
            Vk.QueueFamilyIgnored);
    }

    private void AppendTextureBarrier(
        VulkanCommandContext command,
        in TextureBarrier barrier,
        Span<ImageMemoryBarrier2> destination,
        ref int count)
    {
        VulkanTexture texture = RequireTexture((VulkanDevice)command.Device, barrier.Texture, nameof(barrier));
        ValidateTextureRange(texture.Info, barrier.Range);
        command.Capture(texture);
        if (barrier.Phase == BarrierPhase.End)
            return;
        destination[count++] = CreateImageBarrier(
            texture,
            barrier.Range,
            barrier.SyncBefore,
            barrier.SyncAfter,
            barrier.AccessBefore,
            barrier.AccessAfter,
            barrier.LayoutBefore,
            barrier.LayoutAfter,
            Vk.QueueFamilyIgnored,
            Vk.QueueFamilyIgnored);
    }

    private void AppendQueueAcquire(
        VulkanCommandContext command,
        in QueueAcquire barrier,
        Span<BufferMemoryBarrier2> buffers,
        ref int bufferCount,
        Span<ImageMemoryBarrier2> images,
        ref int imageCount)
    {
        VulkanDevice device = (VulkanDevice)command.Device;
        uint sourceFamily = device.GetFirstQueueFamily(barrier.SourceQueueType);
        uint destinationFamily = device.GetQueue(command.QueueType, command.QueueIndex).FamilyIndex;
        NormalizeQueueFamilies(ref sourceFamily, ref destinationFamily);
        if (barrier.Resource is RhiBuffer publicBuffer)
        {
            VulkanBuffer buffer = RequireBuffer(device, publicBuffer, nameof(barrier));
            command.Capture(buffer);
            buffers[bufferCount++] = CreateBufferBarrier(
                buffer,
                PipelineSync.None,
                barrier.Sync,
                ResourceAccess.NoAccess,
                barrier.Access,
                sourceFamily,
                destinationFamily);
            return;
        }
        VulkanTexture texture = RequireTexture(device, (RhiTexture)barrier.Resource, nameof(barrier));
        TextureSubresourceRange range = barrier.TextureRange ?? throw new ArgumentException(
            "A Texture QueueAcquire requires a subresource range.", nameof(barrier));
        TextureLayout layout = barrier.Layout ?? throw new ArgumentException(
            "A Texture QueueAcquire requires a layout.", nameof(barrier));
        command.Capture(texture);
        images[imageCount++] = CreateImageBarrier(
            texture, range, PipelineSync.None, barrier.Sync,
            ResourceAccess.NoAccess, barrier.Access, layout, layout,
            sourceFamily, destinationFamily);
    }

    private void AppendQueueRelease(
        VulkanCommandContext command,
        in QueueRelease barrier,
        Span<BufferMemoryBarrier2> buffers,
        ref int bufferCount,
        Span<ImageMemoryBarrier2> images,
        ref int imageCount)
    {
        VulkanDevice device = (VulkanDevice)command.Device;
        uint sourceFamily = device.GetQueue(command.QueueType, command.QueueIndex).FamilyIndex;
        uint destinationFamily = device.GetFirstQueueFamily(barrier.DestinationQueueType);
        NormalizeQueueFamilies(ref sourceFamily, ref destinationFamily);
        if (barrier.Resource is RhiBuffer publicBuffer)
        {
            VulkanBuffer buffer = RequireBuffer(device, publicBuffer, nameof(barrier));
            command.Capture(buffer);
            buffers[bufferCount++] = CreateBufferBarrier(
                buffer,
                barrier.Sync,
                PipelineSync.None,
                barrier.Access,
                ResourceAccess.NoAccess,
                sourceFamily,
                destinationFamily);
            return;
        }
        VulkanTexture texture = RequireTexture(device, (RhiTexture)barrier.Resource, nameof(barrier));
        TextureSubresourceRange range = barrier.TextureRange ?? throw new ArgumentException(
            "A Texture QueueRelease requires a subresource range.", nameof(barrier));
        TextureLayout layout = barrier.Layout ?? throw new ArgumentException(
            "A Texture QueueRelease requires a layout.", nameof(barrier));
        command.Capture(texture);
        images[imageCount++] = CreateImageBarrier(
            texture, range, barrier.Sync, PipelineSync.None,
            barrier.Access, ResourceAccess.NoAccess, layout, layout,
            sourceFamily, destinationFamily);
    }

    private void CaptureAliasingResource(
        VulkanCommandContext command,
        in AliasingResource resource)
    {
        VulkanDevice device = (VulkanDevice)command.Device;
        if (resource.Resource is RhiBuffer buffer)
        {
            command.Capture(RequireBuffer(device, buffer, nameof(resource)));
            return;
        }
        VulkanTexture texture = RequireTexture(device, (RhiTexture)resource.Resource, nameof(resource));
        if (resource.TextureRange.HasValue)
            ValidateTextureRange(texture.Info, resource.TextureRange.Value);
        command.Capture(texture);
    }

    private static BufferMemoryBarrier2 CreateBufferBarrier(
        VulkanBuffer buffer,
        PipelineSync syncBefore,
        PipelineSync syncAfter,
        ResourceAccess accessBefore,
        ResourceAccess accessAfter,
        uint sourceFamily,
        uint destinationFamily) => new()
    {
        SType = StructureType.BufferMemoryBarrier2,
        SrcStageMask = ToNative(syncBefore),
        SrcAccessMask = ToNative(accessBefore),
        DstStageMask = ToNative(syncAfter),
        DstAccessMask = ToNative(accessAfter),
        SrcQueueFamilyIndex = sourceFamily,
        DstQueueFamilyIndex = destinationFamily,
        Buffer = buffer.Native,
        Offset = 0,
        Size = Vk.WholeSize,
    };

    private static ImageMemoryBarrier2 CreateImageBarrier(
        VulkanTexture texture,
        in TextureSubresourceRange range,
        PipelineSync syncBefore,
        PipelineSync syncAfter,
        ResourceAccess accessBefore,
        ResourceAccess accessAfter,
        TextureLayout layoutBefore,
        TextureLayout layoutAfter,
        uint sourceFamily,
        uint destinationFamily) => new()
    {
        SType = StructureType.ImageMemoryBarrier2,
        SrcStageMask = ToNative(syncBefore),
        SrcAccessMask = ToNative(accessBefore),
        DstStageMask = ToNative(syncAfter),
        DstAccessMask = ToNative(accessAfter),
        OldLayout = ToNative(layoutBefore),
        NewLayout = ToNative(layoutAfter),
        SrcQueueFamilyIndex = sourceFamily,
        DstQueueFamilyIndex = destinationFamily,
        Image = texture.Native,
        SubresourceRange = ToNative(range),
    };

    private void EmitBarriers(
        VkCommandBuffer command,
        ReadOnlySpan<MemoryBarrier2> memories,
        ReadOnlySpan<BufferMemoryBarrier2> buffers,
        ReadOnlySpan<ImageMemoryBarrier2> images)
    {
        if (memories.IsEmpty && buffers.IsEmpty && images.IsEmpty)
            return;
        fixed (MemoryBarrier2* memoryPointer = memories)
        fixed (BufferMemoryBarrier2* bufferPointer = buffers)
        fixed (ImageMemoryBarrier2* imagePointer = images)
        {
            DependencyInfo dependency = new()
            {
                SType = StructureType.DependencyInfo,
                DependencyFlags = DependencyFlags.ByRegionBit,
                MemoryBarrierCount = checked((uint)memories.Length),
                PMemoryBarriers = memoryPointer,
                BufferMemoryBarrierCount = checked((uint)buffers.Length),
                PBufferMemoryBarriers = bufferPointer,
                ImageMemoryBarrierCount = checked((uint)images.Length),
                PImageMemoryBarriers = imagePointer,
            };
            Api.CmdPipelineBarrier2(command, &dependency);
        }
    }

    private static void NormalizeQueueFamilies(ref uint source, ref uint destination)
    {
        if (source != destination)
            return;
        source = Vk.QueueFamilyIgnored;
        destination = Vk.QueueFamilyIgnored;
    }

    internal static PipelineStageFlags2 ToNative(PipelineSync sync)
    {
        if (sync == PipelineSync.None)
            return PipelineStageFlags2.None;
        if (sync == PipelineSync.All)
            return PipelineStageFlags2.AllCommandsBit;
        PipelineStageFlags2 result = 0;
        Add(PipelineSync.Draw, PipelineStageFlags2.AllGraphicsBit);
        Add(PipelineSync.IndexInput, PipelineStageFlags2.IndexInputBit);
        Add(PipelineSync.VertexShading, PipelineStageFlags2.VertexShaderBit);
        Add(PipelineSync.PixelShading, PipelineStageFlags2.FragmentShaderBit);
        Add(PipelineSync.DepthStencil, PipelineStageFlags2.EarlyFragmentTestsBit | PipelineStageFlags2.LateFragmentTestsBit);
        Add(PipelineSync.RenderTarget, PipelineStageFlags2.ColorAttachmentOutputBit);
        Add(PipelineSync.ComputeShading, PipelineStageFlags2.ComputeShaderBit);
        Add(PipelineSync.RayTracing, PipelineStageFlags2.RayTracingShaderBitKhr);
        Add(PipelineSync.Copy, PipelineStageFlags2.CopyBit);
        Add(PipelineSync.Resolve, PipelineStageFlags2.ResolveBit);
        Add(PipelineSync.ExecuteIndirect, PipelineStageFlags2.DrawIndirectBit);
        Add(PipelineSync.Predication, PipelineStageFlags2.ConditionalRenderingBitExt);
        Add(PipelineSync.AllShading, PipelineStageFlags2.AllGraphicsBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.RayTracingShaderBitKhr);
        Add(PipelineSync.NonPixelShading, PipelineStageFlags2.VertexShaderBit | PipelineStageFlags2.ComputeShaderBit | PipelineStageFlags2.RayTracingShaderBitKhr);
        Add(PipelineSync.Clear, PipelineStageFlags2.ClearBit);
        Add(PipelineSync.AccelerationStructureCopy, PipelineStageFlags2.AccelerationStructureCopyBitKhr);
        Add(PipelineSync.EmitAccelerationStructurePostBuildInfo, PipelineStageFlags2.AccelerationStructureBuildBitKhr);
        Add(PipelineSync.BuildRayTracingAccelerationStructure, PipelineStageFlags2.AccelerationStructureBuildBitKhr);
        Add(PipelineSync.CopyRayTracingAccelerationStructure, PipelineStageFlags2.AccelerationStructureCopyBitKhr);
        return result == 0 ? PipelineStageFlags2.AllCommandsBit : result;

        void Add(PipelineSync source, PipelineStageFlags2 destination)
        {
            if ((sync & source) != 0)
                result |= destination;
        }
    }

    internal static AccessFlags2 ToNative(ResourceAccess access)
    {
        AccessFlags2 result = 0;
        Add(ResourceAccess.VertexBuffer, AccessFlags2.VertexAttributeReadBit);
        Add(ResourceAccess.ConstantBuffer, AccessFlags2.UniformReadBit);
        Add(ResourceAccess.IndexBuffer, AccessFlags2.IndexReadBit);
        Add(ResourceAccess.RenderTarget, AccessFlags2.ColorAttachmentReadBit | AccessFlags2.ColorAttachmentWriteBit);
        Add(ResourceAccess.UnorderedAccess, AccessFlags2.ShaderStorageReadBit | AccessFlags2.ShaderStorageWriteBit);
        Add(ResourceAccess.DepthStencilWrite, AccessFlags2.DepthStencilAttachmentReadBit | AccessFlags2.DepthStencilAttachmentWriteBit);
        Add(ResourceAccess.DepthStencilRead, AccessFlags2.DepthStencilAttachmentReadBit);
        Add(ResourceAccess.ShaderResource, AccessFlags2.ShaderReadBit);
        Add(ResourceAccess.StreamOutput, AccessFlags2.TransformFeedbackWriteBitExt | AccessFlags2.TransformFeedbackCounterWriteBitExt);
        Add(ResourceAccess.IndirectArgument, AccessFlags2.IndirectCommandReadBit);
        Add(ResourceAccess.Predication, AccessFlags2.ConditionalRenderingReadBitExt);
        Add(ResourceAccess.CopyDestination, AccessFlags2.TransferWriteBit);
        Add(ResourceAccess.CopySource, AccessFlags2.TransferReadBit);
        Add(ResourceAccess.ResolveDestination, AccessFlags2.TransferWriteBit);
        Add(ResourceAccess.ResolveSource, AccessFlags2.TransferReadBit);
        Add(ResourceAccess.RayTracingAccelerationStructureRead, AccessFlags2.AccelerationStructureReadBitKhr);
        Add(ResourceAccess.RayTracingAccelerationStructureWrite, AccessFlags2.AccelerationStructureWriteBitKhr);
        Add(ResourceAccess.ShadingRateSource, AccessFlags2.FragmentShadingRateAttachmentReadBitKhr);
        return result;

        void Add(ResourceAccess source, AccessFlags2 destination)
        {
            if ((access & source) != 0)
                result |= destination;
        }
    }

    internal static ImageLayout ToNative(TextureLayout layout) => layout switch
    {
        TextureLayout.Undefined => ImageLayout.Undefined,
        TextureLayout.General => ImageLayout.General,
        TextureLayout.Present => ImageLayout.PresentSrcKhr,
        TextureLayout.RenderTarget => ImageLayout.ColorAttachmentOptimal,
        TextureLayout.UnorderedAccess => ImageLayout.General,
        TextureLayout.DepthStencilWrite => ImageLayout.DepthStencilAttachmentOptimal,
        TextureLayout.DepthStencilRead => ImageLayout.DepthStencilReadOnlyOptimal,
        TextureLayout.ShaderResource => ImageLayout.ShaderReadOnlyOptimal,
        TextureLayout.CopySource or TextureLayout.ResolveSource => ImageLayout.TransferSrcOptimal,
        TextureLayout.CopyDestination or TextureLayout.ResolveDestination => ImageLayout.TransferDstOptimal,
        TextureLayout.ShadingRateSource => ImageLayout.FragmentShadingRateAttachmentOptimalKhr,
        _ => throw new ArgumentOutOfRangeException(nameof(layout)),
    };
}

internal sealed unsafe partial class VulkanBackend
{
    private sealed partial class VulkanCommandContext
    {
        private MemoryBarrier2[] _memoryBarriers = [];
        private BufferMemoryBarrier2[] _bufferBarriers = [];
        private ImageMemoryBarrier2[] _imageBarriers = [];

        internal void PrepareBarrierStorage(
            int memoryCount,
            int bufferCount,
            int imageCount,
            out Span<MemoryBarrier2> memories,
            out Span<BufferMemoryBarrier2> buffers,
            out Span<ImageMemoryBarrier2> images)
        {
            if (_memoryBarriers.Length < memoryCount)
                Array.Resize(ref _memoryBarriers, GrowCapacity(_memoryBarriers.Length, memoryCount));
            if (_bufferBarriers.Length < bufferCount)
                Array.Resize(ref _bufferBarriers, GrowCapacity(_bufferBarriers.Length, bufferCount));
            if (_imageBarriers.Length < imageCount)
                Array.Resize(ref _imageBarriers, GrowCapacity(_imageBarriers.Length, imageCount));
            memories = _memoryBarriers.AsSpan(0, memoryCount);
            buffers = _bufferBarriers.AsSpan(0, bufferCount);
            images = _imageBarriers.AsSpan(0, imageCount);
        }

        private static int GrowCapacity(int current, int required)
        {
            int doubled = current == 0 ? 16 : checked(current * 2);
            return Math.Max(doubled, required);
        }
    }

    private sealed partial class VulkanDevice
    {
        internal uint GetFirstQueueFamily(QueueType type)
        {
            foreach (((QueueType queueType, _), VulkanQueue queue) in _queues)
                if (queueType == type)
                    return queue.FamilyIndex;
            throw new ArgumentOutOfRangeException(nameof(type), $"The Device has no {type} Queue.");
        }
    }
}
