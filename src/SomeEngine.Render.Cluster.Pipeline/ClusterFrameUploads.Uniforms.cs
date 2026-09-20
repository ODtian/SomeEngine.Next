using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

internal sealed partial class ClusterFrameUploads
{
    private readonly Dictionary<GraphBufferInput, UniformData> _uniforms = [];
    private readonly Dictionary<GraphBufferInput, UniformData?> _uniformChanges = [];

    private GraphBufferId _uniformBuffer;
    private GraphBufferId _originalUniformBuffer;
    private byte[] _uniformBytes = [];
    private int _uniformSize;
    private int _originalUniformSize;
    private System.Collections.Immutable.ImmutableList<BufferRange> _uniformFree = [];
    private System.Collections.Immutable.ImmutableList<BufferRange> _originalUniformFree = [];

    private sealed class UniformData(byte[] value, string label, int offset, int capacity)
    {
        internal readonly byte[] Value = value;
        internal readonly string Label = label;
        internal readonly int Offset = offset;
        internal readonly int Capacity = capacity;
        internal bool Bound;
    }

    internal GraphBufferInput DeclareUniform(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> value, string label)
    {
        RequireEdit();
        GraphBufferInput input = edit.CreateBufferInput(BufferUsages.Constant);
        _uniforms.Add(input, AllocateUniform(ref edit, value, label));
        _uniformChanges.Add(input, null);
        return input;
    }

    internal void SetUniformValue(ref RenderGraphEdit edit, GraphBufferInput input, scoped ReadOnlySpan<byte> value)
    {
        RequireEdit();
        UniformData previous = _uniforms[input];
        if (value.SequenceEqual(previous.Value)) return;
        _uniformChanges.TryAdd(input, previous);
        _uniforms[input] = value.Length <= previous.Capacity
            ? new UniformData(value.ToArray(), previous.Label, previous.Offset, previous.Capacity)
            : AllocateUniform(ref edit, value, previous.Label);
        if (value.Length > previous.Capacity) FreeUniform(previous);
    }

    internal void RemoveUniform(GraphBufferInput input)
    {
        RequireEdit();
        _uniformChanges.TryAdd(input, _uniforms[input]);
        FreeUniform(_uniforms[input]);
        _uniforms.Remove(input);
    }

    internal void WriteUniformAndBind(ref RenderGraphFrame frame, int generation,
        scoped ReadOnlySpan<byte> value, GraphBufferInput input)
    {
        if (!_writing || generation != _frameGeneration)
            throw new InvalidOperationException("Cluster upload writes require the active generation.");
        UniformData uniform = _uniforms[input];
        if (value.Length > uniform.Capacity)
        {
            // A larger one-frame value remains an ordinary frame input. Growing retained
            // data is a graph edit; it must not be hidden in the frame update.
            frame.SetBufferInput(input, frame.Upload<byte>(value, BufferUsages.Constant, uniform.Label));
        }
        else
        {
            _uniformBytes.AsSpan(uniform.Offset, uniform.Capacity).Clear();
            value.CopyTo(_uniformBytes.AsSpan(uniform.Offset));
            frame.SetBufferInput(input, new GraphBufferSlice(_uniformBuffer,
                new BufferRange(checked((ulong)uniform.Offset), checked((ulong)uniform.Capacity))));
        }
        uniform.Bound = true;
    }

    private UniformData AllocateUniform(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> value, string label)
    {
        int alignment = checked((int)Math.Max(1u, _device.Capabilities.Limits.ConstantBufferAlignment));
        int capacity = checked((value.Length + alignment - 1) / alignment * alignment);
        int offset = _uniformSize;
        for (int index = 0; index < _uniformFree.Count; index++)
        {
            BufferRange available = _uniformFree[index];
            if (available.Size < (ulong)capacity) continue;
            offset = checked((int)available.Offset);
            _uniformFree = _uniformFree.RemoveAt(index);
            if (available.Size > (ulong)capacity)
                _uniformFree = _uniformFree.Insert(index, new BufferRange(available.Offset + (ulong)capacity,
                    available.Size - (ulong)capacity));
            break;
        }
        _uniformSize = Math.Max(_uniformSize, checked(offset + capacity));
        if (_uniformBytes.Length < _uniformSize)
            Array.Resize(ref _uniformBytes, Math.Max(_uniformSize, Math.Max(256, _uniformBytes.Length * 2)));
        var description = new BufferDesc(checked((ulong)_uniformSize),
            BufferUsages.Constant, "Cluster uniform data");
        if (_uniformBuffer == default)
        {
            _uniformBuffer = edit.DeclareExternalBuffer(description, MemoryType.Upload);
        }
        else edit.SetBufferDescription(_uniformBuffer, description, MemoryType.Upload);
        return new UniformData(value.ToArray(), label, offset, capacity);
    }

    private void FreeUniform(UniformData uniform)
    {
        ulong first = checked((ulong)uniform.Offset);
        ulong end = first + checked((ulong)uniform.Capacity);
        int index = 0;
        while (index < _uniformFree.Count && _uniformFree[index].Offset + _uniformFree[index].Size < first) index++;
        while (index < _uniformFree.Count && _uniformFree[index].Offset <= end)
        {
            BufferRange range = _uniformFree[index];
            first = Math.Min(first, range.Offset);
            end = Math.Max(end, range.Offset + range.Size);
            _uniformFree = _uniformFree.RemoveAt(index);
        }
        _uniformFree = _uniformFree.Insert(index, new BufferRange(first, end - first));
    }

    private void CompleteUniforms(ref RenderGraphFrame frame)
    {
        if (_uniformBuffer == default) return;
        foreach ((GraphBufferInput input, UniformData uniform) in _uniforms)
        {
            if (uniform.Bound) continue;
            _uniformBytes.AsSpan(uniform.Offset, uniform.Capacity).Clear();
            uniform.Value.CopyTo(_uniformBytes.AsSpan(uniform.Offset));
            frame.SetBufferInput(input, new GraphBufferSlice(_uniformBuffer,
                new BufferRange(checked((ulong)uniform.Offset), checked((ulong)uniform.Capacity))));
        }
        frame.SetBufferData(_uniformBuffer, _uniformBytes.AsSpan(0, _uniformSize));
    }
}
