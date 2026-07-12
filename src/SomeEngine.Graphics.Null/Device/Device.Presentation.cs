namespace SomeEngine.Graphics.Null;

public sealed partial class Device
{
    public SwapchainHandle CreateSwapchain(in SwapchainDesc desc)
    {
        EnsureCoordinatorThread();
        desc.Validate(requireWindowHandle: false);
        if ((GetFormatSupport(desc.Format) & FormatSupport.Present) == 0)
            throw UnsupportedError($"Format {desc.Format} cannot be presented by the Null profile.");
        if (desc.ColorSpace == SwapchainColorSpace.Hdr10)
            throw UnsupportedError("The deterministic Null profile does not advertise HDR10 output.");

        lock (_gate)
        {
            EnsureNotDisposed();
            TextureHandle[] images = CreateSwapchainImages(desc);
            try
            {
                (uint slot, uint generation) = _swapchains.Allocate(new SwapchainRecord
                {
                    Desc = desc,
                    Images = images,
                });
                return new SwapchainHandle(_domain, slot, generation);
            }
            catch
            {
                ReleaseAndDestroySwapchainImages(images);
                throw;
            }
        }
    }

    public SwapchainImage AcquireNextImage(SwapchainHandle swapchain)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            if (_lastError.Kind == DeviceErrorKind.DeviceLost)
                throw ValidationError("A lost device cannot acquire another swapchain image.");
            SwapchainRecord record = RequireSwapchain(swapchain);
            if (record.AcquiredImage >= 0)
                throw ValidationError("Present the acquired swapchain image before acquiring another one.");
            uint index = record.NextImage;
            record.NextImage = (index + 1) % checked((uint)record.Images.Length);
            record.AcquiredImage = checked((int)index);
            return new SwapchainImage(record.Images[index], index);
        }
    }

    public PresentResult Present(SwapchainHandle swapchain, uint imageIndex, in PresentOptions options = default)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            SwapchainRecord record = RequireSwapchain(swapchain);
            ValidatePresentImage(record, imageIndex);
            ValidatePresentOptions(record, options);
            ValidatePresentState(record);
            record.AcquiredImage = -1;
            return CompletePresent();
        }
    }

    private void ValidatePresentImage(SwapchainRecord record, uint imageIndex)
    {
        if (record.AcquiredImage < 0 || imageIndex != checked((uint)record.AcquiredImage))
            throw ValidationError("Present requires the exact currently acquired swapchain image.");
    }

    private void ValidatePresentOptions(SwapchainRecord record, in PresentOptions options)
    {
        bool defaultOptions = options == default;
        bool vsync = defaultOptions ? record.Desc.PresentMode == SwapchainPresentMode.Fifo : options.VSync;
        bool tearing = defaultOptions ? record.Desc.AllowTearing && !vsync : options.AllowTearing;
        if (vsync && tearing) throw new ArgumentException("VSync and tearing are mutually exclusive.", nameof(options));
        if (tearing && !record.Desc.AllowTearing)
            throw UnsupportedError("This swapchain was not created with tearing support.");
    }

    private void ValidatePresentState(SwapchainRecord record)
    {
        TextureRecord image = RequireTexture(record.Images[record.AcquiredImage]);
        if (image.States.Any(static state => state != ResourceState.Present))
            throw ValidationError("A swapchain backbuffer must be transitioned to Present before presentation.");
    }

    private PresentResult CompletePresent()
    {
        if (_options.PresentStatus == PresentStatus.DeviceLost)
        {
            DeviceError error = new(DeviceErrorKind.DeviceLost, "The Null profile injected device loss during Present.");
            SetDeviceError(error);
            return new PresentResult(PresentStatus.DeviceLost, error);
        }
        if (_options.PresentStatus == PresentStatus.Occluded)
            return new PresentResult(PresentStatus.Occluded);
        return new PresentResult(PresentStatus.Success);
    }

    public void Resize(SwapchainHandle swapchain, int width, int height)
    {
        EnsureCoordinatorThread();
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        lock (_gate)
        {
            EnsureNotDisposed();
            SwapchainRecord record = RequireSwapchain(swapchain);
            if (record.AcquiredImage >= 0)
                throw ValidationError("A swapchain cannot be resized while an image is acquired.");
            SwapchainDesc resized = record.Desc with { Width = width, Height = height };
            TextureHandle[] replacement = CreateSwapchainImages(resized);
            TextureHandle[] previous = record.Images;
            record.Images = replacement;
            record.Desc = resized;
            record.NextImage = 0;
            ReleaseAndDestroySwapchainImages(previous);
        }
    }

    public void DestroySwapchain(SwapchainHandle swapchain)
    {
        EnsureCoordinatorThread();
        lock (_gate)
        {
            EnsureNotDisposed();
            SwapchainRecord record = RequireSwapchain(swapchain);
            if (record.AcquiredImage >= 0)
                throw ValidationError("A swapchain cannot be destroyed while an image is acquired.");
            ReleaseAndDestroySwapchainImages(record.Images);
            _swapchains.Destroy(swapchain.Domain, swapchain.Slot, swapchain.Generation);
        }
    }

    private TextureHandle[] CreateSwapchainImages(in SwapchainDesc desc)
    {
        TextureDesc textureDesc = new(
            desc.Width,
            desc.Height,
            desc.Format,
            TextureUsage.ColorAttachment | TextureUsage.CopySource | TextureUsage.CopyDestination,
            Name: desc.Name is null ? null : $"{desc.Name} backbuffer");
        ResourceRequirements requirements = GetTextureRequirements(textureDesc);
        TextureHandle[] images = new TextureHandle[desc.BufferCount];
        int created = 0;
        try
        {
            for (; created < images.Length; created++)
            {
                images[created] = CreateSwapchainImage(textureDesc, requirements, nameof(desc));
            }
            return images;
        }
        catch
        {
            ReleaseAndDestroySwapchainImages(images.AsSpan(0, created));
            throw;
        }
    }

    private TextureHandle CreateSwapchainImage(
        in TextureDesc textureDesc,
        in ResourceRequirements requirements,
        string parameter)
    {
        TextureRecord record = CreateTextureRecord(
            textureDesc,
            MemoryType.DeviceLocal,
            new PhysicalAllocationInfo(PhysicalAllocationId.Allocate(_domain), 0, requirements.Size),
            new byte[ToArrayLength(TextureLayout.GetByteSize(textureDesc), parameter)],
            0,
            default);
        Array.Fill(record.States, ResourceState.Present);
        (uint slot, uint generation) = _textures.Allocate(record);
        TextureHandle image = new(_domain, slot, generation);
        _textures.AddChild(image.Domain, image.Slot, image.Generation);
        _statistics = _statistics with { TextureCreates = _statistics.TextureCreates + 1 };
        return image;
    }

    private void ReleaseAndDestroySwapchainImages(ReadOnlySpan<TextureHandle> images)
    {
        foreach (TextureHandle image in images)
        {
            if (!image.IsValid) continue;
            _textures.ReleaseChild(image.Domain, image.Slot, image.Generation);
            _textures.Destroy(image.Domain, image.Slot, image.Generation);
        }
    }

    private SwapchainRecord RequireSwapchain(SwapchainHandle handle) =>
        _swapchains.RequireAlive(handle.Domain, handle.Slot, handle.Generation).Value!;
}
