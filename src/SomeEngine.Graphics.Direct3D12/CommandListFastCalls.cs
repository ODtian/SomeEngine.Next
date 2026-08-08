using System.Runtime.CompilerServices;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal static unsafe class D3D12CommandListFastCalls
{
    // These command-list recording calls are short, non-blocking user-mode
    // operations. Suppressing the CLR transition keeps the RHI's managed
    // receiver/context references from being spilled and reloaded around every
    // draw or root argument. The slots are fixed by ID3D12GraphicsCommandList;
    // later command-list interfaces preserve the inherited vtable prefix.
    private const int DrawInstancedSlot = 12;
    private const int DrawIndexedInstancedSlot = 13;
    private const int DispatchSlot = 14;
    private const int SetComputeRootConstantBufferViewSlot = 37;
    private const int SetGraphicsRootConstantBufferViewSlot = 38;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DrawInstanced(
        ID3D12GraphicsCommandList10* list,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance)
    {
        void** vtable = *(void***)list;
        var call = (delegate* unmanaged[Stdcall, SuppressGCTransition]<
            ID3D12GraphicsCommandList10*, uint, uint, uint, uint, void>)vtable[DrawInstancedSlot];
        call(list, vertexCount, instanceCount, firstVertex, firstInstance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DrawIndexedInstanced(
        ID3D12GraphicsCommandList10* list,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int vertexOffset,
        uint firstInstance)
    {
        void** vtable = *(void***)list;
        var call = (delegate* unmanaged[Stdcall, SuppressGCTransition]<
            ID3D12GraphicsCommandList10*, uint, uint, uint, int, uint, void>)vtable[DrawIndexedInstancedSlot];
        call(list, indexCount, instanceCount, firstIndex, vertexOffset, firstInstance);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Dispatch(
        ID3D12GraphicsCommandList10* list,
        uint x,
        uint y,
        uint z)
    {
        void** vtable = *(void***)list;
        var call = (delegate* unmanaged[Stdcall, SuppressGCTransition]<
            ID3D12GraphicsCommandList10*, uint, uint, uint, void>)vtable[DispatchSlot];
        call(list, x, y, z);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootConstantBufferView(
        ID3D12GraphicsCommandList10* list,
        bool compute,
        uint rootParameter,
        ulong address)
    {
        void** vtable = *(void***)list;
        int slot = compute
            ? SetComputeRootConstantBufferViewSlot
            : SetGraphicsRootConstantBufferViewSlot;
        var call = (delegate* unmanaged[Stdcall, SuppressGCTransition]<
            ID3D12GraphicsCommandList10*, uint, ulong, void>)vtable[slot];
        call(list, rootParameter, address);
    }
}
