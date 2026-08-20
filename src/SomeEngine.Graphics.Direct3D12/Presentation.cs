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

/// <summary>Reports current DXGI swapchain configuration and recent CPU-side presentation activity.</summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable snapshots may be shared.</para>
/// <para><b>Ownership:</b> Pure value; owns no swapchain or OS handle.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; a captured snapshot remains readable.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct D3D12PresentationInfo(
    SwapchainConfig Config,
    ulong Generation,
    bool OutOfDate,
    long AcquireAttemptCount,
    long AcquireTimeoutCount,
    long AcquireFailureCount,
    long PresentAttemptCount,
    long PresentFailureCount,
    long ReconfigureAttemptCount,
    long ReconfigureFailureCount,
    SwapchainAcquireStatus? LastAcquireStatus,
    PresentStatus? LastPresentStatus,
    ReconfigureStatus? LastReconfigureStatus,
    TimeSpan LastAcquireDuration,
    TimeSpan LastPresentDuration,
    TimeSpan LastReconfigureDuration,
    ulong LastSubmissionCompletion);

internal sealed unsafe partial class D3D12Backend
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
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Surface surface = RequireSurface(desc.Surface);
        nativeDevice.ThrowIfUnavailable();
        surface.ThrowIfDisposed();
        _ = nativeDevice.RequireCapability<Presentation>(nameof(CreateSwapchain));

        ValidateSwapchainDescription(nativeDevice, desc);
        bool tearingSupported = QueryTearingSupport(nativeDevice);
        EnsureSupported(
            desc.Config,
            CreateSwapchainSupport(nativeDevice, native: null, tearingSupported),
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

        IDXGISwapChain4* native = CreateNativeSwapchain(
            nativeDevice,
            surface,
            queue,
            nativeDescription);
        try
        {
            SwapchainSupport[] support = CreateSwapchainSupport(nativeDevice, native, tearingSupported);
            EnsureSupported(desc.Config, support, nameof(desc));
            ConfigureNativeSwapchain(nativeDevice, native, desc.Config);
            SwapchainInfo info = new(
                ResolveSwapchainConfig(nativeDevice, native, desc.Config),
                desc.ImageCount,
                1,
                support);
            D3D12Swapchain result = CreateRegisteredSwapchain(
                nativeDevice,
                surface,
                queue,
                native,
                info,
                desc.ImageUsages,
                desc.Label);
            native = null;
            return result;
        }
        finally
        {
            if (native is not null)
                _ = native->Release();
        }
    }

    private IDXGISwapChain4* CreateNativeSwapchain(
        D3D12Device device,
        D3D12Surface surface,
        D3D12Queue queue,
        in SwapChainDesc1 description)
    {
        IDXGIFactory6* factory = EnsureFactory();
        IDXGISwapChain1* initial = null;
        try
        {
            fixed (SwapChainDesc1* nativeDescription = &description)
            {
                ThrowIfFailed(
                    device,
                    factory->CreateSwapChainForHwnd(
                        (IUnknown*)queue.Native,
                        surface.WindowHandle,
                        nativeDescription,
                        null,
                        null,
                        &initial),
                    NativeOperationType.Ordinary,
                    "IDXGIFactory6::CreateSwapChainForHwnd");
            }

            IDXGISwapChain4* result = null;
            Guid iid = IDXGISwapChain4.Guid;
            ThrowIfFailed(
                device,
                initial->QueryInterface(&iid, (void**)&result),
                NativeOperationType.Ordinary,
                "IDXGISwapChain1::QueryInterface(IDXGISwapChain4)");
            ThrowIfFailed(
                device,
                factory->MakeWindowAssociation(
                    surface.WindowHandle,
                    DxgiMakeWindowAssociationNoAltEnter),
                NativeOperationType.Ordinary,
                "IDXGIFactory::MakeWindowAssociation");
            return result;
        }
        finally
        {
            if (initial is not null)
                _ = initial->Release();
        }
    }

    private static SwapchainConfig ResolveSwapchainConfig(
        D3D12Device device,
        IDXGISwapChain4* native,
        in SwapchainConfig requested)
    {
        SwapChainDesc1 resolved = default;
        ThrowIfFailed(
            device,
            native->GetDesc1(&resolved),
            NativeOperationType.Ordinary,
            "IDXGISwapChain1::GetDesc1");
        SwapchainConfig result = requested with
        {
            Width = resolved.Width,
            Height = resolved.Height,
        };
        if (result.Width == 0 || result.Height == 0)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "DXGI created a swapchain with an empty drawable extent.");
        }
        return result;
    }

    private static D3D12Swapchain CreateRegisteredSwapchain(
        D3D12Device device,
        D3D12Surface surface,
        D3D12Queue queue,
        IDXGISwapChain4* native,
        SwapchainInfo info,
        TextureUsages imageUsages,
        string? label)
    {
        D3D12Swapchain result = new(
            device,
            surface,
            queue,
            native,
            info,
            imageUsages,
            label);
        try
        {
            result.InitializeBackBuffers();
            device.RegisterChild(result);
            surface.RegisterSwapchain(result);
            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    public SwapchainAcquireStatus Acquire(
        Swapchain swapchain,
        in SwapchainAcquireOptions options,
        out SwapchainImage image)
    {
        D3D12Swapchain native = RequireSwapchain(swapchain);
        long started = Stopwatch.GetTimestamp();
        try
        {
            SwapchainAcquireStatus status = native.Acquire(options, out image);
            native.RecordAcquire(status, Stopwatch.GetElapsedTime(started));
            return status;
        }
        catch
        {
            native.RecordAcquireFailure(Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    public PresentStatus Present(Queue queue, in SwapchainImage image)
    {
        D3D12Queue nativeQueue = RequireQueue(queue, nameof(queue));
        if (nativeQueue.Type != QueueType.Graphics)
            throw new ArgumentException("Present requires a Graphics Queue.", nameof(queue));
        if (image.Lease is not D3D12SwapchainImageLease lease)
            throw new ArgumentException("The image does not belong to this D3D12 backend.", nameof(image));
        if (!ReferenceEquals(nativeQueue.Device, lease.Swapchain.Device))
            throw new ArgumentException("The Queue and SwapchainImage belong to different Devices.", nameof(image));

        D3D12Swapchain nativeSwapchain = lease.NativeSwapchain;
        nativeSwapchain.RequirePresentationQueue(nativeQueue, nameof(queue));
        nativeQueue.Device.ThrowIfUnavailable();
        nativeSwapchain.ValidatePresent(nativeQueue, lease, image.Sequence);

        using (nativeQueue.Gate.EnterScope())
        {
            nativeQueue.Device.ThrowIfUnavailable();
            long started = Stopwatch.GetTimestamp();
            _ = lease.TryGetSubmissionCompletion(out _, out ulong completion);
            try
            {
                PresentStatus status = nativeSwapchain.PresentUnderQueueGate(
                    nativeQueue,
                    lease,
                    image.Sequence);
                nativeSwapchain.RecordPresent(
                    status,
                    Stopwatch.GetElapsedTime(started),
                    completion);
                return status;
            }
            catch
            {
                nativeSwapchain.RecordPresentFailure(Stopwatch.GetElapsedTime(started));
                throw;
            }
        }
    }

    public ReconfigureStatus Reconfigure(Swapchain swapchain, in SwapchainConfig config)
    {
        D3D12Swapchain native = RequireSwapchain(swapchain);
        long started = Stopwatch.GetTimestamp();
        try
        {
            ReconfigureStatus status = native.Reconfigure(config);
            native.RecordReconfigure(status, Stopwatch.GetElapsedTime(started));
            return status;
        }
        catch
        {
            native.RecordReconfigureFailure(Stopwatch.GetElapsedTime(started));
            throw;
        }
    }

    internal static D3D12PresentationInfo GetPresentationInfo(
        Device device,
        Swapchain swapchain)
    {
        if (device is not D3D12Device nativeDevice)
        {
            throw new ArgumentException(
                "The Device was not created by the Direct3D 12 backend.",
                nameof(device));
        }
        if (swapchain is not D3D12Swapchain nativeSwapchain ||
            !ReferenceEquals(nativeSwapchain.Device, nativeDevice))
        {
            throw new ArgumentException(
                "The Swapchain was not created for this Direct3D 12 Device.",
                nameof(swapchain));
        }
        nativeSwapchain.ThrowIfDisposed();
        return nativeSwapchain.GetPresentationInfo();
    }

    private bool QueryTearingSupport(D3D12Device device)
    {
        uint supported = 0;
        int result = EnsureFactory()->CheckFeatureSupport(
            DxgiFeature.PresentAllowTearing,
            &supported,
            sizeof(uint));
        if (result < 0)
        {
            ThrowAfterDeviceRemovedReasonQuery(
                device,
                result,
                "IDXGIFactory5::CheckFeatureSupport(PresentAllowTearing)");
        }
        return supported != 0;
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
        ValidateHdr10Metadata(config, parameterName);
    }

    private static void ValidateHdr10Metadata(
        in SwapchainConfig config,
        string parameterName)
    {
        if (config.Hdr10Metadata is not Hdr10Metadata metadata)
            return;
        if (config.ColorSpace != ColorSpace.Hdr10)
        {
            throw new ArgumentException(
                "HDR10 metadata can be supplied only for an HDR10 swapchain color space.",
                parameterName);
        }

        const ushort maximumChromaticity = 50_000;
        if (metadata.RedPrimaryX > maximumChromaticity ||
            metadata.RedPrimaryY > maximumChromaticity ||
            metadata.GreenPrimaryX > maximumChromaticity ||
            metadata.GreenPrimaryY > maximumChromaticity ||
            metadata.BluePrimaryX > maximumChromaticity ||
            metadata.BluePrimaryY > maximumChromaticity ||
            metadata.WhitePointX > maximumChromaticity ||
            metadata.WhitePointY > maximumChromaticity)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "HDR10 chromaticity coordinates must be in the CTA-861 range 0..50000.");
        }
        if (metadata.MaximumMasteringLuminance == 0 ||
            metadata.MinimumMasteringLuminance >
                checked((ulong)metadata.MaximumMasteringLuminance * 10_000UL))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "HDR10 mastering luminance values are inconsistent.");
        }
        if (metadata.MaximumContentLightLevel != 0 &&
            metadata.MaximumFrameAverageLightLevel > metadata.MaximumContentLightLevel)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "HDR10 maximum frame-average light level cannot exceed maximum content light level.");
        }
    }

    private static SwapchainSupport[] CreateSwapchainSupport(
        D3D12Device device,
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
                ThrowIfFailed(
                    device,
                    native->CheckColorSpaceSupport(ToNativeColorSpace(colorSpace), &colorSupport),
                    NativeOperationType.Ordinary,
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
        D3D12Device device,
        IDXGISwapChain4* native,
        in SwapchainConfig config)
    {
        ThrowIfFailed(
            device,
            native->SetMaximumFrameLatency(config.MaximumFrameLatency),
            NativeOperationType.Ordinary,
            "IDXGISwapChain2::SetMaximumFrameLatency");

        DxgiColorSpace colorSpace = ToNativeColorSpace(config.ColorSpace);
        uint colorSupport = 0;
        ThrowIfFailed(
            device,
            native->CheckColorSpaceSupport(colorSpace, &colorSupport),
            NativeOperationType.Ordinary,
            "IDXGISwapChain3::CheckColorSpaceSupport");
        if ((colorSupport & (uint)SwapChainColorSpaceSupportFlag.Present) == 0)
        {
            throw new ArgumentException(
                "The DXGI presentation target cannot present the requested color space.",
                nameof(config));
        }
        ThrowIfFailed(
            device,
            native->SetColorSpace1(colorSpace),
            NativeOperationType.Ordinary,
            "IDXGISwapChain3::SetColorSpace1");
        ConfigureHdr10Metadata(device, native, config.Hdr10Metadata);
    }

    private static void ConfigureHdr10Metadata(
        D3D12Device device,
        IDXGISwapChain4* native,
        Hdr10Metadata? metadata)
    {
        if (metadata is not Hdr10Metadata value)
        {
            ThrowIfFailed(
                device,
                native->SetHDRMetaData(HdrMetadataType.None, 0, null),
                NativeOperationType.Ordinary,
                "IDXGISwapChain4::SetHDRMetaData(None)");
            return;
        }

        HdrMetadataHdr10 nativeMetadata = ToNativeHdr10Metadata(value);
        ThrowIfFailed(
            device,
            native->SetHDRMetaData(
                HdrMetadataType.Hdr10,
                (uint)sizeof(HdrMetadataHdr10),
                &nativeMetadata),
            NativeOperationType.Ordinary,
            "IDXGISwapChain4::SetHDRMetaData(HDR10)");
    }

    internal static HdrMetadataHdr10 ToNativeHdr10Metadata(in Hdr10Metadata value)
    {
        HdrMetadataHdr10 result = default;
        result.RedPrimary[0] = value.RedPrimaryX;
        result.RedPrimary[1] = value.RedPrimaryY;
        result.GreenPrimary[0] = value.GreenPrimaryX;
        result.GreenPrimary[1] = value.GreenPrimaryY;
        result.BluePrimary[0] = value.BluePrimaryX;
        result.BluePrimary[1] = value.BluePrimaryY;
        result.WhitePoint[0] = value.WhitePointX;
        result.WhitePoint[1] = value.WhitePointY;
        result.MaxMasteringLuminance = value.MaximumMasteringLuminance;
        result.MinMasteringLuminance = value.MinimumMasteringLuminance;
        result.MaxContentLightLevel = value.MaximumContentLightLevel;
        result.MaxFrameAverageLightLevel = value.MaximumFrameAverageLightLevel;
        return result;
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
        private readonly D3D12Queue _presentationQueue;
        private readonly object _gate = new();
        private readonly object _acquireGate = new();
        private IDXGISwapChain4* _native;
        private nint _frameLatencyHandle;
        private D3D12SwapchainImageLease[] _images = [];
        private D3D12PresentationRetirement? _nativeGeneration;
        private ulong _nextSequence = 1;
        private bool _outOfDate;
        private bool _deviceLost;
        private long _acquireAttemptCount;
        private long _acquireTimeoutCount;
        private long _acquireFailureCount;
        private long _presentAttemptCount;
        private long _presentFailureCount;
        private long _reconfigureAttemptCount;
        private long _reconfigureFailureCount;
        private SwapchainAcquireStatus? _lastAcquireStatus;
        private PresentStatus? _lastPresentStatus;
        private ReconfigureStatus? _lastReconfigureStatus;
        private TimeSpan _lastAcquireDuration;
        private TimeSpan _lastPresentDuration;
        private TimeSpan _lastReconfigureDuration;
        private ulong _lastSubmissionCompletion;

        internal D3D12Swapchain(
            D3D12Device device,
            D3D12Surface surface,
            D3D12Queue presentationQueue,
            IDXGISwapChain4* native,
            SwapchainInfo info,
            TextureUsages imageUsages,
            string? label)
            : base(device, surface, info, imageUsages, label)
        {
            _device = device;
            _surface = surface;
            _presentationQueue = presentationQueue;
            _native = native;
            _frameLatencyHandle = (nint)native->GetFrameLatencyWaitableObject();
            if (_frameLatencyHandle == 0)
            {
                ThrowAfterDeviceRemovedReasonQuery(
                    _device,
                    Marshal.GetHRForLastWin32Error(),
                    "IDXGISwapChain2::GetFrameLatencyWaitableObject");
            }
        }

        internal void InitializeBackBuffers()
        {
            lock (_gate)
            {
                _images = CreateBackBuffers(Info.Config);
                _nativeGeneration = CapturePresentationGeneration(_images);
            }
        }

        internal void RequirePresentationQueue(D3D12Queue queue, string parameterName)
        {
            if (!ReferenceEquals(queue, _presentationQueue))
            {
                throw new ArgumentException(
                    "A SwapchainImage can be submitted and presented only on the Graphics Queue that owns its native swapchain.",
                    parameterName);
            }
        }

        internal D3D12PresentationInfo GetPresentationInfo()
        {
            lock (_gate)
            {
                return new D3D12PresentationInfo(
                    Info.Config,
                    Info.Generation,
                    _outOfDate,
                    _acquireAttemptCount,
                    _acquireTimeoutCount,
                    _acquireFailureCount,
                    _presentAttemptCount,
                    _presentFailureCount,
                    _reconfigureAttemptCount,
                    _reconfigureFailureCount,
                    _lastAcquireStatus,
                    _lastPresentStatus,
                    _lastReconfigureStatus,
                    _lastAcquireDuration,
                    _lastPresentDuration,
                    _lastReconfigureDuration,
                    _lastSubmissionCompletion);
            }
        }

        internal void RecordAcquire(
            SwapchainAcquireStatus status,
            TimeSpan duration)
        {
            lock (_gate)
            {
                _acquireAttemptCount++;
                if (status == SwapchainAcquireStatus.Timeout)
                    _acquireTimeoutCount++;
                _lastAcquireStatus = status;
                _lastAcquireDuration = duration;
            }
        }

        internal void RecordAcquireFailure(TimeSpan duration)
        {
            lock (_gate)
            {
                _acquireAttemptCount++;
                _acquireFailureCount++;
                _lastAcquireStatus = null;
                _lastAcquireDuration = duration;
            }
        }

        internal void RecordPresent(
            PresentStatus status,
            TimeSpan duration,
            ulong completion)
        {
            lock (_gate)
            {
                _presentAttemptCount++;
                _lastPresentStatus = status;
                _lastPresentDuration = duration;
                _lastSubmissionCompletion = completion;
            }
        }

        internal void RecordPresentFailure(TimeSpan duration)
        {
            lock (_gate)
            {
                _presentAttemptCount++;
                _presentFailureCount++;
                _lastPresentStatus = null;
                _lastPresentDuration = duration;
            }
        }

        internal void RecordReconfigure(
            ReconfigureStatus status,
            TimeSpan duration)
        {
            lock (_gate)
            {
                _reconfigureAttemptCount++;
                _lastReconfigureStatus = status;
                _lastReconfigureDuration = duration;
            }
        }

        internal void RecordReconfigureFailure(TimeSpan duration)
        {
            lock (_gate)
            {
                _reconfigureAttemptCount++;
                _reconfigureFailureCount++;
                _lastReconfigureStatus = null;
                _lastReconfigureDuration = duration;
            }
        }

        internal SwapchainAcquireStatus Acquire(
            in SwapchainAcquireOptions options,
            out SwapchainImage image)
        {
            int timeoutMilliseconds = Timeouts.ToMilliseconds(
                options.Timeout,
                nameof(options));
            if (options.PreserveContents)
            {
                throw new NotSupportedException(
                    "The D3D12 swapchain uses FLIP_DISCARD and cannot guarantee preserved back-buffer contents.");
            }
            image = default;
            lock (_acquireGate)
            {
                lock (_gate)
                {
                    ThrowIfOperational();
                    if (_outOfDate)
                        return SwapchainAcquireStatus.OutOfDate;
                }
                long deadline = CreateDeadline(timeoutMilliseconds);
                uint wait = SilkMarshal.WaitWindowsObjects(
                    _frameLatencyHandle,
                    RemainingWindowsMilliseconds(timeoutMilliseconds, deadline));
                if (wait == 0x102)
                    return SwapchainAcquireStatus.Timeout;
                if (wait != 0)
                {
                    ThrowAfterDeviceRemovedReasonQuery(
                        _device,
                        wait == uint.MaxValue
                            ? Marshal.GetHRForLastWin32Error()
                            : unchecked((int)wait),
                        "Waiting for the DXGI frame-latency object");
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
                        int milliseconds = RemainingMilliseconds(timeoutMilliseconds, deadline);
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
                    lease.BeginAcquire(
                        sequence,
                        Info.Generation,
                        PipelineSync.None,
                        ResourceAccess.NoAccess,
                        TextureLayout.Undefined);
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
                RequirePresentationQueue(queue, nameof(queue));
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
                        if (result < 0)
                        {
                            _outOfDate = true;
                            lease.CompletePresent(contentsPreserved: false);
                            Monitor.PulseAll(_gate);
                            ThrowIfFailed(
                                _device,
                                result,
                                NativeOperationType.Ordinary,
                                "IDXGISwapChain::Present");
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

            using (_presentationQueue.Gate.EnterScope())
            {
                lock (_gate)
                {
                    ThrowIfOperational();
                    if (!IsSupported(config))
                    {
                        if (_outOfDate)
                        {
                            throw new InvalidOperationException(
                                "The Swapchain is OutOfDate and requires a supported reconfiguration or replacement.");
                        }
                        return ReconfigureStatus.Unsupported;
                    }
                    foreach (D3D12SwapchainImageLease image in _images)
                    {
                        if (image.IsOutstanding)
                            return ReconfigureStatus.Busy;
                    }
                    if (Info.Generation == ulong.MaxValue)
                        throw new InvalidOperationException("The Swapchain generation domain is exhausted.");

                    DrainPresentationGenerationUnderQueueGate();
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
                        ThrowIfFailed(
                            _device,
                            resizeResult,
                            NativeOperationType.Ordinary,
                            "IDXGISwapChain::ResizeBuffers after crossing the reconfigure commit boundary");
                    }

                    try
                    {
                        ConfigureNativeSwapchain(_device, _native, config);
                        SwapChainDesc1 resolved = default;
                        ThrowIfFailed(
                            _device,
                            _native->GetDesc1(&resolved),
                            NativeOperationType.Ordinary,
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
                        D3D12PresentationRetirement rebuiltGeneration =
                            CapturePresentationGeneration(rebuilt);
                        Info.Config = resolvedConfig;
                        Info.Generation = checked(Info.Generation + 1);
                        _images = rebuilt;
                        _nativeGeneration = rebuiltGeneration;
                        _outOfDate = false;
                        Monitor.PulseAll(_gate);
                        return ReconfigureStatus.Success;
                    }
                    catch
                    {
                        _outOfDate = true;
                        throw;
                    }
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
            using (_presentationQueue.Gate.EnterScope())
            {
                lock (_gate)
                {
                    RetirePresentationGenerationUnderQueueGate();
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
                        ThrowIfFailed(
                            _device,
                            _native->GetBuffer(index, &iid, (void**)&resource),
                            NativeOperationType.Ordinary,
                            "IDXGISwapChain::GetBuffer");

                        ResourceDesc nativeDescription = resource->GetDesc();
                        ResourceAllocationInfo allocation = _device.Native->GetResourceAllocationInfo(
                            _presentationQueue.NodeMask,
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
                            allocation.SizeInBytes,
                            _presentationQueue.NodeMask,
                            _presentationQueue.NodeMask);
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

        private void DrainPresentationGenerationUnderQueueGate()
        {
            D3D12PresentationRetirement? generation =
                Interlocked.Exchange(ref _nativeGeneration, null);
            if (generation is null)
                return;
            try
            {
                QueueCompletion completion = _presentationQueue.SignalCompletionUnderGate();
                _presentationQueue.WaitForCompletionUnderGate(completion.Value);
                _presentationQueue.CollectCompletedUnderGate();
                generation.Dispose();
            }
            catch
            {
                if (_device.Status == DeviceStatus.Lost)
                {
                    _presentationQueue.RegisterUntrustedPresentationRetirementUnderGate(generation);
                }
                else
                {
                    if (Interlocked.CompareExchange(
                            ref _nativeGeneration,
                            generation,
                            null) is not null)
                    {
                        throw new InvalidOperationException(
                            "The presentation generation retirement authority was concurrently replaced.");
                    }
                }
                throw;
            }
        }

        private D3D12PresentationRetirement CapturePresentationGeneration(
            D3D12SwapchainImageLease[] images)
        {
            try
            {
                return D3D12PresentationRetirement.Capture(_native, images);
            }
            catch
            {
                foreach (D3D12SwapchainImageLease image in images)
                {
                    image.Invalidate(deviceLost: false);
                    image.Texture.DisposeFromParent();
                }
                throw;
            }
        }

        private void RetirePresentationGenerationUnderQueueGate()
        {
            D3D12PresentationRetirement? generation =
                Interlocked.Exchange(ref _nativeGeneration, null);
            if (generation is null)
                return;
            try
            {
                QueueCompletion completion = _presentationQueue.SignalCompletionUnderGate();
                _presentationQueue.RegisterPresentationRetirementUnderGate(
                    completion.Value,
                    generation);
            }
            catch (Exception exception)
            {
                _presentationQueue.RegisterUntrustedPresentationRetirementUnderGate(generation);
                try
                {
                    GraphicsException loss = exception as GraphicsException is
                        { Error: GraphicsError.DeviceLost } graphics
                            ? graphics
                            : new GraphicsException(
                                GraphicsError.DeviceLost,
                                "Swapchain disposal could not establish presentation retirement.",
                                exception is GraphicsException native ? native.NativeCode : null,
                                innerException: exception);
                    _ = _device.MarkLost(loss);
                }
                catch
                {
                }
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

        private static long CreateDeadline(int timeoutMilliseconds) =>
            timeoutMilliseconds == Timeout.Infinite
                ? long.MaxValue
                : checked(Stopwatch.GetTimestamp() +
                    (long)Math.Ceiling(
                        timeoutMilliseconds * (double)Stopwatch.Frequency / 1000d));

        private static int RemainingMilliseconds(int timeoutMilliseconds, long deadline)
        {
            if (timeoutMilliseconds == Timeout.Infinite)
                return Timeout.Infinite;
            long remainingTicks = deadline - Stopwatch.GetTimestamp();
            if (remainingTicks <= 0)
                return 0;
            double milliseconds = Math.Ceiling(
                remainingTicks * 1000d / Stopwatch.Frequency);
            return checked((int)Math.Clamp(milliseconds, 0, int.MaxValue));
        }

        private static uint RemainingWindowsMilliseconds(
            int timeoutMilliseconds,
            long deadline) =>
            timeoutMilliseconds == Timeout.Infinite
                ? uint.MaxValue
                : checked((uint)RemainingMilliseconds(timeoutMilliseconds, deadline));
    }

    private sealed class D3D12PresentationRetirement :
        IntrusiveRetirementPayload<D3D12PresentationRetirement>
    {
        private IDXGISwapChain4* _swapchain;
        private NativeLease[] _images;
        private int _disposed;

        private D3D12PresentationRetirement(
            IDXGISwapChain4* swapchain,
            NativeLease[] images)
        {
            _swapchain = swapchain;
            _images = images;
        }

        internal static D3D12PresentationRetirement Capture(
            IDXGISwapChain4* swapchain,
            D3D12SwapchainImageLease[] images)
        {
            if (swapchain is null)
                throw new ObjectDisposedException(nameof(D3D12Swapchain));
            var retained = new NativeLease[images.Length];
            int count = 0;
            _ = swapchain->AddRef();
            try
            {
                for (; count < retained.Length; count++)
                {
                    NativeLease lifetime = images[count].Texture.NativeLifetime;
                    lifetime.Retain();
                    retained[count] = lifetime;
                }
                return new D3D12PresentationRetirement(swapchain, retained);
            }
            catch
            {
                for (int index = 0; index < count; index++)
                    retained[index].Release();
                _ = swapchain->Release();
                throw;
            }
        }

        public override void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            foreach (NativeLease image in _images)
                image.Release();
            _images = [];
            IDXGISwapChain4* swapchain = _swapchain;
            _swapchain = null;
            if (swapchain is not null)
                _ = swapchain->Release();
        }
    }

    private sealed partial class D3D12Queue
    {
        private IntrusiveRetirementChain<D3D12PresentationRetirement>
            _pendingPresentationRetirements;
        private IntrusiveRetirementChain<D3D12PresentationRetirement>
            _untrustedPresentationRetirements;
        internal void RegisterPresentationRetirementUnderGate(
            ulong completion,
            D3D12PresentationRetirement payload) =>
            _pendingPresentationRetirements.Append(payload, completion);

        internal void RegisterUntrustedPresentationRetirementUnderGate(
            D3D12PresentationRetirement payload) =>
            _untrustedPresentationRetirements.Append(payload, 0);

        internal bool CanAbandonNativePayloadsUnderGate =>
            _device.NativeDeviceLossConfirmed;

        internal bool HasUntrustedPresentationRetirementsUnderGate =>
            _untrustedPresentationRetirements.HasAny;

        internal void WaitForCompletionUnderGate(ulong target)
        {
            ulong completed = ReadFinalCompletionValue();
            if (completed == ulong.MaxValue)
            {
                throw PublishDeviceLoss(
                    _device,
                    DxgiErrorDeviceRemoved,
                    "D3D12 reported the device-removal completion sentinel.",
                    DxgiErrorDeviceRemoved);
            }
            if (completed >= target)
                return;

            nint waitEvent = SilkMarshal.CreateWindowsEvent(
                null,
                bManualReset: false,
                bInitialState: false,
                null);
            if (waitEvent == 0)
            {
                ThrowAfterDeviceRemovedReasonQuery(
                    _device,
                    Marshal.GetHRForLastWin32Error(),
                    "Creating the D3D12 completion wait event");
            }
            try
            {
                int setEventResult = Fence->SetEventOnCompletion(target, (void*)waitEvent);
                ThrowIfFailed(
                    _device,
                    setEventResult,
                    NativeOperationType.Ordinary,
                    "ID3D12Fence::SetEventOnCompletion");
                uint wait = SilkMarshal.WaitWindowsObjects(waitEvent, uint.MaxValue);
                if (wait != 0)
                {
                    ThrowAfterDeviceRemovedReasonQuery(
                        _device,
                        wait == uint.MaxValue
                            ? Marshal.GetHRForLastWin32Error()
                            : unchecked((int)wait),
                        "Waiting for the D3D12 Queue completion");
                }
            }
            finally
            {
                _ = SilkMarshal.CloseWindowsHandle(waitEvent);
            }

            completed = ReadFinalCompletionValue();
            if (completed == ulong.MaxValue)
            {
                throw PublishDeviceLoss(
                    _device,
                    DxgiErrorDeviceRemoved,
                    "D3D12 reported the device-removal completion sentinel.",
                    DxgiErrorDeviceRemoved);
            }
            if (completed < target)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "The D3D12 completion event was signaled before its Fence target completed.");
            }
        }

        private ulong ReadFinalCompletionValue() => Fence->GetCompletedValue();

        internal void CollectCompletedUnderGate()
        {
            ulong completed = Fence->GetCompletedValue();
            CollectRetiredPayloadsUnderGate(completed);
            CollectCapabilityRetirementsUnderGate(completed);
            CollectPresentationRetirementsUnderGate(completed);
        }

        internal ulong GetPresentationRetirementTargetUnderGate() =>
            _pendingPresentationRetirements.Target;

        private void CollectPresentationRetirementsUnderGate(ulong completed)
            => _pendingPresentationRetirements.Collect(completed);

        internal void AbandonPresentationRetirementsUnderGate()
        {
            _pendingPresentationRetirements.Abandon();
            _untrustedPresentationRetirements.Abandon();
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
        private ulong[] _swapchainSequences = [];
        private int _swapchainUseCapacity;

        internal void CaptureSwapchainUses(
            ReadOnlySpan<D3D12SwapchainImageLease> leases)
        {
            for (int index = 0; index < leases.Length; index++)
                _swapchainSequences[index] = leases[index].CaptureForRecording();
            for (int index = 0; index < leases.Length; index++)
            {
                D3D12SwapchainImageLease lease = leases[index];
                ulong sequence = _swapchainSequences[index];
                if (_swapchainUses.TryGetValue(
                        lease,
                        out D3D12RecordedSwapchainUse use) &&
                    use.Sequence != sequence)
                {
                    throw new InvalidOperationException(
                        "A command slot cannot reference two acquisitions of the same swapchain image.");
                }
            }
            for (int index = 0; index < leases.Length; index++)
            {
                _swapchainUses[leases[index]] = new D3D12RecordedSwapchainUse(
                    _swapchainSequences[index],
                    PresentReady: false);
            }
        }

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

    private static partial class RequireD3D12
    {
        internal static D3D12Surface Surface(Surface value) =>
            value as D3D12Surface ??
            throw new ArgumentException(
                "The Surface was not created by the Direct3D 12 backend.",
                nameof(value));

        internal static D3D12Swapchain Swapchain(Swapchain value) =>
            value as D3D12Swapchain ??
            throw new ArgumentException(
                "The Swapchain was not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
