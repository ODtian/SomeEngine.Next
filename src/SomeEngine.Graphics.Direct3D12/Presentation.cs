using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using DxgiColorSpace = Silk.NET.DXGI.ColorSpaceType;
using DxgiFeature = Silk.NET.DXGI.Feature;
using DxgiFormat = Silk.NET.DXGI.Format;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    private const uint DxgiUsageShaderInput = 0x10;
    private const uint DxgiUsageRenderTargetOutput = 0x20;
    private const uint DxgiUsageBackBuffer = 0x40;
    private const uint DxgiUsageUnorderedAccess = 0x400;
    private const uint DxgiMakeWindowAssociationNoAltEnter = 0x2;
    private const uint DxgiPresentAllowTearing = 0x2;
    private const int DxgiStatusOccluded = 0x087A0001;
    private const int DxgiStatusModeChanged = 0x087A0007;
    private const int DxgiStatusModeChangeInProgress = 0x087A0008;
    private const int DxgiErrorNotCurrentlyAvailable = unchecked((int)0x887A0022);
    private const int DxgiErrorAccessLost = unchecked((int)0x887A0026);
    private const int DxgiErrorSessionDisconnected = unchecked((int)0x887A0028);
    private const int DxgiErrorRestrictToOutputStale = unchecked((int)0x887A0029);

    public Swapchain CreateSwapchain(Device device, in SwapchainDesc desc)
    {
        if (desc.Surface is null)
            throw new ArgumentNullException(nameof(desc), "SwapchainDesc.Surface is required.");
        D3D12Device nativeDevice = NativeCast.Device(device);
        D3D12Surface surface = NativeCast.Surface(desc.Surface);
        nativeDevice.ThrowIfUnavailable();
        surface.ThrowIfDisposed();

        ValidateSwapchainDescription(nativeDevice, desc);
        bool tearingSupported = QueryTearingSupport();
        EnsureSupported(
            desc.Config,
            CreateSwapchainSupport(native: null, tearingSupported),
            nameof(desc));

        D3D12Queue queue = nativeDevice.GetQueue(QueueType.Graphics, 0);
        uint flags = (uint)SwapChainFlag.FrameLatencyWaitableObject;
        if (tearingSupported)
            flags |= (uint)SwapChainFlag.AllowTearing;

        SwapChainDesc1 nativeDescription = new(
            desc.Config.Width,
            desc.Config.Height,
            ToSwapchainFormat(desc.Config.Format),
            stereo: false,
            new SampleDesc(1, 0),
            ToSwapchainUsage(desc.ImageUsages),
            desc.ImageCount,
            Scaling.Stretch,
            SwapEffect.FlipDiscard,
            AlphaMode.Ignore,
            flags);

        IDXGISwapChain1* initial = null;
        IDXGISwapChain4* native = null;
        try
        {
            IDXGIFactory6* factory = EnsureFactory();
            NativeCall.ThrowIfFailed(
                factory->CreateSwapChainForHwnd(
                    (IUnknown*)queue.Native,
                    surface.WindowHandle,
                    &nativeDescription,
                    null,
                    null,
                    &initial),
                "IDXGIFactory6::CreateSwapChainForHwnd");

            Guid iid = IDXGISwapChain4.Guid;
            NativeCall.ThrowIfFailed(
                initial->QueryInterface(&iid, (void**)&native),
                "IDXGISwapChain1::QueryInterface(IDXGISwapChain4)");

            NativeCall.ThrowIfFailed(
                factory->MakeWindowAssociation(
                    surface.WindowHandle,
                    DxgiMakeWindowAssociationNoAltEnter),
                "IDXGIFactory::MakeWindowAssociation");
            SwapchainSupport[] support = CreateSwapchainSupport(native, tearingSupported);
            EnsureSupported(desc.Config, support, nameof(desc));
            ConfigureNativeSwapchain(native, desc.Config);

            SwapChainDesc1 resolved = default;
            NativeCall.ThrowIfFailed(
                native->GetDesc1(&resolved),
                "IDXGISwapChain1::GetDesc1");
            SwapchainConfig resolvedConfig = desc.Config with
            {
                Width = resolved.Width,
                Height = resolved.Height,
            };
            if (resolvedConfig.Width == 0 || resolvedConfig.Height == 0)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "DXGI created a swapchain with an empty drawable extent.");
            }

            SwapchainInfo info = new(resolvedConfig, desc.ImageCount, 1, support);
            D3D12Swapchain result = new(
                nativeDevice,
                surface,
                native,
                info,
                desc.ImageUsages,
                desc.Label);
            native = null;
            try
            {
                result.InitializeBackBuffers();
                nativeDevice.RegisterChild(result);
                try
                {
                    surface.RegisterSwapchain(result);
                }
                catch
                {
                    result.Dispose();
                    throw;
                }
                return result;
            }
            catch
            {
                result.Dispose();
                throw;
            }
        }
        catch (Exception exception) when (IsDeviceRemoval(exception))
        {
            GraphicsException loss = CreateDeviceLoss(
                nativeDevice,
                exception is GraphicsException graphics ? graphics.NativeCode : null,
                "DXGI swapchain creation detected device removal.",
                exception);
            throw loss;
        }
        finally
        {
            if (initial is not null)
                _ = initial->Release();
            if (native is not null)
                _ = native->Release();
        }
    }

    public SwapchainAcquireStatus Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image)
    {
        D3D12Swapchain native = NativeCast.Swapchain(swapchain);
        return native.Acquire(options, out image);
    }

    public PresentStatus Present(Queue queue, in SwapchainImage image)
    {
        D3D12Queue nativeQueue = NativeCast.Queue(queue);
        if (nativeQueue.Type != QueueType.Graphics)
            throw new ArgumentException("Present requires a Graphics Queue.", nameof(queue));
        if (image.Lease is not D3D12SwapchainImageLease lease)
            throw new ArgumentException("The image does not belong to this D3D12 backend.", nameof(image));
        if (!ReferenceEquals(nativeQueue.Device, lease.Swapchain.Device))
            throw new ArgumentException("The Queue and SwapchainImage belong to different Devices.", nameof(image));

        D3D12Swapchain nativeSwapchain = lease.NativeSwapchain;
        nativeQueue.Device.ThrowIfUnavailable();
        nativeSwapchain.ValidatePresent(nativeQueue, lease, image.Sequence);

        lock (nativeQueue.Gate)
        {
            nativeQueue.Device.ThrowIfUnavailable();
            return nativeSwapchain.PresentUnderQueueGate(nativeQueue, lease, image.Sequence);
        }
    }

    public ReconfigureStatus Reconfigure(Swapchain swapchain, in SwapchainConfig config) =>
        NativeCast.Swapchain(swapchain).Reconfigure(config);

    private bool QueryTearingSupport()
    {
        uint supported = 0;
        int result = EnsureFactory()->CheckFeatureSupport(
            DxgiFeature.PresentAllowTearing,
            &supported,
            sizeof(uint));
        return result >= 0 && supported != 0;
    }

    private static void ValidateSwapchainDescription(
        D3D12Device device,
        in SwapchainDesc description)
    {
        if (description.Surface is null)
            throw new ArgumentNullException(nameof(description), "SwapchainDesc.Surface is required.");
        if (description.ImageCount is < 2 or > 16)
            throw new ArgumentOutOfRangeException(nameof(description), "ImageCount must be between 2 and 16.");

        TextureUsages unsupported = description.ImageUsages &
            (TextureUsages.DepthStencilAttachment |
             TextureUsages.ShadingRate |
             TextureUsages.SamplerFeedback |
             TextureUsages.Shareable);
        if (unsupported != TextureUsages.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(description),
                $"Swapchain ImageUsages contains unsupported usages: {unsupported}.");
        }
        ValidateSwapchainConfig(device, description.Config, nameof(description));
    }

    private static void ValidateSwapchainConfig(
        D3D12Device device,
        in SwapchainConfig config,
        string parameterName)
    {
        if (config.MaximumFrameLatency is < 1 or > 16)
            throw new ArgumentOutOfRangeException(parameterName, "MaximumFrameLatency must be between 1 and 16.");
        if (config.Width > device.Capabilities.Limits.MaximumTextureDimension2D ||
            config.Height > device.Capabilities.Limits.MaximumTextureDimension2D)
        {
            throw new ArgumentOutOfRangeException(parameterName, "The swapchain extent exceeds the Device limit.");
        }
        if (!Enum.IsDefined(config.Format) ||
            !Enum.IsDefined(config.ColorSpace) ||
            !Enum.IsDefined(config.PresentType))
        {
            throw new ArgumentOutOfRangeException(parameterName, "SwapchainConfig contains an unknown enum value.");
        }
        if (config.PresentType == PresentType.Fifo && config.AllowTearing)
            throw new ArgumentException("FIFO presentation cannot allow tearing.", parameterName);
        if (config.PresentType == PresentType.Mailbox && config.AllowTearing)
            throw new ArgumentException("Mailbox presentation cannot allow tearing.", parameterName);
        if (config.PresentType == PresentType.Immediate && !config.AllowTearing)
            throw new ArgumentException("Immediate presentation requires tearing.", parameterName);
    }

    private static SwapchainSupport[] CreateSwapchainSupport(
        IDXGISwapChain4* native,
        bool tearingSupported)
    {
        (Format Format, ColorSpace ColorSpace)[] formats =
        [
            (Format.R8G8B8A8UNorm, ColorSpace.Srgb),
            (Format.R8G8B8A8UNormSrgb, ColorSpace.Srgb),
            (Format.B8G8R8A8UNorm, ColorSpace.Srgb),
            (Format.B8G8R8A8UNormSrgb, ColorSpace.Srgb),
            (Format.R16G16B16A16Float, ColorSpace.ScRgb),
            (Format.R10G10B10A2UNorm, ColorSpace.Hdr10),
        ];
        int modeCount = tearingSupported ? 3 : 2;
        List<SwapchainSupport> result = new(formats.Length * modeCount);
        foreach ((Format format, ColorSpace colorSpace) in formats)
        {
            if (native is not null)
            {
                uint colorSupport = 0;
                NativeCall.ThrowIfFailed(
                    native->CheckColorSpaceSupport(ToNativeColorSpace(colorSpace), &colorSupport),
                    "IDXGISwapChain3::CheckColorSpaceSupport");
                if ((colorSupport & (uint)SwapChainColorSpaceSupportFlag.Present) == 0)
                    continue;
            }
            result.Add(new SwapchainSupport(format, colorSpace, PresentType.Fifo, false));
            result.Add(new SwapchainSupport(format, colorSpace, PresentType.Mailbox, false));
            if (tearingSupported)
                result.Add(new SwapchainSupport(format, colorSpace, PresentType.Immediate, true));
        }
        return [.. result];
    }

    private static void EnsureSupported(
        in SwapchainConfig config,
        ReadOnlySpan<SwapchainSupport> support,
        string parameterName)
    {
        foreach (ref readonly SwapchainSupport candidate in support)
        {
            if (candidate.Format == config.Format &&
                candidate.ColorSpace == config.ColorSpace &&
                candidate.PresentType == config.PresentType &&
                (!config.AllowTearing || candidate.TearingSupported))
            {
                return;
            }
        }
        throw new ArgumentException(
            "The requested format, color space, presentation type, and tearing policy are unsupported.",
            parameterName);
    }

    private static void ConfigureNativeSwapchain(
        IDXGISwapChain4* native,
        in SwapchainConfig config)
    {
        NativeCall.ThrowIfFailed(
            native->SetMaximumFrameLatency(config.MaximumFrameLatency),
            "IDXGISwapChain2::SetMaximumFrameLatency");

        DxgiColorSpace colorSpace = ToNativeColorSpace(config.ColorSpace);
        uint colorSupport = 0;
        NativeCall.ThrowIfFailed(
            native->CheckColorSpaceSupport(colorSpace, &colorSupport),
            "IDXGISwapChain3::CheckColorSpaceSupport");
        if ((colorSupport & (uint)SwapChainColorSpaceSupportFlag.Present) == 0)
        {
            throw new ArgumentException(
                "The DXGI presentation target cannot present the requested color space.",
                nameof(config));
        }
        NativeCall.ThrowIfFailed(
            native->SetColorSpace1(colorSpace),
            "IDXGISwapChain3::SetColorSpace1");
    }

    private static uint ToSwapchainUsage(TextureUsages usages)
    {
        uint result = DxgiUsageBackBuffer;
        if ((usages & TextureUsages.ColorAttachment) != 0)
            result |= DxgiUsageRenderTargetOutput;
        if ((usages & TextureUsages.Sampled) != 0)
            result |= DxgiUsageShaderInput;
        if ((usages & TextureUsages.Storage) != 0)
            result |= DxgiUsageUnorderedAccess;
        return result;
    }

    private static DxgiFormat ToSwapchainFormat(Format format) => format switch
    {
        Format.R8G8B8A8UNorm or Format.R8G8B8A8UNormSrgb =>
            DxgiFormat.FormatR8G8B8A8Unorm,
        Format.B8G8R8A8UNorm or Format.B8G8R8A8UNormSrgb =>
            DxgiFormat.FormatB8G8R8A8Unorm,
        Format.R10G10B10A2UNorm => DxgiFormat.FormatR10G10B10A2Unorm,
        Format.R16G16B16A16Float => DxgiFormat.FormatR16G16B16A16Float,
        _ => throw new ArgumentOutOfRangeException(nameof(format), "The Format is not a DXGI flip-model format."),
    };

    private static DxgiColorSpace ToNativeColorSpace(ColorSpace colorSpace) => colorSpace switch
    {
        ColorSpace.Srgb => DxgiColorSpace.RgbFullG22NoneP709,
        ColorSpace.ScRgb => DxgiColorSpace.RgbFullG10NoneP709,
        ColorSpace.Hdr10 => DxgiColorSpace.RgbFullG2084NoneP2020,
        _ => throw new ArgumentOutOfRangeException(nameof(colorSpace)),
    };

    private static uint ToPresentInterval(PresentType type) => type switch
    {
        PresentType.Immediate => 0,
        PresentType.Mailbox => 0,
        PresentType.Fifo => 1,
        _ => throw new ArgumentOutOfRangeException(nameof(type)),
    };

    private static uint ToPresentFlags(in SwapchainConfig config) =>
        config.AllowTearing ? DxgiPresentAllowTearing : 0;

    private sealed class D3D12Swapchain : Swapchain
    {
        private readonly D3D12Device _device;
        private readonly D3D12Surface _surface;
        private readonly object _gate = new();
        private readonly object _acquireGate = new();
        private IDXGISwapChain4* _native;
        private nint _frameLatencyHandle;
        private D3D12SwapchainImageLease[] _images = [];
        private ulong _nextSequence = 1;
        private bool _outOfDate;
        private bool _deviceLost;
        private int _released;

        internal D3D12Swapchain(
            D3D12Device device,
            D3D12Surface surface,
            IDXGISwapChain4* native,
            SwapchainInfo info,
            TextureUsages imageUsages,
            string? label)
            : base(device, surface, info, imageUsages, label)
        {
            _device = device;
            _surface = surface;
            _native = native;
            _frameLatencyHandle = (nint)native->GetFrameLatencyWaitableObject();
            if (_frameLatencyHandle == 0)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "IDXGISwapChain2 returned no frame-latency waitable object.");
            }
        }

        internal void InitializeBackBuffers()
        {
            lock (_gate)
                _images = CreateBackBuffers(Info.Config);
        }

        internal SwapchainAcquireStatus Acquire(
            in SwapchainAcquireOptions options,
            out SwapchainImage image)
        {
            ValidateTimeout(options.Timeout, nameof(options));
            image = default;
            lock (_acquireGate)
            {
                lock (_gate)
                {
                    ThrowIfOperational();
                    if (_outOfDate)
                        return SwapchainAcquireStatus.OutOfDate;
                }
                long deadline = CreateDeadline(options.Timeout);
                uint wait = SilkMarshal.WaitWindowsObjects(
                    _frameLatencyHandle,
                    RemainingMilliseconds(options.Timeout, deadline));
                if (wait == 0x102)
                    return SwapchainAcquireStatus.Timeout;
                if (wait != 0)
                {
                    throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        "Waiting for the DXGI frame-latency object failed.",
                        Marshal.GetHRForLastWin32Error());
                }

                lock (_gate)
                {
                    ThrowIfOperational();
                    if (_outOfDate)
                        return SwapchainAcquireStatus.OutOfDate;

                    uint imageIndex = _native->GetCurrentBackBufferIndex();
                    if (imageIndex >= _images.Length)
                    {
                        _outOfDate = true;
                        return SwapchainAcquireStatus.OutOfDate;
                    }

                    D3D12SwapchainImageLease lease = _images[imageIndex];
                    while (lease.IsOutstanding)
                    {
                        int milliseconds = RemainingMonitorMilliseconds(options.Timeout, deadline);
                        if (milliseconds == 0 || !Monitor.Wait(_gate, milliseconds))
                            return SwapchainAcquireStatus.Timeout;
                        ThrowIfOperational();
                        if (_outOfDate)
                            return SwapchainAcquireStatus.OutOfDate;
                        imageIndex = _native->GetCurrentBackBufferIndex();
                        if (imageIndex >= _images.Length)
                        {
                            _outOfDate = true;
                            return SwapchainAcquireStatus.OutOfDate;
                        }
                        lease = _images[imageIndex];
                    }

                    if (_nextSequence == ulong.MaxValue)
                        throw new InvalidOperationException("The Swapchain acquisition sequence domain is exhausted.");
                    ulong sequence = _nextSequence++;
                    TextureLayout initialLayout = options.PreserveContents && lease.HasPresented
                        ? TextureLayout.Present
                        : TextureLayout.Undefined;
                    lease.BeginAcquire(
                        sequence,
                        Info.Generation,
                        PipelineSync.None,
                        ResourceAccess.NoAccess,
                        initialLayout);
                    image = new SwapchainImage(lease, sequence);
                    return SwapchainAcquireStatus.Success;
                }
            }
        }

        internal void ValidatePresent(
            D3D12Queue queue,
            D3D12SwapchainImageLease lease,
            ulong sequence)
        {
            lock (_gate)
            {
                ThrowIfOperational();
                if (_outOfDate)
                    throw new InvalidOperationException("The Swapchain is OutOfDate and must be reconfigured.");
                lease.ValidatePresent(queue, sequence);
            }
        }

        internal void ValidateSubmission(
            D3D12Queue queue,
            D3D12SwapchainImageLease lease,
            ulong sequence,
            bool presentReady)
        {
            lock (_gate)
            {
                ThrowIfOperational();
                if (_outOfDate)
                    throw new InvalidOperationException("The Swapchain is OutOfDate and must be reconfigured.");
                if (queue.Type != QueueType.Graphics || !ReferenceEquals(queue.Device, Device))
                {
                    throw new ArgumentException(
                        "A SwapchainImage can be submitted only to its Device's Graphics Queue.",
                        nameof(queue));
                }
                lease.ValidateSubmission(sequence, presentReady);
            }
        }

        internal PresentStatus PresentUnderQueueGate(
            D3D12Queue queue,
            D3D12SwapchainImageLease lease,
            ulong sequence)
        {
            lock (_gate)
            {
                ThrowIfOperational();
                if (_outOfDate)
                    throw new InvalidOperationException("The Swapchain is OutOfDate and must be reconfigured.");
                lease.ValidatePresent(queue, sequence);
                if (!lease.TryBeginPresent(sequence))
                    throw new InvalidOperationException("The SwapchainImage has no Present right.");

                int result = _native->Present(
                    ToPresentInterval(Info.Config.PresentType),
                    ToPresentFlags(Info.Config));
                PresentStatus status;
                switch (result)
                {
                    case 0:
                        status = PresentStatus.Success;
                        break;
                    case DxgiStatusOccluded:
                        status = PresentStatus.Occluded;
                        break;
                    case DxgiStatusModeChanged:
                    case DxgiStatusModeChangeInProgress:
                        status = PresentStatus.Suboptimal;
                        break;
                    case DxgiErrorNotCurrentlyAvailable:
                    case DxgiErrorAccessLost:
                    case DxgiErrorSessionDisconnected:
                    case DxgiErrorRestrictToOutputStale:
                        _outOfDate = true;
                        status = PresentStatus.OutOfDate;
                        break;
                    default:
                        if (IsDeviceRemovalCode(result))
                        {
                            throw CreateDeviceLoss(
                                _device,
                                result,
                                "IDXGISwapChain::Present detected device removal.");
                        }
                        if (result < 0)
                        {
                            _outOfDate = true;
                            lease.CompletePresent(contentsPreserved: false);
                            Monitor.PulseAll(_gate);
                            throw new GraphicsException(
                                GraphicsError.NativeFailure,
                                "IDXGISwapChain::Present failed.",
                                result);
                        }
                        status = PresentStatus.Suboptimal;
                        break;
                }

                lease.CompletePresent(
                    status is PresentStatus.Success or PresentStatus.Suboptimal);
                Monitor.PulseAll(_gate);
                return status;
            }
        }

        internal ReconfigureStatus Reconfigure(in SwapchainConfig config)
        {
            ValidateSwapchainConfig(_device, config, nameof(config));
            if (!IsSupported(config))
                return ReconfigureStatus.Unsupported;

            lock (_gate)
            {
                ThrowIfOperational();
                foreach (D3D12SwapchainImageLease image in _images)
                {
                    if (image.IsOutstanding)
                        return ReconfigureStatus.Busy;
                }
                if (Info.Generation == ulong.MaxValue)
                    throw new InvalidOperationException("The Swapchain generation domain is exhausted.");

                DrainBackBufferWork();
                InvalidateAndReleaseBackBuffers(deviceLost: false);

                uint flags = (uint)SwapChainFlag.FrameLatencyWaitableObject;
                if (SupportsTearing())
                    flags |= (uint)SwapChainFlag.AllowTearing;
                int resizeResult = _native->ResizeBuffers(
                    Info.ImageCount,
                    config.Width,
                    config.Height,
                    ToSwapchainFormat(config.Format),
                    flags);
                if (resizeResult < 0)
                {
                    _outOfDate = true;
                    if (IsDeviceRemovalCode(resizeResult))
                    {
                        throw CreateDeviceLoss(
                            _device,
                            resizeResult,
                            "IDXGISwapChain::ResizeBuffers detected device removal.");
                    }
                    throw new GraphicsException(
                        GraphicsError.NativeFailure,
                        "IDXGISwapChain::ResizeBuffers failed after crossing the reconfigure commit boundary.",
                        resizeResult);
                }

                try
                {
                    ConfigureNativeSwapchain(_native, config);
                    SwapChainDesc1 resolved = default;
                    NativeCall.ThrowIfFailed(
                        _native->GetDesc1(&resolved),
                        "IDXGISwapChain1::GetDesc1");
                    if (resolved.Width == 0 || resolved.Height == 0)
                    {
                        throw new GraphicsException(
                            GraphicsError.NativeFailure,
                            "DXGI resolved the reconfigured swapchain to an empty drawable extent.");
                    }

                    SwapchainConfig resolvedConfig = config with
                    {
                        Width = resolved.Width,
                        Height = resolved.Height,
                    };
                    D3D12SwapchainImageLease[] rebuilt = CreateBackBuffers(resolvedConfig);
                    Info.Config = resolvedConfig;
                    Info.Generation = checked(Info.Generation + 1);
                    _images = rebuilt;
                    _outOfDate = false;
                    Monitor.PulseAll(_gate);
                    return ReconfigureStatus.Success;
                }
                catch (Exception exception)
                {
                    _outOfDate = true;
                    if (IsDeviceRemoval(exception))
                    {
                        throw CreateDeviceLoss(
                            _device,
                            exception is GraphicsException graphics ? graphics.NativeCode : null,
                            "DXGI swapchain reconfiguration detected device removal.",
                            exception);
                    }
                    throw;
                }
            }
        }

        internal void MarkDeviceLost()
        {
            lock (_gate)
            {
                if (_deviceLost)
                    return;
                _deviceLost = true;
                _outOfDate = true;
                foreach (D3D12SwapchainImageLease image in _images)
                    image.Invalidate(deviceLost: true);
                Monitor.PulseAll(_gate);
            }
        }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            lock (_gate)
            {
                InvalidateAndReleaseBackBuffers(deviceLost: _device.Status == DeviceStatus.Lost);
                IDXGISwapChain4* native = _native;
                _native = null;
                if (native is not null)
                    _ = native->Release();
                nint handle = _frameLatencyHandle;
                _frameLatencyHandle = 0;
                if (handle != 0)
                    _ = SilkMarshal.CloseWindowsHandle(handle);
                Monitor.PulseAll(_gate);
            }
            _surface.UnregisterSwapchain(this);
            _device.UnregisterChild(this);
        }

        private D3D12SwapchainImageLease[] CreateBackBuffers(
            in SwapchainConfig config)
        {
            D3D12SwapchainImageLease[] result = new D3D12SwapchainImageLease[Info.ImageCount];
            int created = 0;
            try
            {
                for (uint index = 0; index < Info.ImageCount; index++)
                {
                    NativeResource* resource = null;
                    D3D12Texture? texture = null;
                    try
                    {
                        Guid iid = NativeResource.Guid;
                        NativeCall.ThrowIfFailed(
                            _native->GetBuffer(index, &iid, (void**)&resource),
                            "IDXGISwapChain::GetBuffer");

                        ResourceDesc nativeDescription = resource->GetDesc();
                        ResourceAllocationInfo allocation = _device.Native->GetResourceAllocationInfo(
                            _device.EnabledNodeMask,
                            1,
                            &nativeDescription);
                        EnsureAllocationInfo(allocation, "Swapchain Texture");
                        TextureInfo info = new(
                            TextureDimension.Texture2D,
                            config.Width,
                            config.Height,
                            1,
                            1,
                            1,
                            1,
                            config.Format,
                            ImageUsages,
                            MemoryType.DeviceLocal,
                            ReadOnlySpan<Format>.Empty,
                            0,
                            allocation.SizeInBytes);
                        texture = new D3D12Texture(
                            _device,
                            heap: null,
                            resource,
                            info,
                            Label is null ? null : $"{Label}.Image{index}",
                            PipelineSync.None,
                            ResourceAccess.NoAccess,
                            TextureLayout.Present);
                        resource = null;
                        D3D12SwapchainImageLease lease = new(this, index, texture);
                        texture.SwapchainLease = lease;
                        _device.RegisterChild(texture);
                        result[index] = lease;
                        created++;
                    }
                    catch
                    {
                        if (texture is null)
                        {
                            if (resource is not null)
                                _ = resource->Release();
                        }
                        else
                        {
                            texture.Dispose();
                        }
                        throw;
                    }
                }
                return result;
            }
            catch
            {
                for (int index = 0; index < created; index++)
                {
                    result[index].Invalidate(deviceLost: false);
                    result[index].Texture.DisposeFromParent();
                }
                throw;
            }
        }

        private void DrainBackBufferWork()
        {
            foreach (D3D12SwapchainImageLease image in _images)
            {
                if (!image.TryGetSubmissionCompletion(out D3D12Queue? queue, out ulong value))
                    continue;
                D3D12Queue completedQueue = queue!;
                QueueCompletion completion = new(completedQueue, value);
                WaitStatus wait = _device.Backend.WaitCpu(completion, Timeout.InfiniteTimeSpan);
                if (wait != WaitStatus.Completed)
                    throw new InvalidOperationException("An infinite Queue wait unexpectedly timed out.");
                completedQueue.CollectCompleted();
            }
        }

        private void InvalidateAndReleaseBackBuffers(bool deviceLost)
        {
            D3D12SwapchainImageLease[] images = _images;
            _images = [];
            foreach (D3D12SwapchainImageLease image in images)
            {
                image.Invalidate(deviceLost);
                image.Texture.DisposeFromParent();
            }
        }

        private bool IsSupported(in SwapchainConfig config)
        {
            foreach (ref readonly SwapchainSupport support in Info.Support)
            {
                if (support.Format == config.Format &&
                    support.ColorSpace == config.ColorSpace &&
                    support.PresentType == config.PresentType &&
                    (!config.AllowTearing || support.TearingSupported))
                {
                    return true;
                }
            }
            return false;
        }

        private bool SupportsTearing()
        {
            foreach (ref readonly SwapchainSupport support in Info.Support)
            {
                if (support.TearingSupported)
                    return true;
            }
            return false;
        }

        private void ThrowIfOperational()
        {
            ThrowIfDisposed();
            _device.ThrowIfUnavailable();
            if (_deviceLost)
            {
                throw new GraphicsException(
                    GraphicsError.DeviceLost,
                    "The D3D12 Swapchain is device-lost.");
            }
        }

        private static void ValidateTimeout(TimeSpan timeout, string parameterName)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
                return;
            if (timeout < TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
                throw new ArgumentOutOfRangeException(parameterName, "Timeout must be nonnegative, infinite, or at most Int32.MaxValue milliseconds.");
        }

        private static long CreateDeadline(TimeSpan timeout) =>
            timeout == Timeout.InfiniteTimeSpan
                ? long.MaxValue
                : checked(Stopwatch.GetTimestamp() +
                    (long)Math.Ceiling(timeout.TotalSeconds * Stopwatch.Frequency));

        private static uint RemainingMilliseconds(TimeSpan timeout, long deadline)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
                return uint.MaxValue;
            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                return 0;
            double milliseconds = Math.Ceiling(
                remainingTicks * 1000d / Stopwatch.Frequency);
            return checked((uint)Math.Clamp(milliseconds, 0, int.MaxValue));
        }

        private static int RemainingMonitorMilliseconds(TimeSpan timeout, long deadline)
        {
            if (timeout == Timeout.InfiniteTimeSpan)
                return Timeout.Infinite;
            return checked((int)RemainingMilliseconds(timeout, deadline));
        }
    }

    private sealed class D3D12SwapchainImageLease : SwapchainImageLease
    {
        private readonly object _gate = new();
        private ulong _sequence;
        private D3D12Queue? _submittedQueue;
        private ulong _completion;
        private bool _presentReady;
        private bool _hasPresented;
        private int _outstanding;

        internal D3D12SwapchainImageLease(
            D3D12Swapchain swapchain,
            uint imageIndex,
            D3D12Texture texture)
            : base(swapchain)
        {
            NativeSwapchain = swapchain;
            ImageIndex = imageIndex;
            Texture = texture;
        }

        internal D3D12Swapchain NativeSwapchain { get; }
        internal uint ImageIndex { get; }
        internal D3D12Texture Texture { get; }
        internal bool IsOutstanding => Volatile.Read(ref _outstanding) != 0;
        internal bool HasPresented
        {
            get
            {
                lock (_gate)
                    return _hasPresented;
            }
        }

        internal void BeginAcquire(
            ulong sequence,
            ulong generation,
            PipelineSync initialSync,
            ResourceAccess initialAccess,
            TextureLayout initialLayout)
        {
            lock (_gate)
            {
                _sequence = sequence;
                _submittedQueue = null;
                _completion = 0;
                _presentReady = false;
                BeginAcquire(
                    sequence,
                    generation,
                    Texture,
                    initialSync,
                    initialAccess,
                    initialLayout);
                Volatile.Write(ref _outstanding, 1);
            }
        }

        internal ulong CaptureForRecording()
        {
            lock (_gate)
            {
                ulong sequence = _sequence;
                Validate(sequence);
                if (GetStatus(sequence) != SwapchainImageStatus.Acquired)
                    throw new InvalidOperationException("A swapchain Texture can be recorded only while Acquired.");
                Texture.ThrowIfDisposed();
                return sequence;
            }
        }

        internal bool TryBeginSubmit(
            ulong sequence,
            D3D12Queue queue,
            bool presentReady)
        {
            lock (_gate)
            {
                Validate(sequence);
                if (!presentReady)
                {
                    throw new InvalidOperationException(
                        "The submitted SwapchainImage was not explicitly returned to Present/NoAccess.");
                }
                if (!base.TryBeginSubmit(sequence))
                    return false;
                _submittedQueue = queue;
                _completion = 0;
                _presentReady = true;
                return true;
            }
        }

        internal void ValidateSubmission(ulong sequence, bool presentReady)
        {
            lock (_gate)
            {
                Validate(sequence);
                if (GetStatus(sequence) != SwapchainImageStatus.Acquired)
                    throw new InvalidOperationException("The SwapchainImage has no submission right.");
                if (!presentReady)
                {
                    throw new InvalidOperationException(
                        "The submitted SwapchainImage was not explicitly returned to Present/NoAccess.");
                }
                Texture.ThrowIfDisposed();
            }
        }

        internal new void RestoreAcquired(ulong sequence)
        {
            lock (_gate)
            {
                base.RestoreAcquired(sequence);
                _submittedQueue = null;
                _completion = 0;
                _presentReady = false;
            }
        }

        internal void CommitSubmission(
            ulong sequence,
            D3D12Queue queue,
            ulong completion)
        {
            lock (_gate)
            {
                Validate(sequence);
                if (!ReferenceEquals(_submittedQueue, queue) || completion == 0)
                    throw new InvalidOperationException("The SwapchainImage submission provenance is invalid.");
                _completion = completion;
            }
        }

        internal void ValidatePresent(D3D12Queue queue, ulong sequence)
        {
            lock (_gate)
            {
                Validate(sequence);
                if (GetStatus(sequence) != SwapchainImageStatus.Submitted ||
                    !ReferenceEquals(_submittedQueue, queue) ||
                    _completion == 0 ||
                    !_presentReady)
                {
                    throw new InvalidOperationException(
                        "Present requires the current image's accepted Graphics-Queue submission and explicit Present/NoAccess state.");
                }
                Texture.ThrowIfDisposed();
            }
        }

        internal new bool TryBeginPresent(ulong sequence)
        {
            lock (_gate)
            {
                if (!base.TryBeginPresent(sequence))
                    return false;
                Volatile.Write(ref _outstanding, 0);
                return true;
            }
        }

        internal void CompletePresent(bool contentsPreserved)
        {
            lock (_gate)
            {
                _hasPresented = contentsPreserved;
                _presentReady = false;
            }
        }

        internal bool TryGetSubmissionCompletion(
            out D3D12Queue? queue,
            out ulong completion)
        {
            lock (_gate)
            {
                queue = _submittedQueue;
                completion = _completion;
                return queue is not null && completion != 0;
            }
        }

        internal new void Invalidate(bool deviceLost)
        {
            lock (_gate)
            {
                base.Invalidate(deviceLost);
                _presentReady = false;
                Volatile.Write(ref _outstanding, 0);
            }
        }
    }

    private sealed partial class D3D12Texture
    {
        internal D3D12SwapchainImageLease? SwapchainLease
        {
            get => NativeResource.SwapchainLease;
            set => NativeResource.SwapchainLease = value;
        }
    }

    private sealed partial class D3D12TextureResource
    {
        internal D3D12SwapchainImageLease? SwapchainLease { get; set; }
    }

    private sealed partial class D3D12CommandContext
    {
        internal void RecordSwapchainState(
            D3D12TextureResource texture,
            TextureLayout layout,
            ResourceAccess access) =>
            Recording.RecordSwapchainState(texture, layout, access);
    }

    private sealed partial class D3D12CommandSlot
    {
        private readonly Dictionary<D3D12SwapchainImageLease, D3D12RecordedSwapchainUse>
            _swapchainUses = new(ReferenceEqualityComparer.Instance);

        internal void CaptureSwapchainUse(D3D12TextureResource texture)
        {
            D3D12SwapchainImageLease? lease = texture.SwapchainLease;
            if (lease is null)
                return;
            CaptureSwapchainUse(lease);
        }

        internal void CaptureSwapchainUse(D3D12SwapchainImageLease lease)
        {
            ulong sequence = lease.CaptureForRecording();
            if (_swapchainUses.TryGetValue(lease, out D3D12RecordedSwapchainUse use) &&
                use.Sequence != sequence)
            {
                throw new InvalidOperationException(
                    "A command slot cannot reference two acquisitions of the same swapchain image.");
            }
            _swapchainUses[lease] = new D3D12RecordedSwapchainUse(sequence, PresentReady: false);
        }

        internal void RecordSwapchainState(
            D3D12TextureResource texture,
            TextureLayout layout,
            ResourceAccess access)
        {
            D3D12SwapchainImageLease? lease = texture.SwapchainLease;
            if (lease is null)
                return;
            ulong sequence = lease.CaptureForRecording();
            if (_swapchainUses.TryGetValue(lease, out D3D12RecordedSwapchainUse use) &&
                use.Sequence != sequence)
            {
                throw new InvalidOperationException(
                    "A command slot cannot reference two acquisitions of the same swapchain image.");
            }
            _swapchainUses[lease] = new D3D12RecordedSwapchainUse(
                sequence,
                layout == TextureLayout.Present && access == ResourceAccess.NoAccess);
        }

        internal int SwapchainUseCount => _swapchainUses.Count;

        internal int AccumulateSwapchainUses(
            D3D12SwapchainImageLease[] destinationLeases,
            D3D12SubmittedSwapchainUse[] destinationUses,
            int destinationCount)
        {
            foreach ((D3D12SwapchainImageLease lease, D3D12RecordedSwapchainUse use) in _swapchainUses)
            {
                int destinationIndex = 0;
                while (destinationIndex < destinationCount &&
                       !ReferenceEquals(destinationLeases[destinationIndex], lease))
                {
                    destinationIndex++;
                }

                if (destinationIndex < destinationCount &&
                    destinationUses[destinationIndex].Sequence != use.Sequence)
                {
                    throw new InvalidOperationException(
                        "One Submit cannot reference two acquisitions of the same swapchain image.");
                }
                if (destinationIndex == destinationCount)
                {
                    destinationLeases[destinationIndex] = lease;
                    destinationCount++;
                }
                destinationUses[destinationIndex] = new D3D12SubmittedSwapchainUse(
                    use.Sequence,
                    use.PresentReady);
            }
            return destinationCount;
        }

        internal void ClearSwapchainUses() => _swapchainUses.Clear();
    }

    private sealed partial class D3D12RecordedCommandsLease
    {
        internal int GetSwapchainUseCount(ulong sequence)
        {
            EnsureSequence(sequence);
            return _slot.SwapchainUseCount;
        }

        internal int AccumulateSwapchainUses(
            ulong sequence,
            D3D12SwapchainImageLease[] destinationLeases,
            D3D12SubmittedSwapchainUse[] destinationUses,
            int destinationCount)
        {
            EnsureSequence(sequence);
            return _slot.AccumulateSwapchainUses(
                destinationLeases,
                destinationUses,
                destinationCount);
        }
    }

    private readonly record struct D3D12RecordedSwapchainUse(
        ulong Sequence,
        bool PresentReady);

    private readonly record struct D3D12SubmittedSwapchainUse(
        ulong Sequence,
        bool PresentReady);

    private static partial class NativeCast
    {
        internal static D3D12Surface Surface(Surface value)
        {
#if DEBUG
            return (D3D12Surface)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<Surface, D3D12Surface>(ref value);
#endif
        }

        internal static D3D12Swapchain Swapchain(Swapchain value)
        {
#if DEBUG
            return (D3D12Swapchain)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<Swapchain, D3D12Swapchain>(ref value);
#endif
        }
    }
}
