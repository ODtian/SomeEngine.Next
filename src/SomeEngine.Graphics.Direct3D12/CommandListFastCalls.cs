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
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance) =>
        CommandExecutionTrace.DrawInstanced(context, list, vertexCount, instanceCount, firstVertex, firstInstance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DrawIndexedInstanced(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int vertexOffset,
        uint firstInstance) =>
        CommandExecutionTrace.DrawIndexedInstanced(
        context, list, indexCount,
            instanceCount,
            firstIndex,
            vertexOffset,
            firstInstance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Dispatch(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        uint x,
        uint y,
        uint z) =>
        CommandExecutionTrace.Dispatch(context, list, x, y, z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetPrimitiveTopology(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        D3DPrimitiveTopology topology) =>
        CommandBindingTrace.IASetPrimitiveTopology(context, list, topology);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetViewports(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        uint count,
        NativeViewport* viewports) =>
        CommandBindingTrace.RSSetViewports(context, list, count, viewports);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetScissors(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        uint count,
        Box2D<int>* scissors) =>
        CommandBindingTrace.RSSetScissorRects(context, list, count, scissors);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetPipelineState(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        ID3D12PipelineState* pipeline) =>
        CommandBindingTrace.SetPipelineState(context, list, pipeline);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ResourceBarrier(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        uint count,
        ResourceBarrier* barriers) =>
        CommandTransferTrace.ResourceBarrier(context, list, count, barriers);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetDescriptorHeaps(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        uint count,
        ID3D12DescriptorHeap** heaps) =>
        CommandBindingTrace.SetDescriptorHeaps(context, list, count, heaps);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootSignature(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        bool compute,
        ID3D12RootSignature* rootSignature)
    {
        if (compute)
            CommandBindingTrace.SetComputeRootSignature(context, list, rootSignature);
        else
            CommandBindingTrace.SetGraphicsRootSignature(context, list, rootSignature);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootDescriptorTable(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        bool compute,
        uint rootParameter,
        GpuDescriptorHandle handle)
    {
        if (compute)
            CommandBindingTrace.SetComputeRootDescriptorTable(context, list, rootParameter, handle);
        else
            CommandBindingTrace.SetGraphicsRootDescriptorTable(context, list, rootParameter, handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRenderTargets(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        uint count,
        CpuDescriptorHandle* renderTargets,
        CpuDescriptorHandle* depthStencil) =>
        CommandBindingTrace.OMSetRenderTargets(context, list, count, renderTargets, false, depthStencil);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ClearRenderTargetView(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        CpuDescriptorHandle renderTarget,
        float* color) =>
        CommandExecutionTrace.ClearRenderTargetView(context, list, renderTarget, color, 0, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Barrier(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        uint groupCount,
        BarrierGroup* groups) =>
        CommandTransferTrace.Barrier(context, list, groupCount, groups);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootConstantBufferView(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        bool compute,
        uint rootParameter,
        ulong address)
    {
        if (compute)
            CommandBindingTrace.SetComputeRootConstantBufferView(context, list, rootParameter, address);
        else
            CommandBindingTrace.SetGraphicsRootConstantBufferView(context, list, rootParameter, address);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRoot32BitConstants(
        CommandContext context,
        ID3D12GraphicsCommandList10* list,
        bool compute,
        uint rootParameter,
        uint count,
        void* values)
    {
        if (compute)
            CommandBindingTrace.SetComputeRoot32BitConstants(context, list, rootParameter, count, values, 0);
        else
            CommandBindingTrace.SetGraphicsRoot32BitConstants(context, list, rootParameter, count, values, 0);
    }
}
