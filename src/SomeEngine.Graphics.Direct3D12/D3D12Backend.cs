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
    bool DisableDred = false
);
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. Immutable values may be shared; referenced RHI objects retain their own contracts.</para>
/// <para><b>Ownership:</b> Pure managed value; copying it does not transfer ownership of any referenced RHI object.</para>
/// <para><b>After Dispose:</b> This type has no independent Dispose state; referenced objects retain their own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public readonly record struct D3D12BackendOptions(D3D12ValidationOptions Validation = default, bool UseQueueSpecificCommonLayouts = false);
/// <summary>
/// Creates the Direct3D 12 implementation behind the product-wide
/// <see cref = "IGraphicsBackend"/> contract.
/// </summary>
/// <remarks>
/// <para><b>Thread safety:</b> Thread-safe. The factory is stateless.</para>
/// <para><b>Ownership:</b> The caller owns the returned <see cref = "IGraphicsBackend"/> and must dispose it.</para>
/// <para><b>After Dispose:</b> The factory has no independent Dispose state; each returned backend enforces its own terminal state.</para>
/// <para>See <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-001">RHI-LIFE-001</see>, <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-002">RHI-LIFE-002</see>, and <see href="wiki/architecture/RHI/Lifetime-Concurrency-and-Diagnostics.md#rhi-life-007">RHI-LIFE-007</see>.</para>
/// </remarks>
public static class D3D12GraphicsBackend
{
    public static IGraphicsBackend Create(in D3D12BackendOptions options = default) => new D3D12Backend(options);
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
internal sealed unsafe partial class D3D12Backend : IGraphicsBackend, INativeValidationControl
{
    private const int ENoInterface = unchecked((int)0x80004002);
    private const int DxgiErrorUnsupported = unchecked((int)0x887A0004);
    private const int DxgiErrorSdkComponentMissing = unchecked((int)0x887A002D);
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint AgilitySdkVersion = 619;
    private static readonly Guid SdkConfigurationClassId = new(0x7cda6aca, 0xa03e, 0x49c8, 0x94, 0x58, 0x03, 0x34, 0xd2, 0x0e, 0x07, 0xce);
    private static readonly Guid DebugClassId = new(0xf2352aeb, 0xdd84, 0x49fe, 0xb9, 0x7b, 0xa9, 0xdc, 0xfd, 0xcc, 0x1b, 0x4f);
    private static readonly Guid DredClassId = new(0x4a75bbc4, 0x9ff4, 0x4ad8, 0x9f, 0x18, 0xab, 0xae, 0x84, 0xdc, 0x5f, 0xf2);
    private readonly object _gate = new();
    private readonly GraphicsObjectRegistry _devices;
    private readonly GraphicsObjectRegistry _surfaces;
    private readonly NativeD3D12 _d3d12;
    private readonly NativeDxgi _dxgi;
    private readonly D3D12BackendOptions _options;
    private readonly Action<D3D12Backend>? _beforeLoaderRelease;
    private DisposeGate _disposeGate;
    private GCHandle _releaseFailureRoot;
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
    internal bool UseQueueSpecificCommonLayouts => _options.UseQueueSpecificCommonLayouts;

    internal D3D12Backend(in D3D12BackendOptions options = default) : this(options, null)
    {
    }

    internal D3D12Backend(in D3D12BackendOptions options, Action<D3D12Backend>? beforeLoaderRelease)
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
            _dxgi = NativeDxgi.GetApi((Silk.NET.Core.Contexts.INativeWindowSource)null !);
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

            throw new GraphicsException(GraphicsError.NativeFailure, "Direct3D 12 or DXGI could not be loaded.", innerException: exception);
        }
    }

    public bool TryEnumerateAdapters(in AdapterEnumerationOptions options, Span<AdapterInfo> destination, out int requiredCount)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        IDXGIFactory6* factory = EnsureFactory();
        GpuPreference preference = ToGpuPreference(options.Preference);
        List<AdapterInfo> adapters = [];
        for (uint index = 0;; index++)
        {
            IDXGIAdapter4* adapter = null;
            Guid iid = IDXGIAdapter4.Guid;
            int result = PresentationTrace.EnumAdapterByGpuPreference(factory, index, preference, &iid, (void**)&adapter);
            if (result == DxgiErrorNotFound)
                break;
            ThrowIfFailed(null, result, NativeOperationType.Ordinary, "IDXGIFactory6::EnumAdapterByGpuPreference");
            try
            {
                AdapterDesc3 native = default;
                ThrowIfFailed(null, PresentationTrace.GetDesc3(adapter, &native), NativeOperationType.Ordinary, "IDXGIAdapter4::GetDesc3");
                bool software = (native.Flags & AdapterFlag3.Software) != 0;
                if ((!software || options.IncludeSoftware) && SupportsDirect3D12(adapter))
                    adapters.Add(ToAdapterInfo(adapter, native));
            }
            finally
            {
                if (adapter is not null)
                    _ = PresentationTrace.Release(adapter);
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
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        lock (_gate)
        {
            _deviceCreated = true;
        }

        IDXGIAdapter4* adapter = SelectAdapter(desc.AdapterId, out AdapterInfo adapterInfo, out ID3D12Device10* native);
        try
        {
            D3D12Device device = D3D12Device.Create(this, adapter, native, adapterInfo, desc);
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
                _ = PresentationTrace.Release(adapter);
        }
    }

    public Surface CreateSurface(in SurfaceDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
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

    bool INativeValidationControl.CanEnableNativeValidation => !_deviceCreated;

    void INativeValidationControl.EnableNativeValidation()
    {
        lock (_gate)
        {
            if (_nativeValidationEnabled)
                return;
            EnableDebugFacilities(_options.Validation);
            _nativeValidationEnabled = true;
            if (_factory is not null)
            {
                _ = PresentationTrace.Release(_factory);
                _factory = null;
            }
        }
    }

    public void Dispose()
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
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
            // authority for the remainder of the process. Submission cannot allocate.
            KeepDeviceObjects();
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

            KeepDeviceObjects();
            return;
        }

        lock (_gate)
        {
            if (_factory is not null)
            {
                _ = PresentationTrace.Release(_factory);
                _factory = null;
            }

            if (_deviceFactory is not null)
            {
                _ = DeviceQueryTrace.Release(_deviceFactory);
                _deviceFactory = null;
            }

            if (_sdkConfiguration is not null)
            {
                ResourceTrace.FreeUnusedSDKs(_sdkConfiguration);
                _ = ResourceTrace.Release(_sdkConfiguration);
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
            KeepDeviceObjects();
            return;
        }

        _dxgi.Dispose();
        _d3d12.Dispose();
    }

    private void Register(GraphicsObject child)
    {
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
                break;
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
            if (_factory is not null)
                return _factory;
            uint flags = _debugLayerEnabled ? 1u : 0u;
            Guid iid = IDXGIFactory6.Guid;
            IDXGIFactory6* factory = null;
            ThrowIfFailed(null, _dxgi.CreateDXGIFactory2(flags, &iid, (void**)&factory), NativeOperationType.Ordinary, "CreateDXGIFactory2");
            _factory = factory;
            return factory;
        }
    }

    private bool SupportsDirect3D12(IDXGIAdapter4* adapter)
    {
        Guid iid = ID3D12Device.Guid;
        int result = DevicePipelineTrace.CreateDevice(_deviceFactory, (IUnknown*)adapter, D3DFeatureLevel.Level120, &iid, null);
        if (result == DxgiErrorUnsupported)
            return false;
        ThrowIfFailed(null, result, NativeOperationType.Ordinary, "D3D12CreateDevice(support query)");
        return true;
    }

    private bool TryCreateDevice(IDXGIAdapter4* adapter, out ID3D12Device10* device)
    {
        Guid iid = ID3D12Device10.Guid;
        ID3D12Device10* native = null;
        int result = DevicePipelineTrace.CreateDevice(_deviceFactory, (IUnknown*)adapter, D3DFeatureLevel.Level120, &iid, (void**)&native);
        device = native;
        if (result == DxgiErrorUnsupported)
            return false;
        ThrowIfFailed(null, result, NativeOperationType.Ordinary, "D3D12CreateDevice");
        return true;
    }

    private IDXGIAdapter4* SelectAdapter(AdapterId id, out AdapterInfo info, out ID3D12Device10* device)
    {
        IDXGIFactory6* factory = EnsureFactory();
        for (uint index = 0;; index++)
        {
            IDXGIAdapter4* adapter = null;
            Guid iid = IDXGIAdapter4.Guid;
            int result = PresentationTrace.EnumAdapterByGpuPreference(factory, index, GpuPreference.HighPerformance, &iid, (void**)&adapter);
            if (result == DxgiErrorNotFound)
                break;
            ThrowIfFailed(null, result, NativeOperationType.Ordinary, "IDXGIFactory6::EnumAdapterByGpuPreference");
            AdapterDesc3 native = default;
            try
            {
                ThrowIfFailed(null, PresentationTrace.GetDesc3(adapter, &native), NativeOperationType.Ordinary, "IDXGIAdapter4::GetDesc3");
                AdapterInfo candidate = ToAdapterInfo(adapter, native);
                if ((id.IsDefault || candidate.Id == id) && TryCreateDevice(adapter, out device))
                {
                    info = candidate;
                    return adapter;
                }
            }
            catch
            {
                _ = PresentationTrace.Release(adapter);
                throw;
            }

            _ = PresentationTrace.Release(adapter);
        }

        if (!id.IsDefault)
        {
            IDXGIAdapter4* warp = null;
            Guid warpIid = IDXGIAdapter4.Guid;
            int warpResult = PresentationTrace.EnumWarpAdapter(factory, &warpIid, (void**)&warp);
            ThrowIfFailed(null, warpResult, NativeOperationType.Ordinary, "IDXGIFactory4::EnumWarpAdapter");
            AdapterDesc3 native = default;
            try
            {
                ThrowIfFailed(null, PresentationTrace.GetDesc3(warp, &native), NativeOperationType.Ordinary, "IDXGIAdapter4::GetDesc3");
                AdapterInfo candidate = ToAdapterInfo(warp, native);
                if (!id.IsDefault && candidate.Id == id && TryCreateDevice(warp, out device))
                {
                    info = candidate;
                    return warp;
                }
            }
            catch
            {
                _ = PresentationTrace.Release(warp);
                throw;
            }

            _ = PresentationTrace.Release(warp);
        }

        throw new GraphicsException(
            GraphicsError.NativeFailure,
            id.IsDefault ? "No Direct3D 12 hardware adapter is available." : "The selected Direct3D 12 adapter is not available."
        );
    }

    private static GpuPreference ToGpuPreference(AdapterPreference preference) => preference switch
    {
        AdapterPreference.Unspecified => GpuPreference.Unspecified,
        AdapterPreference.HighPerformance => GpuPreference.HighPerformance,
        AdapterPreference.MinimumPower => GpuPreference.MinimumPower,
        _ => default,
    };
    private static AdapterInfo ToAdapterInfo(IDXGIAdapter4* adapter, in AdapterDesc3 native)
    {
        string name;
        fixed (char* description = native.Description)
            name = new string (description).TrimEnd('\0');
        bool software = (native.Flags & AdapterFlag3.Software) != 0;
        AdapterType type = software ? AdapterType.Cpu : native.DedicatedVideoMemory != 0 ? AdapterType.Discrete : AdapterType.Integrated;
        AdapterId id = new(native.AdapterLuid.Low, unchecked((ulong)(long)native.AdapterLuid.High));
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
            !software
        );
    }

    private static string ReadDriverVersion(IDXGIAdapter4* adapter)
    {
        Guid interfaceId = IDXGIDevice.Guid;
        long version = 0;
        int result = PresentationTrace.CheckInterfaceSupport(adapter, &interfaceId, &version);
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
        int result = DeviceQueryTrace.GetConfigurationInterface(_deviceFactory, &classId, &iid, (void**)&debug);
        if (result < 0 && result is not ENoInterface and not DxgiErrorSdkComponentMissing)
            ThrowIfFailed(null, result, NativeOperationType.Ordinary, "D3D12GetDebugInterface(ID3D12Debug1)");
        if (result >= 0 && debug is null)
        {
            throw new GraphicsException(GraphicsError.NativeFailure, "D3D12GetDebugInterface(ID3D12Debug1) succeeded without returning an interface.");
        }

        if (result >= 0 && debug is not null)
        {
            try
            {
                DeviceDebugTrace.EnableDebugLayer(debug);
                _debugLayerEnabled = true;
                if (!options.DisableGpuBasedValidation)
                {
                    DeviceDebugTrace.SetEnableGPUBasedValidation(debug, true);
                    _gpuBasedValidationEnabled = true;
                }

                if (!options.DisableSynchronizedQueueValidation)
                {
                    DeviceDebugTrace.SetEnableSynchronizedCommandQueueValidation(debug, true);
                    _synchronizedQueueValidationEnabled = true;
                }
            }
            finally
            {
                _ = DeviceDebugTrace.Release(debug);
            }
        }

        if (!options.DisableDred)
        {
            ID3D12DeviceRemovedExtendedDataSettings1* dred = null;
            classId = DredClassId;
            iid = ID3D12DeviceRemovedExtendedDataSettings1.Guid;
            result = DeviceQueryTrace.GetConfigurationInterface(_deviceFactory, &classId, &iid, (void**)&dred);
            if (result < 0 && result is not ENoInterface and not DxgiErrorSdkComponentMissing)
            {
                ThrowIfFailed(null, result, NativeOperationType.Ordinary, "D3D12GetDebugInterface(ID3D12DeviceRemovedExtendedDataSettings1)");
            }

            if (result >= 0 && dred is null)
            {
                throw new GraphicsException(
                    GraphicsError.NativeFailure,
                    "D3D12GetDebugInterface(ID3D12DeviceRemovedExtendedDataSettings1) " + "succeeded without returning an interface."
                );
            }

            if (result >= 0 && dred is not null)
            {
                try
                {
                    DeviceDebugTrace.SetAutoBreadcrumbsEnablement(dred, DredEnablement.ForcedOn);
                    DeviceDebugTrace.SetPageFaultEnablement(dred, DredEnablement.ForcedOn);
                    DeviceDebugTrace.SetBreadcrumbContextEnablement(dred, DredEnablement.ForcedOn);
                    _dredEnabled = true;
                }
                finally
                {
                    _ = DeviceDebugTrace.Release(dred);
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
            "D3D12GetInterface(ID3D12SDKConfiguration1)"
        );
        try
        {
            string sdkPath = Path.Combine(AppContext.BaseDirectory, "D3D12") + Path.DirectorySeparatorChar;
            iid = ID3D12DeviceFactory.Guid;
            ThrowIfFailed(
                null,
                ResourceTrace.CreateDeviceFactory(configuration, AgilitySdkVersion, sdkPath, &iid, (void**)&factory),
                NativeOperationType.Ordinary,
                "ID3D12SDKConfiguration1::CreateDeviceFactory"
            );
            if (factory is null)
            {
                throw new GraphicsException(GraphicsError.NativeFailure, "ID3D12SDKConfiguration1::CreateDeviceFactory succeeded without returning a factory.");
            }

            _sdkConfiguration = configuration;
            configuration = null;
            _deviceFactory = factory;
            factory = null;
        }
        finally
        {
            if (factory is not null)
                _ = DeviceQueryTrace.Release(factory);
            if (configuration is not null)
                _ = ResourceTrace.Release(configuration);
        }
    }

    private void KeepDeviceObjects()
    {
        if (!_releaseFailureRoot.IsAllocated)
            _releaseFailureRoot = GCHandle.Alloc(this);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Device RequireDevice(Device value, string parameterName) => (D3D12Device)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Queue RequireQueue(Queue value, string parameterName) => (D3D12Queue)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Heap RequireHeap(Heap value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12Heap)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Buffer RequireBuffer(Buffer value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12Buffer)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12TextureResource RequireTexture(Texture value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => value is D3D12SamplerFeedbackTexture feedback ? feedback.NativeResource : ((D3D12Texture)value).NativeResource;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Pipeline RequirePipeline(Pipeline value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12Pipeline)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12PipelineCache RequirePipelineCache(PipelineCache value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12PipelineCache)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12QueryPool RequireQueryPool(QueryPool value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12QueryPool)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12ExternalTimeline RequireTimeline(ExternalTimeline value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12ExternalTimeline)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12DescriptorTable RequireDescriptorTable(DescriptorTable value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12DescriptorTable)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12PersistentParameterBindings RequirePersistentParameterBindings(
        PersistentParameterBindings value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    ) => (D3D12PersistentParameterBindings)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12IndirectCommandLayout RequireIndirectCommandLayout(
        IndirectCommandLayout value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    ) => (D3D12IndirectCommandLayout)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12AccelerationStructure RequireAccelerationStructure(
        AccelerationStructure value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    ) => (D3D12AccelerationStructure)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12RayTracingShaderTable RequireRayTracingShaderTable(
        RayTracingShaderTable value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    ) => (D3D12RayTracingShaderTable)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12RayTracingPipeline RequireRayTracingPipeline(Pipeline value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12RayTracingPipeline)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12WorkGraphPipeline RequireWorkGraphPipeline(Pipeline value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12WorkGraphPipeline)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12RecordedBundle RequireBundle(RecordedBundle value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12RecordedBundle)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12SamplerFeedbackTexture RequireSamplerFeedbackTexture(
        SamplerFeedbackTexture value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null
    ) => (D3D12SamplerFeedbackTexture)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12SamplerFeedbackUav RequireSamplerFeedbackUav(SamplerFeedbackUav value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12SamplerFeedbackUav)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Surface RequireSurface(Surface value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12Surface)value;
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private D3D12Swapchain RequireSwapchain(Swapchain value, [CallerArgumentExpression(nameof(value))] string? parameterName = null) => (D3D12Swapchain)value;
    private sealed partial class D3D12Surface : Surface
    {
        private readonly D3D12Backend _backend;
        private readonly object _swapchainsGate = new();
        private readonly GraphicsObjectRegistry _swapchains;
        internal D3D12Surface(D3D12Backend backend, in SurfaceDesc desc) : base(desc.Type, desc.WindowHandle, desc.DisplayHandle, backend, desc.Label)
        {
            _backend = backend;
            _swapchains = new GraphicsObjectRegistry(_swapchainsGate);
        }

        internal void RegisterSwapchain(D3D12Swapchain swapchain)
        {
            _swapchains.Add(swapchain);
        }

        internal void UnregisterSwapchain(D3D12Swapchain swapchain)
        {
            _swapchains.Remove(swapchain);
        }

        internal override void Release(bool fromParent)
        {
            GraphicsObject? swapchains = _swapchains.CloseAndBuildDrainList(secondaryLink: true);
            while (swapchains is D3D12Swapchain swapchain)
            {
                swapchains = swapchain.SecondaryRegistryDrainNext;
                swapchain.SecondaryRegistryDrainNext = null;
                swapchain.DisposeFromParent();
                if (_swapchains.CompleteDrain(swapchain))
                {
                    swapchain.Device.RecordReleaseFailure(new GraphicsException(GraphicsError.NativeFailure, "A Swapchain did not detach during Surface teardown."));
                }
            }

            if (_swapchains.HasRetainedFailures)
                return;
            _backend.Unregister(this);
        }
    }
}
