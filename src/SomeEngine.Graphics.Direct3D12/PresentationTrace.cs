namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class PresentationTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIFactory6EnumAdapterByGpuPreference = new("native", nameof(global::Silk.NET.DXGI.IDXGIFactory6) + "." + nameof(global::Silk.NET.DXGI.IDXGIFactory6.EnumAdapterByGpuPreference), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIAdapter4GetDesc3 = new("native", nameof(global::Silk.NET.DXGI.IDXGIAdapter4) + "." + nameof(global::Silk.NET.DXGI.IDXGIAdapter4.GetDesc3), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIAdapter4Release = new("native", nameof(global::Silk.NET.DXGI.IDXGIAdapter4) + "." + nameof(global::Silk.NET.DXGI.IDXGIAdapter4.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIFactory6Release = new("native", nameof(global::Silk.NET.DXGI.IDXGIFactory6) + "." + nameof(global::Silk.NET.DXGI.IDXGIFactory6.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIFactory6EnumWarpAdapter = new("native", nameof(global::Silk.NET.DXGI.IDXGIFactory6) + "." + nameof(global::Silk.NET.DXGI.IDXGIFactory6.EnumWarpAdapter), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIAdapter4CheckInterfaceSupport = new("native", nameof(global::Silk.NET.DXGI.IDXGIAdapter4) + "." + nameof(global::Silk.NET.DXGI.IDXGIAdapter4.CheckInterfaceSupport), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4Release = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIFactory6CreateSwapChainForHwnd = new("native", nameof(global::Silk.NET.DXGI.IDXGIFactory6) + "." + nameof(global::Silk.NET.DXGI.IDXGIFactory6.CreateSwapChainForHwnd), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain1QueryInterface = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain1) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain1.QueryInterface), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIFactory6MakeWindowAssociation = new("native", nameof(global::Silk.NET.DXGI.IDXGIFactory6) + "." + nameof(global::Silk.NET.DXGI.IDXGIFactory6.MakeWindowAssociation), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain1Release = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain1) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain1.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4GetDesc1 = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.GetDesc1), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIFactory6CheckFeatureSupport = new("native", nameof(global::Silk.NET.DXGI.IDXGIFactory6) + "." + nameof(global::Silk.NET.DXGI.IDXGIFactory6.CheckFeatureSupport), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4CheckColorSpaceSupport = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.CheckColorSpaceSupport), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4SetMaximumFrameLatency = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.SetMaximumFrameLatency), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4SetColorSpace1 = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.SetColorSpace1), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4SetHDRMetaData = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.SetHDRMetaData), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4GetFrameLatencyWaitableObject = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.GetFrameLatencyWaitableObject), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4GetCurrentBackBufferIndex = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.GetCurrentBackBufferIndex), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4Present = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.Present), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4ResizeBuffers = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.ResizeBuffers), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4GetBuffer = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.GetBuffer), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGISwapChain4AddRef = new("native", nameof(global::Silk.NET.DXGI.IDXGISwapChain4) + "." + nameof(global::Silk.NET.DXGI.IDXGISwapChain4.AddRef), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIDXGIAdapter3QueryVideoMemoryInfo = new("native", nameof(global::Silk.NET.DXGI.IDXGIAdapter3) + "." + nameof(global::Silk.NET.DXGI.IDXGIAdapter3.QueryVideoMemoryInfo), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int EnumAdapterByGpuPreference(global::Silk.NET.DXGI.IDXGIFactory6* target, uint @Adapter, global::Silk.NET.DXGI.GpuPreference @GpuPreference, global::System.Guid* @riid, void** @ppvAdapter)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIFactory6EnumAdapterByGpuPreference);
        return target->EnumAdapterByGpuPreference(@Adapter, @GpuPreference, @riid, @ppvAdapter);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetDesc3(global::Silk.NET.DXGI.IDXGIAdapter4* target, global::Silk.NET.DXGI.AdapterDesc3* @pDesc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIAdapter4GetDesc3);
        return target->GetDesc3(@pDesc);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.DXGI.IDXGIAdapter4* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIAdapter4Release);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.DXGI.IDXGIFactory6* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIFactory6Release);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int EnumWarpAdapter(global::Silk.NET.DXGI.IDXGIFactory6* target, global::System.Guid* @riid, void** @ppvAdapter)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIFactory6EnumWarpAdapter);
        return target->EnumWarpAdapter(@riid, @ppvAdapter);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CheckInterfaceSupport(global::Silk.NET.DXGI.IDXGIAdapter4* target, global::System.Guid* @InterfaceName, long* @pUMDVersion)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIAdapter4CheckInterfaceSupport);
        return target->CheckInterfaceSupport(@InterfaceName, @pUMDVersion);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.DXGI.IDXGISwapChain4* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4Release);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateSwapChainForHwnd(global::Silk.NET.DXGI.IDXGIFactory6* target, global::Silk.NET.Core.Native.IUnknown* @pDevice, nint @hWnd, global::Silk.NET.DXGI.SwapChainDesc1* @pDesc, global::Silk.NET.DXGI.SwapChainFullscreenDesc* @pFullscreenDesc, global::Silk.NET.DXGI.IDXGIOutput* @pRestrictToOutput, global::Silk.NET.DXGI.IDXGISwapChain1** @ppSwapChain)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIFactory6CreateSwapChainForHwnd);
        return target->CreateSwapChainForHwnd(@pDevice, @hWnd, @pDesc, @pFullscreenDesc, @pRestrictToOutput, @ppSwapChain);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int QueryInterface(global::Silk.NET.DXGI.IDXGISwapChain1* target, global::System.Guid* @riid, void** @ppvObject)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain1QueryInterface);
        return target->QueryInterface(@riid, @ppvObject);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int MakeWindowAssociation(global::Silk.NET.DXGI.IDXGIFactory6* target, nint @WindowHandle, uint @Flags)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIFactory6MakeWindowAssociation);
        return target->MakeWindowAssociation(@WindowHandle, @Flags);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.DXGI.IDXGISwapChain1* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain1Release);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetDesc1(global::Silk.NET.DXGI.IDXGISwapChain4* target, global::Silk.NET.DXGI.SwapChainDesc1* @pDesc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4GetDesc1);
        return target->GetDesc1(@pDesc);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CheckFeatureSupport(global::Silk.NET.DXGI.IDXGIFactory6* target, global::Silk.NET.DXGI.Feature @Feature, void* @pFeatureSupportData, uint @FeatureSupportDataSize)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIFactory6CheckFeatureSupport);
        return target->CheckFeatureSupport(@Feature, @pFeatureSupportData, @FeatureSupportDataSize);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CheckColorSpaceSupport(global::Silk.NET.DXGI.IDXGISwapChain4* target, global::Silk.NET.DXGI.ColorSpaceType @ColorSpace, uint* @pColorSpaceSupport)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4CheckColorSpaceSupport);
        return target->CheckColorSpaceSupport(@ColorSpace, @pColorSpaceSupport);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int SetMaximumFrameLatency(global::Silk.NET.DXGI.IDXGISwapChain4* target, uint @MaxLatency)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4SetMaximumFrameLatency);
        return target->SetMaximumFrameLatency(@MaxLatency);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int SetColorSpace1(global::Silk.NET.DXGI.IDXGISwapChain4* target, global::Silk.NET.DXGI.ColorSpaceType @ColorSpace)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4SetColorSpace1);
        return target->SetColorSpace1(@ColorSpace);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int SetHDRMetaData(global::Silk.NET.DXGI.IDXGISwapChain4* target, global::Silk.NET.DXGI.HdrMetadataType @Type, uint @Size, void* @pMetaData)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4SetHDRMetaData);
        return target->SetHDRMetaData(@Type, @Size, @pMetaData);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void* GetFrameLatencyWaitableObject(global::Silk.NET.DXGI.IDXGISwapChain4* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4GetFrameLatencyWaitableObject);
        return target->GetFrameLatencyWaitableObject();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint GetCurrentBackBufferIndex(global::Silk.NET.DXGI.IDXGISwapChain4* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4GetCurrentBackBufferIndex);
        return target->GetCurrentBackBufferIndex();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Present(global::Silk.NET.DXGI.IDXGISwapChain4* target, uint @SyncInterval, uint @Flags)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4Present);
        return target->Present(@SyncInterval, @Flags);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int ResizeBuffers(global::Silk.NET.DXGI.IDXGISwapChain4* target, uint @BufferCount, uint @Width, uint @Height, global::Silk.NET.DXGI.Format @NewFormat, uint @SwapChainFlags)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4ResizeBuffers);
        return target->ResizeBuffers(@BufferCount, @Width, @Height, @NewFormat, @SwapChainFlags);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetBuffer(global::Silk.NET.DXGI.IDXGISwapChain4* target, uint @Buffer, global::System.Guid* @riid, void** @ppSurface)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4GetBuffer);
        return target->GetBuffer(@Buffer, @riid, @ppSurface);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint AddRef(global::Silk.NET.DXGI.IDXGISwapChain4* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGISwapChain4AddRef);
        return target->AddRef();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int QueryVideoMemoryInfo(global::Silk.NET.DXGI.IDXGIAdapter3* target, uint @NodeIndex, global::Silk.NET.DXGI.MemorySegmentGroup @MemorySegmentGroup, global::Silk.NET.DXGI.QueryVideoMemoryInfo* @pVideoMemoryInfo)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PresentationTrace.NativeIDXGIAdapter3QueryVideoMemoryInfo);
        return target->QueryVideoMemoryInfo(@NodeIndex, @MemorySegmentGroup, @pVideoMemoryInfo);
    }
}