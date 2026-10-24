namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class DeviceResourceTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateDescriptorHeap = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateDescriptorHeap), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateConstantBufferView = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateConstantBufferView), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateShaderResourceView = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateShaderResourceView), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateUnorderedAccessView = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateUnorderedAccessView), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateSampler = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateSampler), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateQueryHeap = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateQueryHeap), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateHeap = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateHeap), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateCommittedResource3 = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateCommittedResource3), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateCommittedResource = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateCommittedResource), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreatePlacedResource2 = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreatePlacedResource2), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreatePlacedResource = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreatePlacedResource), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateCommittedResource2 = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateCommittedResource2), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateSamplerFeedbackUnorderedAccessView = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateSamplerFeedbackUnorderedAccessView), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateReservedResource2 = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateReservedResource2), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateReservedResource = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateReservedResource), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateRenderTargetView = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateRenderTargetView), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateDepthStencilView = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateDepthStencilView), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateDescriptorHeap(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.DescriptorHeapDesc* @pDescriptorHeapDesc, global::System.Guid* @riid, void** @ppvHeap)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateDescriptorHeap);
        return target->CreateDescriptorHeap(@pDescriptorHeapDesc, @riid, @ppvHeap);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CreateConstantBufferView(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ConstantBufferViewDesc* @pDesc, global::Silk.NET.Direct3D12.CpuDescriptorHandle @DestDescriptor)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateConstantBufferView);
        target->CreateConstantBufferView(@pDesc, @DestDescriptor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CreateShaderResourceView(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pResource, global::Silk.NET.Direct3D12.ShaderResourceViewDesc* @pDesc, global::Silk.NET.Direct3D12.CpuDescriptorHandle @DestDescriptor)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateShaderResourceView);
        target->CreateShaderResourceView(@pResource, @pDesc, @DestDescriptor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CreateUnorderedAccessView(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pResource, global::Silk.NET.Direct3D12.ID3D12Resource* @pCounterResource, global::Silk.NET.Direct3D12.UnorderedAccessViewDesc* @pDesc, global::Silk.NET.Direct3D12.CpuDescriptorHandle @DestDescriptor)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateUnorderedAccessView);
        target->CreateUnorderedAccessView(@pResource, @pCounterResource, @pDesc, @DestDescriptor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CreateSampler(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.SamplerDesc* @pDesc, global::Silk.NET.Direct3D12.CpuDescriptorHandle @DestDescriptor)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateSampler);
        target->CreateSampler(@pDesc, @DestDescriptor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateQueryHeap(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.QueryHeapDesc* @pDesc, global::System.Guid* @riid, void** @ppvHeap)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateQueryHeap);
        return target->CreateQueryHeap(@pDesc, @riid, @ppvHeap);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateHeap(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.HeapDesc* @pDesc, global::System.Guid* @riid, void** @ppvHeap)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateHeap);
        return target->CreateHeap(@pDesc, @riid, @ppvHeap);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateCommittedResource3(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.HeapProperties* @pHeapProperties, global::Silk.NET.Direct3D12.HeapFlags @HeapFlags, global::Silk.NET.Direct3D12.ResourceDesc1* @pDesc, global::Silk.NET.Direct3D12.BarrierLayout @InitialLayout, global::Silk.NET.Direct3D12.ClearValue* @pOptimizedClearValue, global::Silk.NET.Direct3D12.ID3D12ProtectedResourceSession* @pProtectedSession, uint @NumCastableFormats, global::Silk.NET.DXGI.Format* @pCastableFormats, global::System.Guid* @riidResource, void** @ppvResource)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateCommittedResource3);
        return target->CreateCommittedResource3(@pHeapProperties, @HeapFlags, @pDesc, @InitialLayout, @pOptimizedClearValue, @pProtectedSession, @NumCastableFormats, @pCastableFormats, @riidResource, @ppvResource);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateCommittedResource(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.HeapProperties* @pHeapProperties, global::Silk.NET.Direct3D12.HeapFlags @HeapFlags, global::Silk.NET.Direct3D12.ResourceDesc* @pDesc, global::Silk.NET.Direct3D12.ResourceStates @InitialResourceState, global::Silk.NET.Direct3D12.ClearValue* @pOptimizedClearValue, global::System.Guid* @riidResource, void** @ppvResource)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateCommittedResource);
        return target->CreateCommittedResource(@pHeapProperties, @HeapFlags, @pDesc, @InitialResourceState, @pOptimizedClearValue, @riidResource, @ppvResource);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreatePlacedResource2(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ID3D12Heap* @pHeap, ulong @HeapOffset, global::Silk.NET.Direct3D12.ResourceDesc1* @pDesc, global::Silk.NET.Direct3D12.BarrierLayout @InitialLayout, global::Silk.NET.Direct3D12.ClearValue* @pOptimizedClearValue, uint @NumCastableFormats, global::Silk.NET.DXGI.Format* @pCastableFormats, global::System.Guid* @riid, void** @ppvResource)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreatePlacedResource2);
        return target->CreatePlacedResource2(@pHeap, @HeapOffset, @pDesc, @InitialLayout, @pOptimizedClearValue, @NumCastableFormats, @pCastableFormats, @riid, @ppvResource);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreatePlacedResource(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ID3D12Heap* @pHeap, ulong @HeapOffset, global::Silk.NET.Direct3D12.ResourceDesc* @pDesc, global::Silk.NET.Direct3D12.ResourceStates @InitialState, global::Silk.NET.Direct3D12.ClearValue* @pOptimizedClearValue, global::System.Guid* @riid, void** @ppvResource)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreatePlacedResource);
        return target->CreatePlacedResource(@pHeap, @HeapOffset, @pDesc, @InitialState, @pOptimizedClearValue, @riid, @ppvResource);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateCommittedResource2(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.HeapProperties* @pHeapProperties, global::Silk.NET.Direct3D12.HeapFlags @HeapFlags, global::Silk.NET.Direct3D12.ResourceDesc1* @pDesc, global::Silk.NET.Direct3D12.ResourceStates @InitialResourceState, global::Silk.NET.Direct3D12.ClearValue* @pOptimizedClearValue, global::Silk.NET.Direct3D12.ID3D12ProtectedResourceSession* @pProtectedSession, global::System.Guid* @riidResource, void** @ppvResource)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateCommittedResource2);
        return target->CreateCommittedResource2(@pHeapProperties, @HeapFlags, @pDesc, @InitialResourceState, @pOptimizedClearValue, @pProtectedSession, @riidResource, @ppvResource);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CreateSamplerFeedbackUnorderedAccessView(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pTargetedResource, global::Silk.NET.Direct3D12.ID3D12Resource* @pFeedbackResource, global::Silk.NET.Direct3D12.CpuDescriptorHandle @DestDescriptor)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateSamplerFeedbackUnorderedAccessView);
        target->CreateSamplerFeedbackUnorderedAccessView(@pTargetedResource, @pFeedbackResource, @DestDescriptor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateReservedResource2(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ResourceDesc* @pDesc, global::Silk.NET.Direct3D12.BarrierLayout @InitialLayout, global::Silk.NET.Direct3D12.ClearValue* @pOptimizedClearValue, global::Silk.NET.Direct3D12.ID3D12ProtectedResourceSession* @pProtectedSession, uint @NumCastableFormats, global::Silk.NET.DXGI.Format* @pCastableFormats, global::System.Guid* @riid, void** @ppvResource)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateReservedResource2);
        return target->CreateReservedResource2(@pDesc, @InitialLayout, @pOptimizedClearValue, @pProtectedSession, @NumCastableFormats, @pCastableFormats, @riid, @ppvResource);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateReservedResource(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ResourceDesc* @pDesc, global::Silk.NET.Direct3D12.ResourceStates @InitialState, global::Silk.NET.Direct3D12.ClearValue* @pOptimizedClearValue, global::System.Guid* @riid, void** @ppvResource)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateReservedResource);
        return target->CreateReservedResource(@pDesc, @InitialState, @pOptimizedClearValue, @riid, @ppvResource);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CreateRenderTargetView(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pResource, global::Silk.NET.Direct3D12.RenderTargetViewDesc* @pDesc, global::Silk.NET.Direct3D12.CpuDescriptorHandle @DestDescriptor)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateRenderTargetView);
        target->CreateRenderTargetView(@pResource, @pDesc, @DestDescriptor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CreateDepthStencilView(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pResource, global::Silk.NET.Direct3D12.DepthStencilViewDesc* @pDesc, global::Silk.NET.Direct3D12.CpuDescriptorHandle @DestDescriptor)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceResourceTrace.NativeID3D12Device10CreateDepthStencilView);
        target->CreateDepthStencilView(@pResource, @pDesc, @DestDescriptor);
    }
}