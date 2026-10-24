namespace SomeEngine.Graphics.Direct3D12;
internal static unsafe class DevicePipelineTrace
{
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateCommandAllocator = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateCommandAllocator), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateCommandList1 = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateCommandList1), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12DeviceFactoryCreateDevice = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12DeviceFactory) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12DeviceFactory.CreateDevice), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateCommandQueue = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateCommandQueue), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateFence = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateFence), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateCommandSignature = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateCommandSignature), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateComputePipelineState = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateComputePipelineState), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreatePipelineState = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreatePipelineState), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateSharedHandle = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateSharedHandle), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateStateObject = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateStateObject), detail: true);
    internal static readonly global::SomeEngine.Diagnostics.TraceSite NativeID3D12Device10CreateRootSignature = new("native", nameof(global::Silk.NET.Direct3D12.ID3D12Device10) + "." + nameof(global::Silk.NET.Direct3D12.ID3D12Device10.CreateRootSignature), detail: true);
    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateCommandAllocator(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.CommandListType @type, global::System.Guid* @riid, void** @ppCommandAllocator)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreateCommandAllocator);
        return target->CreateCommandAllocator(@type, @riid, @ppCommandAllocator);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateCommandList1(global::Silk.NET.Direct3D12.ID3D12Device10* target, uint @nodeMask, global::Silk.NET.Direct3D12.CommandListType @type, global::Silk.NET.Direct3D12.CommandListFlags @flags, global::System.Guid* @riid, void** @ppCommandList)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreateCommandList1);
        return target->CreateCommandList1(@nodeMask, @type, @flags, @riid, @ppCommandList);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateDevice(global::Silk.NET.Direct3D12.ID3D12DeviceFactory* target, global::Silk.NET.Core.Native.IUnknown* @adapter, global::Silk.NET.Core.Native.D3DFeatureLevel @FeatureLevel, global::System.Guid* @riid, void** @ppvDevice)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12DeviceFactoryCreateDevice);
        return target->CreateDevice(@adapter, @FeatureLevel, @riid, @ppvDevice);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateCommandQueue(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.CommandQueueDesc* @pDesc, global::System.Guid* @riid, void** @ppCommandQueue)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreateCommandQueue);
        return target->CreateCommandQueue(@pDesc, @riid, @ppCommandQueue);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateFence(global::Silk.NET.Direct3D12.ID3D12Device10* target, ulong @InitialValue, global::Silk.NET.Direct3D12.FenceFlags @Flags, global::System.Guid* @riid, void** @ppFence)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreateFence);
        return target->CreateFence(@InitialValue, @Flags, @riid, @ppFence);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateCommandSignature(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.CommandSignatureDesc* @pDesc, global::Silk.NET.Direct3D12.ID3D12RootSignature* @pRootSignature, global::System.Guid* @riid, void** @ppvCommandSignature)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreateCommandSignature);
        return target->CreateCommandSignature(@pDesc, @pRootSignature, @riid, @ppvCommandSignature);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateComputePipelineState(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ComputePipelineStateDesc* @pDesc, global::System.Guid* @riid, void** @ppPipelineState)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreateComputePipelineState);
        return target->CreateComputePipelineState(@pDesc, @riid, @ppPipelineState);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreatePipelineState(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.PipelineStateStreamDesc* @pDesc, global::System.Guid* @riid, void** @ppPipelineState)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreatePipelineState);
        return target->CreatePipelineState(@pDesc, @riid, @ppPipelineState);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateSharedHandle(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.ID3D12DeviceChild* @pObject, global::Silk.NET.Core.Native.SecurityAttributes* @pAttributes, uint @Access, char* @Name, void** @pHandle)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreateSharedHandle);
        return target->CreateSharedHandle(@pObject, @pAttributes, @Access, @Name, @pHandle);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateStateObject(global::Silk.NET.Direct3D12.ID3D12Device10* target, global::Silk.NET.Direct3D12.StateObjectDesc* @pDesc, global::System.Guid* @riid, void** @ppStateObject)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreateStateObject);
        return target->CreateStateObject(@pDesc, @riid, @ppStateObject);
    }

    [global::System.Runtime.CompilerServices.MethodImpl(global::System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    internal static int CreateRootSignature(global::Silk.NET.Direct3D12.ID3D12Device10* target, uint @nodeMask, void* @pBlobWithRootSignature, nuint @blobLengthInBytes, global::System.Guid* @riid, void** @ppvRootSignature)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(global::SomeEngine.Graphics.Direct3D12.DevicePipelineTrace.NativeID3D12Device10CreateRootSignature);
        return target->CreateRootSignature(@nodeMask, @pBlobWithRootSignature, @blobLengthInBytes, @riid, @ppvRootSignature);
    }
}