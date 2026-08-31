namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    internal RhiMemoryRequirements GetBufferMemoryRequirements(
        RhiDevice device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        ValidateBufferDescription(desc, memoryType);
        VkBuffer native = CreateNativeBuffer(nativeDevice, desc);
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements;
            Api.GetBufferMemoryRequirements(nativeDevice.Native, native, &requirements);
            return new RhiMemoryRequirements(
                requirements.Size,
                requirements.Alignment,
                CompatibleHeapFlags(desc.Usages));
        }
        finally
        {
            Api.DestroyBuffer(nativeDevice.Native, native, null);
        }
    }

    internal RhiMemoryRequirements GetTextureMemoryRequirements(
        RhiDevice device,
        in TextureDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        ValidateTextureDescription(desc);
        VkImage native = CreateNativeImage(nativeDevice, desc, aliasable: true);
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements;
            Api.GetImageMemoryRequirements(nativeDevice.Native, native, &requirements);
            return new RhiMemoryRequirements(
                requirements.Size,
                requirements.Alignment,
                CompatibleHeapFlags(desc.Usages));
        }
        finally
        {
            Api.DestroyImage(nativeDevice.Native, native, null);
        }
    }

    internal TextureCopyFootprint GetTextureCopyFootprint(
        RhiDevice device,
        in TextureDesc desc,
        in BufferTextureCopy copy,
        ulong requestedBufferOffset = 0)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        ValidateTextureDescription(desc);
        if (copy.MipLevel >= desc.MipLevelCount || copy.ArrayLayer >= desc.ArrayLayerCount)
            throw new ArgumentOutOfRangeException(nameof(copy));
        (uint blockWidth, uint blockHeight, uint blockBytes) = VulkanFormats.GetBlockInfo(desc.Format);
        uint rowBlocks = DivideRoundUp(copy.Width, blockWidth);
        uint rowCount = DivideRoundUp(copy.Height, blockHeight);
        ulong rowSize = checked((ulong)rowBlocks * blockBytes);
        uint pitchAlignment = nativeDevice.Capabilities.Limits.TextureDataPitchAlignment;
        uint rowPitch = checked((uint)AlignUp(rowSize, pitchAlignment));
        ulong offset = AlignUp(
            requestedBufferOffset,
            nativeDevice.Capabilities.Limits.TextureDataPlacementAlignment);
        ulong totalSize = checked((ulong)rowPitch * rowCount * copy.Depth);
        return new TextureCopyFootprint(offset, rowPitch, rowCount, rowSize, totalSize);
    }

    internal RhiHeap CreateHeap(RhiDevice device, in HeapDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        ValidateHeapDescription(desc);
        VulkanMemoryBlock memory = nativeDevice.AllocateMemory(
            desc.Size,
            uint.MaxValue,
            desc.MemoryType,
            deviceAddress: nativeDevice.SupportsBufferDeviceAddress,
            externalHandleTypes: (desc.Flags & HeapFlags.Shareable) != 0
                ? ExternalMemoryHandleTypeFlags.OpaqueWin32Bit
                : ExternalMemoryHandleTypeFlags.None);
        VulkanHeap? heap = null;
        try
        {
            heap = new VulkanHeap(nativeDevice, memory, desc);
            return RegisterChildOrDispose(nativeDevice, heap);
        }
        catch
        {
            if (heap is null)
                memory.Release();
            throw;
        }
    }

    internal RhiBuffer CreateBuffer(
        RhiDevice device,
        in BufferDesc desc,
        MemoryType memoryType = MemoryType.DeviceLocal)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        ValidateBufferDescription(desc, memoryType);
        VkBuffer native = CreateNativeBuffer(nativeDevice, desc);
        VulkanMemoryBlock? memory = null;
        VulkanBuffer? buffer = null;
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements;
            Api.GetBufferMemoryRequirements(nativeDevice.Native, native, &requirements);
            memory = nativeDevice.AllocateMemory(
                requirements.Size,
                requirements.MemoryTypeBits,
                memoryType,
                deviceAddress: nativeDevice.SupportsBufferDeviceAddress,
                externalHandleTypes: (desc.Usages & BufferUsages.Shareable) != 0
                    ? ExternalMemoryHandleTypeFlags.OpaqueWin32Bit
                    : ExternalMemoryHandleTypeFlags.None);
            nativeDevice.ThrowIfDeviceCallFailed(
                Api.BindBufferMemory(nativeDevice.Native, native, memory.Native, 0),
                "vkBindBufferMemory");
            buffer = new VulkanBuffer(
                nativeDevice,
                native,
                memory,
                heap: null,
                desc,
                memoryType,
                allocationOffset: 0,
                allocationSize: requirements.Size);
            return RegisterChildOrDispose(nativeDevice, buffer);
        }
        catch
        {
            if (buffer is null)
            {
                Api.DestroyBuffer(nativeDevice.Native, native, null);
                memory?.Release();
            }
            throw;
        }
    }

    internal RhiBuffer CreatePlacedBuffer(
        RhiDevice device,
        RhiHeap heap,
        ulong offset,
        in BufferDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanHeap nativeHeap = RequireHeap(nativeDevice, heap, nameof(heap));
        ValidateBufferDescription(desc, nativeHeap.Info.MemoryType);
        bool shareable = (desc.Usages & BufferUsages.Shareable) != 0;
        ValidatePlacedShareability(nativeHeap, shareable);
        if (shareable)
            ValidateExternalBufferPlacement(nativeDevice, desc);
        VkBuffer native = CreateNativeBuffer(nativeDevice, desc);
        VulkanBuffer? buffer = null;
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements = GetBufferMemoryRequirements(
                nativeDevice,
                native,
                rejectDedicated: shareable);
            ValidatePlacedRange(nativeHeap, offset, requirements);
            nativeDevice.ThrowIfDeviceCallFailed(
                Api.BindBufferMemory(nativeDevice.Native, native, nativeHeap.Memory.Native, offset),
                "vkBindBufferMemory(placed)");
            buffer = new VulkanBuffer(
                nativeDevice,
                native,
                ownedMemory: null,
                nativeHeap,
                desc,
                nativeHeap.Info.MemoryType,
                offset,
                requirements.Size);
            return RegisterChildOrDispose(nativeDevice, buffer);
        }
        catch
        {
            if (buffer is null)
                Api.DestroyBuffer(nativeDevice.Native, native, null);
            throw;
        }
    }

    internal RhiTexture CreateTexture(RhiDevice device, in TextureDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        ValidateTextureDescription(desc);
        VkImage native = CreateNativeImage(nativeDevice, desc, aliasable: false);
        VulkanMemoryBlock? memory = null;
        VulkanTexture? texture = null;
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements;
            Api.GetImageMemoryRequirements(nativeDevice.Native, native, &requirements);
            memory = nativeDevice.AllocateMemory(
                requirements.Size,
                requirements.MemoryTypeBits,
                MemoryType.DeviceLocal,
                deviceAddress: false,
                externalHandleTypes: (desc.Usages & TextureUsages.Shareable) != 0
                    ? ExternalMemoryHandleTypeFlags.OpaqueWin32Bit
                    : ExternalMemoryHandleTypeFlags.None);
            nativeDevice.ThrowIfDeviceCallFailed(
                Api.BindImageMemory(nativeDevice.Native, native, memory.Native, 0),
                "vkBindImageMemory");
            texture = new VulkanTexture(
                nativeDevice,
                native,
                memory,
                heap: null,
                desc,
                allocationOffset: 0,
                allocationSize: requirements.Size,
                ownsImage: true);
            return RegisterChildOrDispose(nativeDevice, texture);
        }
        catch
        {
            if (texture is null)
            {
                Api.DestroyImage(nativeDevice.Native, native, null);
                memory?.Release();
            }
            throw;
        }
    }

    internal RhiTexture CreatePlacedTexture(
        RhiDevice device,
        RhiHeap heap,
        ulong offset,
        in TextureDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanHeap nativeHeap = RequireHeap(nativeDevice, heap, nameof(heap));
        if (nativeHeap.Info.MemoryType != MemoryType.DeviceLocal)
            throw new ArgumentException("Placed Textures require a DeviceLocal Heap.", nameof(heap));
        ValidateTextureDescription(desc);
        bool shareable = (desc.Usages & TextureUsages.Shareable) != 0;
        ValidatePlacedShareability(nativeHeap, shareable);
        if (shareable)
            ValidateExternalImagePlacement(nativeDevice, desc);
        VkImage native = CreateNativeImage(nativeDevice, desc, aliasable: true);
        VulkanTexture? texture = null;
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements = GetImageMemoryRequirements(
                nativeDevice,
                native,
                rejectDedicated: shareable);
            ValidatePlacedRange(nativeHeap, offset, requirements);
            nativeDevice.ThrowIfDeviceCallFailed(
                Api.BindImageMemory(nativeDevice.Native, native, nativeHeap.Memory.Native, offset),
                "vkBindImageMemory(placed)");
            texture = new VulkanTexture(
                nativeDevice,
                native,
                ownedMemory: null,
                nativeHeap,
                desc,
                offset,
                requirements.Size,
                ownsImage: true);
            return RegisterChildOrDispose(nativeDevice, texture);
        }
        catch
        {
            if (texture is null)
                Api.DestroyImage(nativeDevice.Native, native, null);
            throw;
        }
    }

    internal MappedBuffer Map(RhiBuffer buffer, MapType type, in BufferRange range)
    {
        VulkanBuffer native = RequireBuffer(buffer, nameof(buffer));
        BufferRange resolved = range.Resolve(native.Info.Size);
        VulkanMappingLease mapping = native.Mapping
            ?? throw new NotSupportedException("Sparse Vulkan Buffers cannot be mapped directly.");
        return mapping.Map(type, resolved);
    }

    internal BufferCbv CreateBufferCbv(RhiDevice device, in BufferCbvDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        _ = RequireBuffer(nativeDevice, desc.Buffer, nameof(desc));
        _ = desc.Range.Resolve(desc.Buffer.Info.Size);
        var view = new VulkanBufferCbv(nativeDevice, desc);
        return RegisterChildOrDispose(nativeDevice, view);
    }

    internal BufferSrv CreateBufferSrv(RhiDevice device, in BufferSrvDesc desc) =>
        CreateBufferView<VulkanBufferSrv, BufferSrvDesc>(
            device,
            desc.Buffer,
            desc.Range,
            desc.Format,
            desc,
            static (nativeDevice, description, native) =>
                new VulkanBufferSrv(nativeDevice, description, native));

    internal BufferUav CreateBufferUav(RhiDevice device, in BufferUavDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        _ = RequireBuffer(nativeDevice, desc.Buffer, nameof(desc));
        _ = desc.Range.Resolve(desc.Buffer.Info.Size);
        if (desc.CounterBuffer is not null)
        {
            VulkanBuffer counter = RequireBuffer(nativeDevice, desc.CounterBuffer, nameof(desc));
            if (desc.CounterOffset > counter.Info.Size - Math.Min(counter.Info.Size, 4))
                throw new ArgumentOutOfRangeException(nameof(desc.CounterOffset));
        }
        VkBufferView native = desc.Format.HasValue
            ? CreateNativeBufferView(nativeDevice, (VulkanBuffer)desc.Buffer, desc.Range, desc.Format.Value)
            : default;
        VulkanBufferUav? view = null;
        try
        {
            view = new VulkanBufferUav(nativeDevice, desc, native);
        }
        catch
        {
            if (native.Handle != 0)
                Api.DestroyBufferView(nativeDevice.Native, native, null);
            throw;
        }
        return RegisterChildOrDispose(nativeDevice, view);
    }

    internal TextureSrv CreateTextureSrv(RhiDevice device, in TextureSrvDesc desc) =>
        CreateTextureView<VulkanTextureSrv, TextureSrvDesc>(
            device,
            desc.Texture,
            desc.Range,
            desc.Format,
            desc.Dimension,
            desc,
            static (nativeDevice, description, native) =>
                new VulkanTextureSrv(nativeDevice, description, native));

    internal TextureUav CreateTextureUav(RhiDevice device, in TextureUavDesc desc) =>
        CreateTextureView<VulkanTextureUav, TextureUavDesc>(
            device,
            desc.Texture,
            desc.Range,
            desc.Format,
            desc.Dimension,
            desc,
            static (nativeDevice, description, native) =>
                new VulkanTextureUav(nativeDevice, description, native));

    internal ColorAttachmentView CreateColorAttachmentView(
        RhiDevice device,
        in ColorAttachmentViewDesc desc) =>
        CreateTextureView<VulkanColorAttachmentView, ColorAttachmentViewDesc>(
            device,
            desc.Texture,
            desc.Range,
            desc.Format,
            desc.Dimension,
            desc,
            static (nativeDevice, description, native) =>
                new VulkanColorAttachmentView(nativeDevice, description, native));

    internal DepthStencilView CreateDepthStencilView(
        RhiDevice device,
        in DepthStencilViewDesc desc) =>
        CreateTextureView<VulkanDepthStencilView, DepthStencilViewDesc>(
            device,
            desc.Texture,
            desc.Range,
            desc.Format,
            desc.Dimension,
            desc,
            static (nativeDevice, description, native) =>
                new VulkanDepthStencilView(nativeDevice, description, native));

    internal RhiSampler CreateSampler(RhiDevice device, in SamplerDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        ValidateSampler(desc);
        SamplerCreateInfo createInfo = new()
        {
            SType = StructureType.SamplerCreateInfo,
            MagFilter = ToNative(desc.MagFilter),
            MinFilter = ToNative(desc.MinFilter),
            MipmapMode = desc.MipFilter == FilterType.Linear
                ? SamplerMipmapMode.Linear
                : SamplerMipmapMode.Nearest,
            AddressModeU = ToNative(desc.AddressU),
            AddressModeV = ToNative(desc.AddressV),
            AddressModeW = ToNative(desc.AddressW),
            MipLodBias = desc.MipLodBias,
            AnisotropyEnable = desc.MaximumAnisotropy > 1,
            MaxAnisotropy = desc.MaximumAnisotropy,
            CompareEnable = desc.Comparison.HasValue,
            CompareOp = ToNative(desc.Comparison.GetValueOrDefault()),
            MinLod = desc.MinimumLod,
            MaxLod = desc.MaximumLod,
            UnnormalizedCoordinates = false,
        };
        SamplerCustomBorderColorCreateInfoEXT customBorder = default;
        createInfo.BorderColor = ConfigureBorderColor(
            nativeDevice,
            desc.BorderColor,
            ref customBorder);
        if (customBorder.SType != 0)
            createInfo.PNext = &customBorder;
        VkSampler native = default;
        nativeDevice.ThrowIfDeviceCallFailed(
            Api.CreateSampler(nativeDevice.Native, &createInfo, null, &native),
            "vkCreateSampler");
        VulkanSampler? sampler = null;
        try
        {
            sampler = new VulkanSampler(nativeDevice, desc, native);
        }
        catch
        {
            Api.DestroySampler(nativeDevice.Native, native, null);
            throw;
        }
        return RegisterChildOrDispose(nativeDevice, sampler);
    }

    private TView CreateBufferView<TView, TDescription>(
        RhiDevice device,
        RhiBuffer buffer,
        in BufferRange range,
        RhiFormat? format,
        in TDescription description,
        Func<VulkanDevice, TDescription, VkBufferView, TView> factory)
        where TView : GraphicsObject
        where TDescription : struct
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanBuffer nativeBuffer = RequireBuffer(nativeDevice, buffer, nameof(buffer));
        _ = range.Resolve(buffer.Info.Size);
        VkBufferView native = format.HasValue
            ? CreateNativeBufferView(nativeDevice, nativeBuffer, range, format.Value)
            : default;
        TView view;
        try
        {
            view = factory(nativeDevice, description, native);
        }
        catch
        {
            if (native.Handle != 0)
                Api.DestroyBufferView(nativeDevice.Native, native, null);
            throw;
        }
        return RegisterChildOrDispose(nativeDevice, view);
    }

    private TView CreateTextureView<TView, TDescription>(
        RhiDevice device,
        RhiTexture texture,
        in TextureSubresourceRange range,
        RhiFormat format,
        TextureViewDimension dimension,
        in TDescription description,
        Func<VulkanDevice, TDescription, VkImageView, TView> factory)
        where TView : GraphicsObject
        where TDescription : struct
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        VulkanTexture nativeTexture = RequireTexture(nativeDevice, texture, nameof(texture));
        ValidateTextureRange(nativeTexture.Info, range);
        ImageViewCreateInfo createInfo = new()
        {
            SType = StructureType.ImageViewCreateInfo,
            Image = nativeTexture.Native,
            ViewType = ToNative(dimension),
            Format = VulkanFormats.ToNative(format),
            Components = new ComponentMapping(
                ComponentSwizzle.Identity,
                ComponentSwizzle.Identity,
                ComponentSwizzle.Identity,
                ComponentSwizzle.Identity),
            SubresourceRange = ToNative(range),
        };
        VkImageView native = default;
        nativeDevice.ThrowIfDeviceCallFailed(
            Api.CreateImageView(nativeDevice.Native, &createInfo, null, &native),
            "vkCreateImageView");
        TView view;
        try
        {
            view = factory(nativeDevice, description, native);
        }
        catch
        {
            Api.DestroyImageView(nativeDevice.Native, native, null);
            throw;
        }
        return RegisterChildOrDispose(nativeDevice, view);
    }

    private VkBuffer CreateNativeBuffer(VulkanDevice device, in BufferDesc desc)
    {
        BufferCreateInfo createInfo = CreateBufferInfo(device, desc);
        ExternalMemoryBufferCreateInfo external = new()
        {
            SType = StructureType.ExternalMemoryBufferCreateInfo,
            HandleTypes = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
        };
        if ((desc.Usages & BufferUsages.Shareable) != 0)
            createInfo.PNext = &external;
        VkBuffer native = default;
        device.ThrowIfDeviceCallFailed(
            Api.CreateBuffer(device.Native, &createInfo, null, &native),
            "vkCreateBuffer");
        return native;
    }

    private static BufferCreateInfo CreateBufferInfo(
        VulkanDevice device,
        in BufferDesc desc)
    {
        BufferUsageFlags usage = ToNative(desc.Usages);
        if (device.SupportsBufferDeviceAddress)
            usage |= BufferUsageFlags.ShaderDeviceAddressBit;
        return new BufferCreateInfo
        {
            SType = StructureType.BufferCreateInfo,
            Size = desc.Size,
            Usage = usage,
            SharingMode = SharingMode.Exclusive,
        };
    }

    private VkImage CreateNativeImage(
        VulkanDevice device,
        in TextureDesc desc,
        bool aliasable,
        bool sparse = false)
    {
        RhiFormat[] viewFormats = desc.PermittedViewFormats.ToArray();
        VkFormat[] nativeViewFormats = new VkFormat[viewFormats.Length];
        for (int index = 0; index < viewFormats.Length; index++)
            nativeViewFormats[index] = VulkanFormats.ToNative(viewFormats[index]);
        uint[] families = device.QueueFamilyIndices;
        fixed (VkFormat* viewFormatPointer = nativeViewFormats)
        fixed (uint* familyPointer = families)
        {
            ImageFormatListCreateInfo formatList = new()
            {
                SType = StructureType.ImageFormatListCreateInfo,
                ViewFormatCount = checked((uint)nativeViewFormats.Length),
                PViewFormats = viewFormatPointer,
            };
            ExternalMemoryImageCreateInfo external = new()
            {
                SType = StructureType.ExternalMemoryImageCreateInfo,
                HandleTypes = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
                PNext = nativeViewFormats.Length == 0 ? null : &formatList,
            };
            ImageCreateFlags flags = aliasable ? ImageCreateFlags.CreateAliasBit : ImageCreateFlags.None;
            if (sparse)
            {
                flags |= ImageCreateFlags.CreateSparseBindingBit |
                    ImageCreateFlags.CreateSparseResidencyBit |
                    ImageCreateFlags.CreateSparseAliasedBit;
            }
            if (nativeViewFormats.Length != 0)
                flags |= ImageCreateFlags.CreateMutableFormatBit;
            if (desc.Dimension == TextureDimension.Texture2D && desc.ArrayLayerCount >= 6)
                flags |= ImageCreateFlags.CreateCubeCompatibleBit;
            ImageCreateInfo createInfo = new()
            {
                SType = StructureType.ImageCreateInfo,
                PNext = (desc.Usages & TextureUsages.Shareable) != 0
                    ? &external
                    : nativeViewFormats.Length == 0 ? null : &formatList,
                Flags = flags,
                ImageType = ToNative(desc.Dimension),
                Format = VulkanFormats.ToNative(desc.Format),
                Extent = new Extent3D(desc.Width, desc.Height, desc.Depth),
                MipLevels = desc.MipLevelCount,
                ArrayLayers = desc.ArrayLayerCount,
                Samples = ToNativeSampleCount(desc.SampleCount),
                Tiling = ImageTiling.Optimal,
                Usage = ToNative(desc.Usages),
                SharingMode = families.Length > 1 ? SharingMode.Concurrent : SharingMode.Exclusive,
                QueueFamilyIndexCount = checked((uint)families.Length),
                PQueueFamilyIndices = familyPointer,
                InitialLayout = ImageLayout.Undefined,
            };
            VkImage native = default;
            device.ThrowIfDeviceCallFailed(
                Api.CreateImage(device.Native, &createInfo, null, &native),
                "vkCreateImage");
            return native;
        }
    }

    private VkBufferView CreateNativeBufferView(
        VulkanDevice device,
        VulkanBuffer buffer,
        in BufferRange range,
        RhiFormat format)
    {
        BufferRange resolved = range.Resolve(buffer.Info.Size);
        BufferViewCreateInfo createInfo = new()
        {
            SType = StructureType.BufferViewCreateInfo,
            Buffer = buffer.Native,
            Format = VulkanFormats.ToNative(format),
            Offset = resolved.Offset,
            Range = resolved.Size,
        };
        VkBufferView native = default;
        device.ThrowIfDeviceCallFailed(
            Api.CreateBufferView(device.Native, &createInfo, null, &native),
            "vkCreateBufferView");
        return native;
    }

    private static void ValidateBufferDescription(in BufferDesc desc, MemoryType memoryType)
    {
        if (desc.Size == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.Usages == BufferUsages.None)
            throw new ArgumentOutOfRangeException(nameof(desc));
        const BufferUsages knownUsages =
            BufferUsages.CopySource |
            BufferUsages.CopyDestination |
            BufferUsages.Constant |
            BufferUsages.ShaderRead |
            BufferUsages.ShaderWrite |
            BufferUsages.Vertex |
            BufferUsages.Index |
            BufferUsages.Indirect |
            BufferUsages.AccelerationStructure |
            BufferUsages.AccelerationStructureInput |
            BufferUsages.Predication |
            BufferUsages.StreamOutput |
            BufferUsages.QueryResolve |
            BufferUsages.Shareable;
        if ((desc.Usages & ~knownUsages) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Buffer usage contains unknown bits.");
        ValidateNodePlacement(desc.NodePlacement);
        ValidateBufferMemoryType(desc, memoryType);
    }

    private static void ValidateBufferMemoryType(in BufferDesc desc, MemoryType memoryType)
    {
        const BufferUsages uploadUsages =
            BufferUsages.CopySource |
            BufferUsages.Constant |
            BufferUsages.ShaderRead |
            BufferUsages.Vertex |
            BufferUsages.Index |
            BufferUsages.Indirect |
            BufferUsages.AccelerationStructureInput |
            BufferUsages.Predication |
            BufferUsages.Shareable;
        const BufferUsages readbackUsages =
            BufferUsages.CopyDestination |
            BufferUsages.QueryResolve |
            BufferUsages.Shareable;
        bool valid = memoryType switch
        {
            MemoryType.DeviceLocal => true,
            MemoryType.Upload => (desc.Usages & ~uploadUsages) == 0,
            MemoryType.Readback => (desc.Usages & ~readbackUsages) == 0,
            _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
        };
        if (!valid)
        {
            throw new ArgumentException(
                $"The Buffer usage is incompatible with {memoryType} memory.",
                nameof(desc));
        }
    }

    private static void ValidateTextureDescription(in TextureDesc desc)
    {
        if (desc.Width == 0 || desc.Height == 0 || desc.Depth == 0 ||
            desc.MipLevelCount == 0 || desc.ArrayLayerCount == 0 || desc.SampleCount == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.Usages == TextureUsages.None)
            throw new ArgumentOutOfRangeException(nameof(desc));
        ValidateNodePlacement(desc.NodePlacement);
        if (desc.Dimension == TextureDimension.Texture1D && (desc.Height != 1 || desc.Depth != 1))
            throw new ArgumentException("A Texture1D must have Height and Depth equal to one.", nameof(desc));
        if (desc.Dimension == TextureDimension.Texture2D && desc.Depth != 1)
            throw new ArgumentException("A Texture2D must have Depth equal to one.", nameof(desc));
        if (desc.Dimension == TextureDimension.Texture3D && desc.ArrayLayerCount != 1)
            throw new ArgumentException("A Texture3D must have one array layer.", nameof(desc));
        if (desc.SampleCount != 1 && (desc.MipLevelCount != 1 || desc.Dimension != TextureDimension.Texture2D))
            throw new ArgumentException("Multisampled Textures must be 2D with one mip level.", nameof(desc));
    }

    private static void ValidateHeapDescription(in HeapDesc desc)
    {
        if (desc.Size == 0 || desc.Alignment == 0 || !BitOperations.IsPow2(desc.Alignment))
            throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.Flags == HeapFlags.None)
            throw new ArgumentOutOfRangeException(nameof(desc));
        if (desc.CreationNodeMask != 1 || desc.VisibleNodeMask != 1)
            throw new NotSupportedException("Vulkan Heap node masks must both be one.");
    }

    private static void ValidateNodePlacement(in ResourceNodePlacement placement)
    {
        if (placement.CreationNodeMask is not 0 and not 1 ||
            placement.VisibleNodeMask is not 0 and not 1)
            throw new NotSupportedException("Vulkan resource node masks must resolve to node zero.");
    }

    private static void ValidatePlacedRange(
        VulkanHeap heap,
        ulong offset,
        in Silk.NET.Vulkan.MemoryRequirements requirements)
    {
        if (offset % requirements.Alignment != 0 ||
            offset > heap.Info.Size || requirements.Size > heap.Info.Size - offset)
            throw new ArgumentOutOfRangeException(nameof(offset));
        if ((requirements.MemoryTypeBits & (1u << checked((int)heap.Memory.TypeIndex))) == 0)
            throw new ArgumentException("The Vulkan Heap memory type is incompatible with the resource.", nameof(heap));
    }

    private static void ValidateTextureRange(
        TextureInfo info,
        in TextureSubresourceRange range)
    {
        if (range.MipLevelCount == 0 || range.ArrayLayerCount == 0 || range.Aspects == TextureAspects.None ||
            range.FirstMipLevel >= info.MipLevelCount ||
            range.MipLevelCount > info.MipLevelCount - range.FirstMipLevel ||
            range.FirstArrayLayer >= info.ArrayLayerCount ||
            range.ArrayLayerCount > info.ArrayLayerCount - range.FirstArrayLayer)
            throw new ArgumentOutOfRangeException(nameof(range));
    }

    private static void ValidateSampler(in SamplerDesc desc)
    {
        if (desc.MaximumAnisotropy == 0 || !float.IsFinite(desc.MipLodBias) ||
            !float.IsFinite(desc.MinimumLod) || float.IsNaN(desc.MaximumLod) ||
            desc.MaximumLod < desc.MinimumLod)
            throw new ArgumentOutOfRangeException(nameof(desc));
    }

    private VulkanHeap RequireHeap(VulkanDevice device, RhiHeap heap, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(heap, parameterName);
        if (heap is not VulkanHeap native || !ReferenceEquals(native.Device, device))
            throw new ArgumentException("The Heap belongs to a different Vulkan Device.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private VulkanBuffer RequireBuffer(RhiBuffer buffer, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(buffer, parameterName);
        if (buffer is not VulkanBuffer native ||
            native.Device is not VulkanDevice nativeDevice ||
            !ReferenceEquals(nativeDevice.Backend, this))
            throw new ArgumentException("The Buffer belongs to a different graphics backend.", parameterName);
        native.ThrowIfDisposed();
        native.Device.ThrowIfUnavailable();
        return native;
    }

    private static VulkanBuffer RequireBuffer(
        VulkanDevice device,
        RhiBuffer buffer,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(buffer, parameterName);
        if (buffer is not VulkanBuffer native || !ReferenceEquals(native.Device, device))
            throw new ArgumentException("The Buffer belongs to a different Vulkan Device.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private static VulkanTexture RequireTexture(
        VulkanDevice device,
        RhiTexture texture,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(texture, parameterName);
        if (texture is not VulkanTexture native || !ReferenceEquals(native.Device, device))
            throw new ArgumentException("The Texture belongs to a different Vulkan Device.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private static BufferUsageFlags ToNative(BufferUsages usages)
    {
        BufferUsageFlags result = 0;
        if ((usages & BufferUsages.CopySource) != 0) result |= BufferUsageFlags.TransferSrcBit;
        if ((usages & BufferUsages.CopyDestination) != 0) result |= BufferUsageFlags.TransferDstBit;
        if ((usages & BufferUsages.Constant) != 0) result |= BufferUsageFlags.UniformBufferBit;
        if ((usages & BufferUsages.ShaderRead) != 0) result |= BufferUsageFlags.StorageBufferBit | BufferUsageFlags.UniformTexelBufferBit;
        if ((usages & BufferUsages.ShaderWrite) != 0) result |= BufferUsageFlags.StorageBufferBit | BufferUsageFlags.StorageTexelBufferBit | BufferUsageFlags.ShaderDeviceAddressBit;
        if ((usages & BufferUsages.Vertex) != 0) result |= BufferUsageFlags.VertexBufferBit;
        if ((usages & BufferUsages.Index) != 0) result |= BufferUsageFlags.IndexBufferBit;
        if ((usages & (BufferUsages.Indirect | BufferUsages.Predication)) != 0)
            result |= BufferUsageFlags.IndirectBufferBit;
        if ((usages & BufferUsages.Indirect) != 0)
            result |= BufferUsageFlags.ShaderDeviceAddressBit;
        if ((usages & BufferUsages.AccelerationStructure) != 0) result |= BufferUsageFlags.AccelerationStructureStorageBitKhr | BufferUsageFlags.ShaderDeviceAddressBit;
        if ((usages & BufferUsages.AccelerationStructureInput) != 0) result |= BufferUsageFlags.AccelerationStructureBuildInputReadOnlyBitKhr | BufferUsageFlags.ShaderDeviceAddressBit;
        if ((usages & BufferUsages.StreamOutput) != 0) result |= BufferUsageFlags.TransformFeedbackBufferBitExt | BufferUsageFlags.TransformFeedbackCounterBufferBitExt;
        if ((usages & BufferUsages.QueryResolve) != 0) result |= BufferUsageFlags.TransferDstBit;
        return result;
    }

    private static ImageUsageFlags ToNative(TextureUsages usages)
    {
        ImageUsageFlags result = 0;
        if ((usages & TextureUsages.CopySource) != 0) result |= ImageUsageFlags.TransferSrcBit;
        if ((usages & TextureUsages.CopyDestination) != 0) result |= ImageUsageFlags.TransferDstBit;
        if ((usages & TextureUsages.Sampled) != 0) result |= ImageUsageFlags.SampledBit;
        if ((usages & (TextureUsages.Storage | TextureUsages.SamplerFeedback)) != 0) result |= ImageUsageFlags.StorageBit;
        if ((usages & TextureUsages.ColorAttachment) != 0) result |= ImageUsageFlags.ColorAttachmentBit;
        if ((usages & TextureUsages.DepthStencilAttachment) != 0) result |= ImageUsageFlags.DepthStencilAttachmentBit;
        if ((usages & TextureUsages.ShadingRate) != 0) result |= ImageUsageFlags.FragmentShadingRateAttachmentBitKhr;
        return result;
    }

    private static HeapFlags CompatibleHeapFlags(BufferUsages usages)
    {
        HeapFlags result = HeapFlags.Buffers;
        if ((usages & BufferUsages.Shareable) != 0) result |= HeapFlags.Shareable;
        return result;
    }

    private static HeapFlags CompatibleHeapFlags(TextureUsages usages)
    {
        HeapFlags result = HeapFlags.Textures;
        if ((usages & (TextureUsages.ColorAttachment | TextureUsages.DepthStencilAttachment)) != 0)
            result |= HeapFlags.Attachments;
        if ((usages & TextureUsages.Shareable) != 0) result |= HeapFlags.Shareable;
        return result;
    }

    private static ImageType ToNative(TextureDimension dimension) => dimension switch
    {
        TextureDimension.Texture1D => ImageType.Type1D,
        TextureDimension.Texture2D => ImageType.Type2D,
        TextureDimension.Texture3D => ImageType.Type3D,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    private static ImageViewType ToNative(TextureViewDimension dimension) => dimension switch
    {
        TextureViewDimension.Texture1D => ImageViewType.Type1D,
        TextureViewDimension.Texture1DArray => ImageViewType.Type1DArray,
        TextureViewDimension.Texture2D or TextureViewDimension.Texture2DMultisampled => ImageViewType.Type2D,
        TextureViewDimension.Texture2DArray or TextureViewDimension.Texture2DMultisampledArray => ImageViewType.Type2DArray,
        TextureViewDimension.Cube => ImageViewType.TypeCube,
        TextureViewDimension.CubeArray => ImageViewType.TypeCubeArray,
        TextureViewDimension.Texture3D => ImageViewType.Type3D,
        _ => throw new ArgumentOutOfRangeException(nameof(dimension)),
    };

    internal static ImageSubresourceRange ToNative(in TextureSubresourceRange range) => new(
        ToNative(range.Aspects),
        range.FirstMipLevel,
        range.MipLevelCount,
        range.FirstArrayLayer,
        range.ArrayLayerCount);

    internal static ImageAspectFlags ToNative(TextureAspects aspects)
    {
        ImageAspectFlags result = 0;
        if ((aspects & TextureAspects.Color) != 0) result |= ImageAspectFlags.ColorBit;
        if ((aspects & TextureAspects.Depth) != 0) result |= ImageAspectFlags.DepthBit;
        if ((aspects & TextureAspects.Stencil) != 0) result |= ImageAspectFlags.StencilBit;
        if ((aspects & TextureAspects.Plane0) != 0) result |= ImageAspectFlags.Plane0Bit;
        if ((aspects & TextureAspects.Plane1) != 0) result |= ImageAspectFlags.Plane1Bit;
        if ((aspects & TextureAspects.Plane2) != 0) result |= ImageAspectFlags.Plane2Bit;
        return result;
    }

    private static SampleCountFlags ToNativeSampleCount(uint count) => count switch
    {
        1 => SampleCountFlags.Count1Bit,
        2 => SampleCountFlags.Count2Bit,
        4 => SampleCountFlags.Count4Bit,
        8 => SampleCountFlags.Count8Bit,
        16 => SampleCountFlags.Count16Bit,
        32 => SampleCountFlags.Count32Bit,
        _ => throw new ArgumentOutOfRangeException(nameof(count)),
    };

    private static Silk.NET.Vulkan.Filter ToNative(FilterType filter) => filter switch
    {
        FilterType.Nearest => Silk.NET.Vulkan.Filter.Nearest,
        FilterType.Linear => Silk.NET.Vulkan.Filter.Linear,
        _ => throw new ArgumentOutOfRangeException(nameof(filter)),
    };

    private static SamplerAddressMode ToNative(AddressType address) => address switch
    {
        AddressType.Repeat => SamplerAddressMode.Repeat,
        AddressType.MirrorRepeat => SamplerAddressMode.MirroredRepeat,
        AddressType.ClampToEdge => SamplerAddressMode.ClampToEdge,
        AddressType.ClampToBorder => SamplerAddressMode.ClampToBorder,
        AddressType.MirrorOnce => SamplerAddressMode.MirrorClampToEdge,
        _ => throw new ArgumentOutOfRangeException(nameof(address)),
    };

    internal static CompareOp ToNative(CompareOperation comparison) => comparison switch
    {
        CompareOperation.Never => CompareOp.Never,
        CompareOperation.Less => CompareOp.Less,
        CompareOperation.Equal => CompareOp.Equal,
        CompareOperation.LessOrEqual => CompareOp.LessOrEqual,
        CompareOperation.Greater => CompareOp.Greater,
        CompareOperation.NotEqual => CompareOp.NotEqual,
        CompareOperation.GreaterOrEqual => CompareOp.GreaterOrEqual,
        CompareOperation.Always => CompareOp.Always,
        _ => throw new ArgumentOutOfRangeException(nameof(comparison)),
    };

    private static BorderColor ConfigureBorderColor(
        VulkanDevice device,
        in Vector4 color,
        ref SamplerCustomBorderColorCreateInfoEXT custom)
    {
        if (color == Vector4.Zero) return BorderColor.FloatTransparentBlack;
        if (color == new Vector4(0, 0, 0, 1)) return BorderColor.FloatOpaqueBlack;
        if (color == Vector4.One) return BorderColor.FloatOpaqueWhite;
        if (!device.ExtendedFeatures.CustomBorderColorWithoutFormat)
        {
            throw new NotSupportedException(
                "The Vulkan adapter requires VK_EXT_custom_border_color for this Sampler border color.");
        }
        custom = new SamplerCustomBorderColorCreateInfoEXT
        {
            SType = StructureType.SamplerCustomBorderColorCreateInfoExt,
            CustomBorderColor = new ClearColorValue(
                color.X,
                color.Y,
                color.Z,
                color.W),
            Format = VkFormat.Undefined,
        };
        return BorderColor.FloatCustomExt;
    }

    internal static ulong AlignUp(ulong value, ulong alignment) =>
        checked((value + alignment - 1) & ~(alignment - 1));

    private static uint DivideRoundUp(uint value, uint divisor) =>
        checked((value + divisor - 1) / divisor);

    private sealed class VulkanMemoryBlock
    {
        private readonly VulkanDevice _device;
        private VkDeviceMemory _native;
        private nint _mapped;

        internal VulkanMemoryBlock(
            VulkanDevice device,
            VkDeviceMemory native,
            ulong size,
            uint typeIndex,
            MemoryPropertyFlags properties,
            nint mapped)
        {
            _device = device;
            _native = native;
            Size = size;
            TypeIndex = typeIndex;
            Properties = properties;
            _mapped = mapped;
        }

        internal VkDeviceMemory Native => _native;
        internal ulong Size { get; }
        internal uint TypeIndex { get; }
        internal MemoryPropertyFlags Properties { get; }
        internal nint Mapped => _mapped;
        internal bool Coherent => (Properties & MemoryPropertyFlags.HostCoherentBit) != 0;

        internal void Release()
        {
            VkDeviceMemory native = _native;
            if (native.Handle == 0)
                return;
            _native = default;
            if (_mapped != 0)
            {
                _device.Backend.Api.UnmapMemory(_device.Native, native);
                _mapped = 0;
            }
            _device.Backend.Api.FreeMemory(_device.Native, native, null);
        }
    }

    private sealed class VulkanHeap : RhiHeap, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanLifetime _lifetime;
        internal VulkanHeap(VulkanDevice device, VulkanMemoryBlock memory, in HeapDesc desc)
            : base(device, new HeapInfo(
                desc.Size,
                desc.Alignment,
                desc.MemoryType,
                desc.Flags,
                desc.CreationNodeMask,
                desc.VisibleNodeMask), desc.Label)
        {
            _device = device;
            Memory = memory;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VulkanMemoryBlock Memory { get; }

        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();

        internal override void Release(bool fromParent)
        {
            _device.UnregisterChild(this);
            _lifetime.Release();
        }

        private void DestroyNative() => Memory.Release();
    }

    private sealed class VulkanBuffer : RhiBuffer, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanMemoryBlock? _ownedMemory;
        private readonly VulkanHeap? _heap;
        private readonly VulkanLifetime _lifetime;
        private VkBuffer _native;

        internal VulkanBuffer(
            VulkanDevice device,
            VkBuffer native,
            VulkanMemoryBlock? ownedMemory,
            VulkanHeap? heap,
            in BufferDesc desc,
            MemoryType memoryType,
            ulong allocationOffset,
            ulong allocationSize,
            PipelineSync? initialSync = null,
            ResourceAccess? initialAccess = null,
            QueueType? initialQueueType = null,
            bool sparse = false)
            : base(
                device,
                heap,
                new BufferInfo(
                    desc.Size,
                    desc.Usages,
                    memoryType,
                    allocationOffset,
                    allocationSize,
                    1,
                    1),
                initialSync ?? InitialSync(memoryType),
                initialAccess ?? InitialAccess(memoryType),
                desc.Label,
                initialQueueType)
        {
            _device = device;
            _native = native;
            _ownedMemory = ownedMemory;
            _heap = heap;
            _heap?.RetainNative();
            VulkanMemoryBlock? memory = ownedMemory ?? heap?.Memory;
            if (!sparse && memory is null)
                throw new ArgumentNullException(nameof(ownedMemory));
            Mapping = memory is null
                ? null
                : new VulkanMappingLease(this, memory, allocationOffset);
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VkBuffer Native => _native;
        internal VulkanMemoryBlock Memory => _ownedMemory ?? _heap?.Memory
            ?? throw new InvalidOperationException("The sparse Buffer has no bound memory.");
        internal ulong DeviceAddress
        {
            get
            {
                BufferDeviceAddressInfo info = new()
                {
                    SType = StructureType.BufferDeviceAddressInfo,
                    Buffer = _native,
                };
                return _device.Backend.Api.GetBufferDeviceAddress(_device.Native, &info);
            }
        }
        internal VulkanMappingLease? Mapping { get; }
        internal VulkanSparseState? SparseState { get; init; }

        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();

        internal override void Release(bool fromParent)
        {
            _device.UnregisterChild(this);
            _lifetime.Release();
        }

        private void DestroyNative()
        {
            SparseState?.Release();
            Mapping?.DisposeCurrent();
            VkBuffer native = _native;
            _native = default;
            if (native.Handle != 0 && _device.Native.Handle != 0)
                _device.Backend.Api.DestroyBuffer(_device.Native, native, null);
            _ownedMemory?.Release();
            _heap?.ReleaseNative();
        }
    }

    private sealed class VulkanTexture : RhiTexture, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanMemoryBlock? _ownedMemory;
        private readonly VulkanHeap? _heap;
        private readonly bool _ownsImage;
        private readonly VulkanLifetime _lifetime;
        private VkImage _native;

        internal VulkanTexture(
            VulkanDevice device,
            VkImage native,
            VulkanMemoryBlock? ownedMemory,
            VulkanHeap? heap,
            in TextureDesc desc,
            ulong allocationOffset,
            ulong allocationSize,
            bool ownsImage,
            PipelineSync initialSync = PipelineSync.None,
            ResourceAccess initialAccess = ResourceAccess.NoAccess,
            TextureLayout initialLayout = TextureLayout.Undefined,
            QueueType? initialQueueType = null)
            : base(
                device,
                heap,
                new TextureInfo(
                    desc.Dimension,
                    desc.Width,
                    desc.Height,
                    desc.Depth,
                    desc.MipLevelCount,
                    desc.ArrayLayerCount,
                    desc.SampleCount,
                    desc.Format,
                    desc.Usages,
                    MemoryType.DeviceLocal,
                    desc.PermittedViewFormats,
                    allocationOffset,
                    allocationSize,
                    1,
                    1),
                initialSync,
                initialAccess,
                initialLayout,
                desc.Label,
                initialQueueType)
        {
            _device = device;
            _native = native;
            _ownedMemory = ownedMemory;
            _heap = heap;
            _heap?.RetainNative();
            _ownsImage = ownsImage;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VkImage Native => _native;
        internal VulkanSparseState? SparseState { get; init; }
        internal VulkanImageState? SwapchainState { get; set; }
        internal VulkanMemoryBlock Memory => _ownedMemory ?? _heap?.Memory
            ?? throw new InvalidOperationException("The sparse or swapchain Texture has no bound memory.");

        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();

        internal override void Release(bool fromParent)
        {
            _device.UnregisterChild(this);
            _lifetime.Release();
        }

        private void DestroyNative()
        {
            SparseState?.Release();
            VkImage native = _native;
            _native = default;
            if (_ownsImage && native.Handle != 0 && _device.Native.Handle != 0)
                _device.Backend.Api.DestroyImage(_device.Native, native, null);
            _ownedMemory?.Release();
            _heap?.ReleaseNative();
        }
    }

    private sealed class VulkanMappingLease : MappingLease
    {
        private readonly VulkanMemoryBlock _memory;
        private readonly ulong _allocationOffset;
        private MapType _type;

        internal VulkanMappingLease(
            VulkanBuffer buffer,
            VulkanMemoryBlock memory,
            ulong allocationOffset)
            : base(buffer)
        {
            _memory = memory;
            _allocationOffset = allocationOffset;
        }

        internal MappedBuffer Map(MapType type, BufferRange range)
        {
            if (_memory.Mapped == 0)
                throw new NotSupportedException("The Vulkan Buffer memory is not host visible.");
            if (type == MapType.Read && Buffer.Info.MemoryType == MemoryType.Upload)
                throw new ArgumentException("Upload Buffers do not support read mappings.", nameof(type));
            if (type == MapType.Write && Buffer.Info.MemoryType == MemoryType.Readback)
                throw new ArgumentException("Readback Buffers do not support write mappings.", nameof(type));
            ulong sequence = PrepareNextSequence();
            _type = type;
            Publish(sequence, range);
            nint pointer = checked(_memory.Mapped + (nint)(_allocationOffset + range.Offset));
            return new MappedBuffer(this, pointer, checked((int)range.Size), sequence);
        }

        protected override void FlushCore(in BufferRange range)
        {
            if (_type == MapType.Read || _memory.Coherent)
                return;
            FlushOrInvalidate(range, flush: true);
        }

        protected override void InvalidateCore(in BufferRange range)
        {
            if (_type == MapType.Write || _memory.Coherent)
                return;
            FlushOrInvalidate(range, flush: false);
        }

        protected override void UnmapCore()
        {
        }

        private void FlushOrInvalidate(in BufferRange range, bool flush)
        {
            VulkanDevice device = (VulkanDevice)Buffer.Device;
            ulong atom = device.NonCoherentAtomSize;
            ulong absoluteOffset = _allocationOffset + range.Offset;
            ulong offset = absoluteOffset & ~(atom - 1);
            ulong end = Math.Min(
                AlignUp(absoluteOffset + range.Size, atom),
                _memory.Size);
            MappedMemoryRange nativeRange = new()
            {
                SType = StructureType.MappedMemoryRange,
                Memory = _memory.Native,
                Offset = offset,
                Size = end - offset,
            };
            Result result = flush
                ? device.Backend.Api.FlushMappedMemoryRanges(device.Native, 1, &nativeRange)
                : device.Backend.Api.InvalidateMappedMemoryRanges(device.Native, 1, &nativeRange);
            device.ThrowIfDeviceCallFailed(
                result,
                flush ? "vkFlushMappedMemoryRanges" : "vkInvalidateMappedMemoryRanges");
        }
    }

    private abstract class VulkanBufferViewBase
    {
        internal abstract VkBufferView Native { get; }
    }

    private sealed class VulkanBufferCbv : BufferCbv, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanBuffer _resource;
        private readonly VulkanLifetime _lifetime;
        internal VulkanBufferCbv(VulkanDevice device, in BufferCbvDesc desc) : base(device, desc)
        {
            _device = device;
            _resource = (VulkanBuffer)desc.Buffer;
            _resource.RetainNative();
            _lifetime = new VulkanLifetime(_resource.ReleaseNative);
        }
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
    }

    private sealed class VulkanBufferSrv : BufferSrv, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanBuffer _resource;
        private readonly VulkanLifetime _lifetime;
        private VkBufferView _native;
        internal VulkanBufferSrv(VulkanDevice device, in BufferSrvDesc desc, VkBufferView native) : base(device, desc) { _device = device; _resource = (VulkanBuffer)desc.Buffer; _resource.RetainNative(); _native = native; _lifetime = new VulkanLifetime(DestroyNative); }
        internal VkBufferView Native => _native;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative() { if (_native.Handle != 0) _device.Backend.Api.DestroyBufferView(_device.Native, _native, null); _native = default; _resource.ReleaseNative(); }
    }

    private sealed class VulkanBufferUav : BufferUav, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanBuffer _resource;
        private readonly VulkanBuffer? _counter;
        private readonly VulkanLifetime _lifetime;
        private VkBufferView _native;
        internal VulkanBufferUav(VulkanDevice device, in BufferUavDesc desc, VkBufferView native) : base(device, desc) { _device = device; _resource = (VulkanBuffer)desc.Buffer; _counter = (VulkanBuffer?)desc.CounterBuffer; _resource.RetainNative(); _counter?.RetainNative(); _native = native; _lifetime = new VulkanLifetime(DestroyNative); }
        internal VkBufferView Native => _native;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative() { if (_native.Handle != 0) _device.Backend.Api.DestroyBufferView(_device.Native, _native, null); _native = default; _counter?.ReleaseNative(); _resource.ReleaseNative(); }
    }

    private interface IVulkanTextureView : IVulkanRetained
    {
        VulkanTexture Texture { get; }
    }

    private static void ValidatePlacedShareability(
        VulkanHeap heap,
        bool resourceShareable)
    {
        if (resourceShareable && (heap.Info.Flags & HeapFlags.Shareable) == 0)
        {
            throw new ArgumentException(
                "A Shareable placed resource requires a Shareable Heap.",
                nameof(heap));
        }
    }

    private static void ValidateExternalBufferPlacement(
        VulkanDevice device,
        in BufferDesc desc)
    {
        BufferCreateInfo create = CreateBufferInfo(device, desc);
        PhysicalDeviceExternalBufferInfo info = new()
        {
            SType = StructureType.PhysicalDeviceExternalBufferInfo,
            Flags = create.Flags,
            Usage = create.Usage,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
        };
        ExternalBufferProperties properties = new()
        {
            SType = StructureType.ExternalBufferProperties,
        };
        device.Backend.Api.GetPhysicalDeviceExternalBufferProperties(
            device.PhysicalDevice,
            &info,
            &properties);
        ValidateExternalMemoryProperties(properties.ExternalMemoryProperties);
    }

    private static void ValidateExternalImagePlacement(
        VulkanDevice device,
        in TextureDesc desc)
    {
        PhysicalDeviceExternalImageFormatInfo external = new()
        {
            SType = StructureType.PhysicalDeviceExternalImageFormatInfo,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
        };
        ImageCreateFlags flags = ImageCreateFlags.CreateAliasBit;
        if (!desc.PermittedViewFormats.IsEmpty)
            flags |= ImageCreateFlags.CreateMutableFormatBit;
        if (desc.Dimension == TextureDimension.Texture2D && desc.ArrayLayerCount >= 6)
            flags |= ImageCreateFlags.CreateCubeCompatibleBit;
        PhysicalDeviceImageFormatInfo2 info = new()
        {
            SType = StructureType.PhysicalDeviceImageFormatInfo2,
            PNext = &external,
            Format = VulkanFormats.ToNative(desc.Format),
            Type = ToNative(desc.Dimension),
            Tiling = ImageTiling.Optimal,
            Usage = ToNative(desc.Usages),
            Flags = flags,
        };
        ExternalImageFormatProperties externalProperties = new()
        {
            SType = StructureType.ExternalImageFormatProperties,
        };
        ImageFormatProperties2 properties = new()
        {
            SType = StructureType.ImageFormatProperties2,
            PNext = &externalProperties,
        };
        Result result = device.Backend.Api.GetPhysicalDeviceImageFormatProperties2(
            device.PhysicalDevice,
            &info,
            &properties);
        if (result == Result.ErrorFormatNotSupported)
            throw new NotSupportedException("The Vulkan image description does not support OpaqueWin32 external memory.");
        device.ThrowIfDeviceCallFailed(
            result,
            "vkGetPhysicalDeviceImageFormatProperties2(external image)");
        ValidateExternalMemoryProperties(externalProperties.ExternalMemoryProperties);
    }

    private static void ValidateExternalMemoryProperties(
        in ExternalMemoryProperties properties)
    {
        if ((properties.CompatibleHandleTypes &
             ExternalMemoryHandleTypeFlags.OpaqueWin32Bit) == 0)
            throw new NotSupportedException("The Vulkan resource is incompatible with OpaqueWin32 external memory.");
        if ((properties.ExternalMemoryFeatures &
             ExternalMemoryFeatureFlags.DedicatedOnlyBit) != 0)
            throw new NotSupportedException("The Vulkan resource requires a dedicated external-memory allocation and cannot be placed in a Heap.");
        if ((properties.ExternalMemoryFeatures &
             (ExternalMemoryFeatureFlags.ImportableBit |
              ExternalMemoryFeatureFlags.ExportableBit)) == 0)
            throw new NotSupportedException("The Vulkan resource cannot use OpaqueWin32 external memory.");
    }

    private static Silk.NET.Vulkan.MemoryRequirements GetBufferMemoryRequirements(
        VulkanDevice device,
        VkBuffer buffer,
        bool rejectDedicated)
    {
        MemoryDedicatedRequirements dedicated = new()
        {
            SType = StructureType.MemoryDedicatedRequirements,
        };
        MemoryRequirements2 requirements = new()
        {
            SType = StructureType.MemoryRequirements2,
            PNext = rejectDedicated ? &dedicated : null,
        };
        BufferMemoryRequirementsInfo2 info = new()
        {
            SType = StructureType.BufferMemoryRequirementsInfo2,
            Buffer = buffer,
        };
        device.Backend.Api.GetBufferMemoryRequirements2(
            device.Native,
            &info,
            &requirements);
        if (rejectDedicated && dedicated.RequiresDedicatedAllocation)
            throw new NotSupportedException("The Vulkan Buffer requires a dedicated external-memory allocation and cannot be placed in a Heap.");
        return requirements.MemoryRequirements;
    }

    private static Silk.NET.Vulkan.MemoryRequirements GetImageMemoryRequirements(
        VulkanDevice device,
        VkImage image,
        bool rejectDedicated)
    {
        MemoryDedicatedRequirements dedicated = new()
        {
            SType = StructureType.MemoryDedicatedRequirements,
        };
        MemoryRequirements2 requirements = new()
        {
            SType = StructureType.MemoryRequirements2,
            PNext = rejectDedicated ? &dedicated : null,
        };
        ImageMemoryRequirementsInfo2 info = new()
        {
            SType = StructureType.ImageMemoryRequirementsInfo2,
            Image = image,
        };
        device.Backend.Api.GetImageMemoryRequirements2(
            device.Native,
            &info,
            &requirements);
        if (rejectDedicated && dedicated.RequiresDedicatedAllocation)
            throw new NotSupportedException("The Vulkan Texture requires a dedicated external-memory allocation and cannot be placed in a Heap.");
        return requirements.MemoryRequirements;
    }

    private sealed class VulkanTextureSrv : TextureSrv, IVulkanTextureView
    {
        private readonly VulkanDevice _device;
        private readonly VulkanTexture _resource;
        private readonly VulkanLifetime _lifetime;
        private VkImageView _native;
        internal VulkanTextureSrv(VulkanDevice device, in TextureSrvDesc desc, VkImageView native) : base(device, desc) { _device = device; _resource = (VulkanTexture)desc.Texture; _resource.RetainNative(); _native = native; _lifetime = new VulkanLifetime(DestroyNative); }
        internal VkImageView Native => _native;
        VulkanTexture IVulkanTextureView.Texture => _resource;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative() { if (_native.Handle != 0) _device.Backend.Api.DestroyImageView(_device.Native, _native, null); _native = default; _resource.ReleaseNative(); }
    }

    private sealed class VulkanTextureUav : TextureUav, IVulkanTextureView
    {
        private readonly VulkanDevice _device;
        private readonly VulkanTexture _resource;
        private readonly VulkanLifetime _lifetime;
        private VkImageView _native;
        internal VulkanTextureUav(VulkanDevice device, in TextureUavDesc desc, VkImageView native) : base(device, desc) { _device = device; _resource = (VulkanTexture)desc.Texture; _resource.RetainNative(); _native = native; _lifetime = new VulkanLifetime(DestroyNative); }
        internal VkImageView Native => _native;
        VulkanTexture IVulkanTextureView.Texture => _resource;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative() { if (_native.Handle != 0) _device.Backend.Api.DestroyImageView(_device.Native, _native, null); _native = default; _resource.ReleaseNative(); }
    }

    private sealed class VulkanColorAttachmentView : ColorAttachmentView, IVulkanTextureView
    {
        private readonly VulkanDevice _device;
        private readonly VulkanTexture _resource;
        private readonly VulkanLifetime _lifetime;
        private VkImageView _native;
        internal VulkanColorAttachmentView(VulkanDevice device, in ColorAttachmentViewDesc desc, VkImageView native) : base(device, desc) { _device = device; _resource = (VulkanTexture)desc.Texture; _resource.RetainNative(); _native = native; _lifetime = new VulkanLifetime(DestroyNative); }
        internal VkImageView Native => _native;
        VulkanTexture IVulkanTextureView.Texture => _resource;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative() { if (_native.Handle != 0) _device.Backend.Api.DestroyImageView(_device.Native, _native, null); _native = default; _resource.ReleaseNative(); }
    }

    private sealed class VulkanDepthStencilView : DepthStencilView, IVulkanTextureView
    {
        private readonly VulkanDevice _device;
        private readonly VulkanTexture _resource;
        private readonly VulkanLifetime _lifetime;
        private VkImageView _native;
        internal VulkanDepthStencilView(VulkanDevice device, in DepthStencilViewDesc desc, VkImageView native) : base(device, desc) { _device = device; _resource = (VulkanTexture)desc.Texture; _resource.RetainNative(); _native = native; _lifetime = new VulkanLifetime(DestroyNative); }
        internal VkImageView Native => _native;
        VulkanTexture IVulkanTextureView.Texture => _resource;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative() { if (_native.Handle != 0) _device.Backend.Api.DestroyImageView(_device.Native, _native, null); _native = default; _resource.ReleaseNative(); }
    }

    private sealed class VulkanSampler : RhiSampler, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanLifetime _lifetime;
        private VkSampler _native;
        internal VulkanSampler(VulkanDevice device, in SamplerDesc desc, VkSampler native) : base(device, desc) { _device = device; _native = native; _lifetime = new VulkanLifetime(DestroyNative); }
        internal VkSampler Native => _native;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative() { if (_native.Handle != 0) _device.Backend.Api.DestroySampler(_device.Native, _native, null); _native = default; }
    }

    private static PipelineSync InitialSync(MemoryType memoryType) => memoryType switch
    {
        MemoryType.Readback => PipelineSync.Copy,
        _ => PipelineSync.None,
    };

    private static ResourceAccess InitialAccess(MemoryType memoryType) => memoryType switch
    {
        MemoryType.Upload => ResourceAccess.NoAccess,
        MemoryType.Readback => ResourceAccess.CopyDestination,
        _ => ResourceAccess.NoAccess,
    };
}

internal sealed unsafe partial class VulkanBackend
{
    private sealed partial class VulkanDevice
    {
        internal uint[] QueueFamilyIndices => _queues.Values
            .Select(static queue => queue.FamilyIndex)
            .Distinct()
            .ToArray();

        internal VulkanMemoryBlock AllocateMemory(
            ulong size,
            uint memoryTypeBits,
            MemoryType memoryType,
            bool deviceAddress,
            ExternalMemoryHandleTypeFlags externalHandleTypes = ExternalMemoryHandleTypeFlags.None,
            nint importHandle = 0)
        {
            (uint typeIndex, MemoryPropertyFlags properties) = FindMemoryType(memoryTypeBits, memoryType);
            MemoryAllocateFlagsInfo flags = new()
            {
                SType = StructureType.MemoryAllocateFlagsInfo,
                Flags = deviceAddress && SupportsBufferDeviceAddress
                    ? MemoryAllocateFlags.DeviceAddressBit
                    : MemoryAllocateFlags.None,
            };
            ExportMemoryAllocateInfo export = new()
            {
                SType = StructureType.ExportMemoryAllocateInfo,
                HandleTypes = externalHandleTypes,
            };
            ImportMemoryWin32HandleInfoKHR import = new()
            {
                SType = StructureType.ImportMemoryWin32HandleInfoKhr,
                HandleType = externalHandleTypes,
                Handle = importHandle,
            };
            void* next = flags.Flags == MemoryAllocateFlags.None ? null : &flags;
            if (externalHandleTypes != ExternalMemoryHandleTypeFlags.None)
            {
                if (importHandle != 0)
                {
                    import.PNext = next;
                    next = &import;
                }
                else
                {
                    export.PNext = next;
                    next = &export;
                }
            }
            MemoryAllocateInfo allocateInfo = new()
            {
                SType = StructureType.MemoryAllocateInfo,
                PNext = next,
                AllocationSize = size,
                MemoryTypeIndex = typeIndex,
            };
            VkDeviceMemory native = default;
#if SOMEENGINE_TESTING
            Backend.FaultHooks.Before(VulkanCallPoint.AllocateMemory);
            bool overridden = Backend.FaultHooks.TryOverride(
                VulkanCallPoint.AllocateMemory,
                out Result injectedResult);
#endif
            Result allocationResult =
#if SOMEENGINE_TESTING
                overridden
                    ? injectedResult
                    :
#endif
                Backend.Api.AllocateMemory(
                Native,
                &allocateInfo,
                null,
                &native);
#if SOMEENGINE_TESTING
            Backend.FaultHooks.After(VulkanCallPoint.AllocateMemory);
#endif
            ThrowIfDeviceCallFailed(allocationResult, "vkAllocateMemory");
            nint mapped = 0;
            try
            {
                if ((properties & MemoryPropertyFlags.HostVisibleBit) != 0)
                {
                    void* pointer = null;
                    ThrowIfDeviceCallFailed(
                        Backend.Api.MapMemory(Native, native, 0, size, 0, &pointer),
                        "vkMapMemory");
                    mapped = (nint)pointer;
                }
                return new VulkanMemoryBlock(this, native, size, typeIndex, properties, mapped);
            }
            catch
            {
                Backend.Api.FreeMemory(Native, native, null);
                throw;
            }
        }

        private (uint Index, MemoryPropertyFlags Properties) FindMemoryType(
            uint memoryTypeBits,
            MemoryType memoryType)
        {
            MemoryPropertyFlags required = memoryType switch
            {
                MemoryType.DeviceLocal => MemoryPropertyFlags.DeviceLocalBit,
                MemoryType.Upload or MemoryType.Readback => MemoryPropertyFlags.HostVisibleBit,
                _ => throw new ArgumentOutOfRangeException(nameof(memoryType)),
            };
            MemoryPropertyFlags preferred = memoryType switch
            {
                MemoryType.Upload => MemoryPropertyFlags.HostCoherentBit,
                MemoryType.Readback => MemoryPropertyFlags.HostCachedBit,
                _ => MemoryPropertyFlags.DeviceLocalBit,
            };
            uint fallback = uint.MaxValue;
            for (uint index = 0; index < _memoryProperties.MemoryTypeCount; index++)
            {
                if ((memoryTypeBits & (1u << checked((int)index))) == 0)
                    continue;
                MemoryPropertyFlags properties = _memoryProperties.MemoryTypes[(int)index].PropertyFlags;
                if ((properties & required) != required)
                    continue;
                if ((properties & preferred) == preferred)
                    return (index, properties);
                fallback = index;
            }
            if (fallback != uint.MaxValue)
            {
                return (
                    fallback,
                    _memoryProperties.MemoryTypes[(int)fallback].PropertyFlags);
            }
            throw new GraphicsException(
                GraphicsError.OutOfMemory,
                $"No Vulkan memory type satisfies {memoryType} with mask 0x{memoryTypeBits:X8}.");
        }
    }
}
