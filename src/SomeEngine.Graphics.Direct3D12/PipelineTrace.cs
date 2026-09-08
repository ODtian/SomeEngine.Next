namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class PipelineTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12PipelineStateRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12PipelineState) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12PipelineState.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12PipelineStateGetCachedBlob = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12PipelineState) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12PipelineState.GetCachedBlob), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12StateObjectRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12StateObject) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12StateObject.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12StateObjectQueryInterface = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12StateObject) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12StateObject.QueryInterface), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12StateObjectPropertiesRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12StateObjectPropertiesGetShaderIdentifier = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties.GetShaderIdentifier), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12RootSignatureRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12RootSignature) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12RootSignature.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12WorkGraphPropertiesRelease = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12StateObjectProperties1Release = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties1.Release), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12StateObjectProperties1GetProgramIdentifier = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties1) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties1.GetProgramIdentifier), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12WorkGraphPropertiesGetWorkGraphIndex = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties.GetWorkGraphIndex), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12WorkGraphPropertiesGetNumEntrypoints = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties.GetNumEntrypoints), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12WorkGraphPropertiesGetWorkGraphMemoryRequirements = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties.GetWorkGraphMemoryRequirements), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12WorkGraphPropertiesGetEntrypointID = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties.GetEntrypointID), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12WorkGraphPropertiesGetEntrypointIndex = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties.GetEntrypointIndex), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12WorkGraphPropertiesGetEntrypointRecordSizeInBytes = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties.GetEntrypointRecordSizeInBytes), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12WorkGraphPropertiesGetEntrypointRecordAlignmentInBytes = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties.GetEntrypointRecordAlignmentInBytes), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12PipelineState* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12PipelineStateRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int GetCachedBlob(global::Silk.NET.Direct3D12.ID3D12PipelineState* target, global::Silk.NET.Core.Native.ID3D10Blob** @ppBlob)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12PipelineStateGetCachedBlob);
        return target->GetCachedBlob(@ppBlob);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12StateObject* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12StateObjectRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int QueryInterface(global::Silk.NET.Direct3D12.ID3D12StateObject* target, global::System.Guid* @riid, void** @ppvObject)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12StateObjectQueryInterface);
        return target->QueryInterface(@riid, @ppvObject);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12StateObjectPropertiesRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void* GetShaderIdentifier(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties* target, string @pExportName)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12StateObjectPropertiesGetShaderIdentifier);
        return target->GetShaderIdentifier(@pExportName);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12RootSignature* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12RootSignatureRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12WorkGraphPropertiesRelease);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint Release(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties1* target)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12StateObjectProperties1Release);
        return target->Release();
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static global::Silk.NET.Direct3D12.ProgramIdentifier GetProgramIdentifier(global::Silk.NET.Direct3D12.ID3D12StateObjectProperties1* target, string @pProgramName)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12StateObjectProperties1GetProgramIdentifier);
        return target->GetProgramIdentifier(@pProgramName);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint GetWorkGraphIndex(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties* target, string @pProgramName)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12WorkGraphPropertiesGetWorkGraphIndex);
        return target->GetWorkGraphIndex(@pProgramName);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint GetNumEntrypoints(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties* target, uint @WorkGraphIndex)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12WorkGraphPropertiesGetNumEntrypoints);
        return target->GetNumEntrypoints(@WorkGraphIndex);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static void GetWorkGraphMemoryRequirements(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties* target, uint @WorkGraphIndex, global::Silk.NET.Direct3D12.WorkGraphMemoryRequirements* @pWorkGraphMemoryRequirements)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12WorkGraphPropertiesGetWorkGraphMemoryRequirements);
        target->GetWorkGraphMemoryRequirements(@WorkGraphIndex, @pWorkGraphMemoryRequirements);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static global::Silk.NET.Direct3D12.NodeID GetEntrypointID(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties* target, uint @WorkGraphIndex, uint @EntrypointIndex)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12WorkGraphPropertiesGetEntrypointID);
        return target->GetEntrypointID(@WorkGraphIndex, @EntrypointIndex);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint GetEntrypointIndex(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties* target, uint @WorkGraphIndex, global::Silk.NET.Direct3D12.NodeID @NodeID)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12WorkGraphPropertiesGetEntrypointIndex);
        return target->GetEntrypointIndex(@WorkGraphIndex, @NodeID);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint GetEntrypointRecordSizeInBytes(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties* target, uint @WorkGraphIndex, uint @EntrypointIndex)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12WorkGraphPropertiesGetEntrypointRecordSizeInBytes);
        return target->GetEntrypointRecordSizeInBytes(@WorkGraphIndex, @EntrypointIndex);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static uint GetEntrypointRecordAlignmentInBytes(global::Silk.NET.Direct3D12.ID3D12WorkGraphProperties* target, uint @WorkGraphIndex, uint @EntrypointIndex)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.PipelineTrace.NativeID3D12WorkGraphPropertiesGetEntrypointRecordAlignmentInBytes);
        return target->GetEntrypointRecordAlignmentInBytes(@WorkGraphIndex, @EntrypointIndex);
    }
}