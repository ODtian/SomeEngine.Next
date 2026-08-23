namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private RhiBuffer ImportBufferCore(
        RhiDevice device,
        ExternalHandle handle,
        in BufferDesc desc,
        in ImportedResourceState state)
    {
        VulkanDevice nativeDevice = RequireExternalResourceDevice(device);
        ValidateExternalHandle(handle);
        if ((desc.Usages & BufferUsages.Shareable) == 0 || state.Layout.HasValue)
            throw new ArgumentException("An imported Buffer requires Shareable usage and no Texture layout.", nameof(desc));
        ValidateBufferDescription(desc, MemoryType.DeviceLocal);
        VkBuffer native = CreateNativeBuffer(nativeDevice, desc);
        VulkanMemoryBlock? memory = null;
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements;
            Api.GetBufferMemoryRequirements(nativeDevice.Native, native, &requirements);
            memory = nativeDevice.AllocateMemory(
                requirements.Size,
                requirements.MemoryTypeBits,
                MemoryType.DeviceLocal,
                NeedsDeviceAddress(desc.Usages),
                ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
                handle.Value);
            ThrowIfFailed(
                Api.BindBufferMemory(nativeDevice.Native, native, memory.Native, 0),
                "vkBindBufferMemory(imported)");
            var buffer = new VulkanBuffer(
                nativeDevice,
                native,
                memory,
                heap: null,
                desc,
                MemoryType.DeviceLocal,
                0,
                requirements.Size,
                state.Sync,
                state.Access,
                state.QueueType);
            nativeDevice.RegisterChild(buffer);
            return buffer;
        }
        catch
        {
            Api.DestroyBuffer(nativeDevice.Native, native, null);
            memory?.Release();
            throw;
        }
    }

    private RhiTexture ImportTextureCore(
        RhiDevice device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state)
    {
        VulkanDevice nativeDevice = RequireExternalResourceDevice(device);
        ValidateExternalHandle(handle);
        if ((desc.Usages & TextureUsages.Shareable) == 0 || !state.Layout.HasValue)
            throw new ArgumentException("An imported Texture requires Shareable usage and an initial layout.", nameof(desc));
        ValidateTextureDescription(desc);
        VkImage native = CreateNativeImage(nativeDevice, desc, aliasable: false);
        VulkanMemoryBlock? memory = null;
        try
        {
            Silk.NET.Vulkan.MemoryRequirements requirements;
            Api.GetImageMemoryRequirements(nativeDevice.Native, native, &requirements);
            memory = nativeDevice.AllocateMemory(
                requirements.Size,
                requirements.MemoryTypeBits,
                MemoryType.DeviceLocal,
                deviceAddress: false,
                ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
                handle.Value);
            ThrowIfFailed(
                Api.BindImageMemory(nativeDevice.Native, native, memory.Native, 0),
                "vkBindImageMemory(imported)");
            var texture = new VulkanTexture(
                nativeDevice,
                native,
                memory,
                heap: null,
                desc,
                0,
                requirements.Size,
                ownsImage: true,
                state.Sync,
                state.Access,
                state.Layout.Value,
                state.QueueType);
            nativeDevice.RegisterChild(texture);
            return texture;
        }
        catch
        {
            Api.DestroyImage(nativeDevice.Native, native, null);
            memory?.Release();
            throw;
        }
    }

    private RhiHeap ImportHeapCore(
        RhiDevice device,
        ExternalHandle handle,
        in HeapDesc desc)
    {
        VulkanDevice nativeDevice = RequireExternalResourceDevice(device);
        ValidateExternalHandle(handle);
        ValidateHeapDescription(desc);
        if ((desc.Flags & HeapFlags.Shareable) == 0)
            throw new ArgumentException("An imported Heap requires Shareable flags.", nameof(desc));
        VulkanMemoryBlock memory = nativeDevice.AllocateMemory(
            desc.Size,
            uint.MaxValue,
            desc.MemoryType,
            nativeDevice.SupportsBufferDeviceAddress,
            ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
            handle.Value);
        try
        {
            var heap = new VulkanHeap(nativeDevice, memory, desc);
            nativeDevice.RegisterChild(heap);
            return heap;
        }
        catch
        {
            memory.Release();
            throw;
        }
    }

    private ExternalHandle ExportBufferCore(RhiBuffer buffer, ExternalHandleType type)
    {
        VulkanBuffer native = RequireBuffer(buffer, nameof(buffer));
        if ((native.Info.Usages & BufferUsages.Shareable) == 0)
            throw new ArgumentException("The Buffer was not created as Shareable.", nameof(buffer));
        return ExportMemory((VulkanDevice)native.Device, native.Memory.Native, type);
    }

    private ExternalHandle ExportTextureCore(RhiTexture texture, ExternalHandleType type)
    {
        if (texture is not VulkanTexture native || native.Device is not VulkanDevice device ||
            !ReferenceEquals(device.Backend, this))
            throw new ArgumentException("The Texture belongs to a different graphics backend.", nameof(texture));
        native.ThrowIfDisposed();
        if ((native.Info.Usages & TextureUsages.Shareable) == 0)
            throw new ArgumentException("The Texture was not created as Shareable.", nameof(texture));
        return ExportMemory(device, native.Memory.Native, type);
    }

    private ExternalHandle ExportHeapCore(RhiHeap heap, ExternalHandleType type)
    {
        if (heap is not VulkanHeap native || native.Device is not VulkanDevice device ||
            !ReferenceEquals(device.Backend, this))
            throw new ArgumentException("The Heap belongs to a different graphics backend.", nameof(heap));
        native.ThrowIfDisposed();
        if ((native.Info.Flags & HeapFlags.Shareable) == 0)
            throw new ArgumentException("The Heap was not created as Shareable.", nameof(heap));
        return ExportMemory(device, native.Memory.Native, type);
    }

    private ExternalHandle ExportMemory(
        VulkanDevice device,
        VkDeviceMemory memory,
        ExternalHandleType type)
    {
        if (type != ExternalHandleType.OpaqueWin32)
            throw new NotSupportedException("This Vulkan Device exposes OpaqueWin32 external memory only.");
        MemoryGetWin32HandleInfoKHR info = new()
        {
            SType = StructureType.MemoryGetWin32HandleInfoKhr,
            Memory = memory,
            HandleType = ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
        };
        nint handle = 0;
        ThrowIfFailed(
            device.ExternalMemoryApi.GetMemoryWin32Handle(device.Native, &info, &handle),
            "vkGetMemoryWin32HandleKHR");
        return new ExternalHandle(
            type,
            handle,
            static value => _ = CloseHandle(value));
    }

    private VulkanDevice RequireExternalResourceDevice(RhiDevice device)
    {
        VulkanDevice native = RequireDevice(device, nameof(device));
        if (!native.TryGetCapability(out ExternalResources? capability) || capability is null)
            throw new NotSupportedException("The Device was not created with ExternalResources support.");
        return native;
    }

    private static void ValidateExternalHandle(ExternalHandle handle)
    {
        ArgumentNullException.ThrowIfNull(handle);
        if (handle.Type != ExternalHandleType.OpaqueWin32)
            throw new NotSupportedException("This Vulkan Device exposes OpaqueWin32 external memory only.");
        _ = handle.Value;
    }
}
