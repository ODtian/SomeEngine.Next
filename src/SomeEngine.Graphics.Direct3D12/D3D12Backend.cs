using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using NativeD3D12 = Silk.NET.Direct3D12.D3D12;
using NativeDxgi = Silk.NET.DXGI.DXGI;

namespace SomeEngine.Graphics.Direct3D12;

public readonly record struct D3D12ValidationOptions(
    bool DisableGpuBasedValidation = false,
    bool DisableSynchronizedQueueValidation = false,
    bool DisableDred = false);

public readonly record struct D3D12BackendOptions(
    D3D12ValidationOptions Validation = default);

/// <summary>
/// The one Direct3D 12 receiver. It owns DXGI, D3D12, and every native child created through it.
/// </summary>
public sealed unsafe partial class D3D12Backend : IGraphicsBackend, INativeValidationControl
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint AgilitySdkVersion = 619;
    private const string AgilitySdkPath = @".\D3D12\";
    private static readonly Guid SdkConfigurationClassId =
        new(0x7cda6aca, 0xa03e, 0x49c8, 0x94, 0x58, 0x03, 0x34, 0xd2, 0x0e, 0x07, 0xce);

    private readonly object _gate = new();
    private readonly HashSet<GraphicsObject> _children = new(ReferenceEqualityComparer.Instance);
    private readonly NativeD3D12 _d3d12;
    private readonly NativeDxgi _dxgi;
    private readonly D3D12BackendOptions _options;

    private IDXGIFactory6* _factory;
    private bool _nativeValidationEnabled;
    private bool _debugLayerEnabled;
    private bool _gpuBasedValidationEnabled;
    private bool _synchronizedQueueValidationEnabled;
    private bool _dredEnabled;
    private bool _deviceCreated;
    private bool _disposed;

    public D3D12Backend(in D3D12BackendOptions options = default)
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("The Direct3D 12 backend requires Windows.");

        _options = options;
        try
        {
            _d3d12 = NativeD3D12.GetApi();
            _dxgi = NativeDxgi.GetApi((Silk.NET.Core.Contexts.INativeWindowSource)null!);
            SelectAgilitySdk();
        }
        catch (Exception exception)
        {
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
            NativeCall.ThrowIfFailed(result, "IDXGIFactory6::EnumAdapterByGpuPreference");

            try
            {
                AdapterDesc3 native = default;
                NativeCall.ThrowIfFailed(
                    adapter->GetDesc3(&native),
                    "IDXGIAdapter4::GetDesc3");

                bool software = (native.Flags & AdapterFlag3.Software) != 0;
                if ((!software || options.IncludeSoftware) && SupportsDirect3D12(adapter))
                    adapters.Add(ToAdapterInfo(adapter, native, software));
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
        if (!Enum.IsDefined(desc.RetirementType))
            throw new ArgumentOutOfRangeException(nameof(desc), "The RetirementType is unknown.");
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

        Span<bool> seenQueueTypes = stackalloc bool[3];
        foreach (ref readonly DeviceQueueDesc queue in desc.Queues)
        {
            int typeIndex = queue.Type switch
            {
                QueueType.Graphics => 0,
                QueueType.Compute => 1,
                QueueType.Copy => 2,
                _ => throw new ArgumentOutOfRangeException(nameof(desc), "A Queue type is unknown."),
            };
            if (seenQueueTypes[typeIndex])
                throw new ArgumentException("Each Queue type must have one consolidated description.", nameof(desc));
            seenQueueTypes[typeIndex] = true;
            if (queue.Count == 0)
                throw new ArgumentOutOfRangeException(nameof(desc), "A Queue count must be nonzero.");
            if (!float.IsFinite(queue.Priority) || queue.Priority is < 0f or > 1f)
                throw new ArgumentOutOfRangeException(nameof(desc), "A Queue priority must be finite and in [0, 1].");
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
        GraphicsObject[] children;
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
            children = [.. _children];
        }

        foreach (GraphicsObject child in children)
            child.DisposeFromParent();

        lock (_gate)
        {
            _children.Clear();
            if (_factory is not null)
            {
                _ = _factory->Release();
                _factory = null;
            }
        }

        try
        {
            _dxgi.Dispose();
        }
        catch
        {
        }

        try
        {
            _d3d12.Dispose();
        }
        catch
        {
        }
    }

    private void Register(GraphicsObject child)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            _children.Add(child);
        }
    }

    private void Unregister(GraphicsObject child)
    {
        lock (_gate)
            _children.Remove(child);
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
            NativeCall.ThrowIfFailed(
                _dxgi.CreateDXGIFactory2(flags, &iid, (void**)&factory),
                "CreateDXGIFactory2");
            _factory = factory;
            return factory;
        }
    }

    private bool SupportsDirect3D12(IDXGIAdapter4* adapter)
    {
        ID3D12Device* device = null;
        Guid iid = ID3D12Device.Guid;
        int result = _d3d12.CreateDevice(
            (IUnknown*)adapter,
            D3DFeatureLevel.Level120,
            &iid,
            (void**)&device);
        if (device is not null)
            _ = device->Release();
        return result >= 0;
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
            NativeCall.ThrowIfFailed(result, "IDXGIFactory6::EnumAdapterByGpuPreference");

            AdapterDesc3 native = default;
            try
            {
                NativeCall.ThrowIfFailed(adapter->GetDesc3(&native), "IDXGIAdapter4::GetDesc3");
                AdapterInfo candidate = ToAdapterInfo(adapter, native, software: false);
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

        IDXGIAdapter4* warp = null;
        Guid warpIid = IDXGIAdapter4.Guid;
        int warpResult = factory->EnumWarpAdapter(&warpIid, (void**)&warp);
        if (warpResult >= 0)
        {
            AdapterDesc3 native = default;
            try
            {
                NativeCall.ThrowIfFailed(warp->GetDesc3(&native), "IDXGIAdapter4::GetDesc3");
                AdapterInfo candidate = ToAdapterInfo(warp, native, software: true);
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

    private static AdapterInfo ToAdapterInfo(IDXGIAdapter4* adapter, in AdapterDesc3 native, bool software)
    {
        string name;
        fixed (char* description = native.Description)
            name = new string(description).TrimEnd('\0');

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
        if (result < 0)
            return "unavailable";

        ulong packed = unchecked((ulong)version);
        uint high = (uint)(packed >> 32);
        uint low = (uint)packed;
        return $"{high >> 16}.{high & 0xFFFF}.{low >> 16}.{low & 0xFFFF}";
    }

    private void EnableDebugFacilities(in D3D12ValidationOptions options)
    {
        ID3D12Debug1* debug = null;
        Guid iid = ID3D12Debug1.Guid;
        int result = _d3d12.GetDebugInterface(&iid, (void**)&debug);
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
            iid = ID3D12DeviceRemovedExtendedDataSettings1.Guid;
            result = _d3d12.GetDebugInterface(&iid, (void**)&dred);
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

    private void SelectAgilitySdk()
    {
        ID3D12SDKConfiguration* configuration = null;
        Guid classId = SdkConfigurationClassId;
        Guid iid = ID3D12SDKConfiguration.Guid;
        NativeCall.ThrowIfFailed(
            _d3d12.GetInterface(&classId, &iid, (void**)&configuration),
            "D3D12GetInterface(ID3D12SDKConfiguration)");
        try
        {
            NativeCall.ThrowIfFailed(
                configuration->SetSDKVersion(AgilitySdkVersion, AgilitySdkPath),
                "ID3D12SDKConfiguration::SetSDKVersion");
        }
        finally
        {
            _ = configuration->Release();
        }
    }

    private void ThrowIfDisposed() =>
        ObjectDisposedException.ThrowIf(_disposed, this);

    private sealed partial class D3D12Surface : Surface
    {
        private readonly D3D12Backend _backend;
        private readonly object _swapchainsGate = new();
        private readonly HashSet<D3D12Swapchain> _swapchains =
            new(ReferenceEqualityComparer.Instance);

        internal D3D12Surface(D3D12Backend backend, in SurfaceDesc desc)
            : base(
                desc.Type,
                desc.WindowHandle,
                desc.DisplayHandle,
                backend,
                desc.Label)
        {
            _backend = backend;
        }

        internal void RegisterSwapchain(D3D12Swapchain swapchain)
        {
            lock (_swapchainsGate)
            {
                ThrowIfDisposed();
                _swapchains.Add(swapchain);
            }
        }

        internal void UnregisterSwapchain(D3D12Swapchain swapchain)
        {
            lock (_swapchainsGate)
                _swapchains.Remove(swapchain);
        }

        internal override void Release(bool fromParent)
        {
            D3D12Swapchain[] swapchains;
            lock (_swapchainsGate)
                swapchains = [.. _swapchains];
            foreach (D3D12Swapchain swapchain in swapchains)
                swapchain.DisposeFromParent();
            lock (_swapchainsGate)
                _swapchains.Clear();
            _backend.Unregister(this);
        }
    }
}

internal static class NativeCall
{
    internal static void ThrowIfFailed(int result, string operation)
    {
        if (result >= 0)
            return;

        throw new GraphicsException(
            GraphicsError.NativeFailure,
            $"{operation} failed with HRESULT 0x{unchecked((uint)result):X8}.",
            result);
    }
}
