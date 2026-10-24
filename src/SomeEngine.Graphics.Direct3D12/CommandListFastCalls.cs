using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.Maths;
using NativeViewport = Silk.NET.Direct3D12.Viewport;

namespace SomeEngine.Graphics.Direct3D12;

internal static unsafe class D3D12CommandListFastCalls
{
    // Keep all managed D3D12 calls on Silk.NET's generated COM surface. This
    // helper only centralizes compute/graphics selection and state-call shape;
    // it must not read COM vtables or emit unmanaged calli instructions.

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DrawInstanced(
        ID3D12GraphicsCommandList10* list,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance) =>
        CommandExecutionTrace.DrawInstanced(list, vertexCount, instanceCount, firstVertex, firstInstance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DrawIndexedInstanced(
        ID3D12GraphicsCommandList10* list,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int vertexOffset,
        uint firstInstance) =>
        CommandExecutionTrace.DrawIndexedInstanced(
        list, indexCount,
            instanceCount,
            firstIndex,
            vertexOffset,
            firstInstance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Dispatch(
        ID3D12GraphicsCommandList10* list,
        uint x,
        uint y,
        uint z) =>
        CommandExecutionTrace.Dispatch(list, x, y, z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetPrimitiveTopology(
        ID3D12GraphicsCommandList10* list,
        D3DPrimitiveTopology topology) =>
        CommandBindingTrace.IASetPrimitiveTopology(list, topology);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetViewports(
        ID3D12GraphicsCommandList10* list,
        uint count,
        NativeViewport* viewports) =>
        CommandBindingTrace.RSSetViewports(list, count, viewports);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetScissors(
        ID3D12GraphicsCommandList10* list,
        uint count,
        Box2D<int>* scissors) =>
        CommandBindingTrace.RSSetScissorRects(list, count, scissors);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetPipelineState(
        ID3D12GraphicsCommandList10* list,
        ID3D12PipelineState* pipeline) =>
        CommandBindingTrace.SetPipelineState(list, pipeline);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ResourceBarrier(
        ID3D12GraphicsCommandList10* list,
        uint count,
        ResourceBarrier* barriers) =>
        CommandTransferTrace.ResourceBarrier(list, count, barriers);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetDescriptorHeaps(
        ID3D12GraphicsCommandList10* list,
        uint count,
        ID3D12DescriptorHeap** heaps) =>
        CommandBindingTrace.SetDescriptorHeaps(list, count, heaps);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootSignature(
        ID3D12GraphicsCommandList10* list,
        bool compute,
        ID3D12RootSignature* rootSignature)
    {
        if (compute)
            CommandBindingTrace.SetComputeRootSignature(list, rootSignature);
        else
            CommandBindingTrace.SetGraphicsRootSignature(list, rootSignature);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootDescriptorTable(
        ID3D12GraphicsCommandList10* list,
        bool compute,
        uint rootParameter,
        GpuDescriptorHandle handle)
    {
        if (compute)
            CommandBindingTrace.SetComputeRootDescriptorTable(list, rootParameter, handle);
        else
            CommandBindingTrace.SetGraphicsRootDescriptorTable(list, rootParameter, handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRenderTargets(
        ID3D12GraphicsCommandList10* list,
        uint count,
        CpuDescriptorHandle* renderTargets,
        CpuDescriptorHandle* depthStencil) =>
        CommandBindingTrace.OMSetRenderTargets(list, count, renderTargets, false, depthStencil);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ClearRenderTargetView(
        ID3D12GraphicsCommandList10* list,
        CpuDescriptorHandle renderTarget,
        float* color) =>
        CommandExecutionTrace.ClearRenderTargetView(list, renderTarget, color, 0, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Barrier(
        ID3D12GraphicsCommandList10* list,
        uint groupCount,
        BarrierGroup* groups) =>
        CommandTransferTrace.Barrier(list, groupCount, groups);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootConstantBufferView(
        ID3D12GraphicsCommandList10* list,
        bool compute,
        uint rootParameter,
        ulong address)
    {
        if (compute)
            CommandBindingTrace.SetComputeRootConstantBufferView(list, rootParameter, address);
        else
            CommandBindingTrace.SetGraphicsRootConstantBufferView(list, rootParameter, address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRoot32BitConstants(
        ID3D12GraphicsCommandList10* list,
        bool compute,
        uint rootParameter,
        uint count,
        void* values)
    {
        if (compute)
            CommandBindingTrace.SetComputeRoot32BitConstants(list, rootParameter, count, values, 0);
        else
            CommandBindingTrace.SetGraphicsRoot32BitConstants(list, rootParameter, count, values, 0);
    }
}
