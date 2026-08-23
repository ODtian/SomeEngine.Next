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
        ThrowIfFailed(
            Api.CreateBuffer(nativeDevice.Native, &createInfo, null, &native),
            "vkCreateBuffer(sparse)");
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements;
            Api.GetBufferMemoryRequirements(nativeDevice.Native, native, &requirements);
            SparseResourceInfo info = new(
                new SparseTileShape(checked((uint)requirements.Alignment), 1, 1),
                DivideRoundUp(requirements.Size, requirements.Alignment),
                default,
                requirements.Alignment);
            var buffer = new VulkanBuffer(
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
                SparseState = new VulkanSparseState(info),
            };
            nativeDevice.RegisterChild(buffer);
            return buffer;
        }
        catch
        {
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
        try
        {
            SparseResourceInfo info = GetSparseTextureInfo(nativeDevice, native, desc);
            var texture = new VulkanTexture(
                nativeDevice,
                native,
                ownedMemory: null,
                heap: null,
                desc,
                0,
                0,
                ownsImage: true)
            {
                SparseState = new VulkanSparseState(info),
            };
            nativeDevice.RegisterChild(texture);
            return texture;
        }
        catch
        {
            Api.DestroyImage(nativeDevice.Native, native, null);
            throw;
        }
    }

    private SparseResourceInfo GetSparseResourceInfoCore(Resource resource)
    {
        ArgumentNullException.ThrowIfNull(resource);
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
        VulkanQueue nativeQueue = RequireQueue(queue, nameof(queue));
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
            PrepareSparseMapping(
                device,
                mapping,
                buffers,
                images,
                opaqueImages,
                prepared,
                retained);
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
        if (copies.IsEmpty)
            return Submit(queue, new QueueSubmitDesc([], [], [], [], []));
        var mappings = new List<SparseMappingDesc>();
        foreach (ref readonly SparseMappingCopyDesc copy in copies)
        {
            VulkanSparseState source = RequireSparseState(copy.Source, nameof(copies));
            _ = RequireSparseState(copy.Destination, nameof(copies));
            uint count = copy.Region.TileCount;
            for (uint index = 0; index < count; index++)
            {
                SparseTileKey sourceKey = new(
                    copy.SourceStart.Subresource,
                    checked(copy.SourceStart.X + index),
                    copy.SourceStart.Y,
                    copy.SourceStart.Z);
                SparseTileCoordinate destination = copy.DestinationStart with
                {
                    X = checked(copy.DestinationStart.X + index),
                };
                SparseTileRegion region = new(destination, 0, 0, 0, 1, Boxed: false);
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

    private void PrepareSparseMapping(
        VulkanDevice device,
        in SparseMappingDesc mapping,
        List<SparseBufferBindRecord> buffers,
        List<SparseImageBindRecord> images,
        List<SparseOpaqueImageBindRecord> opaqueImages,
        List<PreparedSparseMapping> prepared,
        HashSet<IVulkanRetained> retained)
    {
        VulkanSparseState state = RequireSparseState(mapping.Resource, nameof(mapping));
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
            if (mapping.ResourceTiles.Boxed &&
                mapping.ResourceTiles.Start.Subresource % texture.Info.MipLevelCount <
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
        SparseMemoryBind[] bufferBinds = bufferRecords.Select(static value => value.Bind).ToArray();
        SparseBufferMemoryBindInfo[] bufferInfos = new SparseBufferMemoryBindInfo[bufferRecords.Count];
        SparseImageMemoryBind[] imageBinds = imageRecords.Select(static value => value.Bind).ToArray();
        SparseImageMemoryBindInfo[] imageInfos = new SparseImageMemoryBindInfo[imageRecords.Count];
        SparseMemoryBind[] opaqueBinds = opaqueRecords.Select(static value => value.Bind).ToArray();
        SparseImageOpaqueMemoryBindInfo[] opaqueInfos = new SparseImageOpaqueMemoryBindInfo[opaqueRecords.Count];
        IVulkanRetained[] retained = retainedSet.ToArray();
        foreach (IVulkanRetained value in retained)
            value.RetainNative();
        bool accepted = false;
        try
        {
            lock (queue.SubmitGate)
            {
                ulong completionValue = queue.ReserveCompletionValue();
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
                    var pending = new VulkanPendingSubmission(
                        completionValue,
                        [],
                        [],
                        0,
                        retained,
                        retained.Length);
                    queue.PrepareSubmission(pending);
                    ThrowIfFailed(Api.QueueBindSparse(queue.Native, 1, &bind, default), "vkQueueBindSparse");
                    accepted = true;
                    foreach (PreparedSparseMapping mapping in mappings)
                        mapping.State.Apply(mapping.Description, mapping.Heap);
                    queue.RegisterSubmission(pending);
                    return new QueueCompletion(queue, completionValue);
                }
            }
        }
        finally
        {
            if (!accepted)
                for (int index = retained.Length - 1; index >= 0; index--)
                    retained[index].ReleaseNative();
        }
    }

    private static SparseMemoryBind CreateOpaqueBind(
        VulkanSparseState state,
        in SparseMappingDesc mapping,
        VulkanHeap? heap)
    {
        ulong tileCount = Math.Max(mapping.ResourceTiles.TileCount, 1);
        return new SparseMemoryBind(
            checked((ulong)mapping.ResourceTiles.Start.X * state.Info.Alignment),
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
        in TextureDesc desc)
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
        SparseImageMemoryRequirements selected = sparse[0];
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

    private static VulkanSparseState RequireSparseState(Resource resource, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(resource, parameterName);
        return resource switch
        {
            VulkanBuffer { SparseState: not null } buffer => buffer.SparseState,
            VulkanTexture { SparseState: not null } texture => texture.SparseState,
            _ => throw new ArgumentException("The Resource is not a Vulkan sparse resource.", parameterName),
        };
    }

    private static ulong DivideRoundUp(ulong value, ulong divisor) =>
        checked((value + divisor - 1) / divisor);

    private sealed class VulkanSparseState(SparseResourceInfo info)
    {
        private readonly object _gate = new();
        private readonly Dictionary<SparseTileKey, SparseTileBinding> _bindings = [];

        internal SparseResourceInfo Info { get; } = info;

        internal bool TryGet(in SparseTileKey key, out SparseTileBinding binding)
        {
            lock (_gate)
                return _bindings.TryGetValue(key, out binding);
        }

        internal void Apply(in SparseMappingDesc mapping, VulkanHeap? heap)
        {
            lock (_gate)
            {
                uint count = Math.Max(mapping.ResourceTiles.TileCount, 1);
                for (uint index = 0; index < count; index++)
                {
                    SparseTileKey key = new(
                        mapping.ResourceTiles.Start.Subresource,
                        checked(mapping.ResourceTiles.Start.X + index),
                        mapping.ResourceTiles.Start.Y,
                        mapping.ResourceTiles.Start.Z);
                    if (_bindings.Remove(key, out SparseTileBinding previous))
                        previous.Heap.ReleaseNative();
                    if (heap is null)
                        continue;
                    heap.RetainNative();
                    ulong heapOffset = mapping.Type == SparseMappingType.Reused
                        ? mapping.HeapTileOffset
                        : checked(mapping.HeapTileOffset + index);
                    _bindings.Add(key, new SparseTileBinding(heap, heapOffset));
                }
            }
        }

        internal void Release()
        {
            lock (_gate)
            {
                foreach (SparseTileBinding binding in _bindings.Values)
                    binding.Heap.ReleaseNative();
                _bindings.Clear();
            }
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
