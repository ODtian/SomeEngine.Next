using SharpGen.Runtime;
using System.Runtime.InteropServices;
using Vortice.Direct3D12;
using Vortice.DXGI;
using DxgiUsage = Vortice.DXGI.Usage;

namespace SomeEngine.Graphics.Direct3D12;

public sealed partial class Device
{
    private const int DxgiStatusOccluded = 0x087A0001;
    private const int DxgiErrorDeviceHung = unchecked((int)0x887A0006);
    private const int DxgiErrorDeviceRemoved = unchecked((int)0x887A0005);
    private const int DxgiErrorDeviceReset = unchecked((int)0x887A0007);
    private readonly HandleTable<NativeSwapchain> _swapchains;

    public SwapchainHandle CreateSwapchain(in SwapchainDesc desc)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        desc.Validate(requireWindowHandle: true);
        ValidateTearingSupport(desc.AllowTearing);
        try
        {
            return CreateSwapchainCore(desc);
        }
        catch (Exception exception)
        {
            RecordFailure("CreateSwapChainForHwnd", exception);
            throw;
        }
    }

    private void ValidateTearingSupport(bool allowTearing)
    {
        if (!allowTearing) return;
        try
        {
            using IDXGIFactory5 factory = _native.Factory.QueryInterface<IDXGIFactory5>();
            if (!factory.PresentAllowTearing)
                throw new NotSupportedException("DXGI does not support tearing on this system.");
        }
        catch (NotSupportedException)
        {
            throw;
        }
        catch (Exception exception)
        {
            throw new NotSupportedException("DXGI tearing capability could not be queried on this system.", exception);
        }
    }

    private SwapchainHandle CreateSwapchainCore(in SwapchainDesc desc)
    {
        IDXGISwapChain3 swapchain = CreateNativeSwapchain(desc);
        try
        {
            List<NativeTexture> backbuffers = CreateBackbuffers(swapchain, desc);
            return RegisterSwapchain(swapchain, desc, backbuffers);
        }
        catch
        {
            swapchain.Dispose();
            throw;
        }
    }

    private IDXGISwapChain3 CreateNativeSwapchain(in SwapchainDesc desc)
    {
        SwapChainDescription1 description = CreateNativeSwapchainDescription(desc);
        using IDXGISwapChain1 swapchain = _native.Factory.CreateSwapChainForHwnd(
            _native.Graphics.Queue,
            desc.WindowHandle,
            description);
        IDXGISwapChain3 result = swapchain.QueryInterface<IDXGISwapChain3>();
        ApplyColorSpace(result, desc.ColorSpace);
        return result;
    }

    private static SwapChainDescription1 CreateNativeSwapchainDescription(in SwapchainDesc desc) => new()
    {
        Width = checked((uint)desc.Width),
        Height = checked((uint)desc.Height),
        Format = Mappings.Format(desc.Format),
        Stereo = false,
        SampleDescription = new SampleDescription(1, 0),
        BufferUsage = DxgiUsage.RenderTargetOutput,
        BufferCount = checked((uint)desc.BufferCount),
        Scaling = Scaling.Stretch,
        SwapEffect = SwapEffect.FlipDiscard,
        AlphaMode = AlphaMode.Ignore,
        Flags = desc.AllowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None,
    };

    private List<NativeTexture> CreateBackbuffers(IDXGISwapChain3 swapchain, in SwapchainDesc desc)
    {
        TextureDesc imageDescription = BackbufferDescription(desc);
        ResourceRequirements requirements = GetTextureRequirements(imageDescription);
        List<NativeTexture> images = [];
        try
        {
            for (uint index = 0; index < desc.BufferCount; index++)
                images.Add(CreateBackbuffer(swapchain, imageDescription, requirements.Size, index));
            return images;
        }
        catch
        {
            DisposeBackbuffers(images);
            throw;
        }
    }

    private NativeTexture CreateBackbuffer(
        IDXGISwapChain3 swapchain,
        in TextureDesc description,
        ulong size,
        uint index)
    {
        ID3D12Resource resource = swapchain.GetBuffer<ID3D12Resource>(index);
        NativeTexture image = new(
            resource,
            description,
            MemoryType.DeviceLocal,
            ResourceStates.Present,
            parent: null,
            new PhysicalAllocationInfo(PhysicalAllocationId.Allocate(_domain), 0, size),
            isSwapchainImage: true);
        ApplyObjectName(image, resource, description.Name is null ? null : $"{description.Name}.{index}");
        return image;
    }

    private SwapchainHandle RegisterSwapchain(
        IDXGISwapChain3 swapchain,
        in SwapchainDesc desc,
        List<NativeTexture> nativeImages)
    {
        TextureHandle[] images = RegisterBackbuffers(nativeImages);
        nativeImages.Clear();
        NativeSwapchain native = new(swapchain, desc, images) { LogicalName = desc.Name };
        swapchain.DebugName = desc.Name ?? string.Empty;
        try
        {
            HandleKey key = _swapchains.Add(native);
            return new SwapchainHandle(_domain, key.Slot, key.Generation);
        }
        catch
        {
            ReleaseBackbuffers(native.Images);
            throw;
        }
    }

    private static void DisposeBackbuffers(IEnumerable<NativeTexture> images)
    {
        foreach (NativeTexture image in images) image.Dispose();
    }

    public SwapchainImage AcquireNextImage(SwapchainHandle swapchain)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeSwapchain native = GetSwapchain(swapchain);
        if (native.AcquiredImage.HasValue)
            throw new InvalidOperationException("The swapchain already has an acquired image that has not been presented.");
        // Vortice exposes IDXGISwapChain3::GetCurrentBackBufferIndex as a property.
        uint index = native.Swapchain.CurrentBackBufferIndex;
        if (index >= native.Images.Length)
            throw new InvalidOperationException("DXGI returned a swapchain image index outside the registered backbuffer range.");
        native.AcquiredImage = index;
        return new SwapchainImage(native.Images[index], index);
    }

}

public sealed partial class Device
{

    public PresentResult Present(SwapchainHandle swapchain, uint imageIndex, in PresentOptions options = default)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeSwapchain native = GetSwapchain(swapchain);
        if (native.AcquiredImage != imageIndex)
            throw new InvalidOperationException("Present must consume the image returned by the current AcquireNextImage call.");
        try
        {
            var settings = ResolvePresentSettings(native.Description, options);
            ThrowIfDeviceRemovedBeforePresent();
            if (IsPresentationOccluded(native)) return CompleteOccludedPresent(native);
            return SubmitPresent(native, settings.VerticalSync, settings.AllowTearing);
        }
        catch (Exception exception)
        {
            return HandlePresentFailure(native, exception);
        }
    }

    private static (bool VerticalSync, bool AllowTearing) ResolvePresentSettings(
        in SwapchainDesc desc,
        in PresentOptions options)
    {
        bool useDescriptorDefault = options == default;
        bool verticalSync = useDescriptorDefault
            ? desc.PresentMode == SwapchainPresentMode.Fifo
            : options.VSync;
        bool allowTearing = !verticalSync && (useDescriptorDefault ? desc.AllowTearing : options.AllowTearing);
        if (allowTearing && !desc.AllowTearing)
            throw new InvalidOperationException("Tearing was not enabled when the swapchain was created.");
        return (verticalSync, allowTearing);
    }

    private void ThrowIfDeviceRemovedBeforePresent()
    {
        int removedReason = _native.Device.DeviceRemovedReason.Code;
        if (IsDeviceLossCode(removedReason))
            throw new COMException("The D3D12 device was removed before presentation.", removedReason);
    }

    private static bool IsPresentationOccluded(NativeSwapchain swapchain)
    {
        // Flip-model Present can keep returning S_OK for hidden/minimized HWNDs, so
        // native window state and DXGI_PRESENT_TEST are both required.
        if (!IsWindowVisible(swapchain.Description.WindowHandle) || IsIconic(swapchain.Description.WindowHandle))
            return true;
        Result visibility = swapchain.Swapchain.Present(0, PresentFlags.Test);
        if (visibility.Code == DxgiStatusOccluded) return true;
        visibility.CheckError();
        return false;
    }

    private static PresentResult CompleteOccludedPresent(NativeSwapchain swapchain)
    {
        swapchain.AcquiredImage = null;
        return new PresentResult(PresentStatus.Occluded);
    }

    private static PresentResult SubmitPresent(NativeSwapchain swapchain, bool verticalSync, bool allowTearing)
    {
        Result result = swapchain.Swapchain.Present(
            verticalSync ? 1u : 0u,
            allowTearing ? PresentFlags.AllowTearing : PresentFlags.None);
        swapchain.AcquiredImage = null;
        swapchain.PresentationPending = true;
        if (result.Code == DxgiStatusOccluded) return new PresentResult(PresentStatus.Occluded);
        result.CheckError();
        return new PresentResult(PresentStatus.Success);
    }

    private PresentResult HandlePresentFailure(NativeSwapchain swapchain, Exception exception)
    {
        int removedReason = _native.Device.DeviceRemovedReason.Code;
        if (IsDeviceLossCode(exception.HResult) || IsDeviceLossCode(removedReason))
        {
            swapchain.AcquiredImage = null;
            DeviceError error = RecordDeviceLost("Present", exception);
            return new PresentResult(PresentStatus.DeviceLost, error);
        }
        RecordFailure("Present", exception);
        throw new InvalidOperationException("DXGI presentation failed without device loss.", exception);
    }

    public void Resize(SwapchainHandle swapchain, int width, int height)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        NativeSwapchain native = GetSwapchain(swapchain);
        if (native.AcquiredImage.HasValue)
            throw new InvalidOperationException("A swapchain cannot be resized while an image is acquired.");

        WaitForPresentation(native);
        ReleaseBackbuffers(native.Images);
        // Once ResizeBuffers is entered the old backbuffer handles are no longer live.
        // Clear them immediately so an exceptional resize cannot leave disposal pointing
        // at stale generations.
        native.Images = [];
        SwapchainDesc resized = native.Description with { Width = width, Height = height };
        try
        {
            ResizeNativeBuffers(native.Swapchain, resized);
        }
        catch (Exception exception)
        {
            RecordResizeFailure(exception);
            throw;
        }
        ReplaceBackbuffers(native, resized);
    }

    private static void ResizeNativeBuffers(IDXGISwapChain3 swapchain, in SwapchainDesc desc)
    {
        swapchain.ResizeBuffers(
            checked((uint)desc.BufferCount),
            checked((uint)desc.Width),
            checked((uint)desc.Height),
            Mappings.Format(desc.Format),
            desc.AllowTearing ? SwapChainFlags.AllowTearing : SwapChainFlags.None).CheckError();
    }

    private void RecordResizeFailure(Exception exception)
    {
        int removedReason = _native.Device.DeviceRemovedReason.Code;
        if (IsDeviceLossCode(exception.HResult) || IsDeviceLossCode(removedReason))
            RecordDeviceLost("ResizeBuffers", exception);
        else
            RecordFailure("ResizeBuffers", exception);
    }

    private void ReplaceBackbuffers(NativeSwapchain swapchain, in SwapchainDesc desc)
    {
        List<NativeTexture> images = CreateBackbuffers(swapchain.Swapchain, desc);
        try
        {
            swapchain.Images = RegisterBackbuffers(images);
            images.Clear();
            swapchain.Description = desc;
        }
        finally
        {
            DisposeBackbuffers(images);
        }
    }

    public void DestroySwapchain(SwapchainHandle swapchain)
    {
        EnsureCoordinator();
        ThrowIfUnavailable();
        NativeSwapchain native = GetSwapchain(swapchain);
        if (native.AcquiredImage.HasValue)
            throw new InvalidOperationException("Present or abandon the acquired image before destroying its swapchain.");
        WaitForPresentation(native);
        ReleaseBackbuffers(native.Images);
        _ = _swapchains.Remove(swapchain.Domain, swapchain.Slot, swapchain.Generation, "swapchain");
        native.Dispose();
    }

    private NativeSwapchain GetSwapchain(SwapchainHandle swapchain) =>
        _swapchains.Get(swapchain.Domain, swapchain.Slot, swapchain.Generation, "swapchain");

    private TextureHandle[] RegisterBackbuffers(IReadOnlyList<NativeTexture> nativeImages)
    {
        TextureHandle[] handles = new TextureHandle[nativeImages.Count];
        int registered = 0;
        try
        {
            for (; registered < nativeImages.Count; registered++)
            {
                HandleKey key = _textures.Add(nativeImages[registered]);
                handles[registered] = new TextureHandle(_domain, key.Slot, key.Generation);
            }
            return handles;
        }
        catch
        {
            for (int index = 0; index < registered; index++)
            {
                TextureHandle handle = handles[index];
                _ = _textures.Remove(handle.Domain, handle.Slot, handle.Generation, "swapchain image");
            }
            throw;
        }
    }

    private void ReleaseBackbuffers(IReadOnlyList<TextureHandle> handles)
    {
        NativeTexture[] images = new NativeTexture[handles.Count];
        RetirementPoint[] points = new RetirementPoint[handles.Count];
        for (int index = 0; index < handles.Count; index++)
        {
            NativeTexture image = GetTexture(handles[index]);
            if (image.ViewCount != 0)
                throw new InvalidOperationException("Destroy every backbuffer view before resizing or destroying its swapchain.");
            images[index] = image;
            points[index] = BeginRetirement(image);
        }

        foreach (RetirementPoint point in points) WaitForRetirement(point);
        for (int index = 0; index < handles.Count; index++)
        {
            TextureHandle handle = handles[index];
            _ = _textures.Remove(handle.Domain, handle.Slot, handle.Generation, "swapchain image");
            images[index].Dispose();
        }
    }

    private void WaitForRetirement(in RetirementPoint point)
    {
        if (point.Graphics != 0 && !Wait(new GpuCompletion(_domain, QueueType.Graphics, point.Graphics), _options.ShutdownTimeout))
            throw new TimeoutException("Timed out waiting for graphics work that references a swapchain image.");
        if (point.Compute != 0 && !Wait(new GpuCompletion(_domain, QueueType.Compute, point.Compute), _options.ShutdownTimeout))
            throw new TimeoutException("Timed out waiting for compute work that references a swapchain image.");
        if (point.Copy != 0 && !Wait(new GpuCompletion(_domain, QueueType.Copy, point.Copy), _options.ShutdownTimeout))
            throw new TimeoutException("Timed out waiting for copy work that references a swapchain image.");
    }

    private void WaitForPresentation(NativeSwapchain swapchain)
    {
        if (!swapchain.PresentationPending) return;
        NativeQueue queue = _native.Graphics;
        ulong value;
        lock (queue.SubmissionGate)
        {
            value = checked(queue.SubmittedValue + 1);
            queue.Queue.Signal(queue.Fence, value).CheckError();
            queue.SubmittedValue = value;
        }

        if (!Wait(new GpuCompletion(_domain, QueueType.Graphics, value), _options.ShutdownTimeout))
            throw new TimeoutException("Timed out waiting for queued presentation work before releasing swapchain images.");
        swapchain.PresentationPending = false;
    }

    private static TextureDesc BackbufferDescription(in SwapchainDesc desc) => new(
        desc.Width,
        desc.Height,
        desc.Format,
        TextureUsage.ColorAttachment | TextureUsage.CopySource | TextureUsage.CopyDestination,
        Name: desc.Name is null ? "swapchain.backbuffer" : $"{desc.Name}.backbuffer");

    private static void ApplyColorSpace(IDXGISwapChain3 swapchain, SwapchainColorSpace colorSpace)
    {
        ColorSpaceType native = colorSpace switch
        {
            SwapchainColorSpace.Srgb => ColorSpaceType.RgbFullG22NoneP709,
            SwapchainColorSpace.Hdr10 => ColorSpaceType.RgbFullG2084NoneP2020,
            _ => throw new ArgumentOutOfRangeException(nameof(colorSpace)),
        };
        SwapChainColorSpaceSupportFlags support = swapchain.CheckColorSpaceSupport(native);
        if ((support & SwapChainColorSpaceSupportFlags.Present) == 0)
            throw new NotSupportedException($"The DXGI swapchain does not support {colorSpace} presentation.");
        swapchain.SetColorSpace1(native);
    }

    private static bool IsDeviceLossCode(int code) =>
        code is DxgiErrorDeviceHung or DxgiErrorDeviceRemoved or DxgiErrorDeviceReset;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(nint window);

    partial void DisposePresentation()
    {
        foreach (NativeSwapchain swapchain in _swapchains.Drain())
        {
            // Device disposal must honor the same presentation lifetime as explicit
            // swapchain destruction. Releasing a healthy swapchain while DXGI still
            // owns a queued Present can turn the COM Release into an SEHException.
            // A removed device cannot make forward progress, so only skip the drain
            // after device loss has been observed.
            if (!_lost && !IsDeviceLossCode(_native.Device.DeviceRemovedReason.Code))
                WaitForPresentation(swapchain);
            ReleaseBackbuffers(swapchain.Images);
            swapchain.Dispose();
        }
    }
}

internal sealed class NativeSwapchain : IDisposable
{
    public NativeSwapchain(IDXGISwapChain3 swapchain, SwapchainDesc description, TextureHandle[] images)
    {
        Swapchain = swapchain;
        Description = description;
        Images = images;
    }

    public IDXGISwapChain3 Swapchain { get; }
    public SwapchainDesc Description { get; set; }
    public TextureHandle[] Images { get; set; }
    public uint? AcquiredImage { get; set; }
    public bool PresentationPending { get; set; }
    public string? LogicalName { get; set; }
    public void Dispose() => Swapchain.Dispose();
}
