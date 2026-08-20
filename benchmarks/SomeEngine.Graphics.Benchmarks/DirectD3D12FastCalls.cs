using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using NativeResourceBarrier = Silk.NET.Direct3D12.ResourceBarrier;

namespace SomeEngine.Graphics.Benchmarks;

/// <summary>
/// Direct D3D12 calls used only by the optimized managed baseline.
/// </summary>
/// <remarks>
/// Every method below calls the generated Silk.NET COM surface. This helper only
/// provides compile-time dispatch for the benchmark and does not read COM vtables
/// or replace Silk.NET's interop wrappers.
/// </remarks>
internal static unsafe class DirectD3D12FastCalls
{
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void DrawInstanced(
        ID3D12GraphicsCommandList* list,
        uint vertexCount,
        uint instanceCount,
        uint firstVertex,
        uint firstInstance) =>
        list->DrawInstanced(vertexCount, instanceCount, firstVertex, firstInstance);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Dispatch(
        ID3D12GraphicsCommandList* list,
        uint x,
        uint y,
        uint z) =>
        list->Dispatch(x, y, z);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetGraphicsRootConstantBufferView(
        ID3D12GraphicsCommandList* list,
        uint rootParameter,
        ulong address) =>
        list->SetGraphicsRootConstantBufferView(rootParameter, address);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetPipelineState(
        ID3D12GraphicsCommandList* list,
        ID3D12PipelineState* pipeline) =>
        list->SetPipelineState(pipeline);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetGraphicsRootSignature(
        ID3D12GraphicsCommandList* list,
        ID3D12RootSignature* rootSignature) =>
        list->SetGraphicsRootSignature(rootSignature);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetViewports(
        ID3D12GraphicsCommandList* list,
        uint count,
        Silk.NET.Direct3D12.Viewport* viewports) =>
        list->RSSetViewports(count, viewports);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetScissors(
        ID3D12GraphicsCommandList* list,
        uint count,
        Silk.NET.Maths.Box2D<int>* scissors) =>
        list->RSSetScissorRects(count, scissors);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetPrimitiveTopology(
        ID3D12GraphicsCommandList* list,
        D3DPrimitiveTopology topology) =>
        list->IASetPrimitiveTopology(topology);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void SetRenderTargets(
        ID3D12GraphicsCommandList* list,
        uint count,
        CpuDescriptorHandle* renderTargets,
        CpuDescriptorHandle* depthStencil) =>
        list->OMSetRenderTargets(count, renderTargets, false, depthStencil);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ClearRenderTargetView(
        ID3D12GraphicsCommandList* list,
        CpuDescriptorHandle renderTarget,
        float* color) =>
        list->ClearRenderTargetView(renderTarget, color, 0, null);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void ResourceBarrier(
        ID3D12GraphicsCommandList* list,
        uint count,
        NativeResourceBarrier* barriers) =>
        list->ResourceBarrier(count, barriers);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static void Barrier(
        ID3D12GraphicsCommandList10* list,
        uint groupCount,
        BarrierGroup* groups) =>
        list->Barrier(groupCount, groups);
}
