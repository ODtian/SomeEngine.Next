namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class CommandTransferTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10CopyBufferRegion = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.CopyBufferRegion), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10CopyTextureRegion = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.CopyTextureRegion), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10ResolveSubresourceRegion = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.ResolveSubresourceRegion), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10ResourceBarrier = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.ResourceBarrier), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10Barrier = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.Barrier), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10ResolveQueryData = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.ResolveQueryData), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10CopyRaytracingAccelerationStructure = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.CopyRaytracingAccelerationStructure), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CopyBufferRegion(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pDstBuffer, ulong @DstOffset, global::Silk.NET.Direct3D12.ID3D12Resource* @pSrcBuffer, ulong @SrcOffset, ulong @NumBytes)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandTransferTrace.NativeID3D12GraphicsCommandList10CopyBufferRegion);
        target->CopyBufferRegion(@pDstBuffer, @DstOffset, @pSrcBuffer, @SrcOffset, @NumBytes);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CopyTextureRegion(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.TextureCopyLocation* @pDst, uint @DstX, uint @DstY, uint @DstZ, global::Silk.NET.Direct3D12.TextureCopyLocation* @pSrc, global::Silk.NET.Direct3D12.Box* @pSrcBox)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandTransferTrace.NativeID3D12GraphicsCommandList10CopyTextureRegion);
        target->CopyTextureRegion(@pDst, @DstX, @DstY, @DstZ, @pSrc, @pSrcBox);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void ResolveSubresourceRegion(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pDstResource, uint @DstSubresource, uint @DstX, uint @DstY, global::Silk.NET.Direct3D12.ID3D12Resource* @pSrcResource, uint @SrcSubresource, global::Silk.NET.Maths.Box2D<int>* @pSrcRect, global::Silk.NET.DXGI.Format @Format, global::Silk.NET.Direct3D12.ResolveMode @ResolveMode)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandTransferTrace.NativeID3D12GraphicsCommandList10ResolveSubresourceRegion);
        target->ResolveSubresourceRegion(@pDstResource, @DstSubresource, @DstX, @DstY, @pSrcResource, @SrcSubresource, @pSrcRect, @Format, @ResolveMode);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void ResourceBarrier(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @NumBarriers, global::Silk.NET.Direct3D12.ResourceBarrier* @pBarriers)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandTransferTrace.NativeID3D12GraphicsCommandList10ResourceBarrier);
        target->ResourceBarrier(@NumBarriers, @pBarriers);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void Barrier(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @NumBarrierGroups, global::Silk.NET.Direct3D12.BarrierGroup* @pBarrierGroups)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandTransferTrace.NativeID3D12GraphicsCommandList10Barrier);
        target->Barrier(@NumBarrierGroups, @pBarrierGroups);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void ResolveQueryData(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ID3D12QueryHeap* @pQueryHeap, global::Silk.NET.Direct3D12.QueryType @Type, uint @StartIndex, uint @NumQueries, global::Silk.NET.Direct3D12.ID3D12Resource* @pDestinationBuffer, ulong @AlignedDestinationBufferOffset)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandTransferTrace.NativeID3D12GraphicsCommandList10ResolveQueryData);
        target->ResolveQueryData(@pQueryHeap, @Type, @StartIndex, @NumQueries, @pDestinationBuffer, @AlignedDestinationBufferOffset);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void CopyRaytracingAccelerationStructure(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, ulong @DestAccelerationStructureData, ulong @SourceAccelerationStructureData, global::Silk.NET.Direct3D12.RaytracingAccelerationStructureCopyMode @Mode)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandTransferTrace.NativeID3D12GraphicsCommandList10CopyRaytracingAccelerationStructure);
        target->CopyRaytracingAccelerationStructure(@DestAccelerationStructureData, @SourceAccelerationStructureData, @Mode);
    }
}