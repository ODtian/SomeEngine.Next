namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class CommandBindingTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetPipelineState1 = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetPipelineState1), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetPipelineState = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetPipelineState), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetDescriptorHeaps = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetDescriptorHeaps), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetComputeRootSignature = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetComputeRootSignature), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetGraphicsRootSignature = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetGraphicsRootSignature), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetComputeRootDescriptorTable = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetComputeRootDescriptorTable), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetGraphicsRootDescriptorTable = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetGraphicsRootDescriptorTable), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetComputeRootConstantBufferView = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetComputeRootConstantBufferView), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetGraphicsRootConstantBufferView = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetGraphicsRootConstantBufferView), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetComputeRoot32BitConstants = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetComputeRoot32BitConstants), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetGraphicsRoot32BitConstants = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetGraphicsRoot32BitConstants), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetPredication = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetPredication), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetMarker = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetMarker), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SetProgram = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SetProgram), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetPipelineState1(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ID3D12StateObject* @pStateObject)
    {
        target = D3D12Backend.RequireNativeRecording(target);
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetPipelineState1);
        target->SetPipelineState1(@pStateObject);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetPipelineState(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ID3D12PipelineState* @pPipelineState)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetPipelineState);
            commands.Write((nint)@pPipelineState);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetPipelineState);
        target->SetPipelineState(@pPipelineState);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetDescriptorHeaps(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @NumDescriptorHeaps, global::Silk.NET.Direct3D12.ID3D12DescriptorHeap** @ppDescriptorHeaps)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetDescriptorHeaps);
            commands.Write(@NumDescriptorHeaps);
            commands.WriteArray(@ppDescriptorHeaps, checked((int)(NumDescriptorHeaps * sizeof(nint))));
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetDescriptorHeaps);
        target->SetDescriptorHeaps(@NumDescriptorHeaps, @ppDescriptorHeaps);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetComputeRootSignature(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ID3D12RootSignature* @pRootSignature)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetComputeRootSignature);
            commands.Write((nint)@pRootSignature);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetComputeRootSignature);
        target->SetComputeRootSignature(@pRootSignature);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetGraphicsRootSignature(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ID3D12RootSignature* @pRootSignature)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetGraphicsRootSignature);
            commands.Write((nint)@pRootSignature);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetGraphicsRootSignature);
        target->SetGraphicsRootSignature(@pRootSignature);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetComputeRootDescriptorTable(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @RootParameterIndex, global::Silk.NET.Direct3D12.GpuDescriptorHandle @BaseDescriptor)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetComputeRootDescriptorTable);
            commands.Write(@RootParameterIndex);
            commands.Write(@BaseDescriptor);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetComputeRootDescriptorTable);
        target->SetComputeRootDescriptorTable(@RootParameterIndex, @BaseDescriptor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetGraphicsRootDescriptorTable(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @RootParameterIndex, global::Silk.NET.Direct3D12.GpuDescriptorHandle @BaseDescriptor)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetGraphicsRootDescriptorTable);
            commands.Write(@RootParameterIndex);
            commands.Write(@BaseDescriptor);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetGraphicsRootDescriptorTable);
        target->SetGraphicsRootDescriptorTable(@RootParameterIndex, @BaseDescriptor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetComputeRootConstantBufferView(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @RootParameterIndex, ulong @BufferLocation)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetComputeRootConstantBufferView);
            commands.Write(@RootParameterIndex);
            commands.Write(@BufferLocation);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetComputeRootConstantBufferView);
        target->SetComputeRootConstantBufferView(@RootParameterIndex, @BufferLocation);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetGraphicsRootConstantBufferView(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @RootParameterIndex, ulong @BufferLocation)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetGraphicsRootConstantBufferView);
            commands.Write(@RootParameterIndex);
            commands.Write(@BufferLocation);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetGraphicsRootConstantBufferView);
        target->SetGraphicsRootConstantBufferView(@RootParameterIndex, @BufferLocation);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetComputeRoot32BitConstants(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @RootParameterIndex, uint @Num32BitValuesToSet, void* @pSrcData, uint @DestOffsetIn32BitValues)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetComputeRoot32BitConstants);
            commands.Write(@RootParameterIndex);
            commands.Write(@Num32BitValuesToSet);
            commands.WriteArray(@pSrcData, checked((int)(Num32BitValuesToSet * 4)));
            commands.Write(@DestOffsetIn32BitValues);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetComputeRoot32BitConstants);
        target->SetComputeRoot32BitConstants(@RootParameterIndex, @Num32BitValuesToSet, @pSrcData, @DestOffsetIn32BitValues);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetGraphicsRoot32BitConstants(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @RootParameterIndex, uint @Num32BitValuesToSet, void* @pSrcData, uint @DestOffsetIn32BitValues)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetGraphicsRoot32BitConstants);
            commands.Write(@RootParameterIndex);
            commands.Write(@Num32BitValuesToSet);
            commands.WriteArray(@pSrcData, checked((int)(Num32BitValuesToSet * 4)));
            commands.Write(@DestOffsetIn32BitValues);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetGraphicsRoot32BitConstants);
        target->SetGraphicsRoot32BitConstants(@RootParameterIndex, @Num32BitValuesToSet, @pSrcData, @DestOffsetIn32BitValues);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetPredication(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @pBuffer, ulong @AlignedBufferOffset, global::Silk.NET.Direct3D12.PredicationOp @Operation)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetPredication);
            commands.Write((nint)@pBuffer);
            commands.Write(@AlignedBufferOffset);
            commands.Write(@Operation);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetPredication);
        target->SetPredication(@pBuffer, @AlignedBufferOffset, @Operation);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetMarker(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @Metadata, void* @pData, uint @Size)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SetMarker);
            commands.Write(@Metadata);
            commands.WriteArray(@pData, checked((int)(Size)));
            commands.Write(@Size);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetMarker);
        target->SetMarker(@Metadata, @pData, @Size);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SetProgram(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.SetProgramDesc* @pDesc)
    {
        target = D3D12Backend.RequireNativeRecording(target);
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SetProgram);
        target->SetProgram(@pDesc);
    }

    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10IASetPrimitiveTopology = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.IASetPrimitiveTopology), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10RSSetViewports = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.RSSetViewports), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10RSSetScissorRects = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.RSSetScissorRects), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10OMSetRenderTargets = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.OMSetRenderTargets), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10IASetVertexBuffers = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.IASetVertexBuffers), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10IASetIndexBuffer = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.IASetIndexBuffer), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10SOSetTargets = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.SOSetTargets), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10OMSetBlendFactor = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.OMSetBlendFactor), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10OMSetStencilRef = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.OMSetStencilRef), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10OMSetDepthBounds = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.OMSetDepthBounds), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10RSSetDepthBias = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.RSSetDepthBias), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10IASetIndexBufferStripCutValue = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.IASetIndexBufferStripCutValue), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10RSSetShadingRate = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.RSSetShadingRate), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12GraphicsCommandList10RSSetShadingRateImage = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10.RSSetShadingRateImage), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void IASetPrimitiveTopology(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Core.Native.D3DPrimitiveTopology @PrimitiveTopology)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.IASetPrimitiveTopology);
            commands.Write(@PrimitiveTopology);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10IASetPrimitiveTopology);
        target->IASetPrimitiveTopology(@PrimitiveTopology);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void RSSetViewports(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @NumViewports, global::Silk.NET.Direct3D12.Viewport* @pViewports)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.RSSetViewports);
            commands.Write(@NumViewports);
            commands.WriteArray(@pViewports, checked((int)(NumViewports * sizeof(global::Silk.NET.Direct3D12.Viewport))));
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10RSSetViewports);
        target->RSSetViewports(@NumViewports, @pViewports);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void RSSetScissorRects(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @NumRects, global::Silk.NET.Maths.Box2D<int>* @pRects)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.RSSetScissorRects);
            commands.Write(@NumRects);
            commands.WriteArray(@pRects, checked((int)(NumRects * sizeof(global::Silk.NET.Maths.Box2D<int>))));
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10RSSetScissorRects);
        target->RSSetScissorRects(@NumRects, @pRects);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void OMSetRenderTargets(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @NumRenderTargetDescriptors, global::Silk.NET.Direct3D12.CpuDescriptorHandle* @pRenderTargetDescriptors, global::Silk.NET.Core.Bool32 @RTsSingleHandleToDescriptorRange, global::Silk.NET.Direct3D12.CpuDescriptorHandle* @pDepthStencilDescriptor)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.OMSetRenderTargets);
            commands.Write(@NumRenderTargetDescriptors);
            commands.WriteArray(@pRenderTargetDescriptors, checked((int)((RTsSingleHandleToDescriptorRange ? (NumRenderTargetDescriptors == 0 ? 0u : 1u) : NumRenderTargetDescriptors) * sizeof(global::Silk.NET.Direct3D12.CpuDescriptorHandle))));
            commands.Write(@RTsSingleHandleToDescriptorRange);
            commands.WriteArray(@pDepthStencilDescriptor, checked((int)(pDepthStencilDescriptor == null ? 0 : sizeof(global::Silk.NET.Direct3D12.CpuDescriptorHandle))));
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10OMSetRenderTargets);
        target->OMSetRenderTargets(@NumRenderTargetDescriptors, @pRenderTargetDescriptors, @RTsSingleHandleToDescriptorRange, @pDepthStencilDescriptor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void IASetVertexBuffers(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @StartSlot, uint @NumViews, global::Silk.NET.Direct3D12.VertexBufferView* @pViews)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.IASetVertexBuffers);
            commands.Write(@StartSlot);
            commands.Write(@NumViews);
            commands.WriteArray(@pViews, checked((int)(NumViews * sizeof(global::Silk.NET.Direct3D12.VertexBufferView))));
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10IASetVertexBuffers);
        target->IASetVertexBuffers(@StartSlot, @NumViews, @pViews);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void IASetIndexBuffer(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.IndexBufferView* @pView)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.IASetIndexBuffer);
            commands.WriteArray(@pView, checked((int)(pView == null ? 0 : sizeof(global::Silk.NET.Direct3D12.IndexBufferView))));
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10IASetIndexBuffer);
        target->IASetIndexBuffer(@pView);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void SOSetTargets(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @StartSlot, uint @NumViews, global::Silk.NET.Direct3D12.StreamOutputBufferView* @pViews)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.SOSetTargets);
            commands.Write(@StartSlot);
            commands.Write(@NumViews);
            commands.WriteArray(@pViews, checked((int)(NumViews * sizeof(global::Silk.NET.Direct3D12.StreamOutputBufferView))));
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10SOSetTargets);
        target->SOSetTargets(@StartSlot, @NumViews, @pViews);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void OMSetBlendFactor(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, float* @BlendFactor)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.OMSetBlendFactor);
            commands.WriteArray(@BlendFactor, checked((int)(4 * sizeof(float))));
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10OMSetBlendFactor);
        target->OMSetBlendFactor(@BlendFactor);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void OMSetStencilRef(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, uint @StencilRef)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.OMSetStencilRef);
            commands.Write(@StencilRef);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10OMSetStencilRef);
        target->OMSetStencilRef(@StencilRef);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void OMSetDepthBounds(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, float @Min, float @Max)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.OMSetDepthBounds);
            commands.Write(@Min);
            commands.Write(@Max);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10OMSetDepthBounds);
        target->OMSetDepthBounds(@Min, @Max);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void RSSetDepthBias(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, float @DepthBias, float @DepthBiasClamp, float @SlopeScaledDepthBias)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.RSSetDepthBias);
            commands.Write(@DepthBias);
            commands.Write(@DepthBiasClamp);
            commands.Write(@SlopeScaledDepthBias);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10RSSetDepthBias);
        target->RSSetDepthBias(@DepthBias, @DepthBiasClamp, @SlopeScaledDepthBias);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void IASetIndexBufferStripCutValue(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.IndexBufferStripCutValue @IBStripCutValue)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.IASetIndexBufferStripCutValue);
            commands.Write(@IBStripCutValue);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10IASetIndexBufferStripCutValue);
        target->IASetIndexBufferStripCutValue(@IBStripCutValue);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void RSSetShadingRate(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ShadingRate @baseShadingRate, global::Silk.NET.Direct3D12.ShadingRateCombiner* @combiners)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.RSSetShadingRate);
            commands.Write(@baseShadingRate);
            commands.WriteArray(@combiners, checked((int)(2 * sizeof(global::Silk.NET.Direct3D12.ShadingRateCombiner))));
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10RSSetShadingRate);
        target->RSSetShadingRate(@baseShadingRate, @combiners);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void RSSetShadingRateImage(global::Silk.NET.Direct3D12.ID3D12GraphicsCommandList10* target, global::Silk.NET.Direct3D12.ID3D12Resource* @shadingRateImage)
    {
        if (D3D12Backend.CollectCommandData(ref target) is { } commands)
        {
            commands.Write(NativeCommandType.RSSetShadingRateImage);
            commands.Write((nint)@shadingRateImage);
            return;
        }
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.CommandBindingTrace.NativeID3D12GraphicsCommandList10RSSetShadingRateImage);
        target->RSSetShadingRateImage(@shadingRateImage);
    }
}