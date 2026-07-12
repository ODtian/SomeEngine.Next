using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D12;
using Vortice.Direct3D12.Debug;
using Vortice.DXGI;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed class NativeContext : IDisposable
{
    private bool _disposed;

    private NativeContext(
        IDXGIFactory4 factory,
        IDXGIAdapter1 adapter,
        ID3D12Device device,
        NativeQueue graphics,
        NativeQueue compute,
        NativeQueue copy,
        ID3D12InfoQueue? infoQueue,
        ShaderModel highestShaderModel,
        DeviceInfo info,
        DeviceCompilationSnapshot compilation)
    {
        Factory = factory;
        Adapter = adapter;
        Device = device;
        Graphics = graphics;
        Compute = compute;
        Copy = copy;
        InfoQueue = infoQueue;
        DiagnosticQueue = infoQueue is null ? null : new NativeDiagnosticQueue(infoQueue);
        HighestShaderModel = highestShaderModel;
        Info = info;
        Compilation = compilation;
    }

    public IDXGIFactory4 Factory { get; }
    public IDXGIAdapter1 Adapter { get; }
    public ID3D12Device Device { get; }
    public NativeQueue Graphics { get; }
    public NativeQueue Compute { get; }
    public NativeQueue Copy { get; }
    public ID3D12InfoQueue? InfoQueue { get; }
    private NativeDiagnosticQueue? DiagnosticQueue { get; }
    public ShaderModel HighestShaderModel { get; }
    public DeviceInfo Info { get; }
    public DeviceCompilationSnapshot Compilation { get; }

    public GraphicsDiagnostic[] DrainDiagnostics() => DiagnosticQueue?.Drain() ?? [];

    public static NativeContext Create(Options options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Direct3D 12 requires Windows.");
        }

        EnableDebugValidation(options);
        var resources = CreateNativeResources(options);
        try
        {
            DeviceInfo info = CreateDeviceInfo(resources.Adapter, resources.ShaderModel, options.EnableDebugLayer);
            DeviceCompilationSnapshot compilation = CreateCompilationSnapshot(resources.Device);
            return new NativeContext(
                resources.Factory,
                resources.Adapter,
                resources.Device,
                resources.Graphics,
                resources.Compute,
                resources.Copy,
                resources.InfoQueue,
                resources.ShaderModel,
                info,
                compilation);
        }
        catch
        {
            DisposeNativeResources(resources);
            throw;
        }
    }

    private static void EnableDebugValidation(Options options)
    {
        if (!options.EnableDebugLayer) return;
        try
        {
            using ID3D12Debug1 debug = D3D12.D3D12GetDebugInterface<ID3D12Debug1>();
            debug.EnableDebugLayer();
            debug.SetEnableGPUBasedValidation(options.EnableGpuValidation);
            using ID3D12DeviceRemovedExtendedDataSettings1 dred =
                D3D12.D3D12GetDebugInterface<ID3D12DeviceRemovedExtendedDataSettings1>();
            dred.SetAutoBreadcrumbsEnablement(DredEnablement.ForcedOn);
            dred.SetPageFaultEnablement(DredEnablement.ForcedOn);
            dred.SetBreadcrumbContextEnablement(DredEnablement.ForcedOn);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("The D3D12 debug layer was requested but could not be enabled.", exception);
        }
    }

    private static (
        IDXGIFactory4 Factory,
        IDXGIAdapter1 Adapter,
        ID3D12Device Device,
        NativeQueue Graphics,
        NativeQueue Compute,
        NativeQueue Copy,
        ID3D12InfoQueue? InfoQueue,
        ShaderModel ShaderModel) CreateNativeResources(Options options)
    {
        IDXGIFactory4? factory = null;
        IDXGIAdapter1? adapter = null;
        ID3D12Device? device = null;
        var queues = (Graphics: (NativeQueue?)null, Compute: (NativeQueue?)null, Copy: (NativeQueue?)null);
        ID3D12InfoQueue? infoQueue = null;
        try
        {
            factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(options.EnableDebugLayer);
            var selection = options.UseWarpAdapter ? CreateWarpDevice(factory) : CreateHardwareDevice(factory);
            adapter = selection.Adapter;
            device = selection.Device;
            queues = CreateNativeQueues(device);
            infoQueue = options.EnableDebugLayer ? device.QueryInterface<ID3D12InfoQueue>() : null;
            return (factory, adapter, device, queues.Graphics!, queues.Compute!, queues.Copy!, infoQueue, selection.HighestShaderModel);
        }
        catch
        {
            infoQueue?.Dispose();
            queues.Copy?.Dispose();
            queues.Compute?.Dispose();
            queues.Graphics?.Dispose();
            device?.Dispose();
            adapter?.Dispose();
            factory?.Dispose();
            throw;
        }
    }

    private static (NativeQueue Graphics, NativeQueue Compute, NativeQueue Copy) CreateNativeQueues(
        ID3D12Device device)
    {
        NativeQueue? graphics = null;
        NativeQueue? compute = null;
        try
        {
            graphics = NativeQueue.Create(device, QueueType.Graphics);
            compute = NativeQueue.Create(device, QueueType.Compute);
            NativeQueue copy = NativeQueue.Create(device, QueueType.Copy);
            return (graphics, compute, copy);
        }
        catch
        {
            compute?.Dispose();
            graphics?.Dispose();
            throw;
        }
    }

    private static void DisposeNativeResources((
        IDXGIFactory4 Factory,
        IDXGIAdapter1 Adapter,
        ID3D12Device Device,
        NativeQueue Graphics,
        NativeQueue Compute,
        NativeQueue Copy,
        ID3D12InfoQueue? InfoQueue,
        ShaderModel ShaderModel) resources)
    {
        resources.InfoQueue?.Dispose();
        resources.Copy.Dispose();
        resources.Compute.Dispose();
        resources.Graphics.Dispose();
        resources.Device.Dispose();
        resources.Adapter.Dispose();
        resources.Factory.Dispose();
    }

    private static DeviceInfo CreateDeviceInfo(IDXGIAdapter1 adapter, ShaderModel shaderModel, bool debugLayer)
    {
        AdapterDescription1 description = adapter.Description1;
        return new DeviceInfo(
            description.Description.TrimEnd('\0'),
            BackendKind.Direct3D12,
            (description.Flags & AdapterFlags.Software) == 0,
            description.VendorId,
            description.DeviceId,
            QueryDriverVersion(adapter),
            $"D3D12 FL12_0 / SM {DxilProgramInfo.Format(shaderModel)}",
            debugLayer);
    }

    private static DeviceCompilationSnapshot CreateCompilationSnapshot(ID3D12Device device) => new(
        semanticGeneration: 1,
        QueryResourceHeapTier(device),
        [QueueType.Graphics, QueueType.Compute, QueueType.Copy],
        supportsEnhancedBarriers: false,
        supportsAsyncCompute: true,
        supportsCopyQueue: true,
        supportsBindless: false);

    public NativeQueue GetQueue(QueueType queue) => queue switch
    {
        QueueType.Graphics => Graphics,
        QueueType.Compute => Compute,
        QueueType.Copy => Copy,
        _ => throw new ArgumentOutOfRangeException(nameof(queue)),
    };

    private static (IDXGIAdapter1 Adapter, ID3D12Device Device, ShaderModel HighestShaderModel) CreateWarpDevice(
        IDXGIFactory4 factory)
    {
        IDXGIAdapter1 adapter = factory.EnumWarpAdapter<IDXGIAdapter1>();
        ID3D12Device? device = null;
        try
        {
            device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_0);
            ShaderModel highestShaderModel = QueryHighestShaderModel(device);
            RequireBaselineShaderModel(highestShaderModel, adapter.Description1.Description);
            return (adapter, device, highestShaderModel);
        }
        catch
        {
            device?.Dispose();
            adapter.Dispose();
            throw;
        }
    }

    private static (IDXGIAdapter1 Adapter, ID3D12Device Device, ShaderModel HighestShaderModel) CreateHardwareDevice(
        IDXGIFactory4 factory)
    {
        for (uint index = 0; ; index++)
        {
            IDXGIAdapter1? adapter = null;
            try
            {
                factory.EnumAdapters1(index, out adapter).CheckError();
                if ((adapter.Description1.Flags & AdapterFlags.Software) != 0)
                {
                    adapter.Dispose();
                    continue;
                }

                return CreateHardwareDevice(adapter);
            }
            catch (SharpGen.Runtime.SharpGenException) when (adapter is null)
            {
                break;
            }
            catch
            {
                adapter?.Dispose();
            }
        }

        throw new PlatformNotSupportedException(
            "No hardware adapter satisfies the Direct3D 12 FL12_0 and Shader Model 6.2 baseline. " +
            "Use WARP explicitly for software execution.");
    }

    private static (IDXGIAdapter1 Adapter, ID3D12Device Device, ShaderModel HighestShaderModel) CreateHardwareDevice(
        IDXGIAdapter1 adapter)
    {
        ID3D12Device device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_0);
        try
        {
            ShaderModel shaderModel = QueryHighestShaderModel(device);
            RequireBaselineShaderModel(shaderModel, adapter.Description1.Description);
            return (adapter, device, shaderModel);
        }
        catch
        {
            device.Dispose();
            throw;
        }
    }

    private static ResourceHeapTier QueryResourceHeapTier(ID3D12Device device)
    {
        FeatureDataD3D12Options data = device.CheckFeatureSupport<FeatureDataD3D12Options>(Vortice.Direct3D12.Feature.Options);
        return data.ResourceHeapTier == Vortice.Direct3D12.ResourceHeapTier.Tier2
            ? ResourceHeapTier.Tier2
            : ResourceHeapTier.Tier1;
    }

    private static ShaderModel QueryHighestShaderModel(ID3D12Device device)
    {
        ShaderModel[] candidates =
        [
            ShaderModel.Model6_9,
            ShaderModel.Model6_8,
            ShaderModel.Model6_7,
            ShaderModel.Model6_6,
            ShaderModel.Model6_5,
            ShaderModel.Model6_4,
            ShaderModel.Model6_3,
            ShaderModel.Model6_2,
            ShaderModel.Model6_1,
            ShaderModel.Model6_0,
            ShaderModel.Model5_1,
        ];

        foreach (ShaderModel candidate in candidates)
        {
            FeatureDataShaderModel data = new() { HighestShaderModel = candidate };
            if (device.CheckFeatureSupport(Vortice.Direct3D12.Feature.ShaderModel, ref data))
            {
                return data.HighestShaderModel;
            }
        }

        throw new PlatformNotSupportedException("The D3D12 device did not report support for a known shader model.");
    }

    private static void RequireBaselineShaderModel(ShaderModel highestShaderModel, string adapterName)
    {
        if (highestShaderModel >= ShaderModel.Model6_2) return;
        throw new PlatformNotSupportedException(
            $"D3D12 adapter '{adapterName.TrimEnd('\0')}' reports Shader Model " +
            $"{DxilProgramInfo.Format(highestShaderModel)}; SomeEngine requires Shader Model 6.2 or newer.");
    }

    private static string QueryDriverVersion(IDXGIAdapter1 adapter)
    {
        try
        {
            adapter.CheckInterfaceSupport(typeof(ID3D12Device).GUID, out long version).CheckError();
            return $"0x{unchecked((ulong)version):X16}";
        }
        catch (Exception exception)
        {
            return $"unavailable(0x{unchecked((uint)exception.HResult):X8})";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        InfoQueue?.Dispose();
        Copy.Dispose();
        Compute.Dispose();
        Graphics.Dispose();
        Device.Dispose();
        Adapter.Dispose();
        Factory.Dispose();
    }
}

internal sealed class NativeQueue : IDisposable
{
    private NativeQueue(QueueType type, ID3D12CommandQueue queue, ID3D12Fence fence)
    {
        Type = type;
        Queue = queue;
        Fence = fence;
    }

    public QueueType Type { get; }
    public ID3D12CommandQueue Queue { get; }
    public ID3D12Fence Fence { get; }
    public AutoResetEvent CompletionEvent { get; } = new(false);
    public object SubmissionGate { get; } = new();
    public object WaitGate { get; } = new();
    public ulong SubmittedValue { get; set; }

    public static NativeQueue Create(ID3D12Device device, QueueType type)
    {
        CommandListType nativeType = Mappings.CommandListType(type);
        ID3D12CommandQueue queue = device.CreateCommandQueue(nativeType, CommandQueuePriority.Normal, CommandQueueFlags.None, 0);
        ID3D12Fence fence = device.CreateFence(0, FenceFlags.None);
        return new NativeQueue(type, queue, fence);
    }

    public void Dispose()
    {
        CompletionEvent.Dispose();
        Fence.Dispose();
        Queue.Dispose();
    }
}
