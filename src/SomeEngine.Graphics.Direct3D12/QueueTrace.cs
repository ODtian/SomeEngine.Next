namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class QueueTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandQueueWait = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue.Wait), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandQueueExecuteCommandLists = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue.ExecuteCommandLists), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandQueueSignal = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue.Signal), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandQueueRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandQueueGetTimestampFrequency = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue.GetTimestampFrequency), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandQueueGetClockCalibration = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue.GetClockCalibration), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandQueueUpdateTileMappings = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue.UpdateTileMappings), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12CommandQueueCopyTileMappings = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12CommandQueue.CopyTileMappings), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Wait(global::Silk.NET.Direct3D12.ID3D12CommandQueue* target, global::Silk.NET.Direct3D12.ID3D12Fence* @pFence, ulong @Value)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.QueueTrace.NativeID3D12CommandQueueWait);
        return target->Wait(@pFence, @Value);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void ExecuteCommandLists(global::Silk.NET.Direct3D12.ID3D12CommandQueue* target, uint @NumCommandLists, global::Silk.NET.Direct3D12.ID3D12CommandList** @ppCommandLists)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.QueueTrace.NativeID3D12CommandQueueExecuteCommandLists);
        target->ExecuteCommandLists(@NumCommandLists, @ppCommandLists);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int Signal(global::Silk.NET.Direct3D12.ID3D12CommandQueue* target, global::Silk.NET.Direct3D12.ID3D12Fence* @pFence, ulong @Value)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.QueueTrace.NativeID3D12CommandQueueSignal);
        return target->Signal(@pFence, @Value);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12CommandQueue* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.QueueTrace.NativeID3D12CommandQueueRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetTimestampFrequency(global::Silk.NET.Direct3D12.ID3D12CommandQueue* target, ulong* @pFrequency)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.QueueTrace.NativeID3D12CommandQueueGetTimestampFrequency);
        return target->GetTimestampFrequency(@pFrequency);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetClockCalibration(global::Silk.NET.Direct3D12.ID3D12CommandQueue* target, ulong* @pGpuTimestamp, ulong* @pCpuTimestamp)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.QueueTrace.NativeID3D12CommandQueueGetClockCalibration);
        return target->GetClockCalibration(@pGpuTimestamp, @pCpuTimestamp);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void UpdateTileMappings(global::Silk.NET.Direct3D12.ID3D12CommandQueue* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pResource, uint @NumResourceRegions, global::Silk.NET.Direct3D12.TiledResourceCoordinate* @pResourceRegionStartCoordinates, global::Silk.NET.Direct3D12.TileRegionSize* @pResourceRegionSizes, global::Silk.NET.Direct3D12.ID3D12Heap* @pHeap, uint @NumRanges, global::Silk.NET.Direct3D12.TileRangeFlags* @pRangeFlags, uint* @pHeapRangeStartOffsets, uint* @pRangeTileCounts, global::Silk.NET.Direct3D12.TileMappingFlags @Flags)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.QueueTrace.NativeID3D12CommandQueueUpdateTileMappings);
        target->UpdateTileMappings(@pResource, @NumResourceRegions, @pResourceRegionStartCoordinates, @pResourceRegionSizes, @pHeap, @NumRanges, @pRangeFlags, @pHeapRangeStartOffsets, @pRangeTileCounts, @Flags);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CopyTileMappings(global::Silk.NET.Direct3D12.ID3D12CommandQueue* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pDstResource, global::Silk.NET.Direct3D12.TiledResourceCoordinate* @pDstRegionStartCoordinate, global::Silk.NET.Direct3D12.ID3D12Resource* @pSrcResource, global::Silk.NET.Direct3D12.TiledResourceCoordinate* @pSrcRegionStartCoordinate, global::Silk.NET.Direct3D12.TileRegionSize* @pRegionSize, global::Silk.NET.Direct3D12.TileMappingFlags @Flags)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.QueueTrace.NativeID3D12CommandQueueCopyTileMappings);
        target->CopyTileMappings(@pDstResource, @pDstRegionStartCoordinate, @pSrcResource, @pSrcRegionStartCoordinate, @pRegionSize, @Flags);
    }
}