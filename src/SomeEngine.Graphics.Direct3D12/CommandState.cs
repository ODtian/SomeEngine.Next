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
    private const uint MaximumVertexBindings = 32;
    private const uint MaximumStreamOutputBindings = 4;
    public void SetVertexBuffers(CommandContext context, uint firstBinding, ReadOnlySpan<VertexBufferBinding> bindings)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.VertexBuffersEqual(firstBinding, bindings))
            return;
        command.PrepareResolvedResources(bindings.Length);
        VertexBufferView* native = stackalloc VertexBufferView[bindings.Length];
        try
        {
            for (int index = 0; index < bindings.Length; index++)
            {
                ref readonly VertexBufferBinding binding = ref bindings[index];
                D3D12Buffer buffer = RequireBuffer(binding.Buffer);
                command.StoreResolvedResource(index, buffer);
                native[index] = new VertexBufferView(ResourceTrace.GetGPUVirtualAddress(buffer.Native) + binding.Offset, checked((uint)binding.Size), binding.Stride);
            }
        }
        catch
        {
            command.ClearResolvedResources(bindings.Length);
            throw;
        }

        command.RecordResolvedSwapchainImages(bindings.Length);
        CommandBindingTrace.IASetVertexBuffers(command, command.List, firstBinding, checked((uint)bindings.Length), native);
        command.RememberVertexBuffers(firstBinding, bindings);
    }

    public void SetIndexBuffer(CommandContext context, in IndexBufferBinding binding)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.IndexBufferEquals(binding))
            return;
        D3D12Buffer buffer = RequireBuffer(binding.Buffer);
        IndexBufferView native = new(
            ResourceTrace.GetGPUVirtualAddress(buffer.Native) + binding.Offset,
            checked((uint)binding.Size),
            binding.Type == IndexType.UInt16 ? DxgiFormat.FormatR16Uint : DxgiFormat.FormatR32Uint
        );
        CommandBindingTrace.IASetIndexBuffer(command, command.List, &native);
        command.RememberIndexBuffer(binding);
    }

    public void SetStreamOutputBuffers(CommandContext context, uint firstBinding, ReadOnlySpan<StreamOutputBufferBinding> bindings)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.StreamOutputBuffersEqual(firstBinding, bindings))
            return;
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
                    filledSizeLocation = ResourceTrace.GetGPUVirtualAddress(filledNative.Native) + binding.FilledSizeOffset;
                }

                native[index] = new StreamOutputBufferView(ResourceTrace.GetGPUVirtualAddress(buffer.Native) + binding.Offset, binding.Size, filledSizeLocation);
            }
        }
        catch
        {
            command.ClearResolvedResources(checked(bindings.Length * 2));
            throw;
        }

        command.RecordResolvedSwapchainImages(checked(bindings.Length * 2));
        CommandBindingTrace.SOSetTargets(command, command.List, firstBinding, checked((uint)bindings.Length), native);
        command.RememberStreamOutputBuffers(firstBinding, bindings);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void SetViewports(CommandContext context, ReadOnlySpan<Viewport> viewports)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.ViewportsEqual(viewports))
            return;
        SetViewportsSlow(command, viewports);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetViewportsSlow(D3D12CommandContext command, ReadOnlySpan<Viewport> viewports)
    {
        NativeViewport* native = stackalloc NativeViewport[viewports.Length];
        for (int index = 0; index < viewports.Length; index++)
        {
            ref readonly Viewport viewport = ref viewports[index];
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
        D3D12CommandListFastCalls.SetViewports(command, command.List, checked((uint)viewports.Length), native);
        command.RememberViewports(viewports);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    public void SetScissors(CommandContext context, ReadOnlySpan<ScissorRect> scissors)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        foreach (ref readonly ScissorRect rect in scissors)
        {
        }

        if (command.ScissorsEqual(scissors))
            return;
        SetScissorsSlow(command, scissors);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static void SetScissorsSlow(D3D12CommandContext command, ReadOnlySpan<ScissorRect> scissors)
    {
        Box2D<int>* native = stackalloc Box2D<int>[scissors.Length];
        for (int index = 0; index < scissors.Length; index++)
        {
            ref readonly ScissorRect rect = ref scissors[index];
            native[index] = new Box2D<int>(rect.X, rect.Y, checked(rect.X + rect.Width), checked(rect.Y + rect.Height));
        }

        command.PrepareScissors(scissors.Length);
        D3D12CommandListFastCalls.SetScissors(command, command.List, checked((uint)scissors.Length), native);
        command.RememberScissors(scissors);
    }

    public void SetBlendConstants(CommandContext context, in Vector4 value)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.BlendConstantsEqual(value))
            return;
        Vector4 copy = value;
        CommandBindingTrace.OMSetBlendFactor(command, command.List, (float*)&copy);
        command.RememberBlendConstants(value);
    }

    public void SetStencilReference(CommandContext context, uint value)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.StencilReferenceEqual(value))
            return;
        CommandBindingTrace.OMSetStencilRef(command, command.List, value);
        command.RememberStencilReference(value);
    }

    public void SetDepthBounds(CommandContext context, float minimum, float maximum)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.DepthBoundsEqual(minimum, maximum))
            return;
        CommandBindingTrace.OMSetDepthBounds(command, command.List, minimum, maximum);
        command.RememberDepthBounds(minimum, maximum);
    }

    public void SetDepthBias(CommandContext context, int bias, float clamp, float slopeScaledBias)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.DepthBiasEqual(bias, clamp, slopeScaledBias))
            return;
        CommandBindingTrace.RSSetDepthBias(command, command.List, bias, clamp, slopeScaledBias);
        command.RememberDepthBias(bias, clamp, slopeScaledBias);
    }

    public void SetPrimitiveTopology(CommandContext context, PrimitiveTopology topology)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.PrimitiveTopologyEqual(topology))
            return;
        D3D12CommandListFastCalls.SetPrimitiveTopology(command, command.List, ToNativeTopology(topology));
        command.RememberPrimitiveTopology(topology);
    }

    public void SetStripCut(CommandContext context, StripCut stripCut)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.StripCutEqual(stripCut))
            return;
        CommandBindingTrace.IASetIndexBufferStripCutValue(command, command.List, stripCut switch
        {
            StripCut.Disabled => IndexBufferStripCutValue.ValueDisabled,
            StripCut.UInt16 => IndexBufferStripCutValue.Value0xFfff,
            StripCut.UInt32 => IndexBufferStripCutValue.Value0xFfffffff,
            _ => default!,
        });
        command.RememberStripCut(stripCut);
    }

    public void SetPredication(
        CommandContext context,
        Buffer? buffer,
        ulong offset = 0,
        PredicationOperation operation = PredicationOperation.NotEqualZero
    )
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        if (command.PredicationEqual(buffer, offset, operation))
            return;
        D3D12Buffer? native = buffer is null ? null : RequireBuffer(buffer);
        ID3D12Resource* predicate = native is null ? null : native.Native;
        CommandBindingTrace.SetPredication(
            command,
            command.List,
            predicate,
            offset,
            operation == PredicationOperation.EqualZero ? PredicationOp.EqualZero : PredicationOp.NotEqualZero
        );
        command.RememberPredication(buffer, offset, operation);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    public void Draw(CommandContext context, in DrawArguments arguments)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12CommandListFastCalls.DrawInstanced(command, command.List, arguments.VertexCount, arguments.InstanceCount, arguments.FirstVertex, arguments.FirstInstance);
    }

    public void DrawIndexed(CommandContext context, in DrawIndexedArguments arguments)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12CommandListFastCalls.DrawIndexedInstanced(
            command,
            command.List,
            arguments.IndexCount,
            arguments.InstanceCount,
            arguments.FirstIndex,
            arguments.VertexOffset,
            arguments.FirstInstance
        );
    }

    public void Dispatch(CommandContext context, in DispatchArguments arguments)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12CommandListFastCalls.Dispatch(command, command.List, arguments.X, arguments.Y, arguments.Z);
    }

    public void ExecuteBundle(CommandContext context, RecordedBundle bundle)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        D3D12RecordedBundle native = RequireBundle(bundle);
        CommandExecutionTrace.ExecuteBundle(command, command.List, (ID3D12GraphicsCommandList*)native.NativeList);
        command.InvalidateStateShadow();
    }

    public void BeginEvent(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        fixed (byte* label = utf8Label)
            CommandExecutionTrace.BeginEvent(command, command.List, 0, label, checked((uint)utf8Label.Length));
    }

    public void EndEvent(CommandContext context)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        CommandExecutionTrace.EndEvent(command, command.List);
    }

    public void SetMarker(CommandContext context, ReadOnlySpan<byte> utf8Label)
    {
        using var trace = global::SomeEngine.Diagnostics.EngineTrace.Begin(RhiTrace.RhiCommandsAndOther);
        D3D12CommandContext command = ((D3D12CommandContext)context);
        command.BeginPublicCall();
        fixed (byte* label = utf8Label)
            CommandBindingTrace.SetMarker(command, command.List, 0, label, checked((uint)utf8Label.Length));
    }

    private static D3DPrimitiveTopology ToNativeTopology(PrimitiveTopology topology) => topology switch
    {
        PrimitiveTopology.PointList => D3DPrimitiveTopology.D3DPrimitiveTopologyPointlist,
        PrimitiveTopology.LineList => D3DPrimitiveTopology.D3DPrimitiveTopologyLinelist,
        PrimitiveTopology.LineStrip => D3DPrimitiveTopology.D3DPrimitiveTopologyLinestrip,
        PrimitiveTopology.TriangleList => D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglelist,
        PrimitiveTopology.TriangleStrip => D3DPrimitiveTopology.D3DPrimitiveTopologyTrianglestrip,
        _ => default!,
    };
    private sealed partial class D3D12CommandContext
    {
        private readonly VertexBufferBinding[] _vertexBuffers = new VertexBufferBinding[32];
        private uint _vertexBufferSetMask;
        private readonly StreamOutputBufferBinding[] _streamOutputBuffers = new StreamOutputBufferBinding[4];
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
                int entry = BitOperations.TrailingZeroCount(vertexMask);
                _vertexBuffers[entry] = default;
                vertexMask &= vertexMask - 1;
            }

            _vertexBufferSetMask = 0;
            uint streamOutputMask = _streamOutputBufferSetMask;
            while (streamOutputMask != 0)
            {
                int entry = BitOperations.TrailingZeroCount(streamOutputMask);
                _streamOutputBuffers[entry] = default;
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
                uint entry = checked(first + (uint)index);
                uint bit = 1u << checked((int)entry);
                if ((_vertexBufferSetMask & bit) == 0 || _vertexBuffers[checked((int)entry)] != values[index])
                    return false;
            }

            return true;
        }

        internal void RememberVertexBuffers(uint first, ReadOnlySpan<VertexBufferBinding> values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                uint entry = checked(first + (uint)index);
                _vertexBuffers[checked((int)entry)] = values[index];
                _vertexBufferSetMask |= 1u << checked((int)entry);
            }
        }

        internal bool IndexBufferEquals(in IndexBufferBinding value) => _hasIndexBuffer && _indexBuffer == value;
        internal void RememberIndexBuffer(in IndexBufferBinding value)
        {
            _indexBuffer = value;
            _hasIndexBuffer = true;
        }

        internal bool StreamOutputBuffersEqual(uint first, ReadOnlySpan<StreamOutputBufferBinding> values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                uint entry = checked(first + (uint)index);
                byte bit = checked((byte)(1u << checked((int)entry)));
                if ((_streamOutputBufferSetMask & bit) == 0 || _streamOutputBuffers[checked((int)entry)] != values[index])
                    return false;
            }

            return true;
        }

        internal void RememberStreamOutputBuffers(uint first, ReadOnlySpan<StreamOutputBufferBinding> values)
        {
            for (int index = 0; index < values.Length; index++)
            {
                uint entry = checked(first + (uint)index);
                _streamOutputBuffers[checked((int)entry)] = values[index];
                _streamOutputBufferSetMask |= checked((byte)(1u << checked((int)entry)));
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
            return Unsafe.ReadUnaligned<ulong>(ref leftBytes) == Unsafe.ReadUnaligned<ulong>(ref rightBytes) && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref leftBytes, 8)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rightBytes, 8)) && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref leftBytes, 16)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rightBytes, 16));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool ScissorBitsEqual(in ScissorRect left, in ScissorRect right)
        {
            ref byte leftBytes = ref Unsafe.As<ScissorRect, byte>(ref Unsafe.AsRef(in left));
            ref byte rightBytes = ref Unsafe.As<ScissorRect, byte>(ref Unsafe.AsRef(in right));
            return Unsafe.ReadUnaligned<ulong>(ref leftBytes) == Unsafe.ReadUnaligned<ulong>(ref rightBytes) && Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref leftBytes, 8)) == Unsafe.ReadUnaligned<ulong>(ref Unsafe.Add(ref rightBytes, 8));
        }

        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private static bool NormalizedViewportEqualsSlow(in Viewport left, in Viewport right) => NormalizedFloatEquals(left.X, right.X) && NormalizedFloatEquals(left.Y, right.Y) && NormalizedFloatEquals(left.Width, right.Width) && NormalizedFloatEquals(left.Height, right.Height) && NormalizedFloatEquals(left.MinimumDepth, right.MinimumDepth) && NormalizedFloatEquals(left.MaximumDepth, right.MaximumDepth);
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool ViewportSequenceEqualSlow(ReadOnlySpan<Viewport> values) => values.SequenceEqual(_viewports.AsSpan(0, _viewportCount));
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        private bool ScissorSequenceEqualSlow(ReadOnlySpan<ScissorRect> values) => values.SequenceEqual(_scissors.AsSpan(0, _scissorCount));
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
        private static bool NormalizedFloatEquals(float left, float right) => left == right || (float.IsNaN(left) && float.IsNaN(right));
        internal bool BlendConstantsEqual(in Vector4 value) => _hasBlendConstants && _blendConstants.Equals(value);
        internal void RememberBlendConstants(in Vector4 value)
        {
            _blendConstants = value;
            _hasBlendConstants = true;
        }

        internal bool StencilReferenceEqual(uint value) => _hasStencilReference && _stencilReference == value;
        internal void RememberStencilReference(uint value)
        {
            _stencilReference = value;
            _hasStencilReference = true;
        }

        internal bool DepthBoundsEqual(float minimum, float maximum) => _depthBounds is { } current && current.Minimum.Equals(minimum) && current.Maximum.Equals(maximum);
        internal void RememberDepthBounds(float minimum, float maximum) => _depthBounds = (minimum, maximum);
        internal bool DepthBiasEqual(int bias, float clamp, float slope) => _depthBias is { } current && current.Bias == bias && current.Clamp.Equals(clamp) && current.Slope.Equals(slope);
        internal void RememberDepthBias(int bias, float clamp, float slope) => _depthBias = (bias, clamp, slope);
        internal bool PrimitiveTopologyEqual(PrimitiveTopology value) => _primitiveTopology == value;
        internal void RememberPrimitiveTopology(PrimitiveTopology value) => _primitiveTopology = value;
        internal bool StripCutEqual(StripCut value) => _stripCut == value;
        internal void RememberStripCut(StripCut value) => _stripCut = value;
        internal bool PredicationEqual(Buffer? buffer, ulong offset, PredicationOperation operation) => _predication is { } current && ReferenceEquals(current.Buffer, buffer) && current.Offset == offset && current.Operation == operation;
        internal void RememberPredication(Buffer? buffer, ulong offset, PredicationOperation operation) => _predication = (buffer, offset, operation);
    }
}
