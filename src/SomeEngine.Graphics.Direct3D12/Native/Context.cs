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

        if (options.EnableDebugLayer)
        {
            try
            {
                using ID3D12Debug1 debug = D3D12.D3D12GetDebugInterface<ID3D12Debug1>();
                debug.EnableDebugLayer();
                debug.SetEnableGPUBasedValidation(options.EnableGpuValidation);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("The D3D12 debug layer was requested but could not be enabled.", exception);
            }
        }

        IDXGIFactory4? factory = null;
        IDXGIAdapter1? adapter = null;
        ID3D12Device? device = null;
        NativeQueue? graphics = null;
        NativeQueue? compute = null;
        NativeQueue? copy = null;
        ID3D12InfoQueue? infoQueue = null;
        try
        {
            factory = DXGI.CreateDXGIFactory2<IDXGIFactory4>(options.EnableDebugLayer);
            (adapter, device) = options.UseWarpAdapter
                ? CreateWarpDevice(factory)
                : CreateHardwareDevice(factory);

            graphics = NativeQueue.Create(device, QueueType.Graphics);
            compute = NativeQueue.Create(device, QueueType.Compute);
            copy = NativeQueue.Create(device, QueueType.Copy);

            if (options.EnableDebugLayer)
            {
                infoQueue = device.QueryInterface<ID3D12InfoQueue>();
            }

            AdapterDescription1 description = adapter.Description1;
            bool hardware = (description.Flags & AdapterFlags.Software) == 0;
            DeviceInfo info = new(
                description.Description.TrimEnd('\0'),
                BackendKind.Direct3D12,
                hardware,
                description.VendorId,
                description.DeviceId);

            ResourceHeapTier heapTier = QueryResourceHeapTier(device);
            ShaderModel highestShaderModel = QueryHighestShaderModel(device);
            DeviceCompilationSnapshot compilation = new(
                semanticGeneration: 1,
                heapTier,
                [QueueType.Graphics, QueueType.Compute, QueueType.Copy],
                supportsEnhancedBarriers: false,
                supportsAsyncCompute: true,
                supportsCopyQueue: true);

            return new NativeContext(factory, adapter, device, graphics, compute, copy, infoQueue, highestShaderModel, info, compilation);
        }
        catch
        {
            infoQueue?.Dispose();
            copy?.Dispose();
            compute?.Dispose();
            graphics?.Dispose();
            device?.Dispose();
            adapter?.Dispose();
            factory?.Dispose();
            throw;
        }
    }

    public NativeQueue GetQueue(QueueType queue) => queue switch
    {
        QueueType.Graphics => Graphics,
        QueueType.Compute => Compute,
        QueueType.Copy => Copy,
        _ => throw new ArgumentOutOfRangeException(nameof(queue)),
    };

    private static (IDXGIAdapter1 Adapter, ID3D12Device Device) CreateWarpDevice(IDXGIFactory4 factory)
    {
        IDXGIAdapter1 adapter = factory.EnumWarpAdapter<IDXGIAdapter1>();
        try
        {
            return (adapter, D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_0));
        }
        catch
        {
            adapter.Dispose();
            throw;
        }
    }

    private static (IDXGIAdapter1 Adapter, ID3D12Device Device) CreateHardwareDevice(IDXGIFactory4 factory)
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

                ID3D12Device device = D3D12.D3D12CreateDevice<ID3D12Device>(adapter, FeatureLevel.Level_12_0);
                return (adapter, device);
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

        throw new PlatformNotSupportedException("No Direct3D 12 feature-level 12.0 hardware adapter is available. Use WARP explicitly for software execution.");
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
