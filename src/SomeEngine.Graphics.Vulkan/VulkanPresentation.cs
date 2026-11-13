using Silk.NET.Vulkan.Extensions.KHR;

namespace SomeEngine.Graphics.Vulkan;

internal sealed unsafe partial class VulkanBackend
{
    private RhiSurface CreateSurfaceCore(in SurfaceDesc desc)
    {

        if (!OperatingSystem.IsWindows() || desc.Type != NativeWindowType.Win32)
            throw new PlatformNotSupportedException("The Vulkan backend currently requires a Win32 Surface.");
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
        VulkanSwapchainImageUse handle = (VulkanSwapchainImageUse)image.ImageUse!;
        return handle.Present(nativeQueue, image.Sequence);
    }

    private ReconfigureStatus ReconfigureCore(
        Swapchain swapchain,
        in SwapchainConfig config)
    {
        VulkanSwapchain native = RequireSwapchain(swapchain, nameof(swapchain));
        return native.Reconfigure(config);
    }

    private VulkanSurface RequireSurface(RhiSurface surface, string parameterName)
=>(VulkanSurface)surface;

    private VulkanSwapchain RequireSwapchain(Swapchain swapchain, string parameterName)
=>(VulkanSwapchain)swapchain;

    private sealed class VulkanSurface : RhiSurface, IVulkanRetained
    {

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
                    swapchain.Device.RecordReleaseFailure(new GraphicsException(GraphicsError.NativeFailure, "A Vulkan Swapchain did not detach during Surface teardown."));
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
        internal override IReadOnlyList<SwapchainImageUse> ImageUses => _imageUses;
        internal override bool IsOutOfDate => Volatile.Read(ref _outOfDate);
        private VulkanSwapchainImageUse[] _imageUses = [];
        private ulong _nextSequence;
        private bool _outOfDate;

        private VulkanSwapchain(
            VulkanDevice device,
            VulkanSurface surface,
            VkSwapchain native,
            VulkanImageState[] images,
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
                _imageUses = CreateImageUses(device, this, images.Length);
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

            SwapchainSupport[] support = QuerySupport(device, surface);
            RequireConfiguration(desc.Config, support);
            VkSwapchain native = CreateNative(
                device,
                surface,
                desc.ImageCount,
                desc.ImageUsages,
                desc.Config,
                default,
                out SwapchainConfig resolvedConfig);
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

                VulkanSwapchainImageUse? handle = FindAvailableUse();
                if (handle is null)
                    return SwapchainAcquireStatus.Timeout;
                ulong timeout = options.Timeout == Timeout.InfiniteTimeSpan
                    ? ulong.MaxValue
                    : checked((ulong)options.Timeout.Ticks * 100);
                if (_nextSequence == ulong.MaxValue)
                    throw new GraphicsException(GraphicsError.NativeFailure, "Swapchain acquisition sequence exhausted.");
                ulong sequence = _nextSequence + 1;
                uint imageIndex = 0;
                Result result = _device.SwapchainApi.AcquireNextImage(
                    _device.Native,
                    _native,
                    timeout,
                    handle.AcquireSemaphore,
                    default,
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
                handle.Begin(
                    sequence,
                    Info.Generation,
                    _images[checked((int)imageIndex)],
                    options.PreserveContents);
                image = new SwapchainImage(handle, sequence);
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
                ReleaseGeneration();
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
                foreach (VulkanSwapchainImageUse handle in _imageUses)
                    handle.Invalidate(deviceLost: true);
            }
        }

        internal void MarkOutOfDate()
        {
            lock (_gate)
                _outOfDate = true;
        }

        private VulkanSwapchainImageUse? FindAvailableUse()
        {
            foreach (VulkanSwapchainImageUse handle in _imageUses)
                if (handle.CanAcquire)
                    return handle;
            return null;
        }

        private void ReleaseGeneration()
        {
            foreach (VulkanSwapchainImageUse handle in _imageUses)
                handle.Release();
            foreach (VulkanImageState image in _images)
                image.Release();
            _imageUses = [];
            _images = [];
        }

        private static VkSwapchain CreateNative(
            VulkanDevice device,
            VulkanSurface surface,
            uint requestedImageCount,
            TextureUsages usages,
            in SwapchainConfig config,
            VkSwapchain oldSwapchain,
            out SwapchainConfig resolvedConfig)
        {
            KhrSurface surfaceApi = device.Backend._surfaceApi!;
            SurfaceCapabilitiesKHR capabilities;
            ThrowIfFailed(
                surfaceApi.GetPhysicalDeviceSurfaceCapabilities(
                    device.AdapterDevice,
                    surface.Native,
                    &capabilities),
                "vkGetPhysicalDeviceSurfaceCapabilitiesKHR");
            uint imageCount = Math.Max(requestedImageCount, capabilities.MinImageCount);
            if (capabilities.MaxImageCount != 0)
                imageCount = Math.Min(imageCount, capabilities.MaxImageCount);
            Extent2D extent = capabilities.CurrentExtent.Width != uint.MaxValue
                ? capabilities.CurrentExtent
                : new Extent2D(
                    Math.Clamp(config.Width, capabilities.MinImageExtent.Width, capabilities.MaxImageExtent.Width),
                    Math.Clamp(config.Height, capabilities.MinImageExtent.Height, capabilities.MaxImageExtent.Height));
            ImageUsageFlags nativeUsages = ToNative(usages);

            uint family = device.GetQueue(QueueType.Graphics, 0).FamilyIndex;
            Silk.NET.Core.Bool32 presentSupported = false;
            ThrowIfFailed(
                surfaceApi.GetPhysicalDeviceSurfaceSupport(
                    device.AdapterDevice,
                    family,
                    surface.Native,
                    &presentSupported),
                "vkGetPhysicalDeviceSurfaceSupportKHR");

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

        private static VulkanSwapchainImageUse[] CreateImageUses(
            VulkanDevice device,
            VulkanSwapchain swapchain,
            int count)
        {
            var result = new VulkanSwapchainImageUse[count];
            try
            {
                for (int index = 0; index < result.Length; index++)
                    result[index] = new VulkanSwapchainImageUse(
                        device,
                        swapchain,
                        CreateBinarySemaphore(device));
                return result;
            }
            catch
            {
                foreach (VulkanSwapchainImageUse? handle in result)
                    handle?.Release();
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
                api.GetPhysicalDeviceSurfaceFormats(device.AdapterDevice, surface.Native, &formatCount, null),
                "vkGetPhysicalDeviceSurfaceFormatsKHR(count)");
            SurfaceFormatKHR[] formats = new SurfaceFormatKHR[formatCount];
            fixed (SurfaceFormatKHR* pointer = formats)
                ThrowIfFailed(
                    api.GetPhysicalDeviceSurfaceFormats(device.AdapterDevice, surface.Native, &formatCount, pointer),
                    "vkGetPhysicalDeviceSurfaceFormatsKHR(data)"
                );
            uint modeCount = 0;
            ThrowIfFailed(
                api.GetPhysicalDeviceSurfacePresentModes(device.AdapterDevice, surface.Native, &modeCount, null),
                "vkGetPhysicalDeviceSurfacePresentModesKHR(count)");
            PresentModeKHR[] modes = new PresentModeKHR[modeCount];
            fixed (PresentModeKHR* pointer = modes)
                ThrowIfFailed(
                    api.GetPhysicalDeviceSurfacePresentModes(device.AdapterDevice, surface.Native, &modeCount, pointer),
                    "vkGetPhysicalDeviceSurfacePresentModesKHR(data)"
                );
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

        private static void RequireConfiguration(
            in SwapchainConfig config,
            ReadOnlySpan<SwapchainSupport> support)
        {

            if (!Supports(config, support))
                throw new GraphicsException(GraphicsError.NativeFailure, "The surface does not support the requested swapchain configuration.");
        }

        internal uint IndexOf(VulkanImageState? image)
        {
            int index = Array.IndexOf(_images, image);
            return unchecked((uint)index);
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
            throw new GraphicsException(GraphicsError.NativeFailure, "The surface exposes no supported composite-alpha mode.");
        }
    }

    private readonly record struct VulkanSwapchainUse(
        VulkanSwapchainImageUse Handle,
        ulong Sequence,
        ulong Generation);

    private sealed class VulkanImageState(VulkanTexture texture, VkSemaphore renderComplete)
    {
        private readonly object _gate = new();
        private VulkanSwapchainImageUse? _currentUse;
        private ulong _currentSequence;
        private ulong _currentGeneration;

        internal VulkanTexture Texture { get; } = texture;
        internal VkSemaphore RenderComplete { get; } = renderComplete;

        internal void BeginAcquisition(
            VulkanSwapchainImageUse handle,
            ulong sequence,
            ulong generation)
        {
            lock (_gate)
            {
                _currentUse = handle;
                _currentSequence = sequence;
                _currentGeneration = generation;
            }
        }

        internal bool TryGetCurrentUse(out VulkanSwapchainUse use)
        {
            lock (_gate)
            {
                if (_currentUse is null)
                {
                    use = default;
                    return false;
                }
                use = new VulkanSwapchainUse(
                    _currentUse,
                    _currentSequence,
                    _currentGeneration);
                return true;
            }
        }

        internal void EndAcquisition(
            VulkanSwapchainImageUse handle,
            ulong sequence)
        {
            lock (_gate)
            {
                if (!ReferenceEquals(_currentUse, handle) ||
                    _currentSequence != sequence)
                {
                    return;
                }
                _currentUse = null;
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

    private sealed class VulkanSwapchainImageUse : SwapchainImageUse
    {
        private readonly VulkanDevice _device;
        private VulkanImageState? _image;
        private VulkanQueue? _submissionQueue;
        private ulong _submissionCompletion;
        private bool _inUse;

        internal VulkanSwapchainImageUse(
            VulkanDevice device,
            VulkanSwapchain swapchain,
            VkSemaphore acquireSemaphore)
            : base(swapchain)
        {
            _device = device;
            AcquireSemaphore = acquireSemaphore;
        }

        internal VkSemaphore AcquireSemaphore { get; }
        internal VkSemaphore RenderComplete => _image!.RenderComplete;
        internal bool InUse => _inUse;
        internal bool CanAcquire => !_inUse &&
            (_submissionQueue is null || _submissionQueue.GetCompletedValue() >= _submissionCompletion);

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

            return TryBeginSubmit(sequence);
        }

        internal void MarkSubmission(VulkanQueue queue, ulong completion)
        {
            _submissionQueue = queue;
            _submissionCompletion = completion;
        }

        internal PresentStatus Present(VulkanQueue queue, ulong sequence)
        {

            VkSemaphore wait = RenderComplete;
            var owner = (VulkanSwapchain)Swapchain;
            VkSwapchain swapchain = owner.Native;
            uint imageIndex = owner.IndexOf(_image);
            _ = TryBeginPresent(sequence);

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
                    throw new GraphicsException(GraphicsError.NativeFailure, "Vulkan returned an unexpected present result.", nativeCode: (long)result);
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
            _image?.EndAcquisition(this, CurrentSequence);
            Invalidate(deviceLost: false);
            if (AcquireSemaphore.Handle != 0)
                _device.Backend.Api.DestroySemaphore(_device.Native, AcquireSemaphore, null);
            _image = null;
            _inUse = false;
        }

    }

    private static ColorSpaceKHR ToNative(ColorSpace colorSpace) => colorSpace switch
    {
        ColorSpace.Srgb => ColorSpaceKHR.SpaceSrgbNonlinearKhr,
        ColorSpace.ScRgb => ColorSpaceKHR.SpaceExtendedSrgbLinearExt,
        ColorSpace.Hdr10 => ColorSpaceKHR.SpaceHdr10ST2084Ext,
        _ => default,
    };

    private static PresentModeKHR ToNative(PresentType present) => present switch
    {
        PresentType.Immediate => PresentModeKHR.ImmediateKhr,
        PresentType.Mailbox => PresentModeKHR.MailboxKhr,
        PresentType.Fifo => PresentModeKHR.FifoKhr,
        _ => default,
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
