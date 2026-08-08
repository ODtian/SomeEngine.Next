using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.Maths;
using NativeViewport = Silk.NET.Direct3D12.Viewport;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace SomeEngine.Graphics.Direct3D12;

public sealed unsafe partial class D3D12Backend
{
    public void SetVertexBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<VertexBufferBinding> bindings)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.VertexBuffersEqual(firstSlot, bindings))
            return;
        VertexBufferView* native = stackalloc VertexBufferView[bindings.Length];
        for (int index = 0; index < bindings.Length; index++)
        {
            ref readonly VertexBufferBinding binding = ref bindings[index];
            D3D12Buffer buffer = NativeCast.Buffer(binding.Buffer);
            native[index] = new VertexBufferView(
                buffer.Native->GetGPUVirtualAddress() + binding.Offset,
                checked((uint)binding.Size),
                binding.Stride);
            command.Capture(buffer);
        }
        command.List->IASetVertexBuffers(
            firstSlot,
            checked((uint)bindings.Length),
            native);
        command.RememberVertexBuffers(firstSlot, bindings);
    }

    public void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.IndexBufferEquals(binding))
            return;
        D3D12Buffer buffer = NativeCast.Buffer(binding.Buffer);
        IndexBufferView native = new(
            buffer.Native->GetGPUVirtualAddress() + binding.Offset,
            checked((uint)binding.Size),
            binding.Type == IndexType.UInt16
                ? DxgiFormat.FormatR16Uint
                : DxgiFormat.FormatR32Uint);
        command.Capture(buffer);
        command.List->IASetIndexBuffer(&native);
        command.RememberIndexBuffer(binding);
    }

    public void SetStreamOutputBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<StreamOutputBufferBinding> bindings)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.StreamOutputBuffersEqual(firstSlot, bindings))
            return;
        StreamOutputBufferView* native = stackalloc StreamOutputBufferView[bindings.Length];
        for (int index = 0; index < bindings.Length; index++)
        {
            ref readonly StreamOutputBufferBinding binding = ref bindings[index];
            D3D12Buffer buffer = NativeCast.Buffer(binding.Buffer);
            ulong filledSizeLocation = 0;
            if (binding.FilledSizeBuffer is Buffer filled)
            {
                D3D12Buffer filledNative = NativeCast.Buffer(filled);
                filledSizeLocation =
                    filledNative.Native->GetGPUVirtualAddress() + binding.FilledSizeOffset;
                command.Capture(filledNative);
            }
            native[index] = new StreamOutputBufferView(
                buffer.Native->GetGPUVirtualAddress() + binding.Offset,
                binding.Size,
                filledSizeLocation);
            command.Capture(buffer);
        }
        command.List->SOSetTargets(firstSlot, checked((uint)bindings.Length), native);
        command.RememberStreamOutputBuffers(firstSlot, bindings);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.ViewportsEqual(viewports))
            return;
        SetViewportsSlow(command, viewports);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetViewportsSlow(
        D3D12CommandContext command,
        ReadOnlySpan<Viewport> viewports)
    {
        NativeViewport* native = stackalloc NativeViewport[viewports.Length];
        for (int index = 0; index < viewports.Length; index++)
        {
            ref readonly Viewport viewport = ref viewports[index];
            native[index] = new NativeViewport(
                viewport.X,
                viewport.Y,
                viewport.Width,
                viewport.Height,
                viewport.MinimumDepth,
                viewport.MaximumDepth);
        }
        command.List->RSSetViewports(checked((uint)viewports.Length), native);
        command.Recording.RecordViewportSetter();
        command.RememberViewports(viewports);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.ScissorsEqual(scissors))
            return;
        SetScissorsSlow(command, scissors);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetScissorsSlow(
        D3D12CommandContext command,
        ReadOnlySpan<ScissorRect> scissors)
    {
        Box2D<int>* native = stackalloc Box2D<int>[scissors.Length];
        for (int index = 0; index < scissors.Length; index++)
        {
            ref readonly ScissorRect rect = ref scissors[index];
            native[index] = new Box2D<int>(
                rect.X,
                rect.Y,
                checked(rect.X + rect.Width),
                checked(rect.Y + rect.Height));
        }
        command.List->RSSetScissorRects(checked((uint)scissors.Length), native);
        command.Recording.RecordScissorSetter();
        command.RememberScissors(scissors);
    }

    public void SetBlendConstants(CommandContext context, in Vector4 value)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.BlendConstantsEqual(value))
            return;
        Vector4 copy = value;
        command.List->OMSetBlendFactor((float*)&copy);
        command.RememberBlendConstants(value);
    }

    public void SetStencilReference(CommandContext context, uint value)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.StencilReferenceEqual(value))
            return;
        command.List->OMSetStencilRef(value);
        command.RememberStencilReference(value);
    }

    public void SetDepthBounds(CommandContext context, float minimum, float maximum)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.DepthBoundsEqual(minimum, maximum))
            return;
        command.List->OMSetDepthBounds(minimum, maximum);
        command.RememberDepthBounds(minimum, maximum);
    }

    public void SetDepthBias(
        CommandContext context,
        int bias,
        float clamp,
        float slopeScaledBias)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.DepthBiasEqual(bias, clamp, slopeScaledBias))
            return;
        command.List->RSSetDepthBias(bias, clamp, slopeScaledBias);
        command.RememberDepthBias(bias, clamp, slopeScaledBias);
    }

    public void SetPrimitiveTopology(CommandContext context, PrimitiveTopology topology)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.PrimitiveTopologyEqual(topology))
            return;
        command.List->IASetPrimitiveTopology(ToNativeTopology(topology));
        command.RememberPrimitiveTopology(topology);
    }

    public void SetStripCut(CommandContext context, StripCut stripCut)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.StripCutEqual(stripCut))
            return;
        command.List->IASetIndexBufferStripCutValue(stripCut switch
        {
            StripCut.Disabled => IndexBufferStripCutValue.ValueDisabled,
            StripCut.UInt16 => IndexBufferStripCutValue.Value0xFfff,
            StripCut.UInt32 => IndexBufferStripCutValue.Value0xFfffffff,
            _ => throw new ArgumentOutOfRangeException(nameof(stripCut)),
        });
        command.RememberStripCut(stripCut);
    }

    public void SetPredication(
        CommandContext context,
        Buffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        if (command.PredicationEqual(buffer, offset, operation))
            return;
        D3D12Buffer? native = buffer is null ? null : NativeCast.Buffer(buffer);
        if (native is not null)
            command.Capture(native);
        ID3D12Resource* predicate = native is null ? null : native.Native;
        command.List->SetPredication(
            predicate,
            offset,
            operation == PredicationOperation.EqualZero
                ? PredicationOp.EqualZero
                : PredicationOp.NotEqualZero);
        command.RememberPredication(buffer, offset, operation);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Draw(CommandContext context, in DrawArguments arguments)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12CommandListFastCalls.DrawInstanced(
            command.List,
            arguments.VertexCount,
            arguments.InstanceCount,
            arguments.FirstVertex,
            arguments.FirstInstance);
    }

    public void DrawIndexed(CommandContext context, in DrawIndexedArguments arguments)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12CommandListFastCalls.DrawIndexedInstanced(
            command.List,
            arguments.IndexCount,
            arguments.InstanceCount,
            arguments.FirstIndex,
            arguments.VertexOffset,
            arguments.FirstInstance);
    }

    public void Dispatch(CommandContext context, in DispatchArguments arguments)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12CommandListFastCalls.Dispatch(
            command.List,
            arguments.X,
            arguments.Y,
            arguments.Z);
    }

    public void ExecuteBundle(CommandContext context, RecordedBundle bundle)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        D3D12RecordedBundle native = NativeCast.Bundle(bundle);
        command.CaptureBundle(native);
        command.List->ExecuteBundle((ID3D12GraphicsCommandList*)native.NativeList);
        command.InvalidateStateShadow();
    }

    public void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        fixed (byte* label = utf8Label)
            command.List->BeginEvent(0, label, checked((uint)utf8Label.Length));
    }

    public void EndEvent(CommandContext context)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        command.List->EndEvent();
    }

    public void SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        D3D12CommandContext command = NativeCast.CommandContext(context);
        fixed (byte* label = utf8Label)
            command.List->SetMarker(0, label, checked((uint)utf8Label.Length));
    }

    private static D3DPrimitiveTopology ToNativeTopology(PrimitiveTopology topology) =>
        topology switch
        {
            PrimitiveTopology.PointList => D3DPrimitiveTopology.D3DPrimitiveTopologyPointlist,
            PrimitiveTopology.LineList => D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist,
            PrimitiveTopology.LineStrip => D3DPrimitiveTopology.D3DPrimitiveTopologyLinestrip,
            PrimitiveTopology.TriangleList => D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist,
            PrimitiveTopology.TriangleStrip => D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglestrip,
            PrimitiveTopology.PatchList => D3DPrimitiveTopology.D3DPrimitiveTopology1ControlPointPatchlist,
            _ => throw new ArgumentOutOfRangeException(nameof(topology)),
        };

    private sealed partial class D3D12CommandContext
    {
        private readonly Dictionary<uint, VertexBufferBinding> _vertexBuffers = [];
        private readonly Dictionary<uint, StreamOutputBufferBinding> _streamOutputBuffers = [];
        private IndexBufferBinding _indexBuffer;
        private bool _hasIndexBuffer;
        private Viewport[] _viewports = [];
        private Viewport _singleViewport;
        private int _viewportCount;
        private ScissorRect[] _scissors = [];
        private ScissorRect _singleScissor;
        private int _scissorCount;
        private Vector4 _blendConstants;
        private bool _hasBlendConstants;
        private uint _stencilReference;
        private bool _hasStencilReference;
        private (float Minimum, float Maximum)? _depthBounds;
        private (int Bias, float Clamp, float Slope)? _depthBias;
        private PrimitiveTopology? _primitiveTopology;
        private StripCut? _stripCut;
        private (Buffer? Buffer, ulong Offset, PredicationOperation Operation)? _predication;
        internal void ResetEncodingState()
        {
            ResetRenderingState();
            InvalidateStateShadow();
        }

        internal void InvalidateStateShadow()
        {
            _vertexBuffers.Clear();
            _streamOutputBuffers.Clear();
            _indexBuffer = default;
            _hasIndexBuffer = false;
            _viewportCount = 0;
            _scissorCount = 0;
            _hasBlendConstants = false;
            _hasStencilReference = false;
            _depthBounds = null;
            _depthBias = null;
            _primitiveTopology = null;
            _stripCut = null;
            _predication = null;
            ResetMeshAndShadingState();
            ResetWorkGraphState();
            ResetPipelineBindingState();
        }

        internal bool VertexBuffersEqual(uint first, ReadOnlySpan<VertexBufferBinding> values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                if (!_vertexBuffers.TryGetValue(first + (uint)index, out VertexBufferBinding current) ||
                    current != values[index])
                    return false;
            }
            return true;
        }

        internal void RememberVertexBuffers(uint first, ReadOnlySpan<VertexBufferBinding> values)
        {
            for (int index = 0; index < values.Length; index++)
                _vertexBuffers[first + (uint)index] = values[index];
        }

        internal bool IndexBufferEquals(in IndexBufferBinding value) =>
            _hasIndexBuffer && _indexBuffer == value;
        internal void RememberIndexBuffer(in IndexBufferBinding value)
        {
            _indexBuffer = value;
            _hasIndexBuffer = true;
        }

        internal bool StreamOutputBuffersEqual(uint first, ReadOnlySpan<StreamOutputBufferBinding> values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                if (!_streamOutputBuffers.TryGetValue(first + (uint)index, out StreamOutputBufferBinding current) ||
                    current != values[index])
                    return false;
            }
            return true;
        }

        internal void RememberStreamOutputBuffers(uint first, ReadOnlySpan<StreamOutputBufferBinding> values)
        {
            for (int index = 0; index < values.Length; index++)
                _streamOutputBuffers[first + (uint)index] = values[index];
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool ViewportsEqual(ReadOnlySpan<Viewport> values)
        {
            if (values.Length != _viewportCount)
                return false;
            if (values.Length == 1)
            {
                ref readonly Viewport value = ref values[0];
                if (ViewportBitsEqual(_singleViewport, value))
                    return true;
                return
                    NormalizedFloatEquals(_singleViewport.X, value.X) &&
                    NormalizedFloatEquals(_singleViewport.Y, value.Y) &&
                    NormalizedFloatEquals(_singleViewport.Width, value.Width) &&
                    NormalizedFloatEquals(_singleViewport.Height, value.Height) &&
                    NormalizedFloatEquals(_singleViewport.MinimumDepth, value.MinimumDepth) &&
                    NormalizedFloatEquals(_singleViewport.MaximumDepth, value.MaximumDepth);
            }
            return values.SequenceEqual(_viewports.AsSpan(0, _viewportCount));
        }
        internal void RememberViewports(ReadOnlySpan<Viewport> values)
        {
            if (values.Length == 1)
            {
                _singleViewport = values[0];
                _viewportCount = 1;
                return;
            }
            if (_viewports.Length < values.Length)
                Array.Resize(ref _viewports, values.Length);
            values.CopyTo(_viewports);
            _viewportCount = values.Length;
        }
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        internal bool ScissorsEqual(ReadOnlySpan<ScissorRect> values)
        {
            if (values.Length != _scissorCount)
                return false;
            if (values.Length == 1)
            {
                ref readonly ScissorRect value = ref values[0];
                return
                    _singleScissor.X == value.X &&
                    _singleScissor.Y == value.Y &&
                    _singleScissor.Width == value.Width &&
                    _singleScissor.Height == value.Height;
            }
            return values.SequenceEqual(_scissors.AsSpan(0, _scissorCount));
        }
        internal void RememberScissors(ReadOnlySpan<ScissorRect> values)
        {
            if (values.Length == 1)
            {
                _singleScissor = values[0];
                _scissorCount = 1;
                return;
            }
            if (_scissors.Length < values.Length)
                Array.Resize(ref _scissors, values.Length);
            values.CopyTo(_scissors);
            _scissorCount = values.Length;
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool ViewportBitsEqual(in Viewport left, in Viewport right)
        {
            ref byte leftBytes = ref Unsafe.As<Viewport, byte>(ref Unsafe.AsRef(in left));
            ref byte rightBytes = ref Unsafe.As<Viewport, byte>(ref Unsafe.AsRef(in right));
            return Unsafe.ReadUnaligned<ulong>(ref leftBytes) ==
                   Unsafe.ReadUnaligned<ulong>(ref rightBytes) &&
                   Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref leftBytes, 8)) ==
                   Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rightBytes, 8)) &&
                   Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref leftBytes, 16)) ==
                   Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rightBytes, 16));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool NormalizedFloatEquals(float left, float right) =>
            left == right || (float.IsNaN(left) && float.IsNaN(right));

        internal bool BlendConstantsEqual(in Vector4 value) => _hasBlendConstants && _blendConstants.Equals(value);
        internal void RememberBlendConstants(in Vector4 value) { _blendConstants = value; _hasBlendConstants = true; }
        internal bool StencilReferenceEqual(uint value) => _hasStencilReference && _stencilReference == value;
        internal void RememberStencilReference(uint value) { _stencilReference = value; _hasStencilReference = true; }
        internal bool DepthBoundsEqual(float minimum, float maximum) =>
            _depthBounds is { } current && current.Minimum.Equals(minimum) && current.Maximum.Equals(maximum);
        internal void RememberDepthBounds(float minimum, float maximum) => _depthBounds = (minimum, maximum);
        internal bool DepthBiasEqual(int bias, float clamp, float slope) =>
            _depthBias is { } current && current.Bias == bias && current.Clamp.Equals(clamp) && current.Slope.Equals(slope);
        internal void RememberDepthBias(int bias, float clamp, float slope) => _depthBias = (bias, clamp, slope);
        internal bool PrimitiveTopologyEqual(PrimitiveTopology value) => _primitiveTopology == value;
        internal void RememberPrimitiveTopology(PrimitiveTopology value) => _primitiveTopology = value;
        internal bool StripCutEqual(StripCut value) => _stripCut == value;
        internal void RememberStripCut(StripCut value) => _stripCut = value;
        internal bool PredicationEqual(Buffer? buffer, ulong offset, PredicationOperation operation) =>
            _predication is { } current && ReferenceEquals(current.Buffer, buffer) &&
            current.Offset == offset && current.Operation == operation;
        internal void RememberPredication(Buffer? buffer, ulong offset, PredicationOperation operation) =>
            _predication = (buffer, offset, operation);

    }
}
