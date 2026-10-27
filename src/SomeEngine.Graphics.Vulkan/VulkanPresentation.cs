using Silk.NET.Vulkan.Extensions.KHR;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private RhiSurface CreateSurfaceCore(in SurfaceDesc desc)
    {
        ThrowIfDisposed();
        if (!OperatingSystem.IsWindows() || desc.Type != NativeWindowType.Win32)
            throw new PlatformNotSupportedException("The Vulkan backend currently requires a Win32 Surface.");
        if (desc.WindowHandle == 0)
            throw new ArgumentOutOfRangeException(nameof(desc));
        Win32SurfaceCreateInfoKHR createInfo = new()
        {
            SType = StructureType.Win32SurfaceCreateInfoKhr,
            Hinstance = desc.DisplayHandle != 0
                ? desc.DisplayHandle
                : NativeLibrary.GetMainProgramHandle(),
            Hwnd = desc.WindowHandle,
        };
        VkSurface native = default;
        ThrowIfFailed(
            _win32SurfaceApi!.CreateWin32Surface(_instance, &createInfo, null, &native),
            "vkCreateWin32SurfaceKHR");
        var surface = new VulkanSurface(this, native, desc);
        try
        {
            RegisterSurface(surface);
            return surface;
        }
        catch
        {
            surface.Dispose();
            throw;
        }
    }

    private Swapchain CreateSwapchainCore(RhiDevice device, in SwapchainDesc desc)
    {
        VulkanDevice nativeDevice = RequireDevice(device, nameof(device));
        if (!nativeDevice.TryGetCapability(out Presentation? presentation) || presentation is null)
            throw new NotSupportedException("The Device was not created with Presentation support.");
        VulkanSurface surface = RequireSurface(desc.Surface, nameof(desc));
        nativeDevice.CollectRetiredSwapchains(waitForAcquisition: false);
        nativeDevice.ThrowIfUnavailable();
        VulkanSwapchain swapchain = VulkanSwapchain.Create(nativeDevice, surface, desc);
        try
        {
            nativeDevice.RegisterChild(swapchain);
            surface.RegisterSwapchain(swapchain);
            return swapchain;
        }
        catch
        {
            swapchain.Dispose();
            throw;
        }
    }

    private SwapchainAcquireStatus AcquireCore(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image)
    {
        VulkanSwapchain native = RequireSwapchain(swapchain, nameof(swapchain));
        return native.Acquire(options, out image);
    }

    private PresentStatus PresentCore(RhiQueue queue, in SwapchainImage image)
    {
        VulkanQueue nativeQueue = RequireQueue(queue, nameof(queue));
        if (image.Lease is not VulkanSwapchainImageLease lease ||
            !ReferenceEquals(lease.Swapchain.Device, nativeQueue.Device))
            throw new ArgumentException("The SwapchainImage belongs to a different Vulkan Device.", nameof(image));
        return lease.Present(nativeQueue, image.Sequence);
    }

    private ReconfigureStatus ReconfigureCore(
        Swapchain swapchain,
        in SwapchainConfig config)
    {
        VulkanSwapchain native = RequireSwapchain(swapchain, nameof(swapchain));
        return native.Reconfigure(config);
    }

    private VulkanSurface RequireSurface(RhiSurface surface, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(surface, parameterName);
        if (surface is not VulkanSurface native || !ReferenceEquals(native.Backend, this))
            throw new ArgumentException("The Surface belongs to a different graphics backend.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private VulkanSwapchain RequireSwapchain(Swapchain swapchain, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(swapchain, parameterName);
        if (swapchain is not VulkanSwapchain native ||
            native.Device is not VulkanDevice device ||
            !ReferenceEquals(device.Backend, this))
            throw new ArgumentException("The Swapchain belongs to a different graphics backend.", parameterName);
        native.ThrowIfDisposed();
        return native;
    }

    private sealed class VulkanSurface : RhiSurface, IVulkanRetained
    {
        private static readonly InvalidOperationException SwapchainDetachFailure =
            new("A Vulkan Swapchain did not detach during Surface teardown.");

        private readonly VulkanBackend _backend;
        private readonly VulkanLifetime _lifetime;
        private readonly object _swapchainsGate = new();
        private readonly GraphicsObjectRegistry _swapchains;
        private VkSurface _native;

        internal VulkanSurface(VulkanBackend backend, VkSurface native, in SurfaceDesc desc)
            : base(desc.Type, desc.WindowHandle, desc.DisplayHandle, backend, desc.Label)
        {
            _backend = backend;
            _native = native;
            _lifetime = new VulkanLifetime(DestroyNative);
            _swapchains = new GraphicsObjectRegistry(_swapchainsGate);
        }

        internal VulkanBackend Backend => _backend;
        internal VkSurface Native => _native;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();
        internal void RegisterSwapchain(VulkanSwapchain swapchain)
        {
            ThrowIfDisposed();
            _swapchains.Add(swapchain);
        }

        internal void UnregisterSwapchain(VulkanSwapchain swapchain) =>
            _swapchains.Remove(swapchain);

        internal override void Release(bool fromParent)
        {
            GraphicsObject? swapchains = _swapchains.CloseAndBuildDrainList(
                secondaryLink: true);
            while (swapchains is VulkanSwapchain swapchain)
            {
                swapchains = swapchain.SecondaryRegistryDrainNext;
                swapchain.SecondaryRegistryDrainNext = null;
                swapchain.DisposeFromParent();
                if (_swapchains.CompleteDrain(swapchain))
                    swapchain.Device.RecordReleaseFailure(SwapchainDetachFailure);
            }

            if (_swapchains.HasRetainedFailures)
                return;

            // Finish or transfer native ownership before dropping the Backend root.
            _lifetime.Release();
            _backend.UnregisterSurface(this);
        }

        private void DestroyNative() { if (_native.Handle != 0) _backend._surfaceApi!.DestroySurface(_backend.Instance, _native, null); _native = default; }
    }

    private sealed class VulkanSwapchain : Swapchain, IVulkanRetained
    {
        private readonly VulkanDevice _device;
        private readonly VulkanSurface _surface;
        private readonly VulkanLifetime _lifetime;
        private readonly object _gate = new();
        private readonly uint _requestedImageCount;
        private VkSwapchain _native;
        private VulkanImageState[] _images = [];
        private VulkanSwapchainImageLease[] _leases = [];
        private ulong _nextSequence;
        private bool _outOfDate;
        private bool _nativeReleaseFailed;

        internal VulkanSwapchain? NativeRetirementNext;

        private VulkanSwapchain(
            VulkanDevice device,
            VulkanSurface surface,
            VkSwapchain native,
            VulkanImageState[] images,
            uint minimumImageCount,
            SwapchainInfo info,
            in SwapchainDesc desc)
            : base(device, surface, info, desc.ImageUsages, desc.Label)
        {
            _device = device;
            _surface = surface;
            _native = native;
            _images = images;
            _requestedImageCount = desc.ImageCount;
            _surface.RetainNative();
            try
            {
                _lifetime = new VulkanLifetime(RetireNative);
                _leases = CreateLeases(
                    device, this, images.Length, minimumImageCount, info.Config.MaximumFrameLatency);
            }
            catch
            {
                foreach (VulkanImageState image in images)
                    image.Release();
                _images = [];
                _surface.ReleaseNative();
                throw;
            }
        }

        internal new VulkanDevice Device => _device;
        internal VkSwapchain Native => _native;
        public void RetainNative() => _lifetime.Retain();
        public void ReleaseNative() => _lifetime.Release();

        internal static VulkanSwapchain Create(
            VulkanDevice device,
            VulkanSurface surface,
            in SwapchainDesc desc)
        {
            ValidateDescription(desc);
            SwapchainSupport[] support = QuerySupport(device, surface);
            RequireConfiguration(desc.Config, support);
            VkSwapchain native = CreateNative(
                device,
                surface,
                desc.ImageCount,
                desc.ImageUsages,
                desc.Config,
                default,
                out SwapchainConfig resolvedConfig,
                out uint minimumImageCount);
            try
            {
                VkImage[] nativeImages = GetImages(device, native);
                VulkanImageState[] images = CreateImageStates(
                    device,
                    nativeImages,
                    desc.ImageUsages,
                    resolvedConfig);
                SwapchainInfo info = new(
                    resolvedConfig,
                    checked((uint)nativeImages.Length),
                    generation: 1,
                    support);
                return new VulkanSwapchain(
                    device,
                    surface,
                    native,
                    images,
                    minimumImageCount,
                    info,
                    desc);
            }
            catch
            {
                device.SwapchainApi.DestroySwapchain(device.Native, native, null);
                throw;
            }
        }

        internal SwapchainAcquireStatus Acquire(
            in SwapchainAcquireOptions options,
            out SwapchainImage image)
        {
            lock (_gate)
            {
                image = default;
                if (_outOfDate)
                    return SwapchainAcquireStatus.OutOfDate;
                if (options.Timeout < TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
                    throw new ArgumentOutOfRangeException(nameof(options));
                VulkanSwapchainImageLease? lease = FindAvailableLease();
                _device.ThrowIfUnavailable();
                if (lease is null)
                    return SwapchainAcquireStatus.Timeout;
                ulong timeout = options.Timeout == Timeout.InfiniteTimeSpan
                    ? ulong.MaxValue
                    : checked((ulong)options.Timeout.Ticks * 100);
                if (_nextSequence == ulong.MaxValue)
                    throw new OverflowException("Swapchain acquisition sequence exhausted.");
                ulong sequence = _nextSequence + 1;
                uint imageIndex = 0;
                lease.PrepareAcquireFence();
                Result result = _device.SwapchainApi.AcquireNextImage(
                    _device.Native,
                    _native,
                    timeout,
                    lease.AcquireSemaphore,
                    lease.AcquireFence,
                    &imageIndex);
                if (result is Result.Success or Result.SuboptimalKhr)
                    lease.MarkAcquisitionPending();
                if (result == Result.Timeout || result == Result.NotReady)
                    return SwapchainAcquireStatus.Timeout;
                if (result is Result.ErrorOutOfDateKhr or Result.ErrorSurfaceLostKhr)
                {
                    _outOfDate = true;
                    return SwapchainAcquireStatus.OutOfDate;
                }
                if (result is not Result.Success and not Result.SuboptimalKhr)
                    _device.ThrowIfDeviceCallFailed(result, "vkAcquireNextImageKHR");
                if (imageIndex >= _images.Length)
                {
                    throw _device.PublishInternalDeviceLoss(
                        "vkAcquireNextImageKHR returned an invalid image index.");
                }
                _nextSequence = sequence;
                lease.Begin(
                    sequence,
                    Info.Generation,
                    _images[checked((int)imageIndex)],
                    options.PreserveContents);
                image = new SwapchainImage(lease, sequence);
                return SwapchainAcquireStatus.Success;
            }
        }

        internal ReconfigureStatus Reconfigure(in SwapchainConfig config)
        {
            lock (_gate)
            {
                return config == Info.Config
                    ? ReconfigureStatus.Success
                    : ReconfigureStatus.Unsupported;
            }
        }

        internal override void Release(bool fromParent)
        {
            lock (_gate)
            {
                if (_native.Handle == 0)
                    return;
                foreach (VulkanSwapchainImageLease lease in _leases)
                    lease.Invalidate(deviceLost: false);
                foreach (VulkanImageState image in _images)
                    image.Texture.DisposeFromParent();
            }
            _surface.UnregisterSwapchain(this);
            // Transfer the final native owner before dropping the Device child root.
            _lifetime.Release();
            _device.UnregisterChild(this);
        }

        // The final reference transfers the complete native owner to the Device.
        // No image, semaphore, fence, swapchain or Surface reference is freed here.
        private void RetireNative() => _device.DeferSwapchainDestruction(this);

        internal bool TryDestroyRetiredNative(bool waitForAcquisition)
        {
            if (_nativeReleaseFailed)
                return false;
            lock (_gate)
            {
                foreach (VulkanSwapchainImageLease lease in _leases)
                    if (!lease.ObserveAcquisitionCompletion(waitForAcquisition))
                        return false;
            }

            try
            {
                DestroyNative();
                return true;
            }
            catch (Exception exception)
            {
                // Never retry a possibly partial physical release.
                _nativeReleaseFailed = true;
                _device.RecordReleaseFailure(exception);
                return false;
            }
        }

        private void DestroyNative()
        {
            lock (_gate)
            {
                ReleaseGeneration();
                _device.SwapchainApi.DestroySwapchain(_device.Native, _native, null);
                _native = default;
            }
            _surface.ReleaseNative();
        }

        internal void MarkDeviceLostNoThrow()
        {
            lock (_gate)
            {
                foreach (VulkanSwapchainImageLease lease in _leases)
                    lease.Invalidate(deviceLost: true);
            }
        }

        internal void MarkOutOfDate()
        {
            lock (_gate)
                _outOfDate = true;
        }

        private VulkanSwapchainImageLease? FindAvailableLease()
        {
            foreach (VulkanSwapchainImageLease lease in _leases)
                if (lease.CanAcquire)
                    return lease;
            return null;
        }

        private void ReleaseGeneration()
        {
            foreach (VulkanSwapchainImageLease lease in _leases)
                lease.Release();
            foreach (VulkanImageState image in _images)
                image.Release();
            _leases = [];
            _images = [];
        }

        private static VkSwapchain CreateNative(
            VulkanDevice device,
            VulkanSurface surface,
            uint requestedImageCount,
            TextureUsages usages,
            in SwapchainConfig config,
            VkSwapchain oldSwapchain,
            out SwapchainConfig resolvedConfig,
            out uint minimumImageCount)
        {
            KhrSurface surfaceApi = device.Backend._surfaceApi!;
            SurfaceCapabilitiesKHR capabilities;
            ThrowIfFailed(
                surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                    device.PhysicalDevice,
                    surface.Native,
                    &capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            minimumImageCount = capabilities.MinImageCount;
            uint imageCount = Math.Max(requestedImageCount, minimumImageCount);
            if (capabilities.MaxImageCount != 0)
                imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
            Extent2D extent = capabilities.CurrentExtent.Width != uint.MaxValue
                ? capabilities.CurrentExtent
                : new Extent2D(
                    Math.Clamp(config.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                    Math.Clamp(config.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
            ImageUsageFlags nativeUsages = ToNative(usages);
            if ((nativeUsages & capabilities.SupportedUsageFlags) != nativeUsages)
                throw new NotSupportedException("The Surface does not support the requested swapchain image usages.");
            uint family = device.GetQueue(QueueType.Graphics, 0).FamilyIndex;
            Silk.NET.Core.Bool32 presentSupported = false;
            ThrowIfFailed(
                surfaceApi.GetPhysicalDeviceSurfaceSupport(
                    device.PhysicalDevice,
                    family,
                    surface.Native,
                    &presentSupported),
                "vkGetPhysicalDeviceSurfaceSupportKHR");
            if (!presentSupported)
                throw new NotSupportedException("The Device Graphics Queue cannot present to this Surface.");
            CompositeAlphaFlagsKHR composite = SelectCompositeAlpha(capabilities.SupportedCompositeAlpha);
            SwapchainCreateInfoKHR createInfo = new()
            {
                SType = StructureType.SwapchainCreateInfoKhr,
                Surface = surface.Native,
                MinImageCount = imageCount,
                ImageFormat = VulkanFormats.ToNative(config.Format),
                ImageColorSpace = ToNative(config.ColorSpace),
                ImageExtent = extent,
                ImageArrayLayers = 1,
                ImageUsage = nativeUsages,
                ImageSharingMode = SharingMode.Exclusive,
                PreTransform = capabilities.CurrentTransform,
                CompositeAlpha = composite,
                PresentMode = ToNative(config.PresentType),
                Clipped = true,
                OldSwapchain = oldSwapchain,
            };
            VkSwapchain native = default;
            Result result =
                device.SwapchainApi.CreateSwapchain(
                device.Native,
                &createInfo,
                null,
                &native);
            device.ThrowIfDeviceCallFailed(result, "vkCreateSwapchainKHR");
            resolvedConfig = config with { Width = extent.Width, Height = extent.Height };
            return native;
        }

        private static VkImage[] GetImages(VulkanDevice device, VkSwapchain swapchain)
        {
            uint count = 0;
            device.ThrowIfDeviceCallFailed(
                device.SwapchainApi.GetSwapchainImages(device.Native, swapchain, &count, null),
                "vkGetSwapchainImagesKHR(count)");
            VkImage[] images = new VkImage[count];
            fixed (VkImage* pointer = images)
            {
                device.ThrowIfDeviceCallFailed(
                    device.SwapchainApi.GetSwapchainImages(device.Native, swapchain, &count, pointer),
                    "vkGetSwapchainImagesKHR(data)");
            }
            return images;
        }

        private static VulkanImageState[] CreateImageStates(
            VulkanDevice device,
            VkImage[] nativeImages,
            TextureUsages usages,
            in SwapchainConfig config)
        {
            var result = new VulkanImageState[nativeImages.Length];
            try
            {
                for (int index = 0; index < result.Length; index++)
                {
                    TextureDesc desc = new(
                        TextureDimension.Texture2D,
                        config.Width,
                        config.Height,
                        1,
                        1,
                        1,
                        1,
                        config.Format,
                        usages,
                        label: $"Vulkan swapchain image {index}");
                    var texture = new VulkanTexture(
                        device,
                        nativeImages[index],
                        ownedMemory: null,
                        heap: null,
                        desc,
                        0,
                        0,
                        ownsImage: false);
                    VkSemaphore renderComplete = CreateBinarySemaphore(device);
                    var image = new VulkanImageState(texture, renderComplete);
                    texture.SwapchainState = image;
                    result[index] = image;
                }
                return result;
            }
            catch
            {
                foreach (VulkanImageState? image in result)
                    image?.Release();
                throw;
            }
        }

        private static VulkanSwapchainImageLease[] CreateLeases(
            VulkanDevice device,
            VulkanSwapchain swapchain,
            int count,
            uint minimumImageCount,
            uint maximumFrameLatency)
        {
            // Native acquisition must start with no more than S - M images held.
            int capacity = checked(count - (int)minimumImageCount + 1);
            int frameLimit = checked((int)Math.Min(maximumFrameLatency, (uint)int.MaxValue));
            var result = new VulkanSwapchainImageLease[Math.Min(capacity, frameLimit)];
            try
            {
                for (int index = 0; index < result.Length; index++)
                {
                    VkSemaphore semaphore = CreateBinarySemaphore(device);
                    Silk.NET.Vulkan.Fence fence = default;
                    try
                    {
                        FenceCreateInfo createInfo = new()
                        {
                            SType = StructureType.FenceCreateInfo,
                        };
                        device.ThrowIfDeviceCallFailed(
                            device.Backend.Api.CreateFence(device.Native, &createInfo, null, &fence),
                            "vkCreateFence(swapchain acquisition)");
                        result[index] = new VulkanSwapchainImageLease(
                            device, swapchain, semaphore, fence);
                    }
                    catch
                    {
                        if (fence.Handle != 0)
                            device.Backend.Api.DestroyFence(device.Native, fence, null);
                        device.Backend.Api.DestroySemaphore(device.Native, semaphore, null);
                        throw;
                    }
                }
                return result;
            }
            catch
            {
                foreach (VulkanSwapchainImageLease? lease in result)
                    lease?.Release();
                throw;
            }
        }

        private static VkSemaphore CreateBinarySemaphore(VulkanDevice device)
        {
            SemaphoreCreateInfo createInfo = new()
            {
                SType = StructureType.SemaphoreCreateInfo,
            };
            VkSemaphore semaphore = default;
            device.ThrowIfDeviceCallFailed(
                device.Backend.Api.CreateSemaphore(device.Native, &createInfo, null, &semaphore),
                "vkCreateSemaphore(swapchain)");
            return semaphore;
        }

        private static SwapchainSupport[] QuerySupport(VulkanDevice device, VulkanSurface surface)
        {
            KhrSurface api = device.Backend._surfaceApi!;
            uint formatCount = 0;
            ThrowIfFailed(
                api.GetPhysicalDeviceSurfaceFormats(device.PhysicalDevice, surface.Native, &formatCount, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR(count)");
            SurfaceFormatKHR[] formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* pointer = formats)
                ThrowIfFailed(api.GetPhysicalDeviceSurfaceFormats(device.PhysicalDevice, surface.Native, &formatCount, pointer), "vkGetPhysicalDeviceSurfaceFormatsKHR(data)");
            uint modeCount = 0;
            ThrowIfFailed(
                api.GetPhysicalDeviceSurfacePresentModes(device.PhysicalDevice, surface.Native, &modeCount, null),
                "vkGetPhysicalDeviceSurfacePresentModesKHR(count)");
            PresentModeKHR[] modes = new PresentModeKHR[modeCount];
            fixed (PresentModeKHR* pointer = modes)
                ThrowIfFailed(api.GetPhysicalDeviceSurfacePresentModes(device.PhysicalDevice, surface.Native, &modeCount, pointer), "vkGetPhysicalDeviceSurfacePresentModesKHR(data)");
            var result = new List<SwapchainSupport>();
            foreach (SurfaceFormatKHR format in formats)
            {
                if (!VulkanFormats.TryFromNative(format.Format, out RhiFormat rhiFormat) ||
                    !TryFromNative(format.ColorSpace, out ColorSpace colorSpace))
                    continue;
                foreach (PresentModeKHR mode in modes)
                {
                    if (!TryFromNative(mode, out PresentType presentType))
                        continue;
                    result.Add(new SwapchainSupport(
                        rhiFormat,
                        colorSpace,
                        presentType,
                        presentType != PresentType.Fifo));
                }
            }
            return result.ToArray();
        }

        private static void ValidateDescription(in SwapchainDesc desc)
        {
            ArgumentNullException.ThrowIfNull(desc.Surface);
            if (desc.ImageCount < 2 || desc.ImageUsages == TextureUsages.None)
                throw new ArgumentOutOfRangeException(nameof(desc));
        }

        private static void RequireConfiguration(
            in SwapchainConfig config,
            ReadOnlySpan<SwapchainSupport> support)
        {
            if (config.Width == 0 || config.Height == 0 || config.MaximumFrameLatency == 0)
                throw new ArgumentOutOfRangeException(nameof(config));
            if (!Supports(config, support))
                throw new NotSupportedException("The Surface does not support the requested swapchain configuration.");
        }

        internal uint IndexOf(VulkanImageState? image)
        {
            int index = Array.IndexOf(_images, image);
            return index >= 0
                ? checked((uint)index)
                : throw new InvalidOperationException("The acquired image is no longer part of the swapchain.");
        }

        private static bool Supports(
            in SwapchainConfig config,
            ReadOnlySpan<SwapchainSupport> support)
        {
            foreach (ref readonly SwapchainSupport value in support)
            {
                if (value.Format == config.Format && value.ColorSpace == config.ColorSpace &&
                    value.PresentType == config.PresentType &&
                    (!config.AllowTearing || value.TearingSupported))
                    return true;
            }
            return false;
        }

        private static CompositeAlphaFlagsKHR SelectCompositeAlpha(CompositeAlphaFlagsKHR supported)
        {
            CompositeAlphaFlagsKHR[] preference =
            [
                CompositeAlphaFlagsKHR.OpaqueBitKhr,
                CompositeAlphaFlagsKHR.PreMultipliedBitKhr,
                CompositeAlphaFlagsKHR.PostMultipliedBitKhr,
                CompositeAlphaFlagsKHR.InheritBitKhr,
            ];
            foreach (CompositeAlphaFlagsKHR value in preference)
                if ((supported & value) != 0)
                    return value;
            throw new NotSupportedException("The Surface exposes no supported composite-alpha mode.");
        }
    }

    private readonly record struct VulkanSwapchainUse(
        VulkanSwapchainImageLease Lease,
        ulong Sequence,
        ulong Generation);

    private sealed partial class VulkanDevice
    {
        private static readonly InvalidOperationException RetirementJoinFailure =
            new("Vulkan Device teardown reentered its native swapchain collector.");

        private VulkanSwapchain? _retiredSwapchains;
        private int _retiredSwapchainCollectorThreadId;

        internal bool HasRetiredSwapchains =>
            Volatile.Read(ref _retiredSwapchains) is not null ||
            Volatile.Read(ref _retiredSwapchainCollectorThreadId) != 0;

        internal void DeferSwapchainDestruction(VulkanSwapchain swapchain)
        {
            // The existing Device registry gate also protects native-owner transfer.
            lock (_gate)
            {
                swapchain.NativeRetirementNext = _retiredSwapchains;
                _retiredSwapchains = swapchain;
            }
        }

        internal void CollectRetiredSwapchains(bool waitForAcquisition)
        {
            if (!waitForAcquisition && !HasRetiredSwapchains)
                return;
            VulkanSwapchain? remaining;
            lock (_gate)
            {
                // Ordinary collection never waits for another collector. Teardown
                // joins it before taking the final list, releasing _gate while waiting.
                if (!waitForAcquisition &&
                    (IsDisposed || _retiredSwapchainCollectorThreadId != 0))
                {
                    return;
                }
                while (_retiredSwapchainCollectorThreadId != 0)
                {
                    if (_retiredSwapchainCollectorThreadId == Environment.CurrentManagedThreadId)
                    {
                        RecordReleaseFailure(RetirementJoinFailure);
                        return;
                    }
                    Monitor.Wait(_gate);
                }
                remaining = _retiredSwapchains;
                if (remaining is null)
                    return;
                _retiredSwapchains = null;
                _retiredSwapchainCollectorThreadId = Environment.CurrentManagedThreadId;
            }
            VulkanSwapchain? pending = null;
            try
            {
                while (remaining is VulkanSwapchain swapchain)
                {
                    remaining = swapchain.NativeRetirementNext;
                    swapchain.NativeRetirementNext = null;
                    try
                    {
                        if (swapchain.TryDestroyRetiredNative(waitForAcquisition))
                            continue;
                    }
                    catch
                    {
                        swapchain.NativeRetirementNext = pending;
                        pending = swapchain;
                        throw;
                    }
                    swapchain.NativeRetirementNext = pending;
                    pending = swapchain;
                }
            }
            finally
            {
                // Re-root every unresolved owner before releasing the collector join.
                // Native destruction and Surface release never run under Device._gate.
                lock (_gate)
                {
                    while (remaining is VulkanSwapchain remainingOwner)
                    {
                        remaining = remainingOwner.NativeRetirementNext;
                        remainingOwner.NativeRetirementNext = _retiredSwapchains;
                        _retiredSwapchains = remainingOwner;
                    }
                    while (pending is VulkanSwapchain pendingOwner)
                    {
                        pending = pendingOwner.NativeRetirementNext;
                        pendingOwner.NativeRetirementNext = _retiredSwapchains;
                        _retiredSwapchains = pendingOwner;
                    }
                    _retiredSwapchainCollectorThreadId = 0;
                    Monitor.PulseAll(_gate);
                }
            }
        }
    }

    private sealed class VulkanImageState(VulkanTexture texture, VkSemaphore renderComplete)
    {
        private readonly object _gate = new();
        private VulkanSwapchainImageLease? _currentLease;
        private ulong _currentSequence;
        private ulong _currentGeneration;

        internal VulkanTexture Texture { get; } = texture;
        internal VkSemaphore RenderComplete { get; } = renderComplete;

        internal void BeginAcquisition(
            VulkanSwapchainImageLease lease,
            ulong sequence,
            ulong generation)
        {
            lock (_gate)
            {
                _currentLease = lease;
                _currentSequence = sequence;
                _currentGeneration = generation;
            }
        }

        internal bool TryGetCurrentUse(out VulkanSwapchainUse use)
        {
            lock (_gate)
            {
                if (_currentLease is null)
                {
                    use = default;
                    return false;
                }
                use = new VulkanSwapchainUse(
                    _currentLease,
                    _currentSequence,
                    _currentGeneration);
                return true;
            }
        }

        internal void EndAcquisition(
            VulkanSwapchainImageLease lease,
            ulong sequence)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_currentLease, lease) ||
                    _currentSequence != sequence)
                {
                    return;
                }
                _currentLease = null;
                _currentSequence = 0;
                _currentGeneration = 0;
            }
        }

        internal void Release()
        {
            VulkanDevice device = (VulkanDevice)Texture.Device;
            Texture.DisposeFromParent();
            if (RenderComplete.Handle != 0)
                device.Backend.Api.DestroySemaphore(device.Native, RenderComplete, null);
        }
    }

    private sealed class VulkanSwapchainImageLease : SwapchainImageLease
    {
        private readonly VulkanDevice _device;
        private VulkanImageState? _image;
        private VulkanQueue? _submissionQueue;
        private ulong _submissionCompletion;
        private bool _inUse;
        private bool _acquisitionPending;
        private bool _acquireFenceNeedsReset;

        internal VulkanSwapchainImageLease(
            VulkanDevice device,
            VulkanSwapchain swapchain,
            VkSemaphore acquireSemaphore,
            Silk.NET.Vulkan.Fence acquireFence)
            : base(swapchain)
        {
            _device = device;
            AcquireSemaphore = acquireSemaphore;
            AcquireFence = acquireFence;
        }

        internal VkSemaphore AcquireSemaphore { get; }
        internal Silk.NET.Vulkan.Fence AcquireFence { get; }
        internal VkSemaphore RenderComplete => _image?.RenderComplete
            ?? throw new InvalidOperationException("The swapchain lease has no image.");
        internal bool InUse => _inUse;
        internal bool CanAcquire => !_inUse &&
            (_submissionQueue is null || _submissionQueue.GetCompletedValue() >= _submissionCompletion) &&
            ObserveAcquisitionCompletion(waitForAcquisition: false);

        internal void PrepareAcquireFence()
        {
            if (_acquisitionPending)
                throw new InvalidOperationException("A swapchain acquisition is still pending.");
            if (!_acquireFenceNeedsReset)
                return;
            Silk.NET.Vulkan.Fence fence = AcquireFence;
            _device.ThrowIfDeviceCallFailed(
                _device.Backend.Api.ResetFences(_device.Native, 1, &fence),
                "vkResetFences(swapchain acquisition)");
            _acquireFenceNeedsReset = false;
        }

        internal void MarkAcquisitionPending()
        {
            _acquisitionPending = true;
            _acquireFenceNeedsReset = true;
        }

        internal bool ObserveAcquisitionCompletion(bool waitForAcquisition)
        {
            if (!_acquisitionPending)
                return true;
            Silk.NET.Vulkan.Fence fence = AcquireFence;
            Result result = waitForAcquisition
                ? _device.Backend.Api.WaitForFences(_device.Native, 1, &fence, true, ulong.MaxValue)
                : _device.Backend.Api.GetFenceStatus(_device.Native, fence);
            if (!waitForAcquisition && result == Result.NotReady)
                return false;
            if (result == Result.ErrorDeviceLost)
            {
                // For this native wait/query, device loss has completion semantics.
                _acquisitionPending = false;
                _ = _device.PublishDeviceLoss(result, waitForAcquisition
                    ? "vkWaitForFences(swapchain acquisition)"
                    : "vkGetFenceStatus(swapchain acquisition)");
                return true;
            }
            _device.ThrowIfDeviceCallFailed(result, waitForAcquisition
                ? "vkWaitForFences(swapchain acquisition)"
                : "vkGetFenceStatus(swapchain acquisition)");
            _acquisitionPending = false;
            return true;
        }

        internal void Begin(
            ulong sequence,
            ulong generation,
            VulkanImageState image,
            bool preserveContents)
        {
            _image = image;
            _inUse = true;
            BeginAcquire(
                sequence,
                generation,
                image.Texture,
                PipelineSync.None,
                ResourceAccess.NoAccess,
                preserveContents ? TextureLayout.Present : TextureLayout.Undefined);
            image.BeginAcquisition(this, sequence, generation);
        }

        internal bool ClaimSubmit(ulong sequence, VulkanQueue queue)
        {
            if (!ReferenceEquals(queue.Device, _device) || queue.Type != QueueType.Graphics || queue.Index != 0)
                throw new ArgumentException("Swapchain images require the Device Graphics Queue at index zero.", nameof(queue));
            return TryBeginSubmit(sequence);
        }

        internal void MarkSubmission(VulkanQueue queue, ulong completion)
        {
            _submissionQueue = queue;
            _submissionCompletion = completion;
        }

        internal PresentStatus Present(VulkanQueue queue, ulong sequence)
        {
            ValidatePresentQueue(queue);
            if (GetStatus(sequence) != SwapchainImageStatus.Submitted)
                throw new InvalidOperationException("The SwapchainImage has not been submitted or was already presented.");
            VkSemaphore wait = RenderComplete;
            var owner = (VulkanSwapchain)Swapchain;
            VkSwapchain swapchain = owner.Native;
            uint imageIndex = owner.IndexOf(_image);
            if (!TryBeginPresent(sequence))
                throw new InvalidOperationException("The SwapchainImage has not been submitted or was already presented.");
            PresentInfoKHR present = new()
            {
                SType = StructureType.PresentInfoKhr,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &wait,
                SwapchainCount = 1,
                PSwapchains = &swapchain,
                PImageIndices = &imageIndex,
            };
            Result result;
            lock (queue.Gate)
            {
                _device.ThrowIfUnavailable();
                result =
                    _device.SwapchainApi.QueuePresent(queue.Native, &present);
            }
            switch (result)
            {
                case Result.Success:
                    _image!.EndAcquisition(this, sequence);
                    _inUse = false;
                    return PresentStatus.Success;
                case Result.SuboptimalKhr:
                    _image!.EndAcquisition(this, sequence);
                    _inUse = false;
                    return PresentStatus.Suboptimal;
                case Result.ErrorOutOfDateKhr:
                case Result.ErrorSurfaceLostKhr:
                    _image!.EndAcquisition(this, sequence);
                    _inUse = false;
                    owner.MarkOutOfDate();
                    return PresentStatus.OutOfDate;
                case Result.ErrorOutOfHostMemory:
                case Result.ErrorOutOfDeviceMemory:
                    RestoreSubmittedAfterPresentFailure(sequence);
                    _device.ThrowIfDeviceCallFailed(result, "vkQueuePresentKHR");
                    throw new InvalidOperationException("Unreachable Vulkan Present result handling path.");
                case Result.ErrorDeviceLost:
                    _inUse = false;
                    Invalidate(deviceLost: true);
                    throw _device.PublishDeviceLoss(result, "vkQueuePresentKHR");
                default:
                    _inUse = false;
                    Invalidate(deviceLost: true);
                    throw _device.PublishInternalDeviceLoss(
                        $"vkQueuePresentKHR returned uncertain failure {result}.");
            }
        }

        internal void Restore(ulong sequence)
        {
            RestoreAcquired(sequence);
            _submissionQueue = null;
            _submissionCompletion = 0;
        }

        internal void Release()
        {
            if (_acquisitionPending)
                throw new InvalidOperationException("A pending acquisition cannot release its synchronization objects.");
            _image?.EndAcquisition(this, CurrentSequence);
            Invalidate(deviceLost: false);
            if (AcquireSemaphore.Handle != 0)
                _device.Backend.Api.DestroySemaphore(_device.Native, AcquireSemaphore, null);
            if (AcquireFence.Handle != 0)
                _device.Backend.Api.DestroyFence(_device.Native, AcquireFence, null);
            _image = null;
            _inUse = false;
        }

        private void ValidatePresentQueue(VulkanQueue queue)
        {
            if (!ReferenceEquals(queue.Device, _device) || queue.Type != QueueType.Graphics || queue.Index != 0)
                throw new ArgumentException("Swapchain presentation requires the Device Graphics Queue at index zero.", nameof(queue));
        }
    }

    private static ColorSpaceKHR ToNative(ColorSpace colorSpace) => colorSpace switch
    {
        ColorSpace.Srgb => ColorSpaceKHR.SpaceSrgbNonlinearKhr,
        ColorSpace.ScRgb => ColorSpaceKHR.SpaceExtendedSrgbLinearExt,
        ColorSpace.Hdr10 => ColorSpaceKHR.SpaceHdr10ST2084Ext,
        _ => throw new ArgumentOutOfRangeException(nameof(colorSpace)),
    };

    private static PresentModeKHR ToNative(PresentType present) => present switch
    {
        PresentType.Immediate => PresentModeKHR.ImmediateKhr,
        PresentType.Mailbox => PresentModeKHR.MailboxKhr,
        PresentType.Fifo => PresentModeKHR.FifoKhr,
        _ => throw new ArgumentOutOfRangeException(nameof(present)),
    };

    private static bool TryFromNative(ColorSpaceKHR native, out ColorSpace result)
    {
        result = native switch
        {
            ColorSpaceKHR.SpaceSrgbNonlinearKhr => ColorSpace.Srgb,
            ColorSpaceKHR.SpaceExtendedSrgbLinearExt => ColorSpace.ScRgb,
            ColorSpaceKHR.SpaceHdr10ST2084Ext => ColorSpace.Hdr10,
            _ => (ColorSpace)byte.MaxValue,
        };
        return result != (ColorSpace)byte.MaxValue;
    }

    private static bool TryFromNative(PresentModeKHR native, out PresentType result)
    {
        result = native switch
        {
            PresentModeKHR.ImmediateKhr => PresentType.Immediate,
            PresentModeKHR.MailboxKhr => PresentType.Mailbox,
            PresentModeKHR.FifoKhr => PresentType.Fifo,
            _ => (PresentType)byte.MaxValue,
        };
        return result != (PresentType)byte.MaxValue;
    }
}
