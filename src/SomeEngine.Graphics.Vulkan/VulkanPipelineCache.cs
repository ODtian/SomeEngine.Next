using System.Text;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private const ulong VulkanPipelineCacheBackendTag = 0x0000_004E_414B_4C56UL;
    private const byte VulkanNativeCacheFamily = 0;
    private const uint VulkanBackendAbiVersion = 1;
    private static readonly byte[] VulkanNativeCacheKey = CreateVulkanNativeCacheKey();

    private SomeEngine.Graphics.PipelineCache CreatePipelineCacheCore(
        RhiDevice device,
        in PipelineCacheDesc desc,
        CancellationToken cancellationToken)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        ValidatePipelineCachePolicy(desc);
        PipelineCacheLimits limits = PipelineCacheLimits.FromPolicy(
            desc.MaximumEntryCount,
            desc.MaximumByteCount,
            desc.MaximumDecodedByteCount);
        ParsedPipelineCache parsed = PipelineCacheEnvelope.Parse(
            desc.Data,
            limits,
            cancellationToken);
        byte[] compatibility = ComputeVulkanPipelineCacheCompatibility(
            nativeDevice,
            cancellationToken);
        ReadOnlySpan<byte> nativeData = parsed.TryGetCompatibleEntry(
            VulkanPipelineCacheBackendTag,
            VulkanNativeCacheFamily,
            VulkanNativeCacheKey,
            compatibility,
            out PipelineCacheEntry compatible)
            ? compatible.Payload
            : [];

        VkPipelineCache native = CreateNativePipelineCache(
            nativeDevice,
            nativeData,
            cancellationToken);
        VulkanPipelineCache? cache = null;
        try
        {
            cache = new VulkanPipelineCache(
                nativeDevice,
                native,
                parsed.Entries.ToArray(),
                compatibility,
                limits,
                desc.Label);
            cache.ValidateExportPolicy(cancellationToken);
            nativeDevice.RegisterChild(cache);
            return cache;
        }
        catch
        {
            if (cache is not null)
                cache.DestroyUnregistered();
            else if (native.Handle != 0)
                Api.DestroyPipelineCache(nativeDevice.Native, native, null);
            throw;
        }
    }

    private bool TryGetPipelineCacheDataCore(
        SomeEngine.Graphics.PipelineCache cache,
        Span<byte> destination,
        out int requiredByteCount,
        CancellationToken cancellationToken)
    {
        VulkanPipelineCache native = RequirePipelineCache(cache, nameof(cache));
        byte[] data = native.Serialize(cancellationToken);
        requiredByteCount = data.Length;
        if (destination.Length < data.Length)
            return false;
        data.CopyTo(destination);
        return true;
    }

    private void MergePipelineCachesCore(
        SomeEngine.Graphics.PipelineCache destination,
        ReadOnlySpan<SomeEngine.Graphics.PipelineCache> sources,
        CancellationToken cancellationToken)
    {
        VulkanPipelineCache nativeDestination = RequirePipelineCache(
            destination,
            nameof(destination));
        var nativeSources = new VulkanPipelineCache[sources.Length];
        for (int index = 0; index < sources.Length; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            VulkanPipelineCache source = RequirePipelineCache(sources[index], nameof(sources));
            if (!ReferenceEquals(source.Device, nativeDestination.Device))
            {
                throw new ArgumentException(
                    "Pipeline caches must belong to the same Vulkan Device.",
                    nameof(sources));
            }
            nativeSources[index] = source;
        }
        nativeDestination.Merge(nativeSources, cancellationToken);
    }

    private static void ValidatePipelineCachePolicy(in PipelineCacheDesc desc)
    {
        if (desc.MaximumEntryCount < 0)
            throw new ArgumentOutOfRangeException(nameof(desc.MaximumEntryCount));
        if (desc.MaximumByteCount < 0 ||
            desc.MaximumByteCount is > 0 and < PipelineCacheEnvelope.EmptyEnvelopeByteCount)
            throw new ArgumentOutOfRangeException(nameof(desc.MaximumByteCount));
        if (desc.MaximumDecodedByteCount < 0)
            throw new ArgumentOutOfRangeException(nameof(desc.MaximumDecodedByteCount));
    }

    private VkPipelineCache CreateNativePipelineCache(
        VulkanDevice device,
        ReadOnlySpan<byte> initialData,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        fixed (byte* data = initialData)
        {
            PipelineCacheCreateInfo createInfo = new()
            {
                SType = StructureType.PipelineCacheCreateInfo,
                InitialDataSize = checked((nuint)initialData.Length),
                PInitialData = data,
            };
            VkPipelineCache native = default;
            Result result = Api.CreatePipelineCache(
                device.Native,
                &createInfo,
                null,
                &native);
            device.ThrowIfDeviceCallFailed(result, "vkCreatePipelineCache");
            return native;
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
        if (native.IsDisposed && !ReferenceEquals(t_pipelineWorkerCache, native))
            native.ThrowIfDisposed();
        return native;
    }

    private static byte[] ComputeVulkanPipelineCacheCompatibility(
        VulkanDevice device,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        PhysicalDeviceProperties properties;
        device.Backend.Api.GetPhysicalDeviceProperties(device.PhysicalDevice, &properties);
        using MemoryStream stream = new();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(PipelineCacheEnvelope.SchemaVersion);
            writer.Write(VulkanBackendAbiVersion);
            writer.Write(properties.VendorID);
            writer.Write(properties.DeviceID);
            writer.Write(properties.DriverVersion);
            writer.Write(properties.ApiVersion);
            for (int index = 0; index < Vk.UuidSize; index++)
                writer.Write(properties.PipelineCacheUuid[index]);
            writer.Write((int)ShaderTarget.Spirv);
            writer.Write(SlangToolchainIdentity.Version);
        }
        var result = new byte[PipelineCacheEnvelope.HashByteCount];
        PipelineCacheEnvelope.ComputeSha256(
            stream.GetBuffer().AsSpan(0, checked((int)stream.Length)),
            result,
            cancellationToken);
        return result;
    }

    private static byte[] CreateVulkanNativeCacheKey()
    {
        var result = new byte[PipelineCacheEnvelope.HashByteCount];
        PipelineCacheEnvelope.ComputeSha256(
            "SomeEngine.Vulkan.NativePipelineCache"u8,
            result,
            CancellationToken.None);
        return result;
    }

    private sealed class VulkanPipelineCache : SomeEngine.Graphics.PipelineCache
    {
        internal static readonly object MergeGate = new();

        private readonly VulkanDevice _device;
        private readonly byte[] _compatibility;
        private readonly PipelineCacheLimits _limits;
        private PipelineCacheEntry[] _entries;
        private VkPipelineCache _native;
        private int _pipelineCreationUses;
        private bool _disposeRequested;

        internal VulkanPipelineCache(
            VulkanDevice device,
            VkPipelineCache native,
            PipelineCacheEntry[] entries,
            byte[] compatibility,
            PipelineCacheLimits limits,
            string? label)
            : base(device, label)
        {
            _device = device;
            _native = native;
            _entries = entries;
            _compatibility = compatibility;
            _limits = limits;
        }

        internal new VulkanDevice Device => _device;
        internal VkPipelineCache Native => _native;
        internal object Gate { get; } = new();

        internal void RetainForPipelineCreation()
        {
            lock (Gate)
            {
                ThrowIfPhysicallyDisposed();
                if (_disposeRequested)
                    throw new ObjectDisposedException(nameof(VulkanPipelineCache));
                _pipelineCreationUses = checked(_pipelineCreationUses + 1);
            }
        }

        internal void ReleasePipelineCreationUse()
        {
            bool unregister = false;
            lock (Gate)
            {
                if (_pipelineCreationUses <= 0)
                    throw new InvalidOperationException("The Vulkan pipeline-cache use count is unbalanced.");
                _pipelineCreationUses--;
                if (_pipelineCreationUses == 0 && _disposeRequested)
                {
                    DestroyNativeUnderGate();
                    unregister = true;
                }
            }
            if (unregister)
                _device.UnregisterChild(this);
        }

        internal byte[] Serialize(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (Gate)
            {
                ThrowIfPhysicallyDisposed();
                byte[] nativeBlob = GetNativeDataUnderGate(_native, cancellationToken);
                PipelineCacheEntry[] entries = ReplaceLocalEntry(_entries, nativeBlob);
                byte[] envelope = PipelineCacheEnvelope.Serialize(
                    entries,
                    _limits,
                    cancellationToken);
                _entries = entries;
                return envelope;
            }
        }

        internal void ValidateExportPolicy(CancellationToken cancellationToken)
        {
            lock (Gate)
            {
                ThrowIfPhysicallyDisposed();
                byte[] nativeBlob = GetNativeDataUnderGate(_native, cancellationToken);
                _ = PipelineCacheEnvelope.Serialize(
                    ReplaceLocalEntry(_entries, nativeBlob),
                    _limits,
                    cancellationToken);
            }
        }

        internal void Merge(
            VulkanPipelineCache[] sources,
            CancellationToken cancellationToken)
        {
            var caches = new List<VulkanPipelineCache>(sources.Length + 1) { this };
            foreach (VulkanPipelineCache source in sources)
            {
                if (!caches.Contains(source))
                    caches.Add(source);
            }
            lock (MergeGate)
                LockCaches(caches, 0, MergeUnderLocks);

            void MergeUnderLocks()
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (VulkanPipelineCache cache in caches)
                    cache.ThrowIfPhysicallyDisposed();

                PipelineCacheEntry[] candidate = _entries;
                foreach (VulkanPipelineCache source in sources)
                {
                    candidate = PipelineCacheEnvelope.Merge(
                        candidate,
                        source._entries,
                        _limits,
                        cancellationToken);
                }

                VkPipelineCache replacement = _device.Backend.CreateNativePipelineCache(
                    _device,
                    [],
                    cancellationToken);
                try
                {
                    VkPipelineCache[] nativeSources = caches
                        .Select(static cache => cache._native)
                        .ToArray();
                    fixed (VkPipelineCache* pointer = nativeSources)
                    {
                        Result result = _device.Backend.Api.MergePipelineCaches(
                            _device.Native,
                            replacement,
                            checked((uint)nativeSources.Length),
                            pointer);
                        _device.ThrowIfDeviceCallFailed(result, "vkMergePipelineCaches");
                    }

                    byte[] mergedBlob = GetNativeDataUnderGate(
                        replacement,
                        cancellationToken);
                    PipelineCacheEntry[] finalEntries = ReplaceLocalEntry(candidate, mergedBlob);
                    _ = PipelineCacheEnvelope.Serialize(
                        finalEntries,
                        _limits,
                        cancellationToken);

                    VkPipelineCache previous = _native;
                    _native = replacement;
                    replacement = default;
                    _entries = finalEntries;
                    _device.Backend.Api.DestroyPipelineCache(_device.Native, previous, null);
                }
                catch
                {
                    throw;
                }
                finally
                {
                    if (replacement.Handle != 0)
                        _device.Backend.Api.DestroyPipelineCache(_device.Native, replacement, null);
                }
            }
        }

        internal override void Release(bool fromParent)
        {
            bool unregister = false;
            lock (Gate)
            {
                if (_disposeRequested)
                    return;
                _disposeRequested = true;
                if (_pipelineCreationUses == 0)
                {
                    DestroyNativeUnderGate();
                    unregister = true;
                }
            }
            if (unregister)
                _device.UnregisterChild(this);
        }

        internal void DestroyUnregistered()
        {
            lock (Gate)
                DestroyNativeUnderGate();
        }

        private byte[] GetNativeDataUnderGate(
            VkPipelineCache cache,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            nuint size = 0;
            Result result = _device.Backend.Api.GetPipelineCacheData(
                _device.Native,
                cache,
                &size,
                null);
            _device.ThrowIfDeviceCallFailed(result, "vkGetPipelineCacheData(size)");
            if (size > int.MaxValue || size > checked((nuint)_limits.MaximumDecodedByteCount))
                throw new GraphicsException(GraphicsError.NativeFailure, "The Vulkan pipeline cache exceeds its configured decoded-byte limit.");
            var data = new byte[checked((int)size)];
            fixed (byte* pointer = data)
            {
                result = _device.Backend.Api.GetPipelineCacheData(
                    _device.Native,
                    cache,
                    &size,
                    pointer);
            }
            _device.ThrowIfDeviceCallFailed(result, "vkGetPipelineCacheData(data)");
            if (size > checked((nuint)data.Length))
                throw _device.PublishInternalDeviceLoss("vkGetPipelineCacheData returned an invalid byte count.");
            if (size != checked((nuint)data.Length))
                Array.Resize(ref data, checked((int)size));
            return data;
        }

        private PipelineCacheEntry[] ReplaceLocalEntry(
            ReadOnlySpan<PipelineCacheEntry> entries,
            byte[] nativeBlob)
        {
            var result = new List<PipelineCacheEntry>(entries.Length + 1);
            foreach (PipelineCacheEntry entry in entries)
            {
                if (entry.Backend == VulkanPipelineCacheBackendTag &&
                    entry.Family == VulkanNativeCacheFamily &&
                    entry.Key.AsSpan().SequenceEqual(VulkanNativeCacheKey) &&
                    entry.Compatibility.AsSpan().SequenceEqual(_compatibility))
                    continue;
                result.Add(entry);
            }
            result.Add(new PipelineCacheEntry(
                VulkanPipelineCacheBackendTag,
                VulkanNativeCacheFamily,
                VulkanNativeCacheKey,
                _compatibility,
                nativeBlob));
            return result.ToArray();
        }

        private static void LockCaches(
            IReadOnlyList<VulkanPipelineCache> caches,
            int index,
            Action action)
        {
            if (index == caches.Count)
            {
                action();
                return;
            }
            lock (caches[index].Gate)
                LockCaches(caches, index + 1, action);
        }

        private void ThrowIfPhysicallyDisposed()
        {
            if (_native.Handle == 0)
                throw new ObjectDisposedException(nameof(VulkanPipelineCache));
        }

        private void DestroyNativeUnderGate()
        {
            if (_native.Handle != 0)
                _device.Backend.Api.DestroyPipelineCache(_device.Native, _native, null);
            _native = default;
            _entries = [];
        }
    }
}
