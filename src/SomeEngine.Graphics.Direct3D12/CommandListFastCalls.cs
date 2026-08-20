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
        list->DrawInstanced(vertexCount, instanceCount, firstVertex, firstInstance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DrawIndexedInstanced(
        ID3D12GraphicsCommandList10* list,
        uint indexCount,
        uint instanceCount,
        uint firstIndex,
        int vertexOffset,
        uint firstInstance) =>
        list->DrawIndexedInstanced(
            indexCount,
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
        list->Dispatch(x, y, z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetPrimitiveTopology(
        ID3D12GraphicsCommandList10* list,
        D3DPrimitiveTopology topology) =>
        list->IASetPrimitiveTopology(topology);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetViewports(
        ID3D12GraphicsCommandList10* list,
        uint count,
        NativeViewport* viewports) =>
        list->RSSetViewports(count, viewports);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetScissors(
        ID3D12GraphicsCommandList10* list,
        uint count,
        Box2D<int>* scissors) =>
        list->RSSetScissorRects(count, scissors);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetPipelineState(
        ID3D12GraphicsCommandList10* list,
        ID3D12PipelineState* pipeline) =>
        list->SetPipelineState(pipeline);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ResourceBarrier(
        ID3D12GraphicsCommandList10* list,
        uint count,
        ResourceBarrier* barriers) =>
        list->ResourceBarrier(count, barriers);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetDescriptorHeaps(
        ID3D12GraphicsCommandList10* list,
        uint count,
        ID3D12DescriptorHeap** heaps) =>
        list->SetDescriptorHeaps(count, heaps);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootSignature(
        ID3D12GraphicsCommandList10* list,
        bool compute,
        ID3D12RootSignature* rootSignature)
    {
        if (compute)
            list->SetComputeRootSignature(rootSignature);
        else
            list->SetGraphicsRootSignature(rootSignature);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootDescriptorTable(
        ID3D12GraphicsCommandList10* list,
        bool compute,
        uint rootParameter,
        GpuDescriptorHandle handle)
    {
        if (compute)
            list->SetComputeRootDescriptorTable(rootParameter, handle);
        else
            list->SetGraphicsRootDescriptorTable(rootParameter, handle);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRenderTargets(
        ID3D12GraphicsCommandList10* list,
        uint count,
        CpuDescriptorHandle* renderTargets,
        CpuDescriptorHandle* depthStencil) =>
        list->OMSetRenderTargets(count, renderTargets, false, depthStencil);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ClearRenderTargetView(
        ID3D12GraphicsCommandList10* list,
        CpuDescriptorHandle renderTarget,
        float* color) =>
        list->ClearRenderTargetView(renderTarget, color, 0, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Barrier(
        ID3D12GraphicsCommandList10* list,
        uint groupCount,
        BarrierGroup* groups) =>
        list->Barrier(groupCount, groups);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRootConstantBufferView(
        ID3D12GraphicsCommandList10* list,
        bool compute,
        uint rootParameter,
        ulong address)
    {
        if (compute)
            list->SetComputeRootConstantBufferView(rootParameter, address);
        else
            list->SetGraphicsRootConstantBufferView(rootParameter, address);
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
            list->SetComputeRoot32BitConstants(rootParameter, count, values, 0);
        else
            list->SetGraphicsRoot32BitConstants(rootParameter, count, values, 0);
    }
}
