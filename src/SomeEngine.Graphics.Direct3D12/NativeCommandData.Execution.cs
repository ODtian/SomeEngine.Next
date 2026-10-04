using D3D = Silk.NET.Direct3D12;
using Native = Silk.NET.Core.Native;
using SilkCore = Silk.NET.Core;
using Maths = Silk.NET.Maths;
using Dxgi = Silk.NET.DXGI;
using Silk.NET.Direct3D12;

namespace SomeEngine.Graphics.Direct3D12;

internal enum NativeCommandType
{
    SetPipelineState,
    SetDescriptorHeaps,
    SetComputeRootSignature,
    SetGraphicsRootSignature,
    SetComputeRootDescriptorTable,
    SetGraphicsRootDescriptorTable,
    SetComputeRootConstantBufferView,
    SetGraphicsRootConstantBufferView,
    SetComputeRoot32BitConstants,
    SetGraphicsRoot32BitConstants,
    SetPredication,
    SetMarker,
    IASetPrimitiveTopology,
    RSSetViewports,
    RSSetScissorRects,
    OMSetRenderTargets,
    IASetVertexBuffers,
    IASetIndexBuffer,
    SOSetTargets,
    OMSetBlendFactor,
    OMSetStencilRef,
    OMSetDepthBounds,
    RSSetDepthBias,
    IASetIndexBufferStripCutValue,
    RSSetShadingRate,
    RSSetShadingRateImage,
    CopyBufferRegion,
    CopyTextureRegion,
    ResolveSubresourceRegion,
    ResourceBarrier,
    Barrier,
    ResolveQueryData,
    ClearRenderTargetView,
    ClearDepthStencilView,
    DrawInstanced,
    DrawIndexedInstanced,
    Dispatch,
    BeginEvent,
    EndEvent,
    ExecuteIndirect,
    DispatchMesh,
    BeginQuery,
    EndQuery,
    ClearUnorderedAccessViewUint,
    DiscardResource,
}

internal sealed unsafe partial class NativeCommandData
{
    internal void Execute(ID3D12GraphicsCommandList10* target)
    {
        fixed (byte* bytes = _bytes)
        {
            CommandReader reader = new(bytes);
            while (!reader.Reached(bytes + _length))
            {
                switch (reader.Read<NativeCommandType>())
                {
                    case NativeCommandType.DiscardResource:
                    {
                        D3D.ID3D12Resource* resource = (D3D.ID3D12Resource*)reader.Read<nint>();
                        bool hasRegion = reader.Read<bool>();
                        D3D.DiscardRegion region = default;
                        if (hasRegion)
                        {
                            region.NumRects = reader.Read<uint>();
                            region.FirstSubresource = reader.Read<uint>();
                            region.NumSubresources = reader.Read<uint>();
                            region.PRects = (Maths.Box2D<int>*)reader.ReadArray();
                        }
                        CommandExecutionTrace.DiscardResource(target, resource, hasRegion ? &region : null);
                        break;
                    }
                    case NativeCommandType.SetPipelineState:
                    {
                        D3D.ID3D12PipelineState* @pPipelineState = (D3D.ID3D12PipelineState*)reader.Read<nint>();
                        CommandBindingTrace.SetPipelineState(target, @pPipelineState);
                        break;
                    }
                    case NativeCommandType.SetDescriptorHeaps:
                    {
                        uint @NumDescriptorHeaps = reader.Read<uint>();
                        D3D.ID3D12DescriptorHeap** @ppDescriptorHeaps = (D3D.ID3D12DescriptorHeap**)reader.ReadArray();
                        CommandBindingTrace.SetDescriptorHeaps(target, @NumDescriptorHeaps, @ppDescriptorHeaps);
                        break;
                    }
                    case NativeCommandType.SetComputeRootSignature:
                    {
                        D3D.ID3D12RootSignature* @pRootSignature = (D3D.ID3D12RootSignature*)reader.Read<nint>();
                        CommandBindingTrace.SetComputeRootSignature(target, @pRootSignature);
                        break;
                    }
                    case NativeCommandType.SetGraphicsRootSignature:
                    {
                        D3D.ID3D12RootSignature* @pRootSignature = (D3D.ID3D12RootSignature*)reader.Read<nint>();
                        CommandBindingTrace.SetGraphicsRootSignature(target, @pRootSignature);
                        break;
                    }
                    case NativeCommandType.SetComputeRootDescriptorTable:
                    {
                        uint @RootParameterIndex = reader.Read<uint>();
                        D3D.GpuDescriptorHandle @BaseDescriptor = reader.Read<D3D.GpuDescriptorHandle>();
                        CommandBindingTrace.SetComputeRootDescriptorTable(target, @RootParameterIndex, @BaseDescriptor);
                        break;
                    }
                    case NativeCommandType.SetGraphicsRootDescriptorTable:
                    {
                        uint @RootParameterIndex = reader.Read<uint>();
                        D3D.GpuDescriptorHandle @BaseDescriptor = reader.Read<D3D.GpuDescriptorHandle>();
                        CommandBindingTrace.SetGraphicsRootDescriptorTable(target, @RootParameterIndex, @BaseDescriptor);
                        break;
                    }
                    case NativeCommandType.SetComputeRootConstantBufferView:
                    {
                        uint @RootParameterIndex = reader.Read<uint>();
                        ulong @BufferLocation = reader.Read<ulong>();
                        CommandBindingTrace.SetComputeRootConstantBufferView(target, @RootParameterIndex, @BufferLocation);
                        break;
                    }
                    case NativeCommandType.SetGraphicsRootConstantBufferView:
                    {
                        uint @RootParameterIndex = reader.Read<uint>();
                        ulong @BufferLocation = reader.Read<ulong>();
                        CommandBindingTrace.SetGraphicsRootConstantBufferView(target, @RootParameterIndex, @BufferLocation);
                        break;
                    }
                    case NativeCommandType.SetComputeRoot32BitConstants:
                    {
                        uint @RootParameterIndex = reader.Read<uint>();
                        uint @Num32BitValuesToSet = reader.Read<uint>();
                        void* @pSrcData = (void*)reader.ReadArray();
                        uint @DestOffsetIn32BitValues = reader.Read<uint>();
                        CommandBindingTrace.SetComputeRoot32BitConstants(target, @RootParameterIndex, @Num32BitValuesToSet, @pSrcData, @DestOffsetIn32BitValues);
                        break;
                    }
                    case NativeCommandType.SetGraphicsRoot32BitConstants:
                    {
                        uint @RootParameterIndex = reader.Read<uint>();
                        uint @Num32BitValuesToSet = reader.Read<uint>();
                        void* @pSrcData = (void*)reader.ReadArray();
                        uint @DestOffsetIn32BitValues = reader.Read<uint>();
                        CommandBindingTrace.SetGraphicsRoot32BitConstants(target, @RootParameterIndex, @Num32BitValuesToSet, @pSrcData, @DestOffsetIn32BitValues);
                        break;
                    }
                    case NativeCommandType.SetPredication:
                    {
                        D3D.ID3D12Resource* @pBuffer = (D3D.ID3D12Resource*)reader.Read<nint>();
                        ulong @AlignedBufferOffset = reader.Read<ulong>();
                        D3D.PredicationOp @Operation = reader.Read<D3D.PredicationOp>();
                        CommandBindingTrace.SetPredication(target, @pBuffer, @AlignedBufferOffset, @Operation);
                        break;
                    }
                    case NativeCommandType.SetMarker:
                    {
                        uint @Metadata = reader.Read<uint>();
                        void* @pData = (void*)reader.ReadArray();
                        uint @Size = reader.Read<uint>();
                        CommandBindingTrace.SetMarker(target, @Metadata, @pData, @Size);
                        break;
                    }
                    case NativeCommandType.IASetPrimitiveTopology:
                    {
                        Native.D3DPrimitiveTopology @PrimitiveTopology = reader.Read<Native.D3DPrimitiveTopology>();
                        CommandBindingTrace.IASetPrimitiveTopology(target, @PrimitiveTopology);
                        break;
                    }
                    case NativeCommandType.RSSetViewports:
                    {
                        uint @NumViewports = reader.Read<uint>();
                        D3D.Viewport* @pViewports = (D3D.Viewport*)reader.ReadArray();
                        CommandBindingTrace.RSSetViewports(target, @NumViewports, @pViewports);
                        break;
                    }
                    case NativeCommandType.RSSetScissorRects:
                    {
                        uint @NumRects = reader.Read<uint>();
                        Maths.Box2D<int>* @pRects = (Maths.Box2D<int>*)reader.ReadArray();
                        CommandBindingTrace.RSSetScissorRects(target, @NumRects, @pRects);
                        break;
                    }
                    case NativeCommandType.OMSetRenderTargets:
                    {
                        uint @NumRenderTargetDescriptors = reader.Read<uint>();
                        D3D.CpuDescriptorHandle* @pRenderTargetDescriptors = (D3D.CpuDescriptorHandle*)reader.ReadArray();
                        SilkCore.Bool32 @RTsSingleHandleToDescriptorRange = reader.Read<SilkCore.Bool32>();
                        D3D.CpuDescriptorHandle* @pDepthStencilDescriptor = (D3D.CpuDescriptorHandle*)reader.ReadArray();
                        CommandBindingTrace.OMSetRenderTargets(target, @NumRenderTargetDescriptors, @pRenderTargetDescriptors, @RTsSingleHandleToDescriptorRange, @pDepthStencilDescriptor);
                        break;
                    }
                    case NativeCommandType.IASetVertexBuffers:
                    {
                        uint @StartSlot = reader.Read<uint>();
                        uint @NumViews = reader.Read<uint>();
                        D3D.VertexBufferView* @pViews = (D3D.VertexBufferView*)reader.ReadArray();
                        CommandBindingTrace.IASetVertexBuffers(target, @StartSlot, @NumViews, @pViews);
                        break;
                    }
                    case NativeCommandType.IASetIndexBuffer:
                    {
                        D3D.IndexBufferView* @pView = (D3D.IndexBufferView*)reader.ReadArray();
                        CommandBindingTrace.IASetIndexBuffer(target, @pView);
                        break;
                    }
                    case NativeCommandType.SOSetTargets:
                    {
                        uint @StartSlot = reader.Read<uint>();
                        uint @NumViews = reader.Read<uint>();
                        D3D.StreamOutputBufferView* @pViews = (D3D.StreamOutputBufferView*)reader.ReadArray();
                        CommandBindingTrace.SOSetTargets(target, @StartSlot, @NumViews, @pViews);
                        break;
                    }
                    case NativeCommandType.OMSetBlendFactor:
                    {
                        float* @BlendFactor = (float*)reader.ReadArray();
                        CommandBindingTrace.OMSetBlendFactor(target, @BlendFactor);
                        break;
                    }
                    case NativeCommandType.OMSetStencilRef:
                    {
                        uint @StencilRef = reader.Read<uint>();
                        CommandBindingTrace.OMSetStencilRef(target, @StencilRef);
                        break;
                    }
                    case NativeCommandType.OMSetDepthBounds:
                    {
                        float @Min = reader.Read<float>();
                        float @Max = reader.Read<float>();
                        CommandBindingTrace.OMSetDepthBounds(target, @Min, @Max);
                        break;
                    }
                    case NativeCommandType.RSSetDepthBias:
                    {
                        float @DepthBias = reader.Read<float>();
                        float @DepthBiasClamp = reader.Read<float>();
                        float @SlopeScaledDepthBias = reader.Read<float>();
                        CommandBindingTrace.RSSetDepthBias(target, @DepthBias, @DepthBiasClamp, @SlopeScaledDepthBias);
                        break;
                    }
                    case NativeCommandType.IASetIndexBufferStripCutValue:
                    {
                        D3D.IndexBufferStripCutValue @IBStripCutValue = reader.Read<D3D.IndexBufferStripCutValue>();
                        CommandBindingTrace.IASetIndexBufferStripCutValue(target, @IBStripCutValue);
                        break;
                    }
                    case NativeCommandType.RSSetShadingRate:
                    {
                        D3D.ShadingRate @baseShadingRate = reader.Read<D3D.ShadingRate>();
                        D3D.ShadingRateCombiner* @combiners = (D3D.ShadingRateCombiner*)reader.ReadArray();
                        CommandBindingTrace.RSSetShadingRate(target, @baseShadingRate, @combiners);
                        break;
                    }
                    case NativeCommandType.RSSetShadingRateImage:
                    {
                        D3D.ID3D12Resource* @shadingRateImage = (D3D.ID3D12Resource*)reader.Read<nint>();
                        CommandBindingTrace.RSSetShadingRateImage(target, @shadingRateImage);
                        break;
                    }
                    case NativeCommandType.CopyBufferRegion:
                    {
                        D3D.ID3D12Resource* @pDstBuffer = (D3D.ID3D12Resource*)reader.Read<nint>();
                        ulong @DstOffset = reader.Read<ulong>();
                        D3D.ID3D12Resource* @pSrcBuffer = (D3D.ID3D12Resource*)reader.Read<nint>();
                        ulong @SrcOffset = reader.Read<ulong>();
                        ulong @NumBytes = reader.Read<ulong>();
                        CommandTransferTrace.CopyBufferRegion(target, @pDstBuffer, @DstOffset, @pSrcBuffer, @SrcOffset, @NumBytes);
                        break;
                    }
                    case NativeCommandType.CopyTextureRegion:
                    {
                        D3D.TextureCopyLocation* @pDst = (D3D.TextureCopyLocation*)reader.ReadArray();
                        uint @DstX = reader.Read<uint>();
                        uint @DstY = reader.Read<uint>();
                        uint @DstZ = reader.Read<uint>();
                        D3D.TextureCopyLocation* @pSrc = (D3D.TextureCopyLocation*)reader.ReadArray();
                        D3D.Box* @pSrcBox = (D3D.Box*)reader.ReadArray();
                        CommandTransferTrace.CopyTextureRegion(target, @pDst, @DstX, @DstY, @DstZ, @pSrc, @pSrcBox);
                        break;
                    }
                    case NativeCommandType.ResolveSubresourceRegion:
                    {
                        D3D.ID3D12Resource* @pDstResource = (D3D.ID3D12Resource*)reader.Read<nint>();
                        uint @DstSubresource = reader.Read<uint>();
                        uint @DstX = reader.Read<uint>();
                        uint @DstY = reader.Read<uint>();
                        D3D.ID3D12Resource* @pSrcResource = (D3D.ID3D12Resource*)reader.Read<nint>();
                        uint @SrcSubresource = reader.Read<uint>();
                        Maths.Box2D<int>* @pSrcRect = (Maths.Box2D<int>*)reader.ReadArray();
                        Dxgi.Format @Format = reader.Read<Dxgi.Format>();
                        D3D.ResolveMode @ResolveMode = reader.Read<D3D.ResolveMode>();
                        CommandTransferTrace.ResolveSubresourceRegion(target, @pDstResource, @DstSubresource, @DstX, @DstY, @pSrcResource, @SrcSubresource, @pSrcRect, @Format, @ResolveMode);
                        break;
                    }
                    case NativeCommandType.ResourceBarrier:
                    {
                        uint @NumBarriers = reader.Read<uint>();
                        D3D.ResourceBarrier* @pBarriers = (D3D.ResourceBarrier*)reader.ReadArray();
                        CommandTransferTrace.ResourceBarrier(target, @NumBarriers, @pBarriers);
                        break;
                    }
                    case NativeCommandType.Barrier:
                        ExecuteBarriers(target, ref reader);
                        break;
                    case NativeCommandType.ResolveQueryData:
                    {
                        D3D.ID3D12QueryHeap* @pQueryHeap = (D3D.ID3D12QueryHeap*)reader.Read<nint>();
                        D3D.QueryType @Type = reader.Read<D3D.QueryType>();
                        uint @StartIndex = reader.Read<uint>();
                        uint @NumQueries = reader.Read<uint>();
                        D3D.ID3D12Resource* @pDestinationBuffer = (D3D.ID3D12Resource*)reader.Read<nint>();
                        ulong @AlignedDestinationBufferOffset = reader.Read<ulong>();
                        CommandTransferTrace.ResolveQueryData(target, @pQueryHeap, @Type, @StartIndex, @NumQueries, @pDestinationBuffer, @AlignedDestinationBufferOffset);
                        break;
                    }
                    case NativeCommandType.ClearRenderTargetView:
                    {
                        D3D.CpuDescriptorHandle @RenderTargetView = reader.Read<D3D.CpuDescriptorHandle>();
                        float* @ColorRGBA = (float*)reader.ReadArray();
                        uint @NumRects = reader.Read<uint>();
                        Maths.Box2D<int>* @pRects = (Maths.Box2D<int>*)reader.ReadArray();
                        CommandExecutionTrace.ClearRenderTargetView(target, @RenderTargetView, @ColorRGBA, @NumRects, @pRects);
                        break;
                    }
                    case NativeCommandType.ClearDepthStencilView:
                    {
                        D3D.CpuDescriptorHandle @DepthStencilView = reader.Read<D3D.CpuDescriptorHandle>();
                        D3D.ClearFlags @ClearFlags = reader.Read<D3D.ClearFlags>();
                        float @Depth = reader.Read<float>();
                        byte @Stencil = reader.Read<byte>();
                        uint @NumRects = reader.Read<uint>();
                        Maths.Box2D<int>* @pRects = (Maths.Box2D<int>*)reader.ReadArray();
                        CommandExecutionTrace.ClearDepthStencilView(target, @DepthStencilView, @ClearFlags, @Depth, @Stencil, @NumRects, @pRects);
                        break;
                    }
                    case NativeCommandType.DrawInstanced:
                    {
                        uint @VertexCountPerInstance = reader.Read<uint>();
                        uint @InstanceCount = reader.Read<uint>();
                        uint @StartVertexLocation = reader.Read<uint>();
                        uint @StartInstanceLocation = reader.Read<uint>();
                        CommandExecutionTrace.DrawInstanced(target, @VertexCountPerInstance, @InstanceCount, @StartVertexLocation, @StartInstanceLocation);
                        break;
                    }
                    case NativeCommandType.DrawIndexedInstanced:
                    {
                        uint @IndexCountPerInstance = reader.Read<uint>();
                        uint @InstanceCount = reader.Read<uint>();
                        uint @StartIndexLocation = reader.Read<uint>();
                        int @BaseVertexLocation = reader.Read<int>();
                        uint @StartInstanceLocation = reader.Read<uint>();
                        CommandExecutionTrace.DrawIndexedInstanced(target, @IndexCountPerInstance, @InstanceCount, @StartIndexLocation, @BaseVertexLocation, @StartInstanceLocation);
                        break;
                    }
                    case NativeCommandType.Dispatch:
                    {
                        uint @ThreadGroupCountX = reader.Read<uint>();
                        uint @ThreadGroupCountY = reader.Read<uint>();
                        uint @ThreadGroupCountZ = reader.Read<uint>();
                        CommandExecutionTrace.Dispatch(target, @ThreadGroupCountX, @ThreadGroupCountY, @ThreadGroupCountZ);
                        break;
                    }
                    case NativeCommandType.BeginEvent:
                    {
                        uint @Metadata = reader.Read<uint>();
                        void* @pData = (void*)reader.ReadArray();
                        uint @Size = reader.Read<uint>();
                        CommandExecutionTrace.BeginEvent(target, @Metadata, @pData, @Size);
                        break;
                    }
                    case NativeCommandType.EndEvent:
                    {

                        CommandExecutionTrace.EndEvent(target);
                        break;
                    }
                    case NativeCommandType.ExecuteIndirect:
                    {
                        D3D.ID3D12CommandSignature* @pCommandSignature = (D3D.ID3D12CommandSignature*)reader.Read<nint>();
                        uint @MaxCommandCount = reader.Read<uint>();
                        D3D.ID3D12Resource* @pArgumentBuffer = (D3D.ID3D12Resource*)reader.Read<nint>();
                        ulong @ArgumentBufferOffset = reader.Read<ulong>();
                        D3D.ID3D12Resource* @pCountBuffer = (D3D.ID3D12Resource*)reader.Read<nint>();
                        ulong @CountBufferOffset = reader.Read<ulong>();
                        CommandExecutionTrace.ExecuteIndirect(target, @pCommandSignature, @MaxCommandCount, @pArgumentBuffer, @ArgumentBufferOffset, @pCountBuffer, @CountBufferOffset);
                        break;
                    }
                    case NativeCommandType.DispatchMesh:
                    {
                        uint @ThreadGroupCountX = reader.Read<uint>();
                        uint @ThreadGroupCountY = reader.Read<uint>();
                        uint @ThreadGroupCountZ = reader.Read<uint>();
                        CommandExecutionTrace.DispatchMesh(target, @ThreadGroupCountX, @ThreadGroupCountY, @ThreadGroupCountZ);
                        break;
                    }
                    case NativeCommandType.BeginQuery:
                    {
                        D3D.ID3D12QueryHeap* @pQueryHeap = (D3D.ID3D12QueryHeap*)reader.Read<nint>();
                        D3D.QueryType @Type = reader.Read<D3D.QueryType>();
                        uint @Index = reader.Read<uint>();
                        CommandExecutionTrace.BeginQuery(target, @pQueryHeap, @Type, @Index);
                        break;
                    }
                    case NativeCommandType.EndQuery:
                    {
                        D3D.ID3D12QueryHeap* @pQueryHeap = (D3D.ID3D12QueryHeap*)reader.Read<nint>();
                        D3D.QueryType @Type = reader.Read<D3D.QueryType>();
                        uint @Index = reader.Read<uint>();
                        CommandExecutionTrace.EndQuery(target, @pQueryHeap, @Type, @Index);
                        break;
                    }
                    case NativeCommandType.ClearUnorderedAccessViewUint:
                    {
                        D3D.GpuDescriptorHandle @ViewGPUHandleInCurrentHeap = reader.Read<D3D.GpuDescriptorHandle>();
                        D3D.CpuDescriptorHandle @ViewCPUHandle = reader.Read<D3D.CpuDescriptorHandle>();
                        D3D.ID3D12Resource* @pResource = (D3D.ID3D12Resource*)reader.Read<nint>();
                        uint* @Values = (uint*)reader.ReadArray();
                        uint @NumRects = reader.Read<uint>();
                        Maths.Box2D<int>* @pRects = (Maths.Box2D<int>*)reader.ReadArray();
                        CommandExecutionTrace.ClearUnorderedAccessViewUint(target, @ViewGPUHandleInCurrentHeap, @ViewCPUHandle, @pResource, @Values, @NumRects, @pRects);
                        break;
                    }
                }
            }
        }
    }
}
