using System.Numerics;
using System.Runtime.CompilerServices;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D12;
using Silk.NET.Maths;
using NativeViewport = Silk.NET.Direct3D12.Viewport;
using DxgiFormat = Silk.NET.DXGI.Format;

namespace SomeEngine.Graphics.Direct3D12;

internal sealed unsafe partial class D3D12Backend
{
    private const uint MaximumVertexBufferSlots = 32;
    private const uint MaximumStreamOutputSlots = 4;

    public void SetVertexBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<VertexBufferBinding> bindings)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if ((uint)bindings.Length > MaximumVertexBufferSlots ||
            firstSlot > MaximumVertexBufferSlots - (uint)bindings.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindings),
                "D3D12 exposes at most 32 vertex-buffer slots.");
        }
        if (command.VertexBuffersEqual(firstSlot, bindings))
            return;
        command.PrepareCaptures(bindings.Length, 0, bindings.Length);
        command.PrepareResolvedResources(bindings.Length);
        VertexBufferView* native = stackalloc VertexBufferView[bindings.Length];
        try
        {
            for (int index = 0; index < bindings.Length; index++)
            {
                ref readonly VertexBufferBinding binding = ref bindings[index];
                D3D12Buffer buffer = RequireBuffer(binding.Buffer);
                command.StoreResolvedResource(index, buffer);
                native[index] = new VertexBufferView(
                    buffer.Native->GetGPUVirtualAddress() + binding.Offset,
                    checked((uint)binding.Size),
                    binding.Stride);
            }
        }
        catch
        {
            command.ClearResolvedResources(bindings.Length);
            throw;
        }
        command.CaptureResolvedResources(bindings.Length);
        command.List->IASetVertexBuffers(
            firstSlot,
            checked((uint)bindings.Length),
            native);
        command.RememberVertexBuffers(firstSlot, bindings);
    }

    public void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (command.IndexBufferEquals(binding))
            return;
        D3D12Buffer buffer = RequireBuffer(binding.Buffer);
        IndexBufferView native = new(
            buffer.Native->GetGPUVirtualAddress() + binding.Offset,
            checked((uint)binding.Size),
            binding.Type == IndexType.UInt16
                ? DxgiFormat.FormatR16Uint
                : DxgiFormat.FormatR32Uint);
        command.PrepareCaptures(1, 0, 1);
        command.Capture(buffer);
        command.List->IASetIndexBuffer(&native);
        command.RememberIndexBuffer(binding);
    }

    public void SetStreamOutputBuffers(
        CommandContext context,
        uint firstSlot,
        ReadOnlySpan<StreamOutputBufferBinding> bindings)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (command.Bundle)
        {
            throw new InvalidOperationException(
                "Stream-output targets are not legal in a D3D12 command bundle.");
        }
        if ((uint)bindings.Length > MaximumStreamOutputSlots ||
            firstSlot > MaximumStreamOutputSlots - (uint)bindings.Length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bindings),
                "D3D12 exposes at most four stream-output target slots.");
        }
        if (command.StreamOutputBuffersEqual(firstSlot, bindings))
            return;
        command.PrepareCaptures(checked(bindings.Length * 2), 0, checked(bindings.Length * 2));
        command.PrepareResolvedResources(checked(bindings.Length * 2));
        StreamOutputBufferView* native = stackalloc StreamOutputBufferView[bindings.Length];
        try
        {
            for (int index = 0; index < bindings.Length; index++)
            {
                ref readonly StreamOutputBufferBinding binding = ref bindings[index];
                D3D12Buffer buffer = RequireBuffer(binding.Buffer);
                command.StoreResolvedResource(index * 2, buffer);
                ulong filledSizeLocation = 0;
                if (binding.FilledSizeBuffer is Buffer filled)
                {
                    D3D12Buffer filledNative = RequireBuffer(filled);
                    command.StoreResolvedResource(index * 2 + 1, filledNative);
                    filledSizeLocation =
                        filledNative.Native->GetGPUVirtualAddress() + binding.FilledSizeOffset;
                }
                native[index] = new StreamOutputBufferView(
                    buffer.Native->GetGPUVirtualAddress() + binding.Offset,
                    binding.Size,
                    filledSizeLocation);
            }
        }
        catch
        {
            command.ClearResolvedResources(checked(bindings.Length * 2));
            throw;
        }
        command.CaptureResolvedResources(checked(bindings.Length * 2));
        command.List->SOSetTargets(firstSlot, checked((uint)bindings.Length), native);
        command.RememberStreamOutputBuffers(firstSlot, bindings);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (command.Bundle)
            throw new InvalidOperationException("Viewports are not legal in a D3D12 command bundle.");
        if (command.ViewportsEqual(viewports))
            return;
        SetViewportsSlow(command, viewports);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetViewportsSlow(
        D3D12CommandContext command,
        ReadOnlySpan<Viewport> viewports)
    {
        if ((uint)viewports.Length >
            command.NativeDevice.Capabilities.Limits.MaximumViewports)
        {
            throw new ArgumentOutOfRangeException(nameof(viewports));
        }
        NativeViewport* native = stackalloc NativeViewport[viewports.Length];
        for (int index = 0; index < viewports.Length; index++)
        {
            ref readonly Viewport viewport = ref viewports[index];
            if (!float.IsFinite(viewport.X) ||
                !float.IsFinite(viewport.Y) ||
                !float.IsFinite(viewport.Width) ||
                !float.IsFinite(viewport.Height) ||
                !float.IsFinite(viewport.MinimumDepth) ||
                !float.IsFinite(viewport.MaximumDepth) ||
                viewport.Width < 0 ||
                viewport.Height < 0 ||
                viewport.MinimumDepth < 0 ||
                viewport.MaximumDepth > 1 ||
                viewport.MinimumDepth > viewport.MaximumDepth)
            {
                throw new ArgumentException(
                    "A viewport must contain finite coordinates, non-negative dimensions, " +
                    "and an ordered depth interval inside [0, 1].",
                    nameof(viewports));
            }
            native[index] = new NativeViewport
            {
                TopLeftX = viewport.X,
                TopLeftY = viewport.Y,
                Width = viewport.Width,
                Height = viewport.Height,
                MinDepth = viewport.MinimumDepth,
                MaxDepth = viewport.MaximumDepth,
            };
        }
        command.PrepareViewports(viewports.Length);
        D3D12CommandListFastCalls.SetViewports(
            command.List,
            checked((uint)viewports.Length),
            native);
        command.RememberViewports(viewports);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (command.Bundle)
            throw new InvalidOperationException("Scissors are not legal in a D3D12 command bundle.");
        if (command.ScissorsEqual(scissors))
            return;
        SetScissorsSlow(command, scissors);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetScissorsSlow(
        D3D12CommandContext command,
        ReadOnlySpan<ScissorRect> scissors)
    {
        if ((uint)scissors.Length >
            command.NativeDevice.Capabilities.Limits.MaximumViewports)
        {
            throw new ArgumentOutOfRangeException(nameof(scissors));
        }
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
        command.PrepareScissors(scissors.Length);
        D3D12CommandListFastCalls.SetScissors(
            command.List,
            checked((uint)scissors.Length),
            native);
        command.RememberScissors(scissors);
    }

    public void SetBlendConstants(CommandContext context, in Vector4 value)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (command.BlendConstantsEqual(value))
            return;
        Vector4 copy = value;
        command.List->OMSetBlendFactor((float*)&copy);
        command.RememberBlendConstants(value);
    }

    public void SetStencilReference(CommandContext context, uint value)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (command.StencilReferenceEqual(value))
            return;
        command.List->OMSetStencilRef(value);
        command.RememberStencilReference(value);
    }

    public void SetDepthBounds(CommandContext context, float minimum, float maximum)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RequireDynamicStateSupport(command, DynamicStates.DepthBounds);
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
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RequireDynamicStateSupport(command, DynamicStates.DepthBias);
        if (command.DepthBiasEqual(bias, clamp, slopeScaledBias))
            return;
        command.List->RSSetDepthBias(bias, clamp, slopeScaledBias);
        command.RememberDepthBias(bias, clamp, slopeScaledBias);
    }

    public void SetPrimitiveTopology(CommandContext context, PrimitiveTopology topology)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (command.PrimitiveTopologyEqual(topology))
            return;
        D3D12CommandListFastCalls.SetPrimitiveTopology(
            command.List,
            ToNativeTopology(topology));
        command.RememberPrimitiveTopology(topology);
    }

    public void SetStripCut(CommandContext context, StripCut stripCut)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        RequireDynamicStateSupport(command, DynamicStates.StripCut);
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

    private static void RequireDynamicStateSupport(
        D3D12CommandContext command,
        DynamicStates state)
    {
        if ((command.Device.Capabilities.SupportedDynamicStates & state) == 0)
        {
            throw new NotSupportedException(
                $"Dynamic state {state} is unavailable on this Device.");
        }
    }

    public void SetPredication(
        CommandContext context,
        Buffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        if (command.PredicationEqual(buffer, offset, operation))
            return;
        D3D12Buffer? native = buffer is null ? null : RequireBuffer(buffer);
        if (native is not null)
        {
            command.PrepareCaptures(1, 0, 1);
            command.Capture(native);
        }
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
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12CommandListFastCalls.DrawInstanced(
            command.List,
            arguments.VertexCount,
            arguments.InstanceCount,
            arguments.FirstVertex,
            arguments.FirstInstance);
    }

    public void DrawIndexed(CommandContext context, in DrawIndexedArguments arguments)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
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
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12CommandListFastCalls.Dispatch(
            command.List,
            arguments.X,
            arguments.Y,
            arguments.Z);
    }

    public void ExecuteBundle(CommandContext context, RecordedBundle bundle)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        D3D12RecordedBundle native = RequireBundle(bundle);
        command.PrepareBundles(1);
        command.CaptureBundle(native);
        command.List->ExecuteBundle((ID3D12GraphicsCommandList*)native.NativeList);
        command.InvalidateStateShadow();
    }

    public void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        fixed (byte* label = utf8Label)
            command.List->BeginEvent(0, label, checked((uint)utf8Label.Length));
    }

    public void EndEvent(CommandContext context)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
        command.List->EndEvent();
    }

    public void SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        D3D12CommandContext command = RequireCommandContext(context, nameof(context));
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
            _ => throw new ArgumentOutOfRangeException(nameof(topology)),
        };

    private sealed partial class D3D12CommandContext
    {
        private readonly VertexBufferBinding[] _vertexBuffers = new VertexBufferBinding[32];
        private uint _vertexBufferSetMask;
        private readonly StreamOutputBufferBinding[] _streamOutputBuffers =
            new StreamOutputBufferBinding[4];
        private byte _streamOutputBufferSetMask;
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
            uint vertexMask = _vertexBufferSetMask;
            while (vertexMask != 0)
            {
                int slot = BitOperations.TrailingZeroCount(vertexMask);
                _vertexBuffers[slot] = default;
                vertexMask &= vertexMask - 1;
            }
            _vertexBufferSetMask = 0;
            uint streamOutputMask = _streamOutputBufferSetMask;
            while (streamOutputMask != 0)
            {
                int slot = BitOperations.TrailingZeroCount(streamOutputMask);
                _streamOutputBuffers[slot] = default;
                streamOutputMask &= streamOutputMask - 1;
            }
            _streamOutputBufferSetMask = 0;
            if (_hasIndexBuffer)
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
                uint slot = checked(first + (uint)index);
                uint bit = 1u << checked((int)slot);
                if ((_vertexBufferSetMask & bit) == 0 ||
                    _vertexBuffers[checked((int)slot)] != values[index])
                    return false;
            }
            return true;
        }

        internal void RememberVertexBuffers(uint first, ReadOnlySpan<VertexBufferBinding> values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                uint slot = checked(first + (uint)index);
                _vertexBuffers[checked((int)slot)] = values[index];
                _vertexBufferSetMask |= 1u << checked((int)slot);
            }
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
                uint slot = checked(first + (uint)index);
                byte bit = checked((byte)(1u << checked((int)slot)));
                if ((_streamOutputBufferSetMask & bit) == 0 ||
                    _streamOutputBuffers[checked((int)slot)] != values[index])
                    return false;
            }
            return true;
        }

        internal void RememberStreamOutputBuffers(uint first, ReadOnlySpan<StreamOutputBufferBinding> values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                uint slot = checked(first + (uint)index);
                _streamOutputBuffers[checked((int)slot)] = values[index];
                _streamOutputBufferSetMask |= checked((byte)(1u << checked((int)slot)));
            }
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
                return NormalizedViewportEqualsSlow(_singleViewport, value);
            }
            return ViewportSequenceEqualSlow(values);
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
                return ScissorBitsEqual(_singleScissor, value);
            }
            return ScissorSequenceEqualSlow(values);
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
        private static bool ScissorBitsEqual(in ScissorRect left, in ScissorRect right)
        {
            ref byte leftBytes = ref Unsafe.As<ScissorRect, byte>(ref Unsafe.AsRef(in left));
            ref byte rightBytes = ref Unsafe.As<ScissorRect, byte>(ref Unsafe.AsRef(in right));
            return Unsafe.ReadUnaligned<ulong>(ref leftBytes) ==
                   Unsafe.ReadUnaligned<ulong>(ref rightBytes) &&
                   Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref leftBytes, 8)) ==
                   Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rightBytes, 8));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool NormalizedViewportEqualsSlow(
            in Viewport left,
            in Viewport right) =>
            NormalizedFloatEquals(left.X, right.X) &&
            NormalizedFloatEquals(left.Y, right.Y) &&
            NormalizedFloatEquals(left.Width, right.Width) &&
            NormalizedFloatEquals(left.Height, right.Height) &&
            NormalizedFloatEquals(left.MinimumDepth, right.MinimumDepth) &&
            NormalizedFloatEquals(left.MaximumDepth, right.MaximumDepth);

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool ViewportSequenceEqualSlow(ReadOnlySpan<Viewport> values) =>
            values.SequenceEqual(_viewports.AsSpan(0, _viewportCount));

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool ScissorSequenceEqualSlow(ReadOnlySpan<ScissorRect> values) =>
            values.SequenceEqual(_scissors.AsSpan(0, _scissorCount));

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
