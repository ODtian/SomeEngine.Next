namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class ResourceTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12ResourceRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Resource) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Resource.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12ResourceMap = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Resource) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Resource.Map), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12ResourceUnmap = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Resource) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Resource.Unmap), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12ResourceGetGPUVirtualAddress = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Resource) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Resource.GetGPUVirtualAddress), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DescriptorHeapRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DescriptorHeap) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DescriptorHeap.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DescriptorHeapGetCPUDescriptorHandleForHeapStart = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DescriptorHeap) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DescriptorHeap.GetCPUDescriptorHandleForHeapStart), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DescriptorHeapGetGPUDescriptorHandleForHeapStart = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DescriptorHeap) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DescriptorHeap.GetGPUDescriptorHandleForHeapStart), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12ResourceGetDesc = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Resource) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Resource.GetDesc), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandAllocatorRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandAllocator) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandAllocator.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandAllocatorReset = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandAllocator) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandAllocator.Reset), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeIUnknownRelease = new("native", nameof(global::Silk.NET.Core.Native.IUnknown) + "." + nameof(global::Silk.NET.Core.Native.IUnknown.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12FenceGetCompletedValue = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Fence) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Fence.GetCompletedValue), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12FenceSetEventOnCompletion = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Fence) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Fence.SetEventOnCompletion), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12SDKConfiguration1FreeUnusedSDKs = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12SDKConfiguration1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12SDKConfiguration1.FreeUnusedSDKs), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12SDKConfiguration1Release = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12SDKConfiguration1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12SDKConfiguration1.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12SDKConfiguration1CreateDeviceFactory = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12SDKConfiguration1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12SDKConfiguration1.CreateDeviceFactory), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12FenceRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Fence) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Fence.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12ObjectSetName = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Object) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Object.SetName), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandSignatureRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandSignature) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandSignature.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D10BlobRelease = new("native", nameof(global::Silk.NET.Core.Native.ID3D10Blob) + "." + nameof(global::Silk.NET.Core.Native.ID3D10Blob.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D10BlobGetBufferSize = new("native", nameof(global::Silk.NET.Core.Native.ID3D10Blob) + "." + nameof(global::Silk.NET.Core.Native.ID3D10Blob.GetBufferSize), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D10BlobGetBufferPointer = new("native", nameof(global::Silk.NET.Core.Native.ID3D10Blob) + "." + nameof(global::Silk.NET.Core.Native.ID3D10Blob.GetBufferPointer), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12HeapGetDesc = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Heap) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Heap.GetDesc), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12HeapRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Heap) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Heap.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12ResourceGetHeapProperties = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Resource) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Resource.GetHeapProperties), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12QueryHeapRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12QueryHeap) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12QueryHeap.Release), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12Resource* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12ResourceRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Map(global::Silk.NET.Direct3D12.ID3D12Resource* target, uint @Subresource, global::Silk.NET.Direct3D12.Range* @pReadRange, void** @ppData)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12ResourceMap);
        return target->Map(@Subresource, @pReadRange, @ppData);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void Unmap(global::Silk.NET.Direct3D12.ID3D12Resource* target, uint @Subresource, global::Silk.NET.Direct3D12.Range* @pWrittenRange)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12ResourceUnmap);
        target->Unmap(@Subresource, @pWrittenRange);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static ulong GetGPUVirtualAddress(global::Silk.NET.Direct3D12.ID3D12Resource* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12ResourceGetGPUVirtualAddress);
        return target->GetGPUVirtualAddress();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12DescriptorHeap* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12DescriptorHeapRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static global::Silk.NET.Direct3D12.CpuDescriptorHandle GetCPUDescriptorHandleForHeapStart(global::Silk.NET.Direct3D12.ID3D12DescriptorHeap* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12DescriptorHeapGetCPUDescriptorHandleForHeapStart);
        return target->GetCPUDescriptorHandleForHeapStart();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static global::Silk.NET.Direct3D12.GpuDescriptorHandle GetGPUDescriptorHandleForHeapStart(global::Silk.NET.Direct3D12.ID3D12DescriptorHeap* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12DescriptorHeapGetGPUDescriptorHandleForHeapStart);
        return target->GetGPUDescriptorHandleForHeapStart();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static global::Silk.NET.Direct3D12.ResourceDesc GetDesc(global::Silk.NET.Direct3D12.ID3D12Resource* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12ResourceGetDesc);
        return target->GetDesc();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12CommandAllocator* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12CommandAllocatorRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Reset(global::Silk.NET.Direct3D12.ID3D12CommandAllocator* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12CommandAllocatorReset);
        return target->Reset();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Core.Native.IUnknown* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeIUnknownRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static ulong GetCompletedValue(global::Silk.NET.Direct3D12.ID3D12Fence* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12FenceGetCompletedValue);
        return target->GetCompletedValue();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int SetEventOnCompletion(global::Silk.NET.Direct3D12.ID3D12Fence* target, ulong @Value, void* @hEvent)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12FenceSetEventOnCompletion);
        return target->SetEventOnCompletion(@Value, @hEvent);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void FreeUnusedSDKs(global::Silk.NET.Direct3D12.ID3D12SDKConfiguration1* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12SDKConfiguration1FreeUnusedSDKs);
        target->FreeUnusedSDKs();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12SDKConfiguration1* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12SDKConfiguration1Release);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateDeviceFactory(global::Silk.NET.Direct3D12.ID3D12SDKConfiguration1* target, uint @SDKVersion, string @SDKPath, global::System.Guid* @riid, void** @ppvFactory)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12SDKConfiguration1CreateDeviceFactory);
        return target->CreateDeviceFactory(@SDKVersion, @SDKPath, @riid, @ppvFactory);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12Fence* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12FenceRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int SetName(global::Silk.NET.Direct3D12.ID3D12Object* target, char* @Name)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12ObjectSetName);
        return target->SetName(@Name);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12CommandSignature* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12CommandSignatureRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Core.Native.ID3D10Blob* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D10BlobRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static nuint GetBufferSize(global::Silk.NET.Core.Native.ID3D10Blob* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D10BlobGetBufferSize);
        return target->GetBufferSize();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void* GetBufferPointer(global::Silk.NET.Core.Native.ID3D10Blob* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D10BlobGetBufferPointer);
        return target->GetBufferPointer();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static global::Silk.NET.Direct3D12.HeapDesc GetDesc(global::Silk.NET.Direct3D12.ID3D12Heap* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12HeapGetDesc);
        return target->GetDesc();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12Heap* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12HeapRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetHeapProperties(global::Silk.NET.Direct3D12.ID3D12Resource* target, global::Silk.NET.Direct3D12.HeapProperties* @pHeapProperties, global::Silk.NET.Direct3D12.HeapFlags* @pHeapFlags)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12ResourceGetHeapProperties);
        return target->GetHeapProperties(@pHeapProperties, @pHeapFlags);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12QueryHeap* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.ResourceTrace.NativeID3D12QueryHeapRelease);
        return target->Release();
    }
}