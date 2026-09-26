using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Features;

/// <summary>Fixed constant inputs share one declared upload buffer. The graph holds its GPU copies.</summary>
internal sealed class FeatureUniformBuffer
{
    private readonly GraphBufferId _buffer;
    private readonly Dictionary<GraphBufferInput, int> _offsets = [];
    private readonly byte[] _bytes;
    private readonly int _stride;

    internal FeatureUniformBuffer(ref RenderGraphEdit edit, scoped ReadOnlySpan<GraphBufferInput> inputs,
        uint alignment, string label)
    {
        int requiredAlignment = checked((int)Math.Max(1u, alignment));
        _stride = checked((256 + requiredAlignment - 1) / requiredAlignment * requiredAlignment);
        _bytes = new byte[checked(inputs.Length * _stride)];
        _buffer = edit.DeclareExternalBuffer(
            new BufferDesc(checked((ulong)_bytes.Length), BufferUsages.Constant, label), MemoryType.Upload);
        for (int index = 0; index < inputs.Length; index++)
            _offsets.Add(inputs[index], index * _stride);
    }

    internal void Write<T>(ref RenderGraphFrame frame, GraphBufferInput input, in T value) where T : unmanaged
    {
        if (Unsafe.SizeOf<T>() > _stride)
            throw new ArgumentException("The constant value exceeds its declared block.", nameof(value));
        int offset = _offsets[input];
        MemoryMarshal.Write(_bytes.AsSpan(offset, _stride), in value);
        frame.SetBufferInput(input, new GraphBufferSlice(_buffer,
            new BufferRange(checked((ulong)offset), checked((ulong)_stride))));
    }

    internal void Upload(ref RenderGraphFrame frame) => frame.SetBufferData(_buffer, _bytes);
}
