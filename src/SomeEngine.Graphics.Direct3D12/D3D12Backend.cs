using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using NativeD3D12 = Silk.NET.Direct3D12.D3D12;
using NativeDxgi = Silk.NET.DXGI.DXGI;

namespace SomeEngine.Graphics.Direct3D12;

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct D3D12ValidationOptions(
    bool DisableGpuBasedValidation = false,
    bool DisableSynchronizedQueueValidation = false,
    bool DisableDred = false);

/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct D3D12BackendOptions(
    D3D12ValidationOptions Validation = default,
    bool UseQueueSpecificCommonLayouts = false);

/// <summary>
/// Creates the Direct3D 12 implementation behind the product-wide
/// <see cref="IGraphicsBackend"/> contract.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. The factory is stateless.</para>
/// <para><b>Ownership:</b> The caller owns the returned <see cref="IGraphicsBackend"/> and must dispose it.</para>
/// <para><b>After Dispose:</b> The factory has no independent Dispose state; each returned backend enforces its own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public static class D3D12GraphicsBackend
{
    public static IGraphicsBackend Create(in D3D12BackendOptions options = default) =>
        new D3D12Backend(options);
}

/// <summary>
/// The internal Direct3D 12 implementation. It owns DXGI, D3D12, and every native child created through it.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Concurrent Dispose calls are safe and collectively perform one logical release; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Caller-disposed backend-runtime root. Construction creates one ownership
/// right; transferring it to the Validation Layer does not leave a second disposal right. It destroys
/// Devices and backend-created Surfaces before releasing DXGI and D3D12 runtime state.</para>
/// <para><b>After Dispose:</b> No receiver or native-access operation is valid and the runtime never reopens.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
internal sealed unsafe partial class D3D12Backend :
    IGraphicsBackend,
    INativeValidationControl
{
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int DxgiErrorUnsupported = unchecked((int)0x887A0004);
    private const int DxgiErrorSdkComponentMissing = unchecked((int)0x887A002D);
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint AgilitySdkVersion = 619;
    private static readonly Guid SdkConfigurationClassId =
        new(0x7cda6aca, 0xa03e, 0x49c8, 0x94, 0x58, 0x03, 0x34, 0xd2, 0x0e, 0x07, 0xce);
    private static readonly Guid DebugClassId =
        new(0xf2352aeb, 0xdd84, 0x49fe, 0xb9, 0x7b, 0xa9, 0xdc, 0xfd, 0xcc, 0x1b, 0x4f);
    private static readonly Guid DredClassId =
        new(0x4a75bbc4, 0x9ff4, 0x4ad8, 0x9f, 0x18, 0xab, 0xae, 0x84, 0xdc, 0x5f, 0xf2);
    private static D3D12Backend? s_runtimeQuarantineHead;

    private readonly object _gate = new();
    private readonly GraphicsObjectRegistry _devices;
    private readonly GraphicsObjectRegistry _surfaces;
    private readonly NativeD3D12 _d3d12;
    private readonly NativeDxgi _dxgi;
    private readonly D3D12BackendOptions _options;
    private readonly Action<D3D12Backend>? _beforeLoaderRelease;

    private DisposeGate _disposeGate;
    private D3D12Backend? _runtimeQuarantineNext;
    private ID3D12SDKConfiguration1* _sdkConfiguration;
    private ID3D12DeviceFactory* _deviceFactory;
    private IDXGIFactory6* _factory;
    private bool _nativeValidationEnabled;
    private bool _debugLayerEnabled;
    private bool _gpuBasedValidationEnabled;
    private bool _synchronizedQueueValidationEnabled;
    private bool _dredEnabled;
    private bool _deviceCreated;
    private D3D12Device? _diagnosticDevice;

    internal bool UseQueueSpecificCommonLayouts =>
        _options.UseQueueSpecificCommonLayouts;

    internal D3D12Backend(in D3D12BackendOptions options = default)
        : this(options, null)
    {
    }

    internal D3D12Backend(
        in D3D12BackendOptions options,
        Action<D3D12Backend>? beforeLoaderRelease)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Direct3D 12 backend requires Windows.");

        _options = options;
        _beforeLoaderRelease = beforeLoaderRelease;
        _devices = new GraphicsObjectRegistry(_gate);
        _surfaces = new GraphicsObjectRegistry(_gate);
        try
        {
            _d3d12 = NativeD3D12.GetApi();
            _dxgi = NativeDxgi.GetApi((Silk.NET.Core.Contexts.INativeWindowSource)null!);
            CreateDeviceFactory();
        }
        catch (Exception exception)
        {
            try
            {
                _dxgi?.Dispose();
                _d3d12?.Dispose();
            }
            catch
            {
            }
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "Direct3D 12 or DXGI could not be loaded.",
                innerException: exception);
        }
    }

    public bool TryEnumerateAdapters(
        in AdapterEnumerationOptions options,
        Span<AdapterInfo> destination,
        out int requiredCount)
    {
        ThrowIfDisposed();
        IDXGIFactory6* factory = EnsureFactory();
        GpuPreference preference = ToGpuPreference(options.Preference);
        List<AdapterInfo> adapters = [];

        for (uint index = 0; ; index++)
        {
            IDXGIAdapter4* adapter = null;
            Guid iid = IDXGIAdapter4.Guid;
            int result = factory->EnumAdapterByGpuPreference(
                index,
                preference,
                &iid,
                (void**)&adapter);

            if (result == DxgiErrorNotFound)
                break;
            ThrowIfFailed(null, result, NativeOperationType.Ordinary, "IDXGIFactory6::EnumAdapterByGpuPreference");

            try
            {
                AdapterDesc3 native = default;
                ThrowIfFailed(
                    null,
                    adapter->GetDesc3(&native),
                    NativeOperationType.Ordinary,
                    "IDXGIAdapter4::GetDesc3");

                bool software = (native.Flags & AdapterFlag3.Software) != 0;
                if ((!software || options.IncludeSoftware) && SupportsDirect3D12(adapter))
                    adapters.Add(ToAdapterInfo(adapter, native));
            }
            finally
            {
                if (adapter is not null)
                    _ = adapter->Release();
            }
        }

        requiredCount = adapters.Count;
        if (destination.Length < requiredCount)
            return false;

        CollectionsMarshal.AsSpan(adapters).CopyTo(destination);
        return true;
    }

    public Device CreateDevice(in DeviceDesc desc)
    {
        ThrowIfDisposed();
        ValidateDeviceDescription(desc);
        lock (_gate)
        {
            ThrowIfDisposed();
            _deviceCreated = true;
        }

        IDXGIAdapter4* adapter = SelectAdapter(desc.AdapterId, out AdapterInfo adapterInfo);
        try
        {
            D3D12Device device = D3D12Device.Create(this, adapter, adapterInfo, desc);
            adapter = null;
            try
            {
                Register(device);
                return device;
            }
            catch
            {
                device.Dispose();
                throw;
            }
        }
        finally
        {
            if (adapter is not null)
                _ = adapter->Release();
        }
    }

    private static void ValidateDeviceDescription(in DeviceDesc desc)
    {
        if (desc.Queues.IsEmpty)
            throw new ArgumentException("A Device requires at least one Queue description.", nameof(desc));
        if (desc.EnabledNodeMask == 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "EnabledNodeMask must be nonzero.");

        const DeviceFeatures knownFeatures =
            DeviceFeatures.Presentation |
            DeviceFeatures.SparseResources |
            DeviceFeatures.SamplerFeedback |
            DeviceFeatures.Residency |
            DeviceFeatures.RayTracing |
            DeviceFeatures.MeshShaders |
            DeviceFeatures.VariableRateShading |
            DeviceFeatures.WorkGraphs |
            DeviceFeatures.IndirectCommands |
            DeviceFeatures.CalibratedTimestamps |
            DeviceFeatures.LinkedAdapters |
            DeviceFeatures.ExternalResources |
            DeviceFeatures.ExternalTimelines;
        if (((desc.RequiredFeatures | desc.OptionalFeatures) & ~knownFeatures) != 0)
            throw new ArgumentOutOfRangeException(nameof(desc), "The Device feature set contains unknown bits.");

        for (int queueIndex = 0; queueIndex < desc.Queues.Length; queueIndex++)
        {
            ref readonly DeviceQueueDesc queue = ref desc.Queues[queueIndex];
            _ = queue.Type switch
            {
                QueueType.Graphics => 0,
                QueueType.Compute => 1,
                QueueType.Copy => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(desc), "A Queue type is unknown."),
            };
            if (queue.Count == 0)
                throw new ArgumentOutOfRangeException(nameof(desc), "A Queue count must be nonzero.");
            if (!float.IsFinite(queue.Priority) || queue.Priority is < 0f or > 1f)
                throw new ArgumentOutOfRangeException(nameof(desc), "A Queue priority must be finite and in [0, 1].");
            if (queue.NodeIndex >= 32 ||
                (desc.EnabledNodeMask & (1u << checked((int)queue.NodeIndex))) == 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(desc),
                    "A Queue NodeIndex must select one enabled linked-adapter node.");
            }
            for (int priorIndex = 0; priorIndex < queueIndex; priorIndex++)
            {
                ref readonly DeviceQueueDesc prior = ref desc.Queues[priorIndex];
                if (prior.Type == queue.Type && prior.NodeIndex == queue.NodeIndex)
                {
                    throw new ArgumentException(
                        "A Device Queue type and linked-adapter node may be described only once; use Count for multiple Queues.",
                        nameof(desc));
                }
            }
        }
    }

    public Surface CreateSurface(in SurfaceDesc desc)
    {
        ThrowIfDisposed();
        if (desc.Type != NativeWindowType.Win32)
            throw new ArgumentOutOfRangeException(nameof(desc), "Direct3D 12 supports Win32 surfaces.");

        D3D12Surface surface = new(this, desc);
        try
        {
            Register(surface);
            return surface;
        }
        catch
        {
            surface.Dispose();
            throw;
        }
    }

    void INativeValidationControl.EnableNativeValidation()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_deviceCreated)
                throw new InvalidOperationException(
                    "Native validation must be selected before the first Device is created.");
            if (_nativeValidationEnabled)
                return;

            EnableDebugFacilities(_options.Validation);
            _nativeValidationEnabled = true;

            if (_factory is not null)
            {
                _ = _factory->Release();
                _factory = null;
            }
        }
    }

    public void Dispose()
    {
        if (!_disposeGate.TryEnter())
            return;

        try
        {
            ReleaseBackend();
        }
        catch (Exception exception)
        {
            _diagnosticDevice?.RecordReleaseFailure(exception);
            // Once cleanup cannot be proven complete, retain the backend and its runtime
            // authority for the remainder of the process. Publication cannot allocate.
            PublishRuntimeQuarantine();
        }
        finally
        {
            _disposeGate.Exit();
        }
    }

    private void ReleaseBackend()
    {
        GraphicsObject? devices = _devices.CloseAndBuildDrainList();
        while (devices is GraphicsObject device)
        {
            devices = device.RegistryDrainNext;
            device.RegistryDrainNext = null;
            device.DisposeFromParent();
            _ = _devices.CompleteDrain(device);
        }
        GraphicsObject? surfaces = _surfaces.CloseAndBuildDrainList();
        while (surfaces is GraphicsObject surface)
        {
            surfaces = surface.RegistryDrainNext;
            surface.RegistryDrainNext = null;
            surface.DisposeFromParent();
            _ = _surfaces.CompleteDrain(surface);
        }
        if (_devices.HasRetainedFailures || _surfaces.HasRetainedFailures)
        {
            try
            {
                _beforeLoaderRelease?.Invoke(this);
            }
            catch (Exception exception)
            {
                _diagnosticDevice?.RecordReleaseFailure(exception);
            }
            PublishRuntimeQuarantine();
            return;
        }

        lock (_gate)
        {
            if (_factory is not null)
            {
                _ = _factory->Release();
                _factory = null;
            }
            if (_deviceFactory is not null)
            {
                _ = _deviceFactory->Release();
                _deviceFactory = null;
            }
            if (_sdkConfiguration is not null)
            {
                _sdkConfiguration->FreeUnusedSDKs();
                _ = _sdkConfiguration->Release();
                _sdkConfiguration = null;
            }
        }

        // Pinned Silk.NET 2.23 owns a nonzero handle from successful GetApi creation.
        // Its generated VTable.Dispose is empty and its context release forwards that
        // valid handle to NativeLibrary.Free, whose public contract has no failure
        // result or documented exception. Keep the release sequence linear.
        try
        {
            _beforeLoaderRelease?.Invoke(this);
        }
        catch (Exception exception)
        {
            _diagnosticDevice?.RecordReleaseFailure(exception);
            PublishRuntimeQuarantine();
            return;
        }
        _dxgi.Dispose();
        _d3d12.Dispose();
    }

    private void Register(GraphicsObject child)
    {
        ThrowIfDisposed();
        switch (child)
        {
            case D3D12Device:
                _devices.Add(child);
                _diagnosticDevice ??= (D3D12Device)child;
                break;
            case D3D12Surface:
                _surfaces.Add(child);
                break;
            default:
                throw new InvalidOperationException(
                    "Only direct Device and Surface roots belong to the backend registry.");
        }
    }

    private void Unregister(GraphicsObject child)
    {
        if (child is D3D12Device)
            _devices.Remove(child);
        else if (child is D3D12Surface)
            _surfaces.Remove(child);
    }

    private IDXGIFactory6* EnsureFactory()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_factory is not null)
                return _factory;

            uint flags = _debugLayerEnabled ? 1u : 0u;
            Guid iid = IDXGIFactory6.Guid;
            IDXGIFactory6* factory = null;
            ThrowIfFailed(
                null,
                _dxgi.CreateDXGIFactory2(flags, &iid, (void**)&factory),
                NativeOperationType.Ordinary,
                "CreateDXGIFactory2");
            _factory = factory;
            return factory;
        }
    }

    private bool SupportsDirect3D12(IDXGIAdapter4* adapter)
    {
        Guid iid = ID3D12Device.Guid;
        int result = _deviceFactory->CreateDevice(
            (IUnknown*)adapter,
            D3DFeatureLevel.Level120,
            &iid,
            null);
        if (result == DxgiErrorUnsupported)
            return false;
        ThrowIfFailed(null, result, NativeOperationType.Ordinary, "D3D12CreateDevice(support query)");
        return true;
    }

    private IDXGIAdapter4* SelectAdapter(AdapterId id, out AdapterInfo info)
    {
        IDXGIFactory6* factory = EnsureFactory();

        for (uint index = 0; ; index++)
        {
            IDXGIAdapter4* adapter = null;
            Guid iid = IDXGIAdapter4.Guid;
            int result = factory->EnumAdapterByGpuPreference(
                index,
                GpuPreference.HighPerformance,
                &iid,
                (void**)&adapter);
            if (result == DxgiErrorNotFound)
                break;
            ThrowIfFailed(null, result, NativeOperationType.Ordinary, "IDXGIFactory6::EnumAdapterByGpuPreference");

            AdapterDesc3 native = default;
            try
            {
                ThrowIfFailed(null, adapter->GetDesc3(&native), NativeOperationType.Ordinary, "IDXGIAdapter4::GetDesc3");
                AdapterInfo candidate = ToAdapterInfo(adapter, native);
                if ((id.IsDefault || candidate.Id == id) && SupportsDirect3D12(adapter))
                {
                    info = candidate;
                    return adapter;
                }
            }
            catch
            {
                _ = adapter->Release();
                throw;
            }

            _ = adapter->Release();
        }

        if (!id.IsDefault)
        {
            IDXGIAdapter4* warp = null;
            Guid warpIid = IDXGIAdapter4.Guid;
            int warpResult = factory->EnumWarpAdapter(&warpIid, (void**)&warp);
            ThrowIfFailed(null, warpResult, NativeOperationType.Ordinary, "IDXGIFactory4::EnumWarpAdapter");
            AdapterDesc3 native = default;
            try
            {
                ThrowIfFailed(null, warp->GetDesc3(&native), NativeOperationType.Ordinary, "IDXGIAdapter4::GetDesc3");
                AdapterInfo candidate = ToAdapterInfo(warp, native);
                if (!id.IsDefault && candidate.Id == id && SupportsDirect3D12(warp))
                {
                    info = candidate;
                    return warp;
                }
            }
            catch
            {
                _ = warp->Release();
                throw;
            }

            _ = warp->Release();
        }

        throw new GraphicsException(
            GraphicsError.NativeFailure,
            id.IsDefault
                ? "No Direct3D 12 hardware adapter is available."
                : "The selected Direct3D 12 adapter is not available.");
    }

    private static GpuPreference ToGpuPreference(AdapterPreference preference) => preference switch
    {
        AdapterPreference.Unspecified => GpuPreference.Unspecified,
        AdapterPreference.HighPerformance => GpuPreference.HighPerformance,
        AdapterPreference.MinimumPower => GpuPreference.MinimumPower,
        _ => throw new ArgumentOutOfRangeException(nameof(preference)),
    };

    private static AdapterInfo ToAdapterInfo(IDXGIAdapter4* adapter, in AdapterDesc3 native)
    {
        string name;
        fixed (char* description = native.Description)
            name = new string(description).TrimEnd('\0');

        bool software = (native.Flags & AdapterFlag3.Software) != 0;
        AdapterType type = software
            ? AdapterType.Cpu
            : native.DedicatedVideoMemory != 0
                ? AdapterType.Discrete
                : AdapterType.Integrated;

        AdapterId id = new(
            native.AdapterLuid.Low,
            unchecked((ulong)(long)native.AdapterLuid.High));

        string driverVersion = ReadDriverVersion(adapter);

        return new AdapterInfo(
            id,
            type,
            name,
            native.VendorId,
            native.DeviceId,
            native.DedicatedVideoMemory,
            native.DedicatedSystemMemory,
            native.SharedSystemMemory,
            driverVersion,
            !software);
    }

    private static string ReadDriverVersion(IDXGIAdapter4* adapter)
    {
        Guid interfaceId = IDXGIDevice.Guid;
        long version = 0;
        int result = adapter->CheckInterfaceSupport(&interfaceId, &version);
        if (result == DxgiErrorUnsupported)
            return "unavailable";
        ThrowIfFailed(null, result, NativeOperationType.Ordinary, "IDXGIAdapter::CheckInterfaceSupport");

        ulong packed = unchecked((ulong)version);
        uint high = (uint)(packed >> 32);
        uint low = (uint)packed;
        return $"{high >> 16}.{high & 0xFFFF}.{low >> 16}.{low & 0xFFFF}";
    }

    private void EnableDebugFacilities(in D3D12ValidationOptions options)
    {
        ID3D12Debug1* debug = null;
        Guid classId = DebugClassId;
        Guid iid = ID3D12Debug1.Guid;
        int result = _deviceFactory->GetConfigurationInterface(
            &classId,
            &iid,
            (void**)&debug);
        if (result < 0 && result is not ENoInterface and not DxgiErrorSdkComponentMissing)
            ThrowIfFailed(null, result, NativeOperationType.Ordinary, "D3D12GetDebugInterface(ID3D12Debug1)");
        if (result >= 0 && debug is null)
        {
            throw new GraphicsException(
                GraphicsError.NativeFailure,
                "D3D12GetDebugInterface(ID3D12Debug1) succeeded without returning an interface.");
        }
        if (result >= 0 && debug is not null)
        {
            try
            {
                debug->EnableDebugLayer();
                _debugLayerEnabled = true;
                if (!options.DisableGpuBasedValidation)
                {
                    debug->SetEnableGPUBasedValidation(true);
                    _gpuBasedValidationEnabled = true;
                }
                if (!options.DisableSynchronizedQueueValidation)
                {
                    debug->SetEnableSynchronizedCommandQueueValidation(true);
                    _synchronizedQueueValidationEnabled = true;
                }
            }
            finally
            {
                _ = debug->Release();
            }
        }

        if (!options.DisableDred)
        {
            ID3D12DeviceRemovedExtendedDataSettings1* dred = null;
            classId = DredClassId;
            iid = ID3D12DeviceRemovedExtendedDataSettings1.Guid;
            result = _deviceFactory->GetConfigurationInterface(
                &classId,
                &iid,
                (void**)&dred);
            if (result < 0 && result is not ENoInterface and not DxgiErrorSdkComponentMissing)
            {
                ThrowIfFailed(
                    null,
                    result,
                    NativeOperationType.Ordinary,
                    "D3D12GetDebugInterface(ID3D12DeviceRemovedExtendedDataSettings1)");
            }
            if (result >= 0 && dred is null)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "D3D12GetDebugInterface(ID3D12DeviceRemovedExtendedDataSettings1) " +
                    "succeeded without returning an interface.");
            }
            if (result >= 0 && dred is not null)
            {
                try
                {
                    dred->SetAutoBreadcrumbsEnablement(DredEnablement.ForcedOn);
                    dred->SetPageFaultEnablement(DredEnablement.ForcedOn);
                    dred->SetBreadcrumbContextEnablement(DredEnablement.ForcedOn);
                    _dredEnabled = true;
                }
                finally
                {
                    _ = dred->Release();
                }
            }
        }
    }

    private void CreateDeviceFactory()
    {
        ID3D12SDKConfiguration1* configuration = null;
        ID3D12DeviceFactory* factory = null;
        Guid classId = SdkConfigurationClassId;
        Guid iid = ID3D12SDKConfiguration1.Guid;
        ThrowIfFailed(
            null,
            _d3d12.GetInterface(&classId, &iid, (void**)&configuration),
            NativeOperationType.Ordinary,
            "D3D12GetInterface(ID3D12SDKConfiguration1)");
        try
        {
            string sdkPath = Path.Combine(AppContext.BaseDirectory, "D3D12") +
                Path.DirectorySeparatorChar;
            iid = ID3D12DeviceFactory.Guid;
            ThrowIfFailed(
                null,
                configuration->CreateDeviceFactory(
                    AgilitySdkVersion,
                    sdkPath,
                    &iid,
                    (void**)&factory),
                NativeOperationType.Ordinary,
                "ID3D12SDKConfiguration1::CreateDeviceFactory");
            if (factory is null)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "ID3D12SDKConfiguration1::CreateDeviceFactory succeeded without returning a factory.");
            }

            _sdkConfiguration = configuration;
            configuration = null;
            _deviceFactory = factory;
            factory = null;
        }
        finally
        {
            if (factory is not null)
                _ = factory->Release();
            if (configuration is not null)
                _ = configuration->Release();
        }
    }

    private void PublishRuntimeQuarantine()
    {
        while (true)
        {
            D3D12Backend? head = Volatile.Read(ref s_runtimeQuarantineHead);
            for (D3D12Backend? current = head;
                 current is not null;
                 current = Volatile.Read(ref current._runtimeQuarantineNext))
            {
                if (ReferenceEquals(current, this))
                    return;
            }

            Volatile.Write(ref _runtimeQuarantineNext, head);
            if (ReferenceEquals(
                    Interlocked.CompareExchange(ref s_runtimeQuarantineHead, this, head),
                    head))
            {
                return;
            }
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposeGate.IsDisposed, this);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Device RequireDevice(Device value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value is not D3D12Device result)
        {
            throw new ArgumentException(
                "The Device was not created by the Direct3D 12 backend.",
                parameterName);
        }
        if (!ReferenceEquals(result.Backend, this))
        {
            throw new ArgumentException(
                "The Device belongs to another graphics backend instance.",
                parameterName);
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Queue RequireQueue(Queue value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value is not D3D12Queue result)
        {
            throw new ArgumentException(
                "The Queue was not created by the Direct3D 12 backend.",
                parameterName);
        }
        if (!ReferenceEquals(result.NativeDevice.Backend, this))
        {
            throw new ArgumentException(
                "The Queue belongs to another graphics backend instance.",
                parameterName);
        }
        return result;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12CommandContext RequireCommandContext(
        CommandContext value,
        string parameterName)
    {
        if (value is D3D12CommandContext result &&
            ReferenceEquals(result.NativeBackend, this))
        {
            result.BeginPublicCall();
            return result;
        }
        return ThrowInvalidCommandContext(value, parameterName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private D3D12CommandContext ThrowInvalidCommandContext(
        CommandContext value,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (value is not D3D12CommandContext result)
        {
            throw new ArgumentException(
                "The CommandContext was not created by the Direct3D 12 backend.",
                parameterName);
        }
        throw new ArgumentException(
            "The CommandContext belongs to another graphics backend instance.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void RequireBackendOwner(DeviceResource value, string? parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!ReferenceEquals(value.Device.BackendOwner, this))
        {
            throw new ArgumentException(
                "The graphics object belongs to another backend instance.",
                parameterName);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void RequireSameDevice(
        D3D12Device expected,
        DeviceResource value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (!ReferenceEquals(value.Device, expected))
        {
            throw new ArgumentException(
                "The graphics object belongs to another Device.",
                parameterName);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Heap RequireHeap(
        Heap value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12Heap ?? throw new ArgumentException(
            "The Heap was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Buffer RequireBuffer(
        Buffer value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12Buffer ?? throw new ArgumentException(
            "The Buffer was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12TextureResource RequireTexture(
        Texture value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value switch
        {
            D3D12Texture texture => texture.NativeResource,
            D3D12SamplerFeedbackTexture feedback => feedback.NativeResource,
            _ => throw new ArgumentException(
                "The Texture was not created by the Direct3D 12 backend.",
                parameterName),
        };
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Pipeline RequirePipeline(
        Pipeline value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (value is D3D12Pipeline result &&
            ReferenceEquals(result.Device.BackendOwner, this))
        {
            return result;
        }
        return ThrowInvalidPipeline(value, parameterName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private D3D12Pipeline ThrowInvalidPipeline(Pipeline value, string? parameterName)
    {
        RequireBackendOwner(value, parameterName);
        throw new ArgumentException(
            "The Pipeline was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12PipelineCache RequirePipelineCache(
        PipelineCache value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12PipelineCache ?? throw new ArgumentException(
            "The PipelineCache was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12QueryPool RequireQueryPool(
        QueryPool value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12QueryPool ?? throw new ArgumentException(
            "The QueryPool was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12ExternalTimeline RequireTimeline(
        ExternalTimeline value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12ExternalTimeline ?? throw new ArgumentException(
            "The ExternalTimeline was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12DescriptorTable RequireDescriptorTable(
        DescriptorTable value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12DescriptorTable ?? throw new ArgumentException(
            "The DescriptorTable was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12PersistentParameterBindings RequirePersistentParameterBindings(
        PersistentParameterBindings value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (value is D3D12PersistentParameterBindings result &&
            ReferenceEquals(result.Device.BackendOwner, this))
        {
            return result;
        }
        return ThrowInvalidPersistentParameterBindings(value, parameterName);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private D3D12PersistentParameterBindings ThrowInvalidPersistentParameterBindings(
        PersistentParameterBindings value,
        string? parameterName)
    {
        RequireBackendOwner(value, parameterName);
        throw new ArgumentException(
            "The PersistentParameterBindings object was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12IndirectCommandLayout RequireIndirectCommandLayout(
        IndirectCommandLayout value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12IndirectCommandLayout ?? throw new ArgumentException(
            "The IndirectCommandLayout was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12AccelerationStructure RequireAccelerationStructure(
        AccelerationStructure value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12AccelerationStructure ?? throw new ArgumentException(
            "The AccelerationStructure was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12RayTracingShaderTable RequireRayTracingShaderTable(
        RayTracingShaderTable value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12RayTracingShaderTable ?? throw new ArgumentException(
            "The RayTracingShaderTable was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12RayTracingPipeline RequireRayTracingPipeline(
        Pipeline value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12RayTracingPipeline ?? throw new ArgumentException(
            "The Pipeline is not a Direct3D 12 ray-tracing Pipeline.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12WorkGraphPipeline RequireWorkGraphPipeline(
        Pipeline value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12WorkGraphPipeline ?? throw new ArgumentException(
            "The Pipeline is not a Direct3D 12 Work Graph Pipeline.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12RecordedBundle RequireBundle(
        RecordedBundle value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12RecordedBundle ?? throw new ArgumentException(
            "The RecordedBundle was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12SamplerFeedbackTexture RequireSamplerFeedbackTexture(
        SamplerFeedbackTexture value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12SamplerFeedbackTexture ?? throw new ArgumentException(
            "The Texture is not a Direct3D 12 sampler-feedback Texture.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12SamplerFeedbackUav RequireSamplerFeedbackUav(
        SamplerFeedbackUav value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12SamplerFeedbackUav ?? throw new ArgumentException(
            "The SamplerFeedbackUav was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Surface RequireSurface(
        Surface value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!ReferenceEquals(value.BackendOwner, this))
        {
            throw new ArgumentException(
                "The Surface belongs to another backend instance.",
                parameterName);
        }
        return value as D3D12Surface ?? throw new ArgumentException(
            "The Surface was not created by the Direct3D 12 backend.",
            parameterName);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Swapchain RequireSwapchain(
        Swapchain value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        RequireBackendOwner(value, parameterName);
        return value as D3D12Swapchain ?? throw new ArgumentException(
            "The Swapchain was not created by the Direct3D 12 backend.",
            parameterName);
    }

    private sealed partial class D3D12Surface : Surface
    {
        private static readonly InvalidOperationException SwapchainDetachFailure =
            new("A Swapchain did not detach during Surface teardown.");
        private readonly D3D12Backend _backend;
        private readonly object _swapchainsGate = new();
        private readonly GraphicsObjectRegistry _swapchains;

        internal D3D12Surface(D3D12Backend backend, in SurfaceDesc desc)
            : base(
                desc.Type,
                desc.WindowHandle,
                desc.DisplayHandle,
                backend,
                desc.Label)
        {
            _backend = backend;
            _swapchains = new GraphicsObjectRegistry(_swapchainsGate);
        }

        internal void RegisterSwapchain(D3D12Swapchain swapchain)
        {
            ThrowIfDisposed();
            _swapchains.Add(swapchain);
        }

        internal void UnregisterSwapchain(D3D12Swapchain swapchain)
        {
            _swapchains.Remove(swapchain);
        }

        internal override void Release(bool fromParent)
        {
            GraphicsObject? swapchains = _swapchains.CloseAndBuildDrainList(
                secondaryLink: true);
            while (swapchains is D3D12Swapchain swapchain)
            {
                swapchains = swapchain.SecondaryRegistryDrainNext;
                swapchain.SecondaryRegistryDrainNext = null;
                swapchain.DisposeFromParent();
                if (_swapchains.CompleteDrain(swapchain))
                {
                    swapchain.Device.RecordReleaseFailure(SwapchainDetachFailure);
                }
            }
            if (_swapchains.HasRetainedFailures)
                return;
            _backend.Unregister(this);
        }
    }
}
