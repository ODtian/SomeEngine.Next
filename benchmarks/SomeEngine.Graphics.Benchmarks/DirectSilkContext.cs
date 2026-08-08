using System.Diagnostics;
using System.Runtime.InteropServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.DXGI;
using NativeD3D12 = Silk.NET.Direct3D12.D3D12;
using NativeDxgi = Silk.NET.DXGI.DXGI;
using NativeFormat = Silk.NET.DXGI.Format;
using NativeHeapFlags = Silk.NET.Direct3D12.HeapFlags;
using NativeRange = Silk.NET.Direct3D12.Range;
using NativeResource = Silk.NET.Direct3D12.ID3D12Resource;
using NativeResourceDesc = Silk.NET.Direct3D12.ResourceDesc;
using NativeTextureLayout = Silk.NET.Direct3D12.TextureLayout;

namespace SomeEngine.Graphics.Benchmarks;

internal sealed unsafe class DirectSilkContext : IDisposable
{
    private const int DxgiErrorNotFound = unchecked((int)0x887A0002);
    private const uint AgilitySdkVersion = 619;
    private const string AgilitySdkPath = @".\D3D12\";
    private static readonly Guid SdkConfigurationClassId =
        new(0x7cda6aca, 0xa03e, 0x49c8, 0x94, 0x58, 0x03, 0x34, 0xd2, 0x0e, 0x07, 0xce);

    private readonly NativeD3D12 _d3d12;
    private readonly NativeDxgi _dxgi;
    private IDXGIFactory6* _factory;
    private IDXGIAdapter4* _adapter;
    private ID3D12Device* _device;
    private ID3D12RootSignature* _graphicsRoot;
    private ID3D12RootSignature* _computeRoot;
    private ID3D12PipelineState* _graphicsPipeline;
    private ID3D12PipelineState* _computePipeline;
    private int _disposed;

    internal DirectSilkContext(
        AdapterId adapterId,
        byte[] vertexCode,
        byte[] pixelCode,
        byte[] computeCode)
    {
        _d3d12 = NativeD3D12.GetApi();
        _dxgi = NativeDxgi.GetApi((Silk.NET.Core.Contexts.INativeWindowSource)null!);
        try
        {
            SelectAgilitySdk();
            Guid factoryIid = IDXGIFactory6.Guid;
            IDXGIFactory6* factory = null;
            Check(_dxgi.CreateDXGIFactory2(0, &factoryIid, (void**)&factory), "CreateDXGIFactory2");
            _factory = factory;
            _adapter = SelectAdapter(adapterId, out AdapterInfo adapter);
            Adapter = adapter;
            Guid deviceIid = ID3D12Device.Guid;
            ID3D12Device* device = null;
            Check(
                _d3d12.CreateDevice((IUnknown*)_adapter, D3DFeatureLevel.Level120, &deviceIid, (void**)&device),
                "D3D12CreateDevice");
            _device = device;
            FeatureDataD3D12Options12 options12 = default;
            Check(
                _device->CheckFeatureSupport(
                    Silk.NET.Direct3D12.Feature.D3D12Options12,
                    &options12,
                    (uint)sizeof(FeatureDataD3D12Options12)),
                "ID3D12Device::CheckFeatureSupport(D3D12_OPTIONS12)");
            EnhancedBarriers = options12.EnhancedBarriersSupported;
            Graphics = new NativeQueue(_device, CommandListType.Direct);
            Compute = new NativeQueue(_device, CommandListType.Compute);
            Copy = new NativeQueue(_device, CommandListType.Copy);
            _graphicsRoot = CreateGraphicsRootSignature();
            _computeRoot = CreateEmptyComputeRootSignature();
            _graphicsPipeline = CreateGraphicsPipeline(vertexCode, pixelCode);
            _computePipeline = CreateComputePipeline(computeCode);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    internal AdapterInfo Adapter { get; }
    internal ID3D12Device* Device => _device;
    internal IDXGIFactory6* Factory => _factory;
    internal ID3D12RootSignature* GraphicsRoot => _graphicsRoot;
    internal ID3D12RootSignature* ComputeRoot => _computeRoot;
    internal ID3D12PipelineState* GraphicsPipeline => _graphicsPipeline;
    internal ID3D12PipelineState* ComputePipeline => _computePipeline;
    internal bool EnhancedBarriers { get; }
    internal NativeQueue Graphics { get; private set; } = null!;
    internal NativeQueue Compute { get; private set; } = null!;
    internal NativeQueue Copy { get; private set; } = null!;

    internal NativeResource* CreateBuffer(
        ulong size,
        HeapType heapType,
        ResourceStates initialState,
        ResourceFlags flags = ResourceFlags.None)
    {
        HeapProperties properties = new(heapType, CpuPageProperty.Unknown, MemoryPool.Unknown, 1, 1);
        NativeResourceDesc description = new(
            ResourceDimension.Buffer,
            0,
            size,
            1,
            1,
            1,
            NativeFormat.FormatUnknown,
            new SampleDesc(1, 0),
            NativeTextureLayout.LayoutRowMajor,
            flags);
        NativeResource* result = null;
        Guid iid = NativeResource.Guid;
        Check(
            _device->CreateCommittedResource(
                &properties,
                NativeHeapFlags.None,
                &description,
                initialState,
                null,
                &iid,
                (void**)&result),
            "ID3D12Device::CreateCommittedResource(buffer)");
        return result;
    }

    internal NativeResource* CreateTargetTexture()
    {
        HeapProperties properties = new(HeapType.Default, CpuPageProperty.Unknown, MemoryPool.Unknown, 1, 1);
        NativeResourceDesc description = new(
            ResourceDimension.Texture2D,
            0,
            FixedGraphicsProtocol.RenderWidth,
            FixedGraphicsProtocol.RenderHeight,
            1,
            1,
            NativeFormat.FormatR8G8B8A8Unorm,
            new SampleDesc(1, 0),
            NativeTextureLayout.LayoutUnknown,
            ResourceFlags.AllowRenderTarget);
        NativeResource* result = null;
        Guid iid = NativeResource.Guid;
        Check(
            _device->CreateCommittedResource(
                &properties,
                NativeHeapFlags.None,
                &description,
                ResourceStates.Common,
                null,
                &iid,
                (void**)&result),
            "ID3D12Device::CreateCommittedResource(texture)");
        return result;
    }

    internal ID3D12DescriptorHeap* CreateRtvHeap(uint count)
    {
        DescriptorHeapDesc description = new(DescriptorHeapType.Rtv, count, DescriptorHeapFlags.None, 1);
        ID3D12DescriptorHeap* result = null;
        Guid iid = ID3D12DescriptorHeap.Guid;
        Check(_device->CreateDescriptorHeap(&description, &iid, (void**)&result), "ID3D12Device::CreateDescriptorHeap");
        return result;
    }

    internal CpuDescriptorHandle CreateRtv(NativeResource* texture, ID3D12DescriptorHeap* heap, uint index = 0)
    {
        CpuDescriptorHandle start = heap->GetCPUDescriptorHandleForHeapStart();
        uint increment = _device->GetDescriptorHandleIncrementSize(DescriptorHeapType.Rtv);
        CpuDescriptorHandle handle = new(start.Ptr + checked((nuint)(index * increment)));
        RenderTargetViewDesc description = new(
            NativeFormat.FormatR8G8B8A8Unorm,
            RtvDimension.Texture2D,
            texture2D: new Tex2DRtv(0, 0));
        _device->CreateRenderTargetView(texture, &description, handle);
        return handle;
    }

    internal ID3D12QueryHeap* CreateTimestampHeap(CommandListType type, uint count)
    {
        QueryHeapDesc description = new(
            type == CommandListType.Copy ? QueryHeapType.CopyQueueTimestamp : QueryHeapType.Timestamp,
            count,
            1);
        ID3D12QueryHeap* result = null;
        Guid iid = ID3D12QueryHeap.Guid;
        Check(_device->CreateQueryHeap(&description, &iid, (void**)&result), "ID3D12Device::CreateQueryHeap");
        return result;
    }

    internal static byte* MapWrite(NativeResource* resource)
    {
        NativeRange read = default;
        void* pointer = null;
        Check(resource->Map(0, &read, &pointer), "ID3D12Resource::Map(write)");
        return (byte*)pointer;
    }

    internal static byte* MapRead(NativeResource* resource, nuint size)
    {
        NativeRange read = new() { Begin = 0, End = size };
        void* pointer = null;
        Check(resource->Map(0, &read, &pointer), "ID3D12Resource::Map(read)");
        return (byte*)pointer;
    }

    internal static ResourceBarrier Transition(
        NativeResource* resource,
        ResourceStates before,
        ResourceStates after,
        ResourceBarrierFlags flags = ResourceBarrierFlags.None)
    {
        ResourceBarrier result = new()
        {
            Type = ResourceBarrierType.Transition,
            Flags = flags,
        };
        result.Transition = new ResourceTransitionBarrier(resource, uint.MaxValue, before, after);
        return result;
    }

    internal static ResourceBarrier UavBarrier()
    {
        ResourceBarrier result = new()
        {
            Type = ResourceBarrierType.Uav,
            Flags = ResourceBarrierFlags.None,
        };
        result.UAV = new ResourceUavBarrier(null);
        return result;
    }

    internal static void Check(int result, string operation)
    {
        if (result < 0)
            throw new COMException($"{operation} failed with HRESULT 0x{unchecked((uint)result):X8}.", result);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        Release(_graphicsPipeline);
        _graphicsPipeline = null;
        Release(_computePipeline);
        _computePipeline = null;
        Release(_graphicsRoot);
        _graphicsRoot = null;
        Release(_computeRoot);
        _computeRoot = null;
        Graphics?.Dispose();
        Compute?.Dispose();
        Copy?.Dispose();
        Release(_device);
        _device = null;
        Release(_adapter);
        _adapter = null;
        Release(_factory);
        _factory = null;
        try { _dxgi.Dispose(); } catch { }
        try { _d3d12.Dispose(); } catch { }
    }

    internal static void Release<T>(T* pointer) where T : unmanaged
    {
        if (pointer is not null)
            _ = ((IUnknown*)pointer)->Release();
    }

    private void SelectAgilitySdk()
    {
        ID3D12SDKConfiguration* configuration = null;
        Guid classId = SdkConfigurationClassId;
        Guid iid = ID3D12SDKConfiguration.Guid;
        Check(
            _d3d12.GetInterface(&classId, &iid, (void**)&configuration),
            "D3D12GetInterface(ID3D12SDKConfiguration)");
        try
        {
            Check(configuration->SetSDKVersion(AgilitySdkVersion, AgilitySdkPath), "ID3D12SDKConfiguration::SetSDKVersion");
        }
        finally
        {
            Release(configuration);
        }
    }

    private IDXGIAdapter4* SelectAdapter(AdapterId id, out AdapterInfo info)
    {
        for (uint index = 0; ; index++)
        {
            IDXGIAdapter4* adapter = null;
            Guid iid = IDXGIAdapter4.Guid;
            int result = _factory->EnumAdapterByGpuPreference(index, GpuPreference.HighPerformance, &iid, (void**)&adapter);
            if (result == DxgiErrorNotFound)
                break;
            Check(result, "IDXGIFactory6::EnumAdapterByGpuPreference");
            AdapterDesc3 description = default;
            Check(adapter->GetDesc3(&description), "IDXGIAdapter4::GetDesc3");
            AdapterInfo candidate = ToAdapterInfo(adapter, description, (description.Flags & AdapterFlag3.Software) != 0);
            if (candidate.Id == id && SupportsD3D12(adapter))
            {
                info = candidate;
                return adapter;
            }
            Release(adapter);
        }

        IDXGIAdapter4* warp = null;
        Guid warpIid = IDXGIAdapter4.Guid;
        Check(_factory->EnumWarpAdapter(&warpIid, (void**)&warp), "IDXGIFactory4::EnumWarpAdapter");
        AdapterDesc3 warpDescription = default;
        Check(warp->GetDesc3(&warpDescription), "IDXGIAdapter4::GetDesc3(WARP)");
        AdapterInfo warpInfo = ToAdapterInfo(warp, warpDescription, software: true);
        if (warpInfo.Id == id && SupportsD3D12(warp))
        {
            info = warpInfo;
            return warp;
        }
        Release(warp);
        throw new NotSupportedException("The selected Direct Silk adapter is unavailable.");
    }

    private bool SupportsD3D12(IDXGIAdapter4* adapter)
    {
        Guid iid = ID3D12Device.Guid;
        return _d3d12.CreateDevice((IUnknown*)adapter, D3DFeatureLevel.Level120, &iid, null) >= 0;
    }

    private static AdapterInfo ToAdapterInfo(IDXGIAdapter4* adapter, in AdapterDesc3 native, bool software)
    {
        string name;
        fixed (char* description = native.Description)
            name = new string(description).TrimEnd('\0');
        return new AdapterInfo(
            new AdapterId(native.AdapterLuid.Low, unchecked((ulong)(long)native.AdapterLuid.High)),
            software ? AdapterType.Cpu : native.DedicatedVideoMemory != 0 ? AdapterType.Discrete : AdapterType.Integrated,
            name,
            native.VendorId,
            native.DeviceId,
            native.DedicatedVideoMemory,
            native.DedicatedSystemMemory,
            native.SharedSystemMemory,
            ReadDriverVersion(adapter),
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

    private ID3D12RootSignature* CreateGraphicsRootSignature()
    {
        RootParameter1 parameter = new(
            RootParameterType.TypeCbv,
            shaderVisibility: ShaderVisibility.All,
            descriptor: new RootDescriptor1(0, 0, RootDescriptorFlags.DataStatic));
        RootSignatureFlags flags = RootSignatureFlags.AllowInputAssemblerInputLayout |
            RootSignatureFlags.DenyHullShaderRootAccess |
            RootSignatureFlags.DenyDomainShaderRootAccess |
            RootSignatureFlags.DenyGeometryShaderRootAccess |
            RootSignatureFlags.DenyAmplificationShaderRootAccess |
            RootSignatureFlags.DenyMeshShaderRootAccess;
        return CreateRootSignature(&parameter, 1, flags);
    }

    private ID3D12RootSignature* CreateEmptyComputeRootSignature()
    {
        RootSignatureFlags flags = RootSignatureFlags.DenyVertexShaderRootAccess |
            RootSignatureFlags.DenyHullShaderRootAccess |
            RootSignatureFlags.DenyDomainShaderRootAccess |
            RootSignatureFlags.DenyGeometryShaderRootAccess |
            RootSignatureFlags.DenyPixelShaderRootAccess |
            RootSignatureFlags.DenyAmplificationShaderRootAccess |
            RootSignatureFlags.DenyMeshShaderRootAccess;
        return CreateRootSignature(null, 0, flags);
    }

    private ID3D12RootSignature* CreateRootSignature(
        RootParameter1* parameters,
        uint count,
        RootSignatureFlags flags)
    {
        RootSignatureDesc1 description = new(count, parameters, 0, null, flags);
        VersionedRootSignatureDesc versioned = new(D3DRootSignatureVersion.Version11, desc11: description);
        ID3D10Blob* serialized = null;
        ID3D10Blob* errors = null;
        Check(_d3d12.SerializeVersionedRootSignature(&versioned, &serialized, &errors), "D3D12SerializeVersionedRootSignature");
        try
        {
            ID3D12RootSignature* result = null;
            Guid iid = ID3D12RootSignature.Guid;
            Check(
                _device->CreateRootSignature(1, serialized->GetBufferPointer(), serialized->GetBufferSize(), &iid, (void**)&result),
                "ID3D12Device::CreateRootSignature");
            return result;
        }
        finally
        {
            Release(serialized);
            Release(errors);
        }
    }

    private ID3D12PipelineState* CreateGraphicsPipeline(byte[] vertexCode, byte[] pixelCode)
    {
        fixed (byte* vertex = vertexCode)
        fixed (byte* pixel = pixelCode)
        {
            BlendDesc blend = default;
            for (int index = 0; index < 8; index++)
            {
                blend.RenderTarget[index] = new RenderTargetBlendDesc(
                    false,
                    false,
                    Silk.NET.Direct3D12.Blend.One,
                    Silk.NET.Direct3D12.Blend.Zero,
                    BlendOp.Add,
                    Silk.NET.Direct3D12.Blend.One,
                    Silk.NET.Direct3D12.Blend.Zero,
                    BlendOp.Add,
                    LogicOp.Copy,
                    0x0F);
            }
            DepthStencilopDesc stencil = new(
                StencilOp.Keep,
                StencilOp.Keep,
                StencilOp.Keep,
                ComparisonFunc.Never);
            GraphicsPipelineStateDesc description = new()
            {
                PRootSignature = _graphicsRoot,
                VS = new ShaderBytecode(vertex, (nuint)vertexCode.Length),
                PS = new ShaderBytecode(pixel, (nuint)pixelCode.Length),
                BlendState = blend,
                SampleMask = uint.MaxValue,
                RasterizerState = new RasterizerDesc(
                    FillMode.Solid,
                    CullMode.None,
                    true,
                    0,
                    0,
                    0,
                    true,
                    false,
                    false,
                    0,
                    ConservativeRasterizationMode.Off),
                DepthStencilState = new DepthStencilDesc(
                    false,
                    DepthWriteMask.Zero,
                    ComparisonFunc.Less,
                    false,
                    byte.MaxValue,
                    byte.MaxValue,
                    stencil,
                    stencil),
                InputLayout = new InputLayoutDesc(null, 0),
                IBStripCutValue = IndexBufferStripCutValue.ValueDisabled,
                PrimitiveTopologyType = PrimitiveTopologyType.Triangle,
                NumRenderTargets = 1,
                DSVFormat = NativeFormat.FormatUnknown,
                SampleDesc = new SampleDesc(1, 0),
                NodeMask = 1,
                Flags = PipelineStateFlags.None,
            };
            description.RTVFormats[0] = NativeFormat.FormatR8G8B8A8Unorm;
            ID3D12PipelineState* result = null;
            Guid iid = ID3D12PipelineState.Guid;
            Check(_device->CreateGraphicsPipelineState(&description, &iid, (void**)&result), "ID3D12Device::CreateGraphicsPipelineState");
            return result;
        }
    }

    private ID3D12PipelineState* CreateComputePipeline(byte[] computeCode)
    {
        fixed (byte* compute = computeCode)
        {
            ComputePipelineStateDesc description = new(
                _computeRoot,
                new ShaderBytecode(compute, (nuint)computeCode.Length),
                1,
                default,
                PipelineStateFlags.None);
            ID3D12PipelineState* result = null;
            Guid iid = ID3D12PipelineState.Guid;
            Check(_device->CreateComputePipelineState(&description, &iid, (void**)&result), "ID3D12Device::CreateComputePipelineState");
            return result;
        }
    }

    internal sealed unsafe class NativeQueue : IDisposable
    {
        private ID3D12CommandQueue* _queue;
        private ID3D12Fence* _fence;
        private ID3D12CommandAllocator* _allocator;
        private ID3D12GraphicsCommandList10* _list;
        private nint _event;
        private ulong _nextValue = 1;

        internal NativeQueue(ID3D12Device* device, CommandListType type)
        {
            Type = type;
            CommandQueueDesc queueDescription = new(type, (int)CommandQueuePriority.Normal, CommandQueueFlags.None, 1);
            Guid iid = ID3D12CommandQueue.Guid;
            ID3D12CommandQueue* queue = null;
            Check(device->CreateCommandQueue(&queueDescription, &iid, (void**)&queue), "ID3D12Device::CreateCommandQueue");
            _queue = queue;
            iid = ID3D12Fence.Guid;
            ID3D12Fence* fence = null;
            Check(device->CreateFence(0, FenceFlags.None, &iid, (void**)&fence), "ID3D12Device::CreateFence");
            _fence = fence;
            iid = ID3D12CommandAllocator.Guid;
            ID3D12CommandAllocator* allocator = null;
            Check(device->CreateCommandAllocator(type, &iid, (void**)&allocator), "ID3D12Device::CreateCommandAllocator");
            _allocator = allocator;
            iid = ID3D12GraphicsCommandList10.Guid;
            ID3D12GraphicsCommandList10* list = null;
            Check(
                device->CreateCommandList(0, type, _allocator, null, &iid, (void**)&list),
                "ID3D12Device::CreateCommandList");
            _list = list;
            Check(_list->Close(), "ID3D12GraphicsCommandList::Close(initial)");
            _event = CreateEventW(0, false, false, null);
            if (_event == 0)
                throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            ulong frequency = 0;
            Check(_queue->GetTimestampFrequency(&frequency), "ID3D12CommandQueue::GetTimestampFrequency");
            Frequency = frequency;
        }

        internal CommandListType Type { get; }
        internal ulong Frequency;
        internal ID3D12CommandQueue* Queue => _queue;
        internal ID3D12Fence* Fence => _fence;
        internal ID3D12GraphicsCommandList* List => (ID3D12GraphicsCommandList*)_list;
        internal ID3D12GraphicsCommandList10* EnhancedList => _list;

        internal ID3D12GraphicsCommandList* Begin(ID3D12PipelineState* initialPipeline = null)
        {
            Check(_allocator->Reset(), "ID3D12CommandAllocator::Reset");
            Check(_list->Reset(_allocator, initialPipeline), "ID3D12GraphicsCommandList::Reset");
            return (ID3D12GraphicsCommandList*)_list;
        }

        internal ulong Execute()
        {
            Check(_list->Close(), "ID3D12GraphicsCommandList::Close");
            ID3D12CommandList* list = (ID3D12CommandList*)_list;
            _queue->ExecuteCommandLists(1, &list);
            return Signal();
        }

        internal ulong SignalOnly() => Signal();

        internal void WaitGpu(NativeQueue source, ulong value) =>
            Check(_queue->Wait(source._fence, value), "ID3D12CommandQueue::Wait");

        internal void WaitCpu(ulong value)
        {
            if (_fence->GetCompletedValue() >= value)
                return;
            Check(_fence->SetEventOnCompletion(value, (void*)_event), "ID3D12Fence::SetEventOnCompletion");
            uint result = WaitForSingleObject(_event, 30_000);
            if (result != 0)
                throw new TimeoutException($"WaitForSingleObject returned 0x{result:X8}.");
        }

        internal CalibratedTimestampInfo Calibrate()
        {
            ulong gpu = 0;
            ulong cpu = 0;
            Check(_queue->GetClockCalibration(&gpu, &cpu), "ID3D12CommandQueue::GetClockCalibration");
            return new CalibratedTimestampInfo(checked((long)cpu), Stopwatch.Frequency, gpu, Frequency);
        }

        public void Dispose()
        {
            if (_queue is not null && _fence is not null && _nextValue < ulong.MaxValue)
            {
                try
                {
                    ulong value = Signal();
                    WaitCpu(value);
                }
                catch
                {
                }
            }
            if (_event != 0)
            {
                _ = CloseHandle(_event);
                _event = 0;
            }
            Release(_list);
            _list = null;
            Release(_allocator);
            _allocator = null;
            Release(_fence);
            _fence = null;
            Release(_queue);
            _queue = null;
        }

        private ulong Signal()
        {
            if (_nextValue == ulong.MaxValue)
                throw new InvalidOperationException("The Direct Silk fence domain is exhausted.");
            ulong value = _nextValue++;
            Check(_queue->Signal(_fence, value), "ID3D12CommandQueue::Signal");
            return value;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern nint CreateEventW(nint securityAttributes, bool manualReset, bool initialState, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

        [DllImport("kernel32.dll")]
        [return: MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        private static extern bool CloseHandle(nint handle);
    }
}
