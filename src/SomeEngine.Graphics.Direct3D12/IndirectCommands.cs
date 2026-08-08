using Silk.NET.Direct3D12;
using NativeIndirectArgumentDesc = Silk.NET.Direct3D12.IndirectArgumentDesc;
using NativeIndirectArgumentType = Silk.NET.Direct3D12.IndirectArgumentType;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public IndirectCommandLayout CreateIndirectCommandLayout(
        Device device,
        in IndirectCommandLayoutDesc desc)
    {
        D3D12Device nativeDevice = NativeCast.Device(device);
        if (desc.Arguments.IsEmpty)
            throw new ArgumentException("An indirect command layout requires at least one argument.", nameof(desc));
        if (desc.Stride == 0 || desc.Stride % 4 != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(desc), "The indirect command stride is invalid.");
        }

        ID3D12PipelineArtifact? pipeline = null;
        if (desc.Pipeline is Pipeline publicPipeline)
            pipeline = NativeCast.Pipeline(publicPipeline);

        NativeIndirectArgumentDesc[] nativeArguments =
            new NativeIndirectArgumentDesc[desc.Arguments.Length];
        IndirectLayoutEffects effects = IndirectLayoutEffects.None;
        HashSet<uint> vertexSlots = [];
        bool requiresRootSignature = false;
        ulong minimumStride = 0;
        int actionCount = 0;
        for (int index = 0; index < nativeArguments.Length; index++)
        {
            ref readonly SomeEngine.Graphics.IndirectArgumentDesc argument =
                ref desc.Arguments[index];
            NativeIndirectArgumentDesc native = default;
            native.Type = ToNativeIndirectArgumentType(argument.Type);
            ulong size;
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
                    effects |= IndirectLayoutEffects.Compute;
                    actionCount++;
                    throw new NotSupportedException(
                        "This Agility SDK command-signature surface does not expose indirect Work Graph dispatch.");
                case SomeEngine.Graphics.IndirectArgumentType.VertexBuffer:
                    native.VertexBuffer.Slot = argument.Slot;
                    vertexSlots.Add(argument.Slot);
                    effects |= IndirectLayoutEffects.VertexBuffer;
                    size = 16;
                    break;
                case SomeEngine.Graphics.IndirectArgumentType.IndexBuffer:
                    effects |= IndirectLayoutEffects.IndexBuffer;
                    size = 16;
                    break;
                case SomeEngine.Graphics.IndirectArgumentType.Constants:
                    if (argument.ValueCount == 0)
                        throw new ArgumentOutOfRangeException(nameof(desc), "An indirect constant range cannot be empty.");
                    native.Constant.RootParameterIndex = argument.Slot;
                    native.Constant.DestOffsetIn32BitValues = argument.ByteOffset;
                    native.Constant.Num32BitValuesToSet = argument.ValueCount;
                    requiresRootSignature = true;
                    effects |= IndirectLayoutEffects.RootArguments;
                    size = checked((ulong)argument.ValueCount * sizeof(uint));
                    break;
                case SomeEngine.Graphics.IndirectArgumentType.ConstantBuffer:
                    native.ConstantBufferView.RootParameterIndex = argument.Slot;
                    requiresRootSignature = true;
                    effects |= IndirectLayoutEffects.RootArguments;
                    size = 8;
                    break;
                case SomeEngine.Graphics.IndirectArgumentType.ShaderResource:
                    native.ShaderResourceView.RootParameterIndex = argument.Slot;
                    requiresRootSignature = true;
                    effects |= IndirectLayoutEffects.RootArguments;
                    size = 8;
                    break;
                case SomeEngine.Graphics.IndirectArgumentType.UnorderedAccess:
                    native.UnorderedAccessView.RootParameterIndex = argument.Slot;
                    requiresRootSignature = true;
                    effects |= IndirectLayoutEffects.RootArguments;
                    size = 8;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(desc));
            }
            minimumStride = checked(minimumStride + size);
            nativeArguments[index] = native;
        }

        if (actionCount != 1)
        {
            throw new ArgumentException(
                "A D3D12 indirect command layout must contain exactly one draw or dispatch argument.",
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

        ID3D12CommandSignature* signature = null;
        fixed (NativeIndirectArgumentDesc* arguments = nativeArguments)
        {
            CommandSignatureDesc nativeDescription = new(
                desc.Stride,
                (uint)nativeArguments.Length,
                arguments,
                nativeDevice.EnabledNodeMask);
            Guid iid = ID3D12CommandSignature.Guid;
            NativeCall.ThrowIfFailed(
                nativeDevice.Native->CreateCommandSignature(
                    &nativeDescription,
                    requiresRootSignature ? pipeline!.RootSignature : null,
                    &iid,
                    (void**)&signature),
                "ID3D12Device::CreateCommandSignature");
        }

        D3D12IndirectCommandLayout? result = null;
        try
        {
            result = new D3D12IndirectCommandLayout(
                nativeDevice,
                signature,
                desc.Stride,
                desc.Pipeline?.Signature ?? default,
                effects,
                [.. vertexSlots],
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

    public void ExecuteIndirect(
        CommandContext context,
        IndirectCommandLayout layout,
        in BufferRegion arguments,
        uint maximumCommandCount,
        BufferRegion? count = null)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12IndirectCommandLayout nativeLayout = NativeCast.IndirectCommandLayout(layout);
        if (maximumCommandCount == 0)
            throw new ArgumentOutOfRangeException(nameof(maximumCommandCount));

        D3D12Buffer argumentBuffer = NativeCast.Buffer(arguments.Buffer);
        BufferRange argumentRange = arguments.Range.Resolve(argumentBuffer.Info.Size);
        ulong requiredBytes = checked((ulong)nativeLayout.Stride * maximumCommandCount);
        if ((argumentBuffer.Info.Usages & BufferUsages.Indirect) == 0 ||
            argumentRange.Offset % 4 != 0 ||
            argumentRange.Size < requiredBytes)
        {
            throw new ArgumentException("The indirect argument Buffer range is invalid.", nameof(arguments));
        }

        D3D12Buffer? countBuffer = null;
        ulong countOffset = 0;
        if (count is BufferRegion countRegion)
        {
            countBuffer = NativeCast.Buffer(countRegion.Buffer);
            BufferRange countRange = countRegion.Range.Resolve(countBuffer.Info.Size);
            if ((countBuffer.Info.Usages & BufferUsages.Indirect) == 0 ||
                countRange.Offset % 4 != 0 ||
                countRange.Size < sizeof(uint))
            {
                throw new ArgumentException("The indirect count Buffer range is invalid.", nameof(count));
            }
            countOffset = countRange.Offset;
            command.Capture(countBuffer);
        }

        command.Capture(argumentBuffer);
        command.Capture(nativeLayout, nativeLayout.NativeLifetime);
        if (nativeLayout.Pipeline is Pipeline pipeline)
            command.CapturePipelineArtifact(pipeline);
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

    private unsafe interface ID3D12PipelineArtifact
    {
        ID3D12RootSignature* RootSignature { get; }
        NativeLease NativeLifetime { get; }
    }

    private sealed class D3D12IndirectCommandLayout : IndirectCommandLayout
    {
        private readonly D3D12Device _device;
        private readonly NativeLease _native;
        private int _released;

        internal D3D12IndirectCommandLayout(
            D3D12Device device,
            ID3D12CommandSignature* native,
            uint stride,
            in PipelineSignature pipelineSignature,
            IndirectLayoutEffects effects,
            uint[] vertexSlots,
            Pipeline? pipeline,
            string? label)
            : base(device, stride, pipelineSignature, label)
        {
            _device = device;
            _native = new NativeLease((Silk.NET.Core.Native.IUnknown*)native, ownsReference: true);
            Effects = effects;
            VertexSlots = vertexSlots;
            Pipeline = pipeline;
        }

        internal ID3D12CommandSignature* Native =>
            (ID3D12CommandSignature*)_native.Pointer;
        internal NativeLease NativeLifetime => _native;
        internal IndirectLayoutEffects Effects { get; }
        internal uint[] VertexSlots { get; }
        internal Pipeline? Pipeline { get; }

        internal override void Release(bool fromParent)
        {
            if (Interlocked.Exchange(ref _released, 1) != 0)
                return;
            _native.Release();
            _device.UnregisterChild(this);
        }
    }

    private sealed partial class D3D12CommandContext
    {
        internal void CapturePipelineArtifact(Pipeline pipeline)
        {
            ID3D12PipelineArtifact native = NativeCast.Pipeline(pipeline);
            Recording.Capture(pipeline, native.NativeLifetime);
        }

        internal void InvalidateIndirectState(D3D12IndirectCommandLayout layout)
        {
            if ((layout.Effects & IndirectLayoutEffects.VertexBuffer) != 0)
            {
                foreach (uint slot in layout.VertexSlots)
                    _vertexBuffers.Remove(slot);
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

    private static partial class NativeCast
    {
        internal static D3D12IndirectCommandLayout IndirectCommandLayout(
            IndirectCommandLayout value)
        {
#if DEBUG
            return (D3D12IndirectCommandLayout)value;
#else
            return System.Runtime.CompilerServices.Unsafe.As<
                IndirectCommandLayout,
                D3D12IndirectCommandLayout>(ref value);
#endif
        }
    }
}
