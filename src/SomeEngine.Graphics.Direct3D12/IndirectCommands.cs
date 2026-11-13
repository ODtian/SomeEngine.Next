using System.Runtime.InteropServices;
using Silk.NET.Direct3D12;
using SlangShaderSharp;
using NativeIndirectArgumentDesc = Silk.NET.Direct3D12.IndirectArgumentDesc;
using NativeIndirectArgumentType = Silk.NET.Direct3D12.IndirectArgumentType;

namespace SomeEngine.Graphics.Direct3D12;
internal sealed unsafe partial class D3D12Backend
{
    public IndirectCommandLayout CreateIndirectCommandLayout(Device device, in IndirectCommandLayoutDesc desc)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiResourceCreation);
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        D3D12Pipeline? pipeline = null;
        if (desc.Pipeline is Pipeline publicPipeline)
            pipeline = RequirePipeline(publicPipeline);
        NativeIndirectArgumentDesc[] nativeArguments = BuildIndirectArguments(pipeline, desc.Arguments, out IndirectLayoutEffects effects, out uint[] vertexSlots, out bool requiresRootSignature);
        ID3D12CommandSignature* signature = CreateNativeCommandSignature(nativeDevice, pipeline, desc.Stride, nativeArguments, requiresRootSignature);
        D3D12IndirectCommandLayout? result = null;
        try
        {
            result = new D3D12IndirectCommandLayout(nativeDevice, signature, desc.Stride, effects, vertexSlots, pipeline, desc.Pipeline, desc.Label);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
                _ = ResourceTrace.Release(signature);
            else
                result.Dispose();
            throw;
        }
    }

    private static NativeIndirectArgumentDesc[] BuildIndirectArguments(
        D3D12Pipeline? pipeline,
        ReadOnlySpan<SomeEngine.Graphics.IndirectArgumentDesc> arguments,
        out IndirectLayoutEffects effects,
        out uint[] vertexSlots,
        out bool requiresRootSignature
    )
    {
        var nativeArguments = new NativeIndirectArgumentDesc[arguments.Length];
        var entries = new HashSet<uint>();
        effects = IndirectLayoutEffects.None;
        requiresRootSignature = false;

        for (int index = 0; index < nativeArguments.Length; index++)
        {
            ref readonly SomeEngine.Graphics.IndirectArgumentDesc argument = ref arguments[index];
            nativeArguments[index] = BuildIndirectArgument(pipeline, argument, entries, ref effects, ref requiresRootSignature);

        }

        vertexSlots = [..entries];
        return nativeArguments;
    }

    private static NativeIndirectArgumentDesc BuildIndirectArgument(
        D3D12Pipeline? pipeline,
        in SomeEngine.Graphics.IndirectArgumentDesc argument,
        HashSet<uint> vertexSlots,
        ref IndirectLayoutEffects effects,
        ref bool requiresRootSignature
    )
    {

        NativeIndirectArgumentDesc native = default;
        native.Type = ToNativeIndirectArgumentType(argument.Type);
        switch (argument.Type)
        {
            case SomeEngine.Graphics.IndirectArgumentType.Draw:
                effects |= IndirectLayoutEffects.Draw;

                break;
            case SomeEngine.Graphics.IndirectArgumentType.DrawIndexed:
                effects |= IndirectLayoutEffects.Draw;

                break;
            case SomeEngine.Graphics.IndirectArgumentType.Dispatch:
                effects |= IndirectLayoutEffects.Compute;

                break;
            case SomeEngine.Graphics.IndirectArgumentType.DispatchMesh:
                effects |= IndirectLayoutEffects.Draw;

                break;
            case SomeEngine.Graphics.IndirectArgumentType.DispatchRays:
                effects |= IndirectLayoutEffects.Compute;

                break;
            case SomeEngine.Graphics.IndirectArgumentType.WorkGraph:
                return default!;
            case SomeEngine.Graphics.IndirectArgumentType.VertexBuffer:
                native.VertexBuffer.Slot = argument.VertexBinding;
                vertexSlots.Add(argument.VertexBinding);
                effects |= IndirectLayoutEffects.VertexBuffer;

                break;
            case SomeEngine.Graphics.IndirectArgumentType.IndexBuffer:
                effects |= IndirectLayoutEffects.IndexBuffer;

                break;
            case SomeEngine.Graphics.IndirectArgumentType.Constants:
                BuildIndirectConstantsArgument(pipeline, argument, ref native, ref effects, ref requiresRootSignature);

                break;
            case SomeEngine.Graphics.IndirectArgumentType.ConstantBuffer:
                BuildIndirectConstantBufferArgument(pipeline, argument, ref native, ref effects, ref requiresRootSignature);

                break;
            case SomeEngine.Graphics.IndirectArgumentType.ShaderResource:
            case SomeEngine.Graphics.IndirectArgumentType.UnorderedAccess:
                return default!;
            default:
                return default!;
        }

        return native;
    }

    private static void BuildIndirectConstantsArgument(
        D3D12Pipeline? pipeline,
        in SomeEngine.Graphics.IndirectArgumentDesc argument,
        ref NativeIndirectArgumentDesc native,
        ref IndirectLayoutEffects effects,
        ref bool requiresRootSignature
    )
    {
        IndirectRootDestination destination = pipeline!.RootSignature.ResolveIndirectRoot(argument.Parameters!.Value, argument.Type, argument.ByteOffset, argument.ValueCount);
        native.Constant.RootParameterIndex = destination.RootParameterIndex;
        native.Constant.DestOffsetIn32BitValues = destination.DestinationDwordOffset;
        native.Constant.Num32BitValuesToSet = argument.ValueCount;
        requiresRootSignature = true;
        effects |= IndirectLayoutEffects.RootArguments;
    }

    private static void BuildIndirectConstantBufferArgument(
        D3D12Pipeline? pipeline,
        in SomeEngine.Graphics.IndirectArgumentDesc argument,
        ref NativeIndirectArgumentDesc native,
        ref IndirectLayoutEffects effects,
        ref bool requiresRootSignature
    )
    {
        IndirectRootDestination destination = pipeline!.RootSignature.ResolveIndirectRoot(argument.Parameters!.Value, argument.Type, argument.ByteOffset, argument.ValueCount);
        native.ConstantBufferView.RootParameterIndex = destination.RootParameterIndex;
        requiresRootSignature = true;
        effects |= IndirectLayoutEffects.RootArguments;
    }

    private static ID3D12CommandSignature* CreateNativeCommandSignature(
        D3D12Device nativeDevice,
        D3D12Pipeline? pipeline,
        uint stride,
        NativeIndirectArgumentDesc[] nativeArguments,
        bool requiresRootSignature
    )
    {
        ID3D12CommandSignature* signature = null;
        fixed (NativeIndirectArgumentDesc* arguments = nativeArguments)
        {
            CommandSignatureDesc nativeDescription = new(stride, (uint)nativeArguments.Length, arguments, nativeDevice.EnabledNodeMask);
            Guid iid = ID3D12CommandSignature.Guid;
            ThrowIfFailed(nativeDevice, DevicePipelineTrace.CreateCommandSignature(
                nativeDevice.Native,
                &nativeDescription,
                requiresRootSignature ? pipeline!.RootSignature.Native : null,
                &iid,
                (void**)&signature
            ), NativeOperationType.Ordinary, "ID3D12Device::CreateCommandSignature");
        }

        return signature;
    }

    public void ExecuteIndirect(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count = null
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        IndirectCommands capability = command.NativeDevice.RequireCapability<IndirectCommands>(nameof(ExecuteIndirect));
        D3D12IndirectCommandLayout nativeLayout = RequireIndirectCommandLayout(layout);
        if (nativeLayout.RequiredRoot is D3D12RootSignatureState requiredRoot)
        {
            D3D12Pipeline currentPipeline = command.Pipeline;
        }

        D3D12Buffer argumentBuffer = RequireBuffer(arguments.Buffer);
        BufferRange argumentRange = arguments.Range.Expand(argumentBuffer.Info.Size);
        ulong requiredBytes = checked((ulong)nativeLayout.Stride * maximumCommandCount);
        D3D12Buffer? countBuffer = null;
        ulong countOffset = 0;
        if (count is BufferRegion countRegion)
        {
            countBuffer = RequireBuffer(countRegion.Buffer);
            BufferRange countRange = countRegion.Range.Expand(countBuffer.Info.Size);
            countOffset = countRange.Offset;
        }

        int retainedCount = countBuffer is null ? 2 : 3;
        ID3D12Resource* nativeCountBuffer = countBuffer is null ? null : countBuffer.Native;
        CommandExecutionTrace.ExecuteIndirect(
            command,
            command.List,
            nativeLayout.Native,
            maximumCommandCount,
            argumentBuffer.Native,
            argumentRange.Offset,
            nativeCountBuffer,
            countOffset
        );
        command.InvalidateIndirectState(nativeLayout);
    }

    private static NativeIndirectArgumentType ToNativeIndirectArgumentType(SomeEngine.Graphics.IndirectArgumentType value) => value switch
    {
        SomeEngine.Graphics.IndirectArgumentType.Draw => NativeIndirectArgumentType.Draw,
        SomeEngine.Graphics.IndirectArgumentType.DrawIndexed => NativeIndirectArgumentType.DrawIndexed,
        SomeEngine.Graphics.IndirectArgumentType.Dispatch => NativeIndirectArgumentType.Dispatch,
        SomeEngine.Graphics.IndirectArgumentType.DispatchMesh => NativeIndirectArgumentType.DispatchMesh,
        SomeEngine.Graphics.IndirectArgumentType.DispatchRays => NativeIndirectArgumentType.DispatchRays,
        SomeEngine.Graphics.IndirectArgumentType.VertexBuffer => NativeIndirectArgumentType.VertexBufferView,
        SomeEngine.Graphics.IndirectArgumentType.IndexBuffer => NativeIndirectArgumentType.IndexBufferView,
        SomeEngine.Graphics.IndirectArgumentType.Constants => NativeIndirectArgumentType.Constant,
        SomeEngine.Graphics.IndirectArgumentType.ConstantBuffer => NativeIndirectArgumentType.ConstantBufferView,
        SomeEngine.Graphics.IndirectArgumentType.ShaderResource => NativeIndirectArgumentType.ShaderResourceView,
        SomeEngine.Graphics.IndirectArgumentType.UnorderedAccess => NativeIndirectArgumentType.UnorderedAccessView,
        SomeEngine.Graphics.IndirectArgumentType.WorkGraph => default!,
        _ => default!,
    };
    private static bool IsAction(SomeEngine.Graphics.IndirectArgumentType value) => value is SomeEngine.Graphics.IndirectArgumentType.Draw or SomeEngine.Graphics.IndirectArgumentType.DrawIndexed or SomeEngine.Graphics.IndirectArgumentType.Dispatch or SomeEngine.Graphics.IndirectArgumentType.DispatchMesh or SomeEngine.Graphics.IndirectArgumentType.DispatchRays or SomeEngine.Graphics.IndirectArgumentType.WorkGraph;
    [Flags]
    private enum IndirectLayoutEffects : byte
    {
        None = 0,
        Draw = 1 << 0,
        Compute = 1 << 1,
        VertexBuffer = 1 << 2,
        IndexBuffer = 1 << 3,
        RootArguments = 1 << 4,
    }

    private sealed class D3D12IndirectCommandLayout : IndirectCommandLayout
    {
        private readonly D3D12Device _device;
        private readonly NativeResourceData _native;
        private SlangShaderData? _slangCode;
        internal D3D12IndirectCommandLayout(
            D3D12Device device,
            ID3D12CommandSignature* native,
            uint stride,
            IndirectLayoutEffects effects,
            uint[] vertexSlots,
            D3D12Pipeline? nativePipeline,
            Pipeline? pipeline,
            string? label
        ) : base(device, stride, pipeline, label)
        {
            _device = device;
            NativeResourceData? pipelineState = null;
            SlangShaderData? slangCode = null;
            try
            {
                if (nativePipeline is not null)
                {
                    pipelineState = nativePipeline.NativeData;
                    slangCode = nativePipeline.GetSlangCodeReference();
                    RequiredRoot = nativePipeline.RootSignature;
                }

                _native = new NativeResourceData((Silk.NET.Core.Native.IUnknown*)native, ownsReference: true, dependency: pipelineState);
                _slangCode = slangCode;
                slangCode = null;
            }
            catch
            {
                throw;
            }

            Effects = effects;
            VertexSlots = vertexSlots;
        }

        internal ID3D12CommandSignature* Native => (ID3D12CommandSignature*)_native.Pointer;
        internal NativeResourceData NativeData => _native;
        internal D3D12RootSignatureState? RequiredRoot { get; }
        internal IndirectLayoutEffects Effects { get; }
        internal uint[] VertexSlots { get; }

        internal override void Release(bool fromParent)
        {
            _native.Release();
            _ = Interlocked.Exchange(ref _slangCode, null);
            _device.UnregisterChild(this);
        }
    }

    private sealed partial class D3D12CommandContext
    {
        internal void InvalidateIndirectState(D3D12IndirectCommandLayout layout)
        {
            if ((layout.Effects & IndirectLayoutEffects.VertexBuffer) != 0)
            {
                foreach (uint entry in layout.VertexSlots)
                {
                    int index = checked((int)entry);
                    _vertexBuffers[index] = default;
                    _vertexBufferSetMask &= ~(1u << index);
                }
            }

            if ((layout.Effects & IndirectLayoutEffects.IndexBuffer) != 0)
                _hasIndexBuffer = false;
            if ((layout.Effects & IndirectLayoutEffects.RootArguments) != 0)
                InvalidateParameterBindingState();
        }

        internal void InvalidateParameterBindingState()
        {
            ClearRootBindingState();
        }
    }

    private static partial class RequireD3D12
    {
        internal static D3D12IndirectCommandLayout IndirectCommandLayout(IndirectCommandLayout value) => (D3D12IndirectCommandLayout)value;
    }
}
