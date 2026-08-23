namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private SomeEngine.Graphics.PipelineCache CreatePipelineCacheCore(
        RhiDevice device,
        in PipelineCacheDesc desc,
        CancellationToken cancellationToken)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        cancellationToken.ThrowIfCancellationRequested();
        if (desc.MaximumEntryCount < 0 || desc.MaximumByteCount < 0 || desc.MaximumDecodedByteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        byte[] initialData = desc.Data.ToArray();
        fixed (byte* data = initialData)
        {
            PipelineCacheCreateInfo createInfo = new()
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = checked((nuint)initialData.Length),
                PInitialData = data,
            };
            VkPipelineCache native = default;
            ThrowIfFailed(
                Api.CreatePipelineCache(nativeDevice.Native, &createInfo, null, &native),
                "vkCreatePipelineCache");
            var cache = new VulkanPipelineCache(
                nativeDevice,
                native,
                desc.MaximumByteCount,
                desc.Label);
            nativeDevice.RegisterChild(cache);
            return cache;
        }
    }

    private bool TryGetPipelineCacheDataCore(
        SomeEngine.Graphics.PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount,
        CancellationToken cancellationToken)
    {
        VulkanPipelineCache native = RequirePipelineCache(cache, nameof(cache));
        cancellationToken.ThrowIfCancellationRequested();
        lock (native.Gate)
        {
            nuint size = 0;
            ThrowIfFailed(
                Api.GetPipelineCacheData(native.Device.Native, native.Native, &size, null),
                "vkGetPipelineCacheData(size)");
            if (size > int.MaxValue ||
                native.MaximumByteCount > 0 && size > checked((nuint)native.MaximumByteCount))
                throw new GraphicsException(GraphicsError.NativeFailure, "The Vulkan pipeline cache exceeds its configured byte limit.");
            requiredByteCount = checked((int)size);
            if (destination.Length < requiredByteCount)
                return false;
            fixed (byte* data = destination)
            {
                ThrowIfFailed(
                    Api.GetPipelineCacheData(native.Device.Native, native.Native, &size, data),
                    "vkGetPipelineCacheData(data)");
            }
            requiredByteCount = checked((int)size);
            return true;
        }
    }

    private void MergePipelineCachesCore(
        SomeEngine.Graphics.PipelineCache destination,
        ReadOnlySpan<SomeEngine.Graphics.PipelineCache> sources,
        CancellationToken cancellationToken)
    {
        VulkanPipelineCache nativeDestination = RequirePipelineCache(destination, nameof(destination));
        VkPipelineCache[] nativeSources = new VkPipelineCache[sources.Length];
        for (int index = 0; index < sources.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VulkanPipelineCache source = RequirePipelineCache(sources[index], nameof(sources));
            if (!ReferenceEquals(source.Device, nativeDestination.Device))
                throw new ArgumentException("Pipeline caches must belong to the same Vulkan Device.", nameof(sources));
            nativeSources[index] = source.Native;
        }
        lock (nativeDestination.Gate)
        {
            fixed (VkPipelineCache* pointer = nativeSources)
            {
                ThrowIfFailed(
                    Api.MergePipelineCaches(
                        nativeDestination.Device.Native,
                        nativeDestination.Native,
                        checked((uint)nativeSources.Length),
                        pointer),
                    "vkMergePipelineCaches");
            }
        }
    }

    private VulkanPipelineCache? ResolvePipelineCache(
        VulkanDevice device,
        SomeEngine.Graphics.PipelineCache? cache)
    {
        if (cache is null)
            return null;
        VulkanPipelineCache native = RequirePipelineCache(cache, nameof(cache));
        if (!ReferenceEquals(native.Device, device))
            throw new ArgumentException("The PipelineCache belongs to a different Vulkan Device.", nameof(cache));
        return native;
    }

    private VulkanPipelineCache RequirePipelineCache(
        SomeEngine.Graphics.PipelineCache cache,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(cache, parameterName);
        if (cache is not VulkanPipelineCache native ||
            !ReferenceEquals(native.Device.Backend, this))
            throw new ArgumentException("The PipelineCache belongs to a different graphics backend.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private sealed class VulkanPipelineCache : SomeEngine.Graphics.PipelineCache
    {
        private readonly VulkanDevice _device;
        private VkPipelineCache _native;

        internal VulkanPipelineCache(
            VulkanDevice device,
            VkPipelineCache native,
            int maximumByteCount,
            string? label)
            : base(device, label)
        {
            _device = device;
            _native = native;
            MaximumByteCount = maximumByteCount;
        }

        internal new VulkanDevice Device => _device;
        internal VkPipelineCache Native => _native;
        internal int MaximumByteCount { get; }
        internal object Gate { get; } = new();

        internal override void Release(bool fromParent)
        {
            lock (Gate)
            {
                if (_native.Handle != 0)
                    _device.Backend.Api.DestroyPipelineCache(_device.Native, _native, null);
                _native = default;
            }
            _device.UnregisterChild(this);
        }
    }
}
