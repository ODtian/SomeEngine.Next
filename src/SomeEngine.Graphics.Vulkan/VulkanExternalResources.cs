namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private RhiBuffer ImportBufferCore(
        RhiDevice device,
        ExternalHandle handle,
        in BufferDesc desc,
        in ImportedResourceState state) =>
        throw DirectResourceSharingNotSupported();

    private RhiTexture ImportTextureCore(
        RhiDevice device,
        ExternalHandle handle,
        in TextureDesc desc,
        in ImportedResourceState state) =>
        throw DirectResourceSharingNotSupported();

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
        // Opaque Win32 handles are deliberately excluded from
        // vkGetMemoryWin32HandlePropertiesKHR. Vulkan-created opaque payloads
        // must instead be imported with the same allocation size and memory
        // type index used by the exporter. HeapDesc carries the former and our
        // deterministic memory-type selection recreates the latter for the
        // same physical adapter.
        VulkanMemoryBlock memory = nativeDevice.AllocateMemory(
            desc.Size,
            uint.MaxValue,
            desc.MemoryType,
            nativeDevice.SupportsBufferDeviceAddress,
            ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
            handle.Value);
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

    private ExternalHandle ExportBufferCore(RhiBuffer buffer, ExternalHandleType type) =>
        throw DirectResourceSharingNotSupported();

    private ExternalHandle ExportTextureCore(RhiTexture texture, ExternalHandleType type) =>
        throw DirectResourceSharingNotSupported();

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
        Result result = device.ExternalMemoryApi.GetMemoryWin32Handle(
            device.Native,
            &info,
            &handle);
        device.ThrowIfDeviceCallFailed(result, "vkGetMemoryWin32HandleKHR");
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

    private static ExternalMemoryHandleTypeFlags ToNativeMemoryHandleType(
        ExternalHandleType type) => type switch
    {
        ExternalHandleType.OpaqueWin32 => ExternalMemoryHandleTypeFlags.OpaqueWin32Bit,
        ExternalHandleType.OpaqueWin32Kmt => ExternalMemoryHandleTypeFlags.OpaqueWin32KmtBit,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static NotSupportedException DirectResourceSharingNotSupported() =>
        new(
            "Direct Vulkan Buffer/Texture sharing is not enabled; " +
            "share a Heap and recreate placed resources at the same offsets.");
}
