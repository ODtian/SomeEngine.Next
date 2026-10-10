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

            _backend.UnregisterSurface(this);
            _lifetime.Release();
        }

        private void DestroyNative() { if (_native.Handle != 0) _backend._surfaceApi!.DestroySurface(_backend.Instance, _native, null); _native = default; }
    }

    private sealed class VulkanSwapchain : Swapchain
    {
        private readonly VulkanDevice _device;
        private readonly VulkanSurface _surface;
        private readonly object _gate = new();
        private readonly uint _requestedImageCount;
        private VkSwapchain _native;
        private VulkanImageState[] _images = [];
        private VulkanSwapchainImageLease[] _leases = [];
        private ulong _nextSequence;
        private bool _outOfDate;

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
                _leases = CreateLeases(device, this, images.Length, minimumImageCount, info.Config.MaximumFrameLatency);
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

        internal static VulkanSwapchain Create(
            VulkanDevice device,
            VulkanSurface surface,
            in SwapchainDesc desc)
        {
            ValidateDescription(desc);
            SwapchainSupport[] support = QuerySupport(device, surface);
            RequireConfiguration(desc.Config, support);
            SwapchainCreateInfoKHR createInfo = GetCreateInfo(
                device, surface, desc.ImageCount, desc.ImageUsages, desc.Config,
                out SwapchainConfig resolvedConfig, out uint minimumImageCount);
            if (createInfo.ImageExtent.Width == 0 || createInfo.ImageExtent.Height == 0)
                throw new NotSupportedException("A minimized Surface has no drawable extent.");
            VkSwapchain native = CreateNative(device, createInfo);
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
                _device.ThrowIfUnavailable();
                image = default;
                if (options.Timeout < TimeSpan.Zero && options.Timeout != Timeout.InfiniteTimeSpan)
                    throw new ArgumentOutOfRangeException(nameof(options));
                if (options.PreserveContents)
                    throw new NotSupportedException("Swapchain contents are discarded during acquisition.");
                if (IsIconic(_surface.WindowHandle))
                    _outOfDate = true;
                if (_outOfDate)
                    return SwapchainAcquireStatus.OutOfDate;
                VulkanSwapchainImageLease? lease = FindAvailableLease();
                if (lease is null)
                    return SwapchainAcquireStatus.Timeout;
                ulong timeout = options.Timeout == Timeout.InfiniteTimeSpan
                    ? ulong.MaxValue
                    : checked((ulong)options.Timeout.Ticks * 100);
                if (_nextSequence >= ulong.MaxValue - 1)
                    throw new OverflowException("Swapchain acquisition sequence exhausted.");
                ulong sequence = _nextSequence + 1;
                uint imageIndex = 0;
                lease.PrepareAcquire();
                Result result = _device.SwapchainApi.AcquireNextImage(
                    _device.Native,
                    _native,
                    timeout,
                    lease.AcquireSemaphore,
                    lease.AcquireFence,
                    &imageIndex);
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
                    _images[checked((int)imageIndex)]);
                image = new SwapchainImage(lease, sequence);
                return SwapchainAcquireStatus.Success;
            }
        }

        internal ReconfigureStatus Reconfigure(in SwapchainConfig config)
        {
            lock (_gate)
            {
                _device.ThrowIfUnavailable();
                ValidateConfiguration(config);
                SwapchainSupport[] support = QuerySupport(_device, _surface);
                if (!Supports(config, support))
                    return ReconfigureStatus.Unsupported;
                foreach (VulkanSwapchainImageLease lease in _leases)
                    if (!lease.CanAcquire)
                        return ReconfigureStatus.Busy;
                if (IsIconic(_surface.WindowHandle))
                    return ReconfigureStatus.Busy;

                SwapchainCreateInfoKHR createInfo = GetCreateInfo(
                    _device, _surface, _requestedImageCount, ImageUsages, config,
                    out SwapchainConfig resolvedConfig, out uint minimumImageCount);
                if (createInfo.ImageExtent.Width == 0 || createInfo.ImageExtent.Height == 0)
                    return ReconfigureStatus.Busy;
                if (!_outOfDate && resolvedConfig == Info.Config)
                    return ReconfigureStatus.Success;
                ulong generation = checked(Info.Generation + 1);
                VkSwapchain previous = _native;
                createInfo.OldSwapchain = previous;
                VkSwapchain replacement = default;
                VulkanImageState[] images = [];
                VulkanSwapchainImageLease[] leases = [];
                _outOfDate = true;
                try
                {
                    // Vulkan retires oldSwapchain even when creation fails. Once this call
                    // begins, a retry must create without the retired handle.
                    replacement = CreateNative(_device, createInfo);
                    VkImage[] nativeImages = GetImages(_device, replacement);
                    images = CreateImageStates(_device, nativeImages, ImageUsages, resolvedConfig);
                    leases = CreateLeases(_device, this, images.Length, minimumImageCount, config.MaximumFrameLatency);
                    SwapchainInfo nextInfo = new(
                        resolvedConfig, checked((uint)images.Length), generation, support);
                    ReleaseGeneration();
                    _native = replacement;
                    _images = images;
                    _leases = leases;
                    Info = nextInfo;
                    _outOfDate = false;
                    return ReconfigureStatus.Success;
                }
                catch
                {
                    foreach (VulkanSwapchainImageLease lease in leases) lease.Release();
                    foreach (VulkanImageState image in images) image.Release();
                    if (replacement.Handle != 0)
                        _device.SwapchainApi.DestroySwapchain(_device.Native, replacement, null);
                    ReleaseGeneration();
                    _native = default;
                    throw;
                }
                finally
                {
                    if (previous.Handle != 0)
                        _device.SwapchainApi.DestroySwapchain(_device.Native, previous, null);
                }
            }
        }

        internal override void Release(bool fromParent)
        {
            lock (_gate)
            {
                foreach (VulkanSwapchainImageLease lease in _leases)
                    lease.WaitForCompletion();
                ReleaseGeneration();
                if (_native.Handle != 0)
                    _device.SwapchainApi.DestroySwapchain(_device.Native, _native, null);
                _native = default;
            }
            _surface.UnregisterSwapchain(this);
            _surface.ReleaseNative();
            _device.UnregisterChild(this);
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

        private static SwapchainCreateInfoKHR GetCreateInfo(
            VulkanDevice device,
            VulkanSurface surface,
            uint requestedImageCount,
            TextureUsages usages,
            in SwapchainConfig config,
            out SwapchainConfig resolvedConfig,
            out uint minimumImageCount)
        {
            KhrSurface surfaceApi = device.Backend._surfaceApi!;
            SurfaceCapabilitiesKHR capabilities;
            device.ThrowIfDeviceCallFailed(
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
            device.ThrowIfDeviceCallFailed(
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
                Clipped = (usages & TextureUsages.CopySource) == 0,
            };
            resolvedConfig = config with { Width = extent.Width, Height = extent.Height };
            return createInfo;
        }

        private static VkSwapchain CreateNative(
            VulkanDevice device, SwapchainCreateInfoKHR createInfo)
        {
            VkSwapchain native = default;
            Result result =
                device.SwapchainApi.CreateSwapchain(
                device.Native,
                &createInfo,
                null,
                &native);
            device.ThrowIfDeviceCallFailed(result, "vkCreateSwapchainKHR");
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
                    VkSemaphore renderComplete;
                    try
                    {
                        renderComplete = CreateBinarySemaphore(device);
                    }
                    catch
                    {
                        texture.DisposeFromParent();
                        throw;
                    }
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
            // Acquiring more than S - M + 1 images can prevent forward progress,
            // even with an infinite acquire timeout (VUID-surface-07783).
            int capacity = checked(count - (int)minimumImageCount + 1);
            var result = new VulkanSwapchainImageLease[Math.Min(capacity, checked((int)Math.Min(maximumFrameLatency, int.MaxValue)))];
            try
            {
                for (int index = 0; index < result.Length; index++)
                    result[index] = VulkanSwapchainImageLease.Create(device, swapchain);
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
            device.ThrowIfDeviceCallFailed(
                api.GetPhysicalDeviceSurfaceFormats(device.PhysicalDevice, surface.Native, &formatCount, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR(count)");
            SurfaceFormatKHR[] formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* pointer = formats)
                device.ThrowIfDeviceCallFailed(api.GetPhysicalDeviceSurfaceFormats(device.PhysicalDevice, surface.Native, &formatCount, pointer), "vkGetPhysicalDeviceSurfaceFormatsKHR(data)");
            uint modeCount = 0;
            device.ThrowIfDeviceCallFailed(
                api.GetPhysicalDeviceSurfacePresentModes(device.PhysicalDevice, surface.Native, &modeCount, null),
                "vkGetPhysicalDeviceSurfacePresentModesKHR(count)");
            PresentModeKHR[] modes = new PresentModeKHR[modeCount];
            fixed (PresentModeKHR* pointer = modes)
                device.ThrowIfDeviceCallFailed(api.GetPhysicalDeviceSurfacePresentModes(device.PhysicalDevice, surface.Native, &modeCount, pointer), "vkGetPhysicalDeviceSurfacePresentModesKHR(data)");
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
                        presentType == PresentType.Immediate));
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
            ValidateConfiguration(config);
            if (!Supports(config, support))
                throw new NotSupportedException("The Surface does not support the requested swapchain configuration.");
        }

        private static void ValidateConfiguration(in SwapchainConfig config)
        {
            if (config.Width == 0 || config.Height == 0 || config.MaximumFrameLatency == 0)
                throw new ArgumentOutOfRangeException(nameof(config));
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
            if (config.Hdr10Metadata is not null ||
                (config.PresentType == PresentType.Immediate && !config.AllowTearing))
                return false;
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
        private bool _hasAcquisition;
        private bool _presentEnqueued;

        private VulkanSwapchainImageLease(VulkanDevice device, VulkanSwapchain swapchain)
            : base(swapchain)
        {
            _device = device;
            try
            {
                SemaphoreCreateInfo semaphoreInfo = new() { SType = StructureType.SemaphoreCreateInfo };
                VkSemaphore semaphore = default;
                device.ThrowIfDeviceCallFailed(
                    device.Backend.Api.CreateSemaphore(device.Native, &semaphoreInfo, null, &semaphore),
                    "vkCreateSemaphore(acquire)");
                AcquireSemaphore = semaphore;
                AcquireFence = CreateFence();
                PresentFence = CreateFence();
            }
            catch
            {
                Release();
                throw;
            }
        }

        internal static VulkanSwapchainImageLease Create(VulkanDevice device, VulkanSwapchain swapchain) =>
            new(device, swapchain);

        internal VkSemaphore AcquireSemaphore { get; private set; }
        internal Fence AcquireFence { get; private set; }
        private Fence PresentFence { get; set; }
        internal VkSemaphore RenderComplete => _image?.RenderComplete
            ?? throw new InvalidOperationException("The swapchain lease has no image.");
        internal bool CanAcquire => !_inUse && NativeOperationsComplete();

        internal void PrepareAcquire()
        {
            Fence* fences = stackalloc Fence[2] { AcquireFence, PresentFence };
            _device.ThrowIfDeviceCallFailed(
                _device.Backend.Api.ResetFences(_device.Native, 2, fences),
                "vkResetFences(swapchain)");
            _hasAcquisition = false;
            _presentEnqueued = false;
            _submissionQueue = null;
            _submissionCompletion = 0;
        }

        internal void Begin(ulong sequence, ulong generation, VulkanImageState image)
        {
            _image = image;
            _inUse = true;
            _hasAcquisition = true;
            BeginAcquire(
                sequence, generation, image.Texture,
                PipelineSync.None, ResourceAccess.NoAccess, TextureLayout.Undefined);
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
            var swapchain = (VulkanSwapchain)Swapchain;
            VkSwapchain native = swapchain.Native;
            uint imageIndex = swapchain.IndexOf(_image);
            if (!TryBeginPresent(sequence))
                throw new InvalidOperationException("The SwapchainImage has not been submitted or was already presented.");
            Fence fence = PresentFence;
            SwapchainPresentFenceInfoEXT fenceInfo = new()
            {
                SType = StructureType.SwapchainPresentFenceInfoExt,
                SwapchainCount = 1,
                PFences = &fence,
            };
            PresentInfoKHR present = new()
            {
                SType = StructureType.PresentInfoKhr,
                PNext = &fenceInfo,
                WaitSemaphoreCount = 1,
                PWaitSemaphores = &wait,
                SwapchainCount = 1,
                PSwapchains = &native,
                PImageIndices = &imageIndex,
            };
            Result result;
            lock (queue.Gate)
            {
                _device.ThrowIfUnavailable();
                result = _device.SwapchainApi.QueuePresent(queue.Native, &present);
            }
            switch (result)
            {
                case Result.Success:
                case Result.SuboptimalKhr:
                    CompletePresent(sequence);
                    return IsIconic(swapchain.Surface.WindowHandle)
                        ? PresentStatus.Occluded
                        : result == Result.SuboptimalKhr ? PresentStatus.Suboptimal : PresentStatus.Success;
                case Result.ErrorOutOfDateKhr:
                case Result.ErrorSurfaceLostKhr:
                    // These errors still enqueue the semaphore wait and present fence.
                    CompletePresent(sequence);
                    swapchain.MarkOutOfDate();
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

        private void CompletePresent(ulong sequence)
        {
            _presentEnqueued = true;
            _image!.EndAcquisition(this, sequence);
            _inUse = false;
        }

        internal void Restore(ulong sequence)
        {
            RestoreAcquired(sequence);
            _submissionQueue = null;
            _submissionCompletion = 0;
        }

        private bool NativeOperationsComplete()
        {
            if (_hasAcquisition && !FenceComplete(AcquireFence)) return false;
            if (_submissionQueue is not null &&
                _submissionQueue.GetCompletedValue() < _submissionCompletion) return false;
            return !_presentEnqueued || FenceComplete(PresentFence);
        }

        private bool FenceComplete(Fence fence)
        {
            Result result = _device.Backend.Api.GetFenceStatus(_device.Native, fence);
            if (result == Result.NotReady) return false;
            _device.ThrowIfDeviceCallFailed(result, "vkGetFenceStatus(swapchain)");
            return true;
        }

        internal void WaitForCompletion()
        {
            if (_device.Loss is not null) return;
            if (_hasAcquisition) WaitFence(AcquireFence);
            if (_submissionQueue is not null)
            {
                VkSemaphore semaphore = _submissionQueue.CompletionSemaphore;
                ulong value = _submissionCompletion;
                SemaphoreWaitInfo wait = new()
                {
                    SType = StructureType.SemaphoreWaitInfo,
                    SemaphoreCount = 1,
                    PSemaphores = &semaphore,
                    PValues = &value,
                };
                _device.ThrowIfDeviceCallFailed(
                    _device.Backend.Api.WaitSemaphores(_device.Native, &wait, ulong.MaxValue),
                    "vkWaitSemaphores(swapchain submission)");
            }
            if (_presentEnqueued) WaitFence(PresentFence);
        }

        private void WaitFence(Fence fence) => _device.ThrowIfDeviceCallFailed(
            _device.Backend.Api.WaitForFences(_device.Native, 1, &fence, true, ulong.MaxValue),
            "vkWaitForFences(swapchain)");

        private Fence CreateFence()
        {
            FenceCreateInfo info = new() { SType = StructureType.FenceCreateInfo };
            Fence fence = default;
            _device.ThrowIfDeviceCallFailed(
                _device.Backend.Api.CreateFence(_device.Native, &info, null, &fence),
                "vkCreateFence(swapchain)");
            return fence;
        }

        internal void Release()
        {
            _image?.EndAcquisition(this, CurrentSequence);
            Invalidate(deviceLost: _device.Loss is not null);
            if (AcquireSemaphore.Handle != 0)
                _device.Backend.Api.DestroySemaphore(_device.Native, AcquireSemaphore, null);
            if (AcquireFence.Handle != 0)
                _device.Backend.Api.DestroyFence(_device.Native, AcquireFence, null);
            if (PresentFence.Handle != 0)
                _device.Backend.Api.DestroyFence(_device.Native, PresentFence, null);
            AcquireSemaphore = default;
            AcquireFence = default;
            PresentFence = default;
            _image = null;
            _inUse = false;
        }

        private void ValidatePresentQueue(VulkanQueue queue)
        {
            if (!ReferenceEquals(queue.Device, _device) || queue.Type != QueueType.Graphics || queue.Index != 0)
                throw new ArgumentException("Swapchain presentation requires the Device Graphics Queue at index zero.", nameof(queue));
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

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
