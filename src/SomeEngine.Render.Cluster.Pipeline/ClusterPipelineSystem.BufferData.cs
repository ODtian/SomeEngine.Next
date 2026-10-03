using System.Collections.Immutable;
using SomeEngine.Graphics;
using SomeEngine.RenderGraph;

namespace SomeEngine.Render.Cluster.Pipeline;

public sealed partial class ClusterPipelineSystem
{
    private readonly record struct BufferWrite(BufferRange Range, ClusterBytes Bytes);
    private readonly record struct ShaderParameters(byte[] Bytes, int Offset, int Capacity);

    private GraphBufferId CreateBufferData(ref RenderGraphEdit edit, ClusterBytes bytes,
        BufferUsages usages, string label)
    {
        var description = new BufferDesc(checked((ulong)Math.Max(bytes.Length, 4)),
            usages | BufferUsages.CopyDestination, label);
        GraphBufferId buffer = edit.CreatePersistentBuffer(description);
        GraphPassId upload = edit.AddBufferUploadPass(buffer);
        GraphData.BufferDescriptions = GraphData.BufferDescriptions.Add(buffer, (description, upload));
        QueueBufferWrites(buffer, bytes, [new BufferRange(0, checked((ulong)bytes.Length))]);
        return buffer;
    }

    private GraphBufferId CreateBufferData(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> bytes,
        BufferUsages usages, string label) => CreateBufferData(ref edit, ClusterBytes.FromBytes(bytes), usages, label);

    private void UpdateBufferData(ref RenderGraphEdit edit, GraphBufferId buffer,
        ClusterBytes bytes, scoped ReadOnlySpan<BufferRange> changes)
    {
        var data = GraphData.BufferDescriptions[buffer];
        BufferDesc description = data.Description;
        ulong size = checked((ulong)Math.Max(bytes.Length, 4));
        if (description.Size != size)
        {
            description = description with { Size = size };
            edit.SetBufferDescription(buffer, description);
            GraphData.BufferDescriptions = GraphData.BufferDescriptions.SetItem(buffer, (description, data.UploadPass));
            GraphData.BufferWrites = GraphData.BufferWrites.Remove(buffer);
            QueueBufferWrites(buffer, bytes, [new BufferRange(0, checked((ulong)bytes.Length))]);
        }
        else QueueBufferWrites(buffer, bytes, changes);
    }

    private void QueueBufferWrites(GraphBufferId buffer, ClusterBytes bytes,
        scoped ReadOnlySpan<BufferRange> changes)
    {
        var writes = GraphData.BufferWrites.GetValueOrDefault(buffer, ImmutableList<BufferWrite>.Empty);
        foreach (BufferRange range in changes)
        {
            if (range.Size == 0) continue;
            writes = writes.Add(new BufferWrite(range, bytes));
        }
        GraphData.BufferWrites = GraphData.BufferWrites.SetItem(buffer, writes);
    }

    private void RemoveBufferData(ref RenderGraphEdit edit, GraphBufferId buffer)
    {
        edit.Remove(buffer);
        GraphData.BufferDescriptions = GraphData.BufferDescriptions.Remove(buffer);
        GraphData.BufferWrites = GraphData.BufferWrites.Remove(buffer);
    }

    private GraphBufferInput CreateParameters(ref RenderGraphEdit edit,
        scoped ReadOnlySpan<byte> bytes, string label)
    {
        GraphBufferInput input = edit.CreateBufferInput(BufferUsages.Constant);
        GraphData.Parameters = GraphData.Parameters.Add(input, AllocateParameters(ref edit, bytes));
        return input;
    }

    private ShaderParameters AllocateParameters(ref RenderGraphEdit edit, scoped ReadOnlySpan<byte> bytes)
    {
        int alignment = checked((int)Math.Max(1u, _device.Capabilities.Limits.ConstantBufferAlignment));
        int capacity = checked((bytes.Length + alignment - 1) / alignment * alignment);
        int offset;
        if (GraphData.ParameterFreeOffsets.TryGetValue(capacity, out var available) && !available.IsEmpty)
        {
            offset = available.Peek();
            GraphData.ParameterFreeOffsets = GraphData.ParameterFreeOffsets.SetItem(capacity, available.Pop());
        }
        else
        {
            offset = GraphData.ParameterByteCount;
            GraphData.ParameterByteCount = checked(offset + capacity);
            var description = new BufferDesc(checked((ulong)GraphData.ParameterByteCount),
                BufferUsages.Constant, "Cluster shader parameters");
            if (GraphData.ShaderParameterBytes == default)
                GraphData.ShaderParameterBytes = edit.DeclareExternalBuffer(description, MemoryType.Upload);
            else edit.SetBufferDescription(GraphData.ShaderParameterBytes, description, MemoryType.Upload);
        }
        return new ShaderParameters(bytes.ToArray(), offset, capacity);
    }

    private void UpdateParameters(ref RenderGraphEdit edit, GraphBufferInput input, scoped ReadOnlySpan<byte> bytes)
    {
        ShaderParameters previous = GraphData.Parameters[input];
        if (bytes.Length <= previous.Capacity)
            GraphData.Parameters = GraphData.Parameters.SetItem(input, previous with { Bytes = bytes.ToArray() });
        else
        {
            ReleaseParameters(previous);
            GraphData.Parameters = GraphData.Parameters.SetItem(input, AllocateParameters(ref edit, bytes));
        }
    }

    private void ReleaseParameters(ShaderParameters parameters) => GraphData.ParameterFreeOffsets =
        GraphData.ParameterFreeOffsets.SetItem(parameters.Capacity,
            GraphData.ParameterFreeOffsets.GetValueOrDefault(parameters.Capacity, ImmutableStack<int>.Empty).Push(parameters.Offset));

    private void RemoveParameters(GraphBufferInput input)
    {
        ReleaseParameters(GraphData.Parameters[input]);
        GraphData.Parameters = GraphData.Parameters.Remove(input);
    }
}
