namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private RhiBuffer CreateReservedBufferCore(RhiDevice device, in BufferDesc desc)
    {
        VulkanDevice nativeDevice = RequireSparseDevice(device);
        ValidateBufferDescription(desc, MemoryType.DeviceLocal);
        BufferCreateInfo createInfo = CreateBufferInfo(nativeDevice, desc);
        createInfo.Flags = BufferCreateFlags.SparseBindingBit |
            BufferCreateFlags.SparseResidencyBit |
            BufferCreateFlags.SparseAliasedBit;
        VkBuffer native = default;
        nativeDevice.ThrowIfDeviceCallFailed(
            Api.CreateBuffer(nativeDevice.Native, &createInfo, null, &native),
            "vkCreateBuffer(sparse)");
        VulkanBuffer? buffer = null;
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements;
            Api.GetBufferMemoryRequirements(nativeDevice.Native, native, &requirements);
            SparseResourceInfo info = new(
                new SparseTileShape(checked((uint)requirements.Alignment), 1, 1),
                DivideRoundUp(requirements.Size, requirements.Alignment),
                default,
                requirements.Alignment);
            buffer = new VulkanBuffer(
                nativeDevice,
                native,
                ownedMemory: null,
                heap: null,
                desc,
                MemoryType.DeviceLocal,
                0,
                0,
                sparse: true)
            {
                SparseState = new VulkanSparseState(info, requirements.MemoryTypeBits),
            };
            return RegisterChildOrDispose(nativeDevice, buffer);
        }
        catch
        {
            if (buffer is null)
                Api.DestroyBuffer(nativeDevice.Native, native, null);
            throw;
        }
    }

    private RhiTexture CreateReservedTextureCore(RhiDevice device, in TextureDesc desc)
    {
        VulkanDevice nativeDevice = RequireSparseDevice(device);
        ValidateTextureDescription(desc);
        FormatSupport support = nativeDevice.Capabilities.GetFormatSupport(desc.Format);
        FormatFeatures required = desc.Dimension == TextureDimension.Texture3D
            ? FormatFeatures.SparseTexture3D
            : FormatFeatures.SparseTexture2D;
        if ((support.Features & required) == 0)
            throw new NotSupportedException("The Vulkan format does not support sparse residency for this dimension.");
        VkImage native = CreateNativeImage(nativeDevice, desc, aliasable: true, sparse: true);
        VulkanTexture? texture = null;
        try
        {
            SparseResourceInfo info = GetSparseTextureInfo(
                nativeDevice,
                native,
                desc,
                out uint memoryTypeBits,
                out SparseImageMemoryRequirements imageRequirements);
            texture = new VulkanTexture(
                nativeDevice,
                native,
                ownedMemory: null,
                heap: null,
                desc,
                0,
                0,
                ownsImage: true)
            {
                SparseState = new VulkanSparseState(info, memoryTypeBits, imageRequirements),
            };
            return RegisterChildOrDispose(nativeDevice, texture);
        }
        catch
        {
            if (texture is null)
                Api.DestroyImage(nativeDevice.Native, native, null);
            throw;
        }
    }

    private SparseResourceInfo GetSparseResourceInfoCore(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.Device is not VulkanDevice device ||
            !ReferenceEquals(device.Backend, this))
        {
            throw new ArgumentException(
                "The Resource belongs to a different graphics backend.",
                nameof(resource));
        }
        resource.ThrowIfDisposed();
        device.ThrowIfUnavailable();
        VulkanSparseState? state = resource switch
        {
            VulkanBuffer buffer => buffer.SparseState,
            VulkanTexture texture => texture.SparseState,
            _ => null,
        };
        return state?.Info
            ?? throw new ArgumentException("The Resource is not a Vulkan sparse resource.", nameof(resource));
    }

    private QueueCompletion UpdateSparseMappingsCore(
        RhiQueue queue,
        ReadOnlySpan<SparseMappingDesc> mappings)
    {
        VulkanQueue nativeQueue = RequireSparseQueue(queue, nameof(queue));
        VulkanDevice device = RequireSparseDevice(nativeQueue.Device);
        if (mappings.IsEmpty)
            return Submit(nativeQueue, new QueueSubmitDesc([], [], [], [], []));
        var buffers = new List<SparseBufferBindRecord>();
        var images = new List<SparseImageBindRecord>();
        var opaqueImages = new List<SparseOpaqueImageBindRecord>();
        var prepared = new List<PreparedSparseMapping>();
        var retained = new HashSet<IVulkanRetained>();
        foreach (ref readonly SparseMappingDesc mapping in mappings)
        {
            VulkanSparseState state = RequireSparseState(mapping.Resource, device, nameof(mappings));
            SparseTileCoordinate[] tiles = ExpandTiles(mapping.Resource, state, mapping.ResourceTiles);
            for (int index = 0; index < tiles.Length; index++)
            {
                SparseMappingDesc tile = mapping with
                {
                    ResourceTiles = new SparseTileRegion(tiles[index], 1, 1, 1, 1, Boxed: false),
                    HeapTileOffset = mapping.Type == SparseMappingType.Reused
                        ? mapping.HeapTileOffset : checked(mapping.HeapTileOffset + (uint)index),
                };
                PrepareSparseMapping(device, tile, buffers, images, opaqueImages, prepared, retained);
            }
        }
        return ExecuteSparseBind(
            nativeQueue,
            buffers,
            images,
            opaqueImages,
            prepared,
            retained);
    }

    private QueueCompletion CopySparseMappingsCore(
        RhiQueue queue,
        ReadOnlySpan<SparseMappingCopyDesc> copies)
    {
        VulkanQueue nativeQueue = RequireSparseQueue(queue, nameof(queue));
        VulkanDevice device = (VulkanDevice)nativeQueue.Device;
        if (copies.IsEmpty)
            return Submit(queue, new QueueSubmitDesc([], [], [], [], []));
        var mappings = new List<SparseMappingDesc>();
        foreach (ref readonly SparseMappingCopyDesc copy in copies)
        {
            VulkanSparseState source = RequireSparseState(copy.Source, device, nameof(copies));
            VulkanSparseState destinationState = RequireSparseState(copy.Destination, device, nameof(copies));
            SparseTileCoordinate[] sourceTiles = ExpandTiles(copy.Source, source,
                copy.Region with { Start = copy.SourceStart });
            SparseTileCoordinate[] destinationTiles = ExpandTiles(copy.Destination, destinationState,
                copy.Region with { Start = copy.DestinationStart });
            for (int index = 0; index < sourceTiles.Length; index++)
            {
                SparseTileCoordinate sourceTile = sourceTiles[index];
                SparseTileKey sourceKey = new(sourceTile.Subresource, sourceTile.X, sourceTile.Y, sourceTile.Z);
                SparseTileRegion region = new(destinationTiles[index], 1, 1, 1, 1, Boxed: false);
                if (source.TryGet(sourceKey, out SparseTileBinding binding))
                {
                    mappings.Add(new SparseMappingDesc(
                        copy.Destination,
                        region,
                        SparseMappingType.Mapped,
                        binding.Heap,
                        binding.HeapTileOffset));
                }
                else
                {
                    mappings.Add(new SparseMappingDesc(
                        copy.Destination,
                        region,
                        SparseMappingType.Unmapped,
                        null,
                        0));
                }
            }
        }
        return UpdateSparseMappingsCore(queue, CollectionsMarshal.AsSpan(mappings));
    }

    private static SparseTileCoordinate[] ExpandTiles(
        Resource resource, VulkanSparseState state, in SparseTileRegion region)
    {
        uint count = region.Boxed
            ? checked(region.Width * region.Height * region.Depth) : region.TileCount;
        if (count == 0) throw new ArgumentOutOfRangeException(nameof(region));
        var result = new SparseTileCoordinate[checked((int)count)];
        if (resource is VulkanBuffer)
        {
            if (region.Boxed || region.Start.Subresource != 0 || region.Start.Y != 0 || region.Start.Z != 0 ||
                (ulong)region.Start.X + count > state.Info.TotalTileCount)
                throw new ArgumentOutOfRangeException(nameof(region));
            for (uint index = 0; index < count; index++)
                result[index] = region.Start with { X = checked(region.Start.X + index) };
            return result;
        }
        VulkanTexture texture = (VulkanTexture)resource;
        uint mipCount = texture.Info.MipLevelCount;
        uint mip = region.Start.Subresource % mipCount;
        uint layer = region.Start.Subresource / mipCount;
        if (layer >= texture.Info.ArrayLayerCount) throw new ArgumentOutOfRangeException(nameof(region));
        if (mip >= state.Info.PackedMips.StandardMipLevelCount)
        {
            if (region.Boxed || region.Start.Y != 0 || region.Start.Z != 0 ||
                (ulong)region.Start.X + count > state.Info.PackedMips.PackedMipTileCount)
                throw new ArgumentOutOfRangeException(nameof(region));
            bool sharedTail = (state.ImageRequirements.FormatProperties.Flags &
                SparseImageFormatFlags.SingleMiptailBit) != 0;
            uint subresource = (sharedTail ? 0 : layer) * mipCount + state.Info.PackedMips.StandardMipLevelCount;
            for (uint index = 0; index < count; index++)
                result[index] = new SparseTileCoordinate(subresource, checked(region.Start.X + index), 0, 0);
            return result;
        }
        SparseTileShape shape = state.Info.TileShape;
        uint width = checked((uint)DivideRoundUp(MipExtent(texture.Info.Width, mip), shape.Width));
        uint height = checked((uint)DivideRoundUp(MipExtent(texture.Info.Height, mip), shape.Height));
        uint depth = checked((uint)DivideRoundUp(MipExtent(texture.Info.Depth, mip), shape.Depth));
        if (region.Start.X >= width || region.Start.Y >= height || region.Start.Z >= depth)
            throw new ArgumentOutOfRangeException(nameof(region));
        ulong start = region.Start.X + (ulong)width * (region.Start.Y + (ulong)height * region.Start.Z);
        if (region.Boxed)
        {
            if (region.Width > width - region.Start.X || region.Height > height - region.Start.Y ||
                region.Depth > depth - region.Start.Z) throw new ArgumentOutOfRangeException(nameof(region));
        }
        else if (start + count > (ulong)width * height * depth)
            throw new ArgumentOutOfRangeException(nameof(region));
        for (uint index = 0; index < count; index++)
        {
            uint x, y, z;
            if (region.Boxed)
            {
                x = region.Start.X + index % region.Width;
                y = region.Start.Y + index / region.Width % region.Height;
                z = region.Start.Z + index / region.Width / region.Height;
            }
            else
            {
                ulong address = start + index;
                x = (uint)(address % width);
                y = (uint)(address / width % height);
                z = (uint)(address / width / height);
            }
            result[index] = new SparseTileCoordinate(region.Start.Subresource, x, y, z);
        }
        return result;
    }

    private void PrepareSparseMapping(
        VulkanDevice device,
        in SparseMappingDesc mapping,
        List<SparseBufferBindRecord> buffers,
        List<SparseImageBindRecord> images,
        List<SparseOpaqueImageBindRecord> opaqueImages,
        List<PreparedSparseMapping> prepared,
        HashSet<IVulkanRetained> retained)
    {
        VulkanSparseState state = RequireSparseState(mapping.Resource, device, nameof(mapping));
        VulkanHeap? heap = mapping.Type == SparseMappingType.Unmapped
            ? null
            : mapping.Heap is RhiHeap publicHeap
                ? RequireHeap(device, publicHeap, nameof(mapping))
                : throw new ArgumentException("A mapped sparse range requires a Heap.", nameof(mapping));
        if (heap is not null)
        {
            ulong requiredTiles = Math.Max(mapping.ResourceTiles.TileCount, 1);
            ulong requiredBytes = checked((mapping.HeapTileOffset + requiredTiles) * state.Info.Alignment);
            if (requiredBytes > heap.Info.Size)
                throw new ArgumentOutOfRangeException(nameof(mapping.HeapTileOffset));
            if ((state.MemoryTypeBits &
                 (1u << checked((int)heap.Memory.TypeIndex))) == 0)
            {
                throw new ArgumentException(
                    "The Heap memory type is incompatible with the sparse Resource.",
                    nameof(mapping));
            }
            retained.Add(heap);
        }
        if (mapping.Resource is VulkanBuffer buffer)
        {
            if (mapping.ResourceTiles.Start.Subresource != 0 || mapping.ResourceTiles.Boxed)
                throw new ArgumentException("Sparse Buffer mappings must be linear subresource-zero ranges.", nameof(mapping));
            buffers.Add(new SparseBufferBindRecord(
                buffer.Native,
                CreateOpaqueBind(state, mapping, heap)));
            retained.Add(buffer);
        }
        else if (mapping.Resource is VulkanTexture texture)
        {
            if (mapping.ResourceTiles.Start.Subresource % texture.Info.MipLevelCount <
                    state.Info.PackedMips.StandardMipLevelCount)
            {
                images.Add(new SparseImageBindRecord(
                    texture.Native,
                    CreateImageBind(texture, state, mapping, heap)));
            }
            else
            {
                opaqueImages.Add(new SparseOpaqueImageBindRecord(
                    texture.Native,
                    CreateOpaqueBind(state, mapping, heap)));
            }
            retained.Add(texture);
        }
        else
        {
            throw new ArgumentException("The sparse Resource belongs to another backend.", nameof(mapping));
        }
        prepared.Add(new PreparedSparseMapping(state, mapping, heap));
    }

    private QueueCompletion ExecuteSparseBind(
        VulkanQueue queue,
        List<SparseBufferBindRecord> bufferRecords,
        List<SparseImageBindRecord> imageRecords,
        List<SparseOpaqueImageBindRecord> opaqueRecords,
        List<PreparedSparseMapping> mappings,
        HashSet<IVulkanRetained> retainedSet)
    {
        VulkanDevice device = (VulkanDevice)queue.Device;
        SparseMemoryBind[] bufferBinds = bufferRecords.Select(static value => value.Bind).ToArray();
        SparseBufferMemoryBindInfo[] bufferInfos = new SparseBufferMemoryBindInfo[bufferRecords.Count];
        SparseImageMemoryBind[] imageBinds = imageRecords.Select(static value => value.Bind).ToArray();
        SparseImageMemoryBindInfo[] imageInfos = new SparseImageMemoryBindInfo[imageRecords.Count];
        SparseMemoryBind[] opaqueBinds = opaqueRecords.Select(static value => value.Bind).ToArray();
        SparseImageOpaqueMemoryBindInfo[] opaqueInfos = new SparseImageOpaqueMemoryBindInfo[opaqueRecords.Count];
        List<VulkanSparseChangeSet> changes = BuildSparseChanges(mappings);
        VulkanQueueWork? work = null;
        bool committed = false;
        try
        {
            work = queue.TakeWork();
            work.EnsureCapacity(0, 0, 0, checked(retainedSet.Count + changes.Count), 0, 0);
            foreach (IVulkanRetained value in retainedSet)
            {
                value.RetainNative();
                work.RetainedTimelines[work.TimelineCount++] = value;
            }
            foreach (VulkanSparseChangeSet change in changes)
            {
                VulkanSparseBindings bindings = change.State.CaptureBindings();
                work.RetainedTimelines[work.TimelineCount++] = bindings;
            }

            lock (queue.Gate)
            {
                device.ThrowIfUnavailable();
                ulong completionValue = queue.ReserveCompletionValue();
                work.CompletionValue = completionValue;
                VkSemaphore completion = queue.CompletionSemaphore;
                fixed (SparseMemoryBind* bufferBindPointer = bufferBinds)
                fixed (SparseBufferMemoryBindInfo* bufferInfoPointer = bufferInfos)
                fixed (SparseImageMemoryBind* imageBindPointer = imageBinds)
                fixed (SparseImageMemoryBindInfo* imageInfoPointer = imageInfos)
                fixed (SparseMemoryBind* opaqueBindPointer = opaqueBinds)
                fixed (SparseImageOpaqueMemoryBindInfo* opaqueInfoPointer = opaqueInfos)
                {
                    for (int index = 0; index < bufferInfos.Length; index++)
                    {
                        bufferInfoPointer[index] = new SparseBufferMemoryBindInfo(
                            bufferRecords[index].Buffer,
                            1,
                            bufferBindPointer + index);
                    }
                    for (int index = 0; index < imageInfos.Length; index++)
                    {
                        imageInfoPointer[index] = new SparseImageMemoryBindInfo(
                            imageRecords[index].Image,
                            1,
                            imageBindPointer + index);
                    }
                    for (int index = 0; index < opaqueInfos.Length; index++)
                    {
                        opaqueInfoPointer[index] = new SparseImageOpaqueMemoryBindInfo(
                            opaqueRecords[index].Image,
                            1,
                            opaqueBindPointer + index);
                    }
                    TimelineSemaphoreSubmitInfo timeline = new()
                    {
                        SType = StructureType.TimelineSemaphoreSubmitInfo,
                        SignalSemaphoreValueCount = 1,
                        PSignalSemaphoreValues = &completionValue,
                    };
                    BindSparseInfo bind = new()
                    {
                        SType = StructureType.BindSparseInfo,
                        PNext = &timeline,
                        BufferBindCount = checked((uint)bufferInfos.Length),
                        PBufferBinds = bufferInfoPointer,
                        ImageBindCount = checked((uint)imageInfos.Length),
                        PImageBinds = imageInfoPointer,
                        ImageOpaqueBindCount = checked((uint)opaqueInfos.Length),
                        PImageOpaqueBinds = opaqueInfoPointer,
                        SignalSemaphoreCount = 1,
                        PSignalSemaphores = &completion,
                    };
                    Result result = QueueBindSparseNative(queue, &bind);
                    switch (result)
                    {
                        case Result.Success:
                            work.NativeAccepted = true;
                            break;
                        case Result.ErrorOutOfHostMemory:
                        case Result.ErrorOutOfDeviceMemory:
                            device.ThrowIfDeviceCallFailed(result, "vkQueueBindSparse");
                            break;
                        case Result.ErrorDeviceLost:
                            throw device.PublishDeviceLoss(result, "vkQueueBindSparse");
                        default:
                            throw device.PublishInternalDeviceLoss(
                                $"vkQueueBindSparse returned uncertain failure {result}.");
                    }

                    foreach (VulkanSparseChangeSet change in changes)
                        change.CommitNoThrow();
                    committed = true;
                    queue.AppendAcceptedNoThrow(work);
                    work = null!;
                    return new QueueCompletion(queue, completionValue);
                }
            }
        }
        finally
        {
            ExitSparseChanges(changes);
            if (committed)
            {
                foreach (VulkanSparseChangeSet change in changes)
                    change.ReleasePreviousNoThrow();
            }
            else
            {
                foreach (VulkanSparseChangeSet change in changes)
                    change.ReleaseReplacementNoThrow();
            }

            if (work is not null)
            {
                for (int index = work.TimelineCount - 1; index >= 0; index--)
                    work.RetainedTimelines[index].ReleaseNative();
                queue.ReturnWork(work);
            }
        }
    }

    private Result QueueBindSparseNative(VulkanQueue queue, BindSparseInfo* bind)
    {
        Result result = Api.QueueBindSparse(queue.Native, 1, bind, default);
        return result;
    }

    private static List<VulkanSparseChangeSet> BuildSparseChanges(
        List<PreparedSparseMapping> mappings)
    {
        VulkanSparseState[] states = mappings
            .Select(static mapping => mapping.State)
            .Distinct<VulkanSparseState>(ReferenceEqualityComparer.Instance)
            .OrderBy(static state => state.Id)
            .ToArray();
        int entered = 0;
        var changes = new List<VulkanSparseChangeSet>(states.Length);
        try
        {
            for (; entered < states.Length; entered++)
                states[entered].Enter();
            foreach (VulkanSparseState state in states)
            {
                changes.Add(new VulkanSparseChangeSet(
                    state,
                    state.BuildReplacement(mappings)));
            }
            return changes;
        }
        catch
        {
            foreach (VulkanSparseChangeSet change in changes)
                change.ReleaseReplacementNoThrow();
            for (int index = entered - 1; index >= 0; index--)
                states[index].Exit();
            throw;
        }
    }

    private static void ExitSparseChanges(List<VulkanSparseChangeSet> changes)
    {
        for (int index = changes.Count - 1; index >= 0; index--)
            changes[index].State.Exit();
    }

    private static SparseMemoryBind CreateOpaqueBind(
        VulkanSparseState state,
        in SparseMappingDesc mapping,
        VulkanHeap? heap)
    {
        ulong tileCount = Math.Max(mapping.ResourceTiles.TileCount, 1);
        ulong resourceOffset = checked((ulong)mapping.ResourceTiles.Start.X * state.Info.Alignment);
        if (mapping.Resource is VulkanTexture texture)
        {
            uint layer = mapping.ResourceTiles.Start.Subresource / texture.Info.MipLevelCount;
            resourceOffset = checked(resourceOffset + state.ImageRequirements.ImageMipTailOffset +
                layer * state.ImageRequirements.ImageMipTailStride);
        }
        return new SparseMemoryBind(
            resourceOffset,
            checked(tileCount * state.Info.Alignment),
            heap?.Memory.Native ?? default,
            heap is null ? 0 : checked(mapping.HeapTileOffset * state.Info.Alignment),
            SparseMemoryBindFlags.None);
    }

    private static SparseImageMemoryBind CreateImageBind(
        VulkanTexture texture,
        VulkanSparseState state,
        in SparseMappingDesc mapping,
        VulkanHeap? heap)
    {
        uint mip = mapping.ResourceTiles.Start.Subresource % texture.Info.MipLevelCount;
        uint layer = mapping.ResourceTiles.Start.Subresource / texture.Info.MipLevelCount;
        SparseTileShape tile = state.Info.TileShape;
        uint width = Math.Min(
            checked(mapping.ResourceTiles.Width * tile.Width),
            MipExtent(texture.Info.Width, mip) - checked(mapping.ResourceTiles.Start.X * tile.Width));
        uint height = Math.Min(
            checked(mapping.ResourceTiles.Height * tile.Height),
            MipExtent(texture.Info.Height, mip) - checked(mapping.ResourceTiles.Start.Y * tile.Height));
        uint depth = Math.Min(
            checked(mapping.ResourceTiles.Depth * tile.Depth),
            MipExtent(texture.Info.Depth, mip) - checked(mapping.ResourceTiles.Start.Z * tile.Depth));
        return new SparseImageMemoryBind(
            new ImageSubresource(VulkanFormats.Aspects(texture.Info.Format), mip, layer),
            new Offset3D(
                checked((int)(mapping.ResourceTiles.Start.X * tile.Width)),
                checked((int)(mapping.ResourceTiles.Start.Y * tile.Height)),
                checked((int)(mapping.ResourceTiles.Start.Z * tile.Depth))),
            new Extent3D(width, height, depth),
            heap?.Memory.Native ?? default,
            heap is null ? 0 : checked(mapping.HeapTileOffset * state.Info.Alignment),
            SparseMemoryBindFlags.None);
    }

    private static SparseResourceInfo GetSparseTextureInfo(
        VulkanDevice device,
        VkImage image,
        in TextureDesc desc,
        out uint memoryTypeBits,
        out SparseImageMemoryRequirements imageRequirements)
    {
        uint count = 0;
        device.Backend.Api.GetImageSparseMemoryRequirements(device.Native, image, &count, null);
        if (count == 0)
            throw new NotSupportedException("The Vulkan image exposes no sparse memory requirements.");
        SparseImageMemoryRequirements[] sparse = new SparseImageMemoryRequirements[count];
        fixed (SparseImageMemoryRequirements* pointer = sparse)
            device.Backend.Api.GetImageSparseMemoryRequirements(device.Native, image, &count, pointer);
        Silk.NET.Vulkan.MemoryRequirements memory;
        device.Backend.Api.GetImageMemoryRequirements(device.Native, image, &memory);
        memoryTypeBits = memory.MemoryTypeBits;
        SparseImageMemoryRequirements selected = sparse[0];
        imageRequirements = selected;
        ulong tileCount = DivideRoundUp(memory.Size, memory.Alignment);
        uint packedCount = checked((uint)DivideRoundUp(
            selected.ImageMipTailSize,
            memory.Alignment));
        return new SparseResourceInfo(
            new SparseTileShape(
                selected.FormatProperties.ImageGranularity.Width,
                selected.FormatProperties.ImageGranularity.Height,
                selected.FormatProperties.ImageGranularity.Depth),
            tileCount,
            new SparsePackedMipInfo(
                selected.ImageMipTailFirstLod,
                desc.MipLevelCount - Math.Min(desc.MipLevelCount, selected.ImageMipTailFirstLod),
                checked((uint)(selected.ImageMipTailOffset / memory.Alignment)),
                packedCount),
            memory.Alignment);
    }

    private VulkanDevice RequireSparseDevice(RhiDevice device)
    {
        VulkanDevice native = RequireDevice(device, nameof(device));
        if (!native.TryGetCapability(out SparseResources? capability) || capability is null)
            throw new NotSupportedException("The Device was not created with SparseResources support.");
        return native;
    }

    private VulkanQueue RequireSparseQueue(RhiQueue queue, string parameterName)
    {
        VulkanQueue native = RequireQueue(queue, parameterName);
        if (native.Type != QueueType.Graphics ||
            native.Index != 0 ||
            !native.SupportsSparseBinding)
        {
            throw new NotSupportedException(
                "Sparse mappings require Vulkan Graphics Queue 0 with sparse-binding support.");
        }
        return native;
    }

    private static VulkanSparseState RequireSparseState(
        Resource resource,
        VulkanDevice device,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(resource, parameterName);
        if (!ReferenceEquals(resource.Device, device))
        {
            throw new ArgumentException(
                "The sparse Resource belongs to a different Vulkan Device.",
                parameterName);
        }
        resource.ThrowIfDisposed();
        return resource switch
        {
            VulkanBuffer { SparseState: not null } buffer => buffer.SparseState,
            VulkanTexture { SparseState: not null } texture => texture.SparseState,
            _ => throw new ArgumentException("The Resource is not a Vulkan sparse resource.", parameterName),
        };
    }

    private static ulong DivideRoundUp(ulong value, ulong divisor) =>
        checked((value + divisor - 1) / divisor);

    private sealed class VulkanSparseState(
        SparseResourceInfo info,
        uint memoryTypeBits,
        SparseImageMemoryRequirements imageRequirements = default)
    {
        private static long s_nextId;
        private readonly object _gate = new();
        private VulkanSparseBindings _bindings = new([]);

        internal long Id { get; } = Interlocked.Increment(ref s_nextId);
        internal SparseResourceInfo Info { get; } = info;
        internal uint MemoryTypeBits { get; } = memoryTypeBits;
        internal SparseImageMemoryRequirements ImageRequirements { get; } = imageRequirements;

        internal void Enter() => Monitor.Enter(_gate);
        internal void Exit() => Monitor.Exit(_gate);

        internal bool TryGet(in SparseTileKey key, out SparseTileBinding binding)
        {
            lock (_gate)
                return _bindings.Values.TryGetValue(key, out binding);
        }

        internal VulkanSparseBindings CaptureBindings()
        {
            lock (_gate)
            {
                _bindings.RetainNative();
                return _bindings;
            }
        }

        internal VulkanSparseBindings BuildReplacement(
            List<PreparedSparseMapping> mappings)
        {
            var replacement = new Dictionary<SparseTileKey, SparseTileBinding>(
                _bindings.Values.Count + mappings.Count);
            try
            {
                foreach ((SparseTileKey key, SparseTileBinding binding) in _bindings.Values)
                {
                    binding.Heap.RetainNative();
                    replacement.Add(key, binding);
                }

                foreach (PreparedSparseMapping prepared in mappings)
                {
                    if (!ReferenceEquals(prepared.State, this))
                        continue;
                    SparseMappingDesc mapping = prepared.Description;
                    uint count = Math.Max(mapping.ResourceTiles.TileCount, 1);
                    for (uint index = 0; index < count; index++)
                    {
                        SparseTileKey key = new(
                            mapping.ResourceTiles.Start.Subresource,
                            checked(mapping.ResourceTiles.Start.X + index),
                            mapping.ResourceTiles.Start.Y,
                            mapping.ResourceTiles.Start.Z);
                        if (replacement.Remove(key, out SparseTileBinding previous))
                            previous.Heap.ReleaseNative();
                        if (prepared.Heap is null)
                            continue;
                        prepared.Heap.RetainNative();
                        ulong heapOffset = mapping.Type == SparseMappingType.Reused
                            ? mapping.HeapTileOffset
                            : checked(mapping.HeapTileOffset + index);
                        replacement.Add(key, new SparseTileBinding(prepared.Heap, heapOffset));
                    }
                }
                return new VulkanSparseBindings(replacement);
            }
            catch
            {
                ReleaseBindingsNoThrow(replacement);
                throw;
            }
        }

        internal VulkanSparseBindings ReplaceBindingsNoThrow(
            VulkanSparseBindings replacement)
        {
            VulkanSparseBindings previous = _bindings;
            _bindings = replacement;
            return previous;
        }

        internal static void ReleaseBindingsNoThrow(
            Dictionary<SparseTileKey, SparseTileBinding>? bindings)
        {
            if (bindings is null)
                return;
            foreach (SparseTileBinding binding in bindings.Values)
            {
                try
                {
                    binding.Heap.ReleaseNative();
                }
                catch
                {
                }
            }
            bindings.Clear();
        }

        internal void Release()
        {
            lock (_gate)
            {
                _bindings.ReleaseNative();
            }
        }
    }

    private sealed class VulkanSparseBindings : IVulkanRetained
    {
        private readonly VulkanLifetime _lifetime;

        internal VulkanSparseBindings(Dictionary<SparseTileKey, SparseTileBinding> values)
        {
            Values = values;
            _lifetime = new(() => VulkanSparseState.ReleaseBindingsNoThrow(Values));
        }

        internal Dictionary<SparseTileKey, SparseTileBinding> Values { get; }
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
    }

    private sealed class VulkanSparseChangeSet(
        VulkanSparseState state,
        VulkanSparseBindings replacement)
    {
        private VulkanSparseBindings? _replacement = replacement;
        private VulkanSparseBindings? _previous;

        internal VulkanSparseState State { get; } = state;

        internal void CommitNoThrow()
        {
            _previous = State.ReplaceBindingsNoThrow(_replacement!);
            _replacement = null;
        }

        internal void ReleaseReplacementNoThrow()
        {
            _replacement?.ReleaseNative();
            _replacement = null;
        }

        internal void ReleasePreviousNoThrow()
        {
            _previous?.ReleaseNative();
            _previous = null;
        }
    }

    private readonly record struct SparseTileKey(uint Subresource, uint X, uint Y, uint Z);
    private readonly record struct SparseTileBinding(VulkanHeap Heap, ulong HeapTileOffset);
    private readonly record struct SparseBufferBindRecord(VkBuffer Buffer, SparseMemoryBind Bind);
    private readonly record struct SparseImageBindRecord(VkImage Image, SparseImageMemoryBind Bind);
    private readonly record struct SparseOpaqueImageBindRecord(VkImage Image, SparseMemoryBind Bind);
    private readonly record struct PreparedSparseMapping(
        VulkanSparseState State,
        SparseMappingDesc Description,
        VulkanHeap? Heap);
}
