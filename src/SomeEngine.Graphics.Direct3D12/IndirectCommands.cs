using System.Runtime.InteropServices;
using Silk.NET.Direct3D12;
using SlangShaderSharp;
using NativeIndirectArgumentDesc = Silk.NET.Direct3D12.IndirectArgumentDesc;
using NativeIndirectArgumentType = Silk.NET.Direct3D12.IndirectArgumentType;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    public IndirectCommandLayout CreateIndirectCommandLayout(
        Device device,
        in IndirectCommandLayoutDesc desc)
    {
        D3D12Device nativeDevice = RequireDevice(device, nameof(device));
        IndirectCommands capability = nativeDevice
            .RequireCapability<IndirectCommands>(nameof(CreateIndirectCommandLayout));
        if (desc.Arguments.IsEmpty)
            throw new ArgumentException("An indirect command layout requires at least one argument.", nameof(desc));
        if (desc.Stride == 0 ||
            desc.Stride % capability.ArgumentBufferAlignment != 0 ||
            desc.Stride > capability.MaximumStride)
        {
            throw new ArgumentOutOfRangeException(nameof(desc), "The indirect command stride is invalid.");
        }

        D3D12Pipeline? pipeline = null;
        if (desc.Pipeline is Pipeline publicPipeline)
            pipeline = RequirePipeline(publicPipeline);

        NativeIndirectArgumentDesc[] nativeArguments = BuildIndirectArguments(
            capability,
            pipeline,
            desc.Arguments,
            out IndirectLayoutEffects effects,
            out uint[] vertexSlots,
            out bool requiresRootSignature,
            out ulong minimumStride,
            out int actionCount);
        ValidateIndirectCommandLayout(
            desc,
            effects,
            requiresRootSignature,
            minimumStride,
            actionCount,
            pipeline);
        ID3D12CommandSignature* signature = CreateNativeCommandSignature(
            nativeDevice,
            pipeline,
            desc.Stride,
            nativeArguments,
            requiresRootSignature);

        D3D12IndirectCommandLayout? result = null;
        try
        {
            result = new D3D12IndirectCommandLayout(
                nativeDevice,
                signature,
                desc.Stride,
                effects,
                vertexSlots,
                pipeline,
                desc.Pipeline,
                desc.Label);
            nativeDevice.RegisterChild(result);
            return result;
        }
        catch
        {
            if (result is null)
                _ = signature->Release();
            else
                result.Dispose();
            throw;
        }
    }

    private static NativeIndirectArgumentDesc[] BuildIndirectArguments(
        IndirectCommands capability,
        D3D12Pipeline? pipeline,
        ReadOnlySpan<SomeEngine.Graphics.IndirectArgumentDesc> arguments,
        out IndirectLayoutEffects effects,
        out uint[] vertexSlots,
        out bool requiresRootSignature,
        out ulong minimumStride,
        out int actionCount)
    {
        var nativeArguments = new NativeIndirectArgumentDesc[arguments.Length];
        var slots = new HashSet<uint>();
        effects = IndirectLayoutEffects.None;
        requiresRootSignature = false;
        minimumStride = 0;
        actionCount = 0;
        for (int index = 0; index < nativeArguments.Length; index++)
        {
            ref readonly SomeEngine.Graphics.IndirectArgumentDesc argument = ref arguments[index];
            if (!capability.Supports(argument.Type))
            {
                throw new NotSupportedException(
                    $"Indirect argument type {argument.Type} is unavailable.");
            }
            nativeArguments[index] = BuildIndirectArgument(
                pipeline,
                argument,
                slots,
                ref effects,
                ref requiresRootSignature,
                ref actionCount,
                out ulong argumentSize);
            minimumStride = checked(minimumStride + argumentSize);
        }
        vertexSlots = [.. slots];
        return nativeArguments;
    }

    private static NativeIndirectArgumentDesc BuildIndirectArgument(
        D3D12Pipeline? pipeline,
        in SomeEngine.Graphics.IndirectArgumentDesc argument,
        HashSet<uint> vertexSlots,
        ref IndirectLayoutEffects effects,
        ref bool requiresRootSignature,
        ref int actionCount,
        out ulong size)
    {
        NativeIndirectArgumentDesc native = default;
        native.Type = ToNativeIndirectArgumentType(argument.Type);
        switch (argument.Type)
        {
            case SomeEngine.Graphics.IndirectArgumentType.Draw:
                effects |= IndirectLayoutEffects.Draw;
                actionCount++;
                size = 16;
                break;
            case SomeEngine.Graphics.IndirectArgumentType.DrawIndexed:
                effects |= IndirectLayoutEffects.Draw;
                actionCount++;
                size = 20;
                break;
            case SomeEngine.Graphics.IndirectArgumentType.Dispatch:
                effects |= IndirectLayoutEffects.Compute;
                actionCount++;
                size = 12;
                break;
            case SomeEngine.Graphics.IndirectArgumentType.DispatchMesh:
                effects |= IndirectLayoutEffects.Draw;
                actionCount++;
                size = 12;
                break;
            case SomeEngine.Graphics.IndirectArgumentType.DispatchRays:
                effects |= IndirectLayoutEffects.Compute;
                actionCount++;
                size = 104;
                break;
            case SomeEngine.Graphics.IndirectArgumentType.WorkGraph:
                throw new NotSupportedException(
                    "This Agility SDK command-signature surface does not expose indirect Work Graph dispatch.");
            case SomeEngine.Graphics.IndirectArgumentType.VertexBuffer:
                ValidateIndirectVertexBufferArgument(argument);
                native.VertexBuffer.Slot = argument.VertexBufferSlot;
                vertexSlots.Add(argument.VertexBufferSlot);
                effects |= IndirectLayoutEffects.VertexBuffer;
                size = 16;
                break;
            case SomeEngine.Graphics.IndirectArgumentType.IndexBuffer:
                effects |= IndirectLayoutEffects.IndexBuffer;
                size = 16;
                break;
            case SomeEngine.Graphics.IndirectArgumentType.Constants:
                BuildIndirectConstantsArgument(
                    pipeline,
                    argument,
                    ref native,
                    ref effects,
                    ref requiresRootSignature);
                size = checked((ulong)argument.ValueCount * sizeof(uint));
                break;
            case SomeEngine.Graphics.IndirectArgumentType.ConstantBuffer:
                BuildIndirectConstantBufferArgument(
                    pipeline,
                    argument,
                    ref native,
                    ref effects,
                    ref requiresRootSignature);
                size = 8;
                break;
            case SomeEngine.Graphics.IndirectArgumentType.ShaderResource:
            case SomeEngine.Graphics.IndirectArgumentType.UnorderedAccess:
                throw new NotSupportedException(
                    "The current D3D12 layout compiler places Slang resource bindings in descriptor tables; ExecuteIndirect cannot replace individual table entries.");
            default:
                throw new ArgumentOutOfRangeException("desc");
        }
        return native;
    }

    private static void ValidateIndirectVertexBufferArgument(
        in SomeEngine.Graphics.IndirectArgumentDesc argument)
    {
        if (argument.Parameters != VariableLayoutReflection.Null || argument.ByteOffset != 0 ||
            argument.ValueCount != 0)
        {
            throw new ArgumentException(
                "A vertex-buffer indirect argument accepts only VertexBufferSlot.",
                "desc");
        }
    }

    private static void BuildIndirectConstantsArgument(
        D3D12Pipeline? pipeline,
        in SomeEngine.Graphics.IndirectArgumentDesc argument,
        ref NativeIndirectArgumentDesc native,
        ref IndirectLayoutEffects effects,
        ref bool requiresRootSignature)
    {
        if (pipeline is null)
        {
            throw new ArgumentException(
                "Indirect constants require a Pipeline and a Slang parameter-object layout.",
                "desc");
        }
        IndirectRootDestination destination = pipeline.RootSignature.ResolveIndirectRoot(
            argument.Parameters,
            argument.Type,
            argument.ByteOffset,
            argument.ValueCount);
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
        ref bool requiresRootSignature)
    {
        if (pipeline is null)
        {
            throw new ArgumentException(
                "An indirect constant-buffer argument requires a Pipeline and a Slang parameter-object layout.",
                "desc");
        }
        IndirectRootDestination destination = pipeline.RootSignature.ResolveIndirectRoot(
            argument.Parameters,
            argument.Type,
            argument.ByteOffset,
            argument.ValueCount);
        native.ConstantBufferView.RootParameterIndex = destination.RootParameterIndex;
        requiresRootSignature = true;
        effects |= IndirectLayoutEffects.RootArguments;
    }

    private static void ValidateIndirectCommandLayout(
        in IndirectCommandLayoutDesc desc,
        IndirectLayoutEffects effects,
        bool requiresRootSignature,
        ulong minimumStride,
        int actionCount,
        D3D12Pipeline? pipeline)
    {
        if (actionCount != 1)
        {
            throw new ArgumentException(
                "A D3D12 indirect command layout must contain exactly one draw or dispatch argument.",
                nameof(desc));
        }
        if (!IsAction(desc.Arguments[^1].Type))
        {
            throw new ArgumentException(
                "The draw or dispatch argument must be the final entry in a D3D12 indirect command layout.",
                nameof(desc));
        }
        if ((effects & IndirectLayoutEffects.Draw) != 0 &&
            (effects & IndirectLayoutEffects.Compute) != 0)
        {
            throw new ArgumentException("An indirect layout cannot mix graphics and compute actions.", nameof(desc));
        }
        if ((ulong)desc.Stride < minimumStride)
            throw new ArgumentOutOfRangeException(nameof(desc), "The stride is smaller than its indirect arguments.");
        if (requiresRootSignature && pipeline is null)
        {
            throw new ArgumentException(
                "Indirect root arguments require a compatible Pipeline root layout.",
                nameof(desc));
        }
    }

    private static ID3D12CommandSignature* CreateNativeCommandSignature(
        D3D12Device nativeDevice,
        D3D12Pipeline? pipeline,
        uint stride,
        NativeIndirectArgumentDesc[] nativeArguments,
        bool requiresRootSignature)
    {
        ID3D12CommandSignature* signature = null;
        fixed (NativeIndirectArgumentDesc* arguments = nativeArguments)
        {
            CommandSignatureDesc nativeDescription = new(
                stride,
                (uint)nativeArguments.Length,
                arguments,
                nativeDevice.EnabledNodeMask);
            Guid iid = ID3D12CommandSignature.Guid;
            ThrowIfFailed(
                nativeDevice,
                nativeDevice.Native->CreateCommandSignature(
                    &nativeDescription,
                    requiresRootSignature ? pipeline!.RootSignature.Native : null,
                    &iid,
                    (void**)&signature),
                NativeOperationType.Ordinary,
                "ID3D12Device::CreateCommandSignature");
        }
        return signature;
    }

    public void ExecuteIndirect(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count = null)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        IndirectCommands capability = command.NativeDevice
            .RequireCapability<IndirectCommands>(nameof(ExecuteIndirect));
        D3D12IndirectCommandLayout nativeLayout = RequireIndirectCommandLayout(layout);
        if (maximumCommandCount == 0 || maximumCommandCount > capability.MaximumCommandCount)
            throw new ArgumentOutOfRangeException(nameof(maximumCommandCount));

        if (nativeLayout.RequiredRoot is D3D12RootSignatureState requiredRoot)
        {
            D3D12Pipeline currentPipeline = command.Pipeline;
            if (currentPipeline.RootSignature.Native != requiredRoot.Native)
            {
                throw new InvalidOperationException(
                    "The current Pipeline does not use the native root signature required by " +
                    "the IndirectCommandLayout.");
            }
        }

        D3D12Buffer argumentBuffer = RequireBuffer(arguments.Buffer);
        BufferRange argumentRange = arguments.Range.Resolve(argumentBuffer.Info.Size);
        ulong requiredBytes = checked((ulong)nativeLayout.Stride * maximumCommandCount);
        if ((argumentBuffer.Info.Usages & BufferUsages.Indirect) == 0 ||
            argumentRange.Offset % capability.ArgumentBufferAlignment != 0 ||
            argumentRange.Size < requiredBytes)
        {
            throw new ArgumentException("The indirect argument Buffer range is invalid.", nameof(arguments));
        }

        D3D12Buffer? countBuffer = null;
        ulong countOffset = 0;
        if (count is BufferRegion countRegion)
        {
            countBuffer = RequireBuffer(countRegion.Buffer);
            BufferRange countRange = countRegion.Range.Resolve(countBuffer.Info.Size);
            if ((countBuffer.Info.Usages & BufferUsages.Indirect) == 0 ||
                countRange.Offset % capability.CountBufferAlignment != 0 ||
                countRange.Size < sizeof(uint))
            {
                throw new ArgumentException("The indirect count Buffer range is invalid.", nameof(count));
            }
            countOffset = countRange.Offset;
        }

        int retainedCount = countBuffer is null ? 2 : 3;
        command.PrepareCaptures(retainedCount, 0, countBuffer is null ? 1 : 2);
        if (countBuffer is not null)
            command.Capture(countBuffer);
        command.Capture(argumentBuffer);
        command.Capture(nativeLayout, nativeLayout.NativeLifetime);
        ID3D12Resource* nativeCountBuffer = countBuffer is null ? null : countBuffer.Native;
        command.List->ExecuteIndirect(
            nativeLayout.Native,
            maximumCommandCount,
            argumentBuffer.Native,
            argumentRange.Offset,
            nativeCountBuffer,
            countOffset);
        command.InvalidateIndirectState(nativeLayout);
    }

    private static NativeIndirectArgumentType ToNativeIndirectArgumentType(
        SomeEngine.Graphics.IndirectArgumentType value) => value switch
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
        SomeEngine.Graphics.IndirectArgumentType.WorkGraph =>
            throw new NotSupportedException("Indirect Work Graph dispatch is unavailable."),
        _ => throw new ArgumentOutOfRangeException(nameof(value)),
    };

    private static bool IsAction(SomeEngine.Graphics.IndirectArgumentType value) =>
        value is SomeEngine.Graphics.IndirectArgumentType.Draw or
            SomeEngine.Graphics.IndirectArgumentType.DrawIndexed or
            SomeEngine.Graphics.IndirectArgumentType.Dispatch or
            SomeEngine.Graphics.IndirectArgumentType.DispatchMesh or
            SomeEngine.Graphics.IndirectArgumentType.DispatchRays or
            SomeEngine.Graphics.IndirectArgumentType.WorkGraph;

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
        private readonly NativeLease _native;
        private RetainedSlangProgram? _program;

        internal D3D12IndirectCommandLayout(
            D3D12Device device,
            ID3D12CommandSignature* native,
            uint stride,
            IndirectLayoutEffects effects,
            uint[] vertexSlots,
            D3D12Pipeline? nativePipeline,
            Pipeline? pipeline,
            string? label)
            : base(device, stride, pipeline, label)
        {
            _device = device;
            NativeLease? pipelineState = null;
            RetainedSlangProgram? program = null;
            try
            {
                if (nativePipeline is not null)
                {
                    pipelineState = nativePipeline.NativeLifetime;
                    program = nativePipeline.RetainProgramReference();
                    RequiredRoot = nativePipeline.RootSignature;
                }
                _native = new NativeLease(
                    (Silk.NET.Core.Native.IUnknown*)native,
                    ownsReference: true,
                    dependency: pipelineState);
                _program = program;
                program = null;
            }
            catch
            {
                program?.Dispose();
                throw;
            }
            Effects = effects;
            VertexSlots = vertexSlots;
        }

        internal ID3D12CommandSignature* Native =>
            (ID3D12CommandSignature*)_native.Pointer;
        internal NativeLease NativeLifetime => _native;
        internal D3D12RootSignatureState? RequiredRoot { get; }
        internal IndirectLayoutEffects Effects { get; }
        internal uint[] VertexSlots { get; }

        internal override void Release(bool fromParent)
        {
            _native.Release();
            Interlocked.Exchange(ref _program, null)?.Dispose();
            _device.UnregisterChild(this);
        }
    }

    private sealed partial class D3D12CommandContext
    {
        internal void CapturePipeline(D3D12Pipeline pipeline)
        {
            Recording.Capture(pipeline.NativeLifetime);
        }

        internal void InvalidateIndirectState(D3D12IndirectCommandLayout layout)
        {
            if ((layout.Effects & IndirectLayoutEffects.VertexBuffer) != 0)
            {
                foreach (uint slot in layout.VertexSlots)
                {
                    int index = checked((int)slot);
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
        internal static D3D12IndirectCommandLayout IndirectCommandLayout(
            IndirectCommandLayout value) =>
            value as D3D12IndirectCommandLayout ??
            throw new ArgumentException(
                "The IndirectCommandLayout was not created by the Direct3D 12 backend.",
                nameof(value));
    }
}
