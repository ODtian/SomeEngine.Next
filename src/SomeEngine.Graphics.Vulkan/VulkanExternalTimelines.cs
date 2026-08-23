namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private ExternalTimeline CreateExternalTimelineCore(
        RhiDevice device,
        ulong initialValue,
        string? label)
    {
        VulkanDevice nativeDevice = RequireExternalTimelineDevice(device);
        VkSemaphore native = CreateTimelineSemaphore(
            nativeDevice,
            initialValue,
            ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit);
        var timeline = new VulkanExternalTimeline(nativeDevice, native, label);
        nativeDevice.RegisterChild(timeline);
        return timeline;
    }

    private ExternalTimeline ImportTimelineCore(
        RhiDevice device,
        ExternalHandle handle,
        string? label)
    {
        VulkanDevice nativeDevice = RequireExternalTimelineDevice(device);
        ArgumentNullException.ThrowIfNull(handle);
        VkSemaphore native = CreateTimelineSemaphore(
            nativeDevice,
            0,
            ExternalSemaphoreHandleTypeFlags.None);
        try
        {
            ImportSemaphoreWin32HandleInfoKHR import = new()
            {
                SType = StructureType.ImportSemaphoreWin32HandleInfoKhr,
                Semaphore = native,
                HandleType = ToNativeSemaphoreHandleType(handle.Type),
                Handle = handle.Value,
            };
            ThrowIfFailed(
                nativeDevice.ExternalSemaphoreApi.ImportSemaphoreWin32Handle(
                    nativeDevice.Native,
                    &import),
                "vkImportSemaphoreWin32HandleKHR");
            var timeline = new VulkanExternalTimeline(nativeDevice, native, label);
            nativeDevice.RegisterChild(timeline);
            return timeline;
        }
        catch
        {
            Api.DestroySemaphore(nativeDevice.Native, native, null);
            throw;
        }
    }

    private ExternalHandle ExportTimelineCore(
        ExternalTimeline timeline,
        ExternalHandleType type)
    {
        VulkanExternalTimeline native = RequireExternalTimeline(timeline, nameof(timeline));
        SemaphoreGetWin32HandleInfoKHR info = new()
        {
            SType = StructureType.SemaphoreGetWin32HandleInfoKhr,
            Semaphore = native.Native,
            HandleType = ToNativeSemaphoreHandleType(type),
        };
        nint handle = 0;
        ThrowIfFailed(
            native.NativeDevice.ExternalSemaphoreApi.GetSemaphoreWin32Handle(
                native.NativeDevice.Native,
                &info,
                &handle),
            "vkGetSemaphoreWin32HandleKHR");
        return new ExternalHandle(
            type,
            handle,
            type == ExternalHandleType.OpaqueWin32
                ? static value => _ = CloseHandle(value)
                : null);
    }

    private static VkSemaphore CreateTimelineSemaphore(
        VulkanDevice device,
        ulong initialValue,
        ExternalSemaphoreHandleTypeFlags exportTypes)
    {
        ExportSemaphoreCreateInfo export = new()
        {
            SType = StructureType.ExportSemaphoreCreateInfo,
            HandleTypes = exportTypes,
        };
        SemaphoreTypeCreateInfo timeline = new()
        {
            SType = StructureType.SemaphoreTypeCreateInfo,
            PNext = exportTypes == ExternalSemaphoreHandleTypeFlags.None ? null : &export,
            SemaphoreType = SemaphoreType.Timeline,
            InitialValue = initialValue,
        };
        SemaphoreCreateInfo createInfo = new()
        {
            SType = StructureType.SemaphoreCreateInfo,
            PNext = &timeline,
        };
        VkSemaphore native = default;
        ThrowIfFailed(
            device.Backend.Api.CreateSemaphore(device.Native, &createInfo, null, &native),
            "vkCreateSemaphore(external timeline)");
        return native;
    }

    private VulkanDevice RequireExternalTimelineDevice(RhiDevice device)
    {
        VulkanDevice native = RequireDevice(device, nameof(device));
        if (!native.TryGetCapability(out ExternalTimelines? capability) || capability is null)
            throw new NotSupportedException("The Device was not created with ExternalTimelines support.");
        return native;
    }

    private VulkanExternalTimeline RequireExternalTimeline(
        ExternalTimeline timeline,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(timeline, parameterName);
        if (timeline is not VulkanExternalTimeline native ||
            !ReferenceEquals(native.NativeDevice.Backend, this))
            throw new ArgumentException("The timeline belongs to a different graphics backend.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private static ExternalSemaphoreHandleTypeFlags ToNativeSemaphoreHandleType(
        ExternalHandleType type) => type switch
    {
        ExternalHandleType.OpaqueWin32 => ExternalSemaphoreHandleTypeFlags.OpaqueWin32Bit,
        ExternalHandleType.OpaqueWin32Kmt => ExternalSemaphoreHandleTypeFlags.OpaqueWin32KmtBit,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial int CloseHandle(nint handle);

    private sealed class VulkanExternalTimeline : ExternalTimeline, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanLifetime _lifetime;
        private VkSemaphore _native;

        internal VulkanExternalTimeline(
            VulkanDevice device,
            VkSemaphore native,
            string? label)
            : base(device, label)
        {
            _device = device;
            _native = native;
            _lifetime = new VulkanLifetime(DestroyNative);
        }

        internal VulkanDevice NativeDevice => _device;
        internal VkSemaphore Native => _native;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal override void Release(bool fromParent) { _device.UnregisterChild(this); _lifetime.Release(); }
        private void DestroyNative()
        {
            if (_native.Handle != 0)
                _device.Backend.Api.DestroySemaphore(_device.Native, _native, null);
            _native = default;
        }
    }
}
