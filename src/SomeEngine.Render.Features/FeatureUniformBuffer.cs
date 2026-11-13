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
    private readonly int _byteCount;
    private readonly string _label;
    private readonly int _stride;

    internal FeatureUniformBuffer(ref RenderGraphEdit edit, scoped ReadOnlySpan<GraphBufferInput> inputs,
        uint alignment, string label)
    {
        _label = label;
        int requiredAlignment = checked((int)Math.Max(1u, alignment));
        _stride = checked((256 + requiredAlignment - 1) / requiredAlignment * requiredAlignment);
        _byteCount = checked(inputs.Length * _stride);
        _buffer = edit.CreateTransientBuffer(
            new BufferDesc(checked((ulong)_byteCount), BufferUsages.Constant, label), MemoryType.Upload);
        for (int index = 0; index < inputs.Length; index++)
            _offsets.Add(inputs[index], index * _stride);
    }

    private FeatureUniformBuffer(GraphBufferId buffer, Dictionary<GraphBufferInput, int> offsets,
        int byteCount, int stride, string label)
        => (_buffer, _offsets, _byteCount, _stride, _label) = (buffer, offsets, byteCount, stride, label);

    internal FeatureUniformBuffer AddInputs(ref RenderGraphEdit edit, scoped ReadOnlySpan<GraphBufferInput> inputs)
    {
        var offsets = new Dictionary<GraphBufferInput, int>(_offsets);
        int byteCount = _byteCount;
        foreach (GraphBufferInput input in inputs)
        {
            offsets.Add(input, byteCount);
            byteCount += _stride;
        }
        edit.SetBufferDescription(_buffer, new BufferDesc((ulong)byteCount, BufferUsages.Constant, _label));
        return new(_buffer, offsets, byteCount, _stride, _label);
    }

    internal void Write<T>(ref RenderGraphFrame frame, GraphBufferInput input, in T value) where T : unmanaged
    {
        if (Unsafe.SizeOf<T>() > _stride)
            throw new ArgumentException("The constant value exceeds its declared block.", nameof(value));
        int offset = _offsets[input];
        byte[] bytes = frame.GetFrameData(this, () => new byte[_byteCount]);
        MemoryMarshal.Write(bytes.AsSpan(offset, _stride), in value);
        frame.SetBufferInput(input, new GraphBufferSlice(_buffer,
            new BufferRange(checked((ulong)offset), checked((ulong)_stride))));
    }

    internal void Upload(ref RenderGraphFrame frame) => frame.SetBufferData(_buffer, frame.GetFrameData(this, () => new byte[_byteCount]));
}
