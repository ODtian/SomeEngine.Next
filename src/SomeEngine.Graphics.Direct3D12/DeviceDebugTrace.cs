namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class DeviceDebugTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceRemovedExtendedDataSettings1SetAutoBreadcrumbsEnablement = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1.SetAutoBreadcrumbsEnablement), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceRemovedExtendedDataSettings1SetPageFaultEnablement = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1.SetPageFaultEnablement), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceRemovedExtendedDataSettings1SetBreadcrumbContextEnablement = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1.SetBreadcrumbContextEnablement), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceRemovedExtendedDataSettings1Release = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceRemovedExtendedData1GetAutoBreadcrumbsOutput1 = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedData1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedData1.GetAutoBreadcrumbsOutput1), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceRemovedExtendedData1GetPageFaultAllocationOutput1 = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedData1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedData1.GetPageFaultAllocationOutput1), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceRemovedExtendedData1Release = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedData1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedData1.Release), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetAutoBreadcrumbsEnablement(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1* target, global::Silk.NET.Direct3D12.DredEnablement @Enablement)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12DeviceRemovedExtendedDataSettings1SetAutoBreadcrumbsEnablement);
        target->SetAutoBreadcrumbsEnablement(@Enablement);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetPageFaultEnablement(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1* target, global::Silk.NET.Direct3D12.DredEnablement @Enablement)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12DeviceRemovedExtendedDataSettings1SetPageFaultEnablement);
        target->SetPageFaultEnablement(@Enablement);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetBreadcrumbContextEnablement(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1* target, global::Silk.NET.Direct3D12.DredEnablement @Enablement)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12DeviceRemovedExtendedDataSettings1SetBreadcrumbContextEnablement);
        target->SetBreadcrumbContextEnablement(@Enablement);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedDataSettings1* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12DeviceRemovedExtendedDataSettings1Release);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetAutoBreadcrumbsOutput1(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedData1* target, global::Silk.NET.Direct3D12.DredAutoBreadcrumbsOutput1* @pOutput)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12DeviceRemovedExtendedData1GetAutoBreadcrumbsOutput1);
        return target->GetAutoBreadcrumbsOutput1(@pOutput);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetPageFaultAllocationOutput1(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedData1* target, global::Silk.NET.Direct3D12.DredPageFaultOutput1* @pOutput)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12DeviceRemovedExtendedData1GetPageFaultAllocationOutput1);
        return target->GetPageFaultAllocationOutput1(@pOutput);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12DeviceRemovedExtendedData1* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12DeviceRemovedExtendedData1Release);
        return target->Release();
    }

    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Debug1EnableDebugLayer = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Debug1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Debug1.EnableDebugLayer), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Debug1SetEnableGPUBasedValidation = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Debug1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Debug1.SetEnableGPUBasedValidation), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Debug1SetEnableSynchronizedCommandQueueValidation = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Debug1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Debug1.SetEnableSynchronizedCommandQueueValidation), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Debug1Release = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Debug1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Debug1.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12InfoQueueGetNumStoredMessagesAllowedByRetrievalFilter = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12InfoQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12InfoQueue.GetNumStoredMessagesAllowedByRetrievalFilter), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12InfoQueueRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12InfoQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12InfoQueue.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12InfoQueueGetMessageA = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12InfoQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12InfoQueue.GetMessageA), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void EnableDebugLayer(global::Silk.NET.Direct3D12.ID3D12Debug1* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12Debug1EnableDebugLayer);
        target->EnableDebugLayer();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetEnableGPUBasedValidation(global::Silk.NET.Direct3D12.ID3D12Debug1* target, global::Silk.NET.Core.Bool32 @Enable)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12Debug1SetEnableGPUBasedValidation);
        target->SetEnableGPUBasedValidation(@Enable);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetEnableSynchronizedCommandQueueValidation(global::Silk.NET.Direct3D12.ID3D12Debug1* target, global::Silk.NET.Core.Bool32 @Enable)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12Debug1SetEnableSynchronizedCommandQueueValidation);
        target->SetEnableSynchronizedCommandQueueValidation(@Enable);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12Debug1* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12Debug1Release);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static ulong GetNumStoredMessagesAllowedByRetrievalFilter(global::Silk.NET.Direct3D12.ID3D12InfoQueue* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12InfoQueueGetNumStoredMessagesAllowedByRetrievalFilter);
        return target->GetNumStoredMessagesAllowedByRetrievalFilter();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12InfoQueue* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12InfoQueueRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetMessageA(global::Silk.NET.Direct3D12.ID3D12InfoQueue* target, ulong @MessageIndex, global::Silk.NET.Direct3D12.Message* @pMessage, nuint* @pMessageByteLength)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DeviceDebugTrace.NativeID3D12InfoQueueGetMessageA);
        return target->GetMessageA(@MessageIndex, @pMessage, @pMessageByteLength);
    }
}